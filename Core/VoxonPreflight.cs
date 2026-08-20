// ═══════════════════════════════════════════════════════════════════════════
//  VoxonPreflight.cs — portable pre-launch initialization screen orchestration
//
//  Drop-in copy from VoxonPlyPlayback (see D:\git\playbackPLY\VoxonBootInit).
//  The companion to VoxonHardwareCheck.cs. Where VoxonHardwareCheck answers
//  "is the hardware plugged in?", this drives the whole boot screen around it:
//
//      • polls for the hardware with a live countdown,
//      • shows Retry / Continue-in-Simulator / Quit and reacts to the operator,
//      • auto-falls-through to Simulator mode if nobody chooses before timeout
//        (so a kiosk/unattended install always reaches a running state),
//
//  …and returns a single clean verdict the host acts on:
//
//      PreflightOutcome.Hardware   → init the SDK + start the motor
//      PreflightOutcome.Simulator  → init the SDK, skip the motor
//      PreflightOutcome.Quit       → exit the app
//
//  It is UI-agnostic. The host supplies an IPreflightUi (or the callback
//  adapter, PreflightUi.FromCallbacks) wiring whatever splash/window it has —
//  Avalonia, WinForms, WPF, or a console. No dependency on app settings.
//
//  Usage (blocking; call from a background/boot thread, NOT the UI thread):
//
//      using Voxon;
//
//      var outcome = VoxonPreflight.Run(
//          VoxonHardwareCheck.VX2XL,
//          ui:             mySplashBridge,         // implements IPreflightUi
//          timeoutSeconds: 8,
//          log:            msg => App.Log(msg));
//
//      switch (outcome)
//      {
//          case PreflightOutcome.Quit:      return;           // shut down
//          case PreflightOutcome.Simulator: simulator = true; break;
//          case PreflightOutcome.Hardware:  simulator = false; break;
//      }
//      // …continue to SDK init; start the motor only when !simulator.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Threading;

namespace Voxon
{
    /// <summary>What the host should do once the pre-flight screen finishes.</summary>
    public enum PreflightOutcome
    {
        /// <summary>Hardware confirmed present — init SDK and start the motor.</summary>
        Hardware,
        /// <summary>No hardware (operator chose, or timed out) — init SDK, no motor.</summary>
        Simulator,
        /// <summary>Operator chose to quit — shut the app down.</summary>
        Quit,
    }

    /// <summary>The three operator choices, as written to <see cref="IPreflightUi.PollChoice"/>.</summary>
    public static class PreflightChoice
    {
        public const int None      = 0;
        public const int Retry     = 1;
        public const int Simulator = 2;
        public const int Quit      = 3;
    }

    /// <summary>
    /// The host's bridge to its boot/splash UI. All three members are called
    /// from the thread that runs <see cref="VoxonPreflight.Run"/> (a background
    /// thread). Implementations must be safe to invoke from there — typically
    /// they just set volatile fields the UI thread reads, or marshal onto the
    /// UI thread internally.
    /// </summary>
    public interface IPreflightUi
    {
        /// <summary>Update the splash status line. <paramref name="warning"/> hints
        /// the UI to style it as a warning/error (e.g. red).</summary>
        void ShowStatus(string message, bool warning = false);

        /// <summary>Show or hide the Retry / Continue-in-Simulator / Quit buttons.</summary>
        void ShowButtons(bool visible);

        /// <summary>Return the operator's pending choice and CLEAR it back to
        /// <see cref="PreflightChoice.None"/>, or return None if nothing is pending.
        /// Polled frequently; must be cheap and non-blocking.</summary>
        int PollChoice();
    }

    public static class VoxonPreflight
    {
        /// <summary>
        /// Run the full pre-launch hardware screen and return the verdict.
        /// Blocks the calling thread (poll loop + button wait). Never throws for
        /// a missing device — only an unexpected UI/callback exception would
        /// propagate.
        /// </summary>
        /// <param name="spec">Which model to look for (e.g. <c>VoxonHardwareCheck.VX2XL</c>).</param>
        /// <param name="ui">Bridge to the host's splash UI.</param>
        /// <param name="timeoutSeconds">Seconds to poll before auto-entering Simulator mode.</param>
        /// <param name="pollSeconds">Seconds between hardware re-checks.</param>
        /// <param name="log">Optional diagnostic sink. On failure the full PnP
        /// device list is logged here so identifiers can be tuned later.</param>
        public static PreflightOutcome Run(
            VoxonHardwareSpec spec,
            IPreflightUi ui,
            double timeoutSeconds = 8.0,
            double pollSeconds    = 1.0,
            Action<string>? log   = null)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (ui   == null) throw new ArgumentNullException(nameof(ui));
            log ??= _ => { };

            while (true)   // outer loop — Retry restarts the whole check
            {
                // Buttons stay visible the whole time so the operator can bail
                // into Simulator (or Quit) without waiting out the timeout.
                ui.ShowButtons(true);
                ui.ShowStatus($"Checking for Voxon {spec.ModelName} hardware…");
                log($"[Preflight] Checking: model={spec.ModelName}, required={spec.RequiredDeviceCount}");

                // A button click cancels the poll early via this token.
                using var cts = new CancellationTokenSource();
                HardwareCheckResult result;
                try
                {
                    result = VoxonHardwareCheck.WaitForHardware(
                        spec,
                        timeout:      TimeSpan.FromSeconds(timeoutSeconds),
                        pollInterval: TimeSpan.FromSeconds(pollSeconds),
                        onPoll: (elapsed, partial) =>
                        {
                            // Count DOWN so the operator sees how long until the
                            // app falls through to Simulator mode on its own.
                            double remaining = Math.Max(0, timeoutSeconds - elapsed.TotalSeconds);
                            ui.ShowStatus(
                                $"Checking for Voxon {spec.ModelName} hardware… ({remaining:F0}s)\n"
                              + $"Found {partial.Found} of {partial.Required} devices.");
                            if (ui.PollChoice() != PreflightChoice.None) cts.Cancel();
                        },
                        cancellationToken: cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Operator clicked mid-poll — synthesize a failed result so
                    // the choice handler below runs.
                    result = new HardwareCheckResult
                    {
                        Ok       = false,
                        Found    = 0,
                        Required = spec.RequiredDeviceCount,
                    };
                }

                // Hardware found → proceed normally.
                if (result.Ok)
                {
                    log($"[Preflight] OK: {result.Found}/{result.Required} matched.");
                    foreach (var m in result.Matched) log($"[Preflight]   {m}");
                    ui.ShowButtons(false);
                    ui.ShowStatus($"Voxon {spec.ModelName} detected. Initializing…");
                    return PreflightOutcome.Hardware;
                }

                // Did the operator make a choice (during or before timeout)?
                int choice = ui.PollChoice();
                if (choice != PreflightChoice.None)
                {
                    switch (choice)
                    {
                        case PreflightChoice.Retry:
                            log("[Preflight] Operator chose Retry.");
                            continue;                       // restart outer loop
                        case PreflightChoice.Simulator:
                            log("[Preflight] Operator chose Continue in Simulator.");
                            ui.ShowButtons(false);
                            ui.ShowStatus("Simulator mode. Initializing…");
                            return PreflightOutcome.Simulator;
                        default:
                            log("[Preflight] Operator chose Quit.");
                            ui.ShowButtons(false);
                            return PreflightOutcome.Quit;
                    }
                }

                // Timed out with no hardware and no choice → log the full device
                // list once (for tuning identifiers) and auto-enter Simulator so
                // the app boots unattended.
                log($"[Preflight] FAILED: {result.Found}/{result.Required} matched."
                  + (result.Error != null ? $"  Error: {result.Error}" : ""));
                log($"[Preflight] All PnP devices ({result.AllDevices.Count}):");
                foreach (var d in result.AllDevices) log($"[Preflight]   {d}");

                log("[Preflight] Timed out — auto-entering Simulator mode.");
                ui.ShowButtons(false);
                ui.ShowStatus(
                    $"Voxon {spec.ModelName} hardware not detected.\nContinuing in Simulator mode…",
                    warning: true);
                return PreflightOutcome.Simulator;
            }
        }

        /// <summary>
        /// Headless convenience overload: no buttons, no operator interaction.
        /// Polls for the timeout, then returns <see cref="PreflightOutcome.Hardware"/>
        /// if found or <see cref="PreflightOutcome.Simulator"/> otherwise. Handy
        /// for tests, servers, or a minimal splash that only shows status text.
        /// </summary>
        public static PreflightOutcome RunHeadless(
            VoxonHardwareSpec spec,
            double timeoutSeconds        = 8.0,
            double pollSeconds           = 1.0,
            Action<string>? onStatus     = null,
            Action<string>? log          = null)
            => Run(spec, new CallbackUi(onStatus, null, () => PreflightChoice.None),
                   timeoutSeconds, pollSeconds, log);

        /// <summary>
        /// Build an <see cref="IPreflightUi"/> from three callbacks, so a host
        /// can wire its splash without declaring a class. Any callback may be null.
        /// </summary>
        public static IPreflightUi FromCallbacks(
            Action<string, bool>? showStatus,
            Action<bool>?         showButtons,
            Func<int>?            pollChoice)
            => new CallbackUi(
                   showStatus == null ? (Action<string>?)null : msg => showStatus(msg, false),
                   showButtons, pollChoice, showStatus);

        // Adapter backing both FromCallbacks and RunHeadless.
        private sealed class CallbackUi : IPreflightUi
        {
            private readonly Action<string>?        _status;
            private readonly Action<string, bool>?  _statusKind;
            private readonly Action<bool>?          _buttons;
            private readonly Func<int>?             _choice;

            public CallbackUi(Action<string>? status, Action<bool>? buttons,
                              Func<int>? choice, Action<string, bool>? statusKind = null)
            {
                _status = status; _buttons = buttons; _choice = choice; _statusKind = statusKind;
            }

            public void ShowStatus(string message, bool warning = false)
            {
                if (_statusKind != null) _statusKind(message, warning);
                else                     _status?.Invoke(message);
            }
            public void ShowButtons(bool visible) => _buttons?.Invoke(visible);
            public int  PollChoice()              => _choice?.Invoke() ?? PreflightChoice.None;
        }
    }
}
