// ═══════════════════════════════════════════════════════════════════════════
//  VoxonHardwareCheck.cs — portable pre-flight check for Voxon USB devices
//
//  Drop-in copy from VoxonPlyPlayback (see D:\git\playbackPLY\VoxonBootInit).
//  Confirms the expected USB hardware is visible in Windows Plug-and-Play
//  BEFORE the app touches LedHostInit / LedWinInit, so the SDK never has to
//  deal with a half-connected or absent device.  Self-contained: no dependency
//  on app settings, logging, or UI — the caller drives logging via onPoll and
//  reacts to the returned HardwareCheckResult.
//
//  Identification:
//    - NameMatches: case-insensitive substrings tested against each device's
//      friendly name (Win32_PnPEntity.Name).
//    - HardwareIdMatches: case-insensitive substrings tested against each
//      device's hardware ID (Win32_PnPEntity.DeviceID), suitable for the
//      "USB\VID_xxxx&PID_yyyy" form that uniquely identifies USB chipsets.
//    A device passes if EITHER list has at least one matching entry.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading;

namespace Voxon
{
    /// <summary>One physical USB board that must be present for a Voxon unit.
    /// A device satisfies the board if its friendly name OR hardware ID contains
    /// any of the board's match substrings (case-insensitive).</summary>
    public sealed record VoxonBoard(
        string DisplayName,
        IReadOnlyList<string> NameMatches,
        IReadOnlyList<string> HardwareIdMatches);

    /// <summary>Identification for one Voxon hardware model.
    ///
    /// No longer carries a motor RPM. It used to hold a per-model DefaultMotorRpm --
    /// 600 for the VX2XL, 900 for the VX2 -- which was a guess dressed up as a fact:
    /// USB detection cannot tell the two models apart (they expose the same two
    /// boards), so the value picked depended on which spec the code happened to name.
    /// It also contradicted itself, with a comment at the Motor On button saying the
    /// VX2XL's default was 900 while the table said 600. One constant now answers it
    /// -- see VoxonHardwareCheck.StartupRpm.</summary>
    public sealed record VoxonHardwareSpec(
        string ModelName,
        int    RequiredDeviceCount,
        IReadOnlyList<VoxonBoard> Boards);

    /// <summary>Outcome of a check, including diagnostics for the caller.</summary>
    public sealed class HardwareCheckResult
    {
        /// <summary>True only when EVERY board was found.</summary>
        public bool Ok        { get; init; }
        /// <summary>How many of the required boards were found.</summary>
        public int  Found     { get; init; }
        public int  Required  { get; init; }
        /// <summary>Display names of the boards that WERE found.</summary>
        public IReadOnlyList<string> PresentBoards { get; init; } = Array.Empty<string>();
        /// <summary>Display names of the boards that are MISSING (empty when Ok).</summary>
        public IReadOnlyList<string> MissingBoards { get; init; } = Array.Empty<string>();
        /// <summary>Matching devices, formatted as "Name [DeviceID]".</summary>
        public IReadOnlyList<string> Matched    { get; init; } = Array.Empty<string>();
        /// <summary>Everything WMI returned — useful for log-then-tune-identifiers.</summary>
        public IReadOnlyList<string> AllDevices { get; init; } = Array.Empty<string>();
        /// <summary>Non-null if the WMI query itself failed (rare).</summary>
        public string? Error { get; init; }
    }

    public static class VoxonHardwareCheck
    {
        /// <summary>The RPM the platter is started at, and the RPM the Motor On button
        /// commands. One number, deliberately: it is the speed the operator wants on this
        /// bench, and it was previously derived from which hardware spec the code named,
        /// which detection cannot actually establish.</summary>
        public const int StartupRpm = 900;

        // Confirmed Voxon device identifiers. Both VX2 and VX2XL expose the SAME
        // two USB boards — detection alone cannot tell the models apart, which is
        // exactly why the caller asks the operator when no hardware is present.
        // The two specs are otherwise identical; the motor speed is StartupRpm above.
        public static readonly VoxonBoard FtdiBridge = new(
            DisplayName:       "FTDI USB 3.0 data bridge (FT600)",
            NameMatches:       new[] { "FTDI SuperSpeed-FIFO Bridge", "FTDI FT600" },
            HardwareIdMatches: new[] { "VID_0403&PID_601E" });

        public static readonly VoxonBoard VoxonController = new(
            DisplayName:       "Voxon Photonics controller board",
            NameMatches:       Array.Empty<string>(),
            HardwareIdMatches: new[] { "VID_BE3D&PID_FB32" });

        private static readonly VoxonBoard[] Boards = { FtdiBridge, VoxonController };

        public static readonly VoxonHardwareSpec VX2XL = new(
            ModelName:           "VX2XL",
            RequiredDeviceCount: Boards.Length,
            Boards:              Boards);

        public static readonly VoxonHardwareSpec VX2 = new(
            ModelName:           "VX2",
            RequiredDeviceCount: Boards.Length,
            Boards:              Boards);

        /// <summary>Single-shot enumeration. WMI typically takes ~0.5–1.5 s on first
        /// call. Each board is evaluated independently so the caller can name which
        /// board is missing on a partial (one-cable) connection.</summary>
        public static HardwareCheckResult Check(VoxonHardwareSpec spec)
        {
            var all     = new List<string>();
            var devices = new List<(string Name, string Id)>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity");
                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    string name = (obj["Name"]?.ToString()    ?? "").Trim();
                    string id   = (obj["DeviceID"]?.ToString() ?? "").Trim();
                    if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(id)) continue;

                    all.Add($"{name} [{id}]");
                    devices.Add((name, id));
                }
            }
            catch (Exception ex)
            {
                return new HardwareCheckResult
                {
                    Ok       = false,
                    Found    = 0,
                    Required = spec.RequiredDeviceCount,
                    Error    = ex.Message,
                    AllDevices = all,
                };
            }

            var present = new List<string>();
            var missing = new List<string>();
            var matched = new List<string>();
            foreach (var board in spec.Boards)
            {
                string? hit = null;
                foreach (var (name, id) in devices)
                    if (MatchesBoard(name, id, board)) { hit = $"{name} [{id}]"; break; }

                if (hit != null) { present.Add(board.DisplayName); matched.Add(hit); }
                else             { missing.Add(board.DisplayName); }
            }

            return new HardwareCheckResult
            {
                Ok            = missing.Count == 0,
                Found         = present.Count,
                Required      = spec.RequiredDeviceCount,
                PresentBoards = present,
                MissingBoards = missing,
                Matched       = matched,
                AllDevices    = all,
            };
        }

        /// <summary>
        /// Poll <see cref="Check"/> every <paramref name="pollInterval"/> until
        /// the check passes or <paramref name="timeout"/> elapses. The
        /// <paramref name="onPoll"/> callback fires after every iteration with
        /// (elapsed-wall-time, current-result); useful for updating a splash
        /// or status line as devices appear during hot-plug.
        /// </summary>
        public static HardwareCheckResult WaitForHardware(
            VoxonHardwareSpec spec,
            TimeSpan timeout,
            TimeSpan pollInterval,
            Action<TimeSpan, HardwareCheckResult>? onPoll = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            HardwareCheckResult result;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = Check(spec);
                onPoll?.Invoke(sw.Elapsed, result);

                if (result.Ok)             return result;
                if (sw.Elapsed >= timeout) return result;

                try { Thread.Sleep(pollInterval); }
                catch (ThreadInterruptedException) { return result; }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static bool MatchesBoard(string name, string id, VoxonBoard board)
        {
            foreach (var s in board.NameMatches)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    id  .Contains(s, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (var s in board.HardwareIdMatches)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (id.Contains(s, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
