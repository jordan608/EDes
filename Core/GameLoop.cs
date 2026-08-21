// ═══════════════════════════════════════════════════════════════════════════
//  GameLoop.cs — Voxon SDK lifecycle + main rendering loop
//
//  CRITICAL — read before modifying:
//
//  1. STACK CORRUPTION DEFENCE
//     The Voxon SDK DLLs zero managed stack frames during LedWinInit and
//     LedHostInit. Any local variable that holds a managed reference on the
//     stack (including method parameters) may be silently set to null after
//     these calls return.
//
//     Defence pattern used here:
//       a. Store `settings` in a STATIC field (s_settings) BEFORE the first
//          SDK call. Static fields are in the GC static area (managed heap),
//          not on the thread stack — the SDK cannot reach them.
//       b. After every SDK init call, call RescueSettings() which returns
//          s_settings if the local variable was zeroed.
//       c. RunLoop / RunLoopCore are [NoOptimization | NoInlining] to prevent
//          the JIT from copy-propagating `this._settings` back through the
//          frames and losing the GC root.
//
//  2. THREADING
//     All SDK calls (LedHost, LedWin, DrawVox, etc.) MUST happen on the
//     background STA thread that GameLoop runs on. Never call SDK functions
//     from the Avalonia UI thread.
//
//  3. OBJECT CREATION ORDER
//     Construct the engine services and call game.Init() BEFORE LedWinInit /
//     LedHostInit. The SDK init calls modify Win32/process state in a way that
//     can prevent class constructors (incl. the game's asset load) from running
//     correctly afterward.
//
//  4. PREVIEW BUFFER
//     We allocate our own pinned byte[] and call Rend2D directly, exactly
//     like VLEDStudio. The LedWin window is created offscreen (−32000) so
//     it never receives OS focus or keyboard messages. All keyboard input
//     uses NativeInput (GetAsyncKeyState), which bypasses the message queue.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Voxon;

namespace EDes
{
    public class GameLoop
    {
        private readonly GameSettings _settings;
        private readonly IVoxonGame   _game;

        // ── Static rescue fields ──────────────────────────────────────────────
        // The SDK init zeroes managed references on the stack (locals AND `this`),
        // so the loop must NOT read instance fields after init. Everything the loop
        // needs is held in static fields (GC static area — never zeroed), seeded
        // before the SDK calls and copied back into locals afterward. See header.
        private static GameSettings?   s_settings;
        private static IVoxonGame?     s_game;
        private static LightingSystem? s_lighting;
        private static AudioManager?   s_audio;
        private static ParticleManager? s_particles;
        private static SpriteBurstRenderer? s_sprites;

        public GameLoop(GameSettings settings, IVoxonGame game)
        {
            _settings = settings;
            _game     = game;
        }

        /// <summary>
        /// Run the game loop on the CALLING thread.
        /// Call this from a dedicated background STA thread (see App.axaml.cs).
        /// </summary>
        public void RunOnCurrentThread() => RunLoop();

        // ── NoOptimization / NoInlining prevent JIT from erasing the GC root ──
        [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
        private void RunLoop()
        {
            if (_settings == null)
            {
                Program.SafeLog("[GameLoop] _settings is null — aborting");
                return;
            }
            RunLoopCore(_settings);
        }

        [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
        private void RunLoopCore(GameSettings settings)
        {
            App.Log("[GameLoop] Thread started");

            // ── Step 1: save to statics BEFORE any SDK call ───────────────────
            // Reading the instance field _game here is safe (no SDK call yet).
            s_settings = settings;
            IVoxonGame      game     = _game;
            s_game = game;
            // Declared here so the finally block can see them; assigned in Step 3.
            LightingSystem      lighting  = null!;
            AudioManager        audio     = null!;
            ParticleManager     particles = null!;
            SpriteBurstRenderer sprites   = null!;

            // ── Step 2: grace period — let Avalonia compositor fully start ────
            // vxl_init performs Win32 calls that can interfere with Avalonia's
            // still-starting compositor. 1 second is enough for XAML, layout,
            // first paint, and the Win32 message pump to all be stable.
            App.Log("[GameLoop] Waiting 1s for Avalonia to settle...");
            Thread.Sleep(1000);

            // ── Step 2b: pre-flight hardware check (BEFORE any SDK call) ──────
            // VoxonHardwareCheck/VoxonPreflight are self-contained (WMI only, no
            // SDK dependency) so this is safe to run before Step 3. The splash
            // overlay is bridged via GameSettings.Splash* fields — see
            // MainWindow.axaml.cs's OnStatusTick and the button Click handlers.
            // Detection cannot distinguish VX2 from VX2XL (both expose the same
            // two USB boards) — swap VX2XL for VX2 below if you need VX2 defaults
            // (only Default
            // Rpm differs; both are otherwise identical here).
            var spec = VoxonHardwareCheck.VX2XL;
            var preflightUi = VoxonPreflight.FromCallbacks(
                showStatus:  (msg, warn) => { settings.SplashStatus = msg; settings.SplashWarning = warn; },
                showButtons: visible     => settings.SplashShowButtons = visible,
                pollChoice:  () =>
                {
                    int c = settings.SplashChoice;
                    settings.SplashChoice = PreflightChoice.None;
                    return c;
                });
            var preflightOutcome = VoxonPreflight.Run(spec, preflightUi, timeoutSeconds: 8.0, log: App.Log);
            settings = RescueSettings(settings)!;
            if (preflightOutcome == PreflightOutcome.Quit)
            {
                App.Log("[GameLoop] Operator chose Quit at pre-flight — shutting down.");
                settings.RequestShutdown?.Invoke();
                return;
            }
            bool hardwareConfirmed = preflightOutcome == PreflightOutcome.Hardware;

            vxl_state_t vs = new vxl_state_t();
            const string ProgramName = "EDes";

            // ── Step 3: create engine services + init the game BEFORE SDK init ─
            // See header note 3 — object construction (incl. the game's asset load
            // in Init) must happen before the SDK init calls.
            App.Log("[GameLoop] Creating engine services + initialising game...");
            try
            {
                lighting  = new LightingSystem();
                audio     = new AudioManager();
                particles = new ParticleManager();
                sprites   = new SpriteBurstRenderer();
                s_lighting = lighting; s_audio = audio; s_particles = particles; s_sprites = sprites;
                game.Init(new GameContext
                {
                    Settings  = settings,
                    Lighting  = lighting,
                    Audio     = audio,
                    Particles = particles,
                    Sprites   = sprites,
                    Players   = App.Players,
                });
            }
            catch (Exception ex)
            {
                App.Log($"[GameLoop] Game init FAILED: {ex}");
                Program.SafeLog($"[GameLoop] Game init FAILED:\n{ex}");
                return;
            }
            settings = RescueSettings(settings)!;

            // ── Step 4: initialize LedHost (3D volumetric rendering) ─────────
            App.Log("[GameLoop] Initializing LedHostCS...");
            LedHostCS ledHost = new LedHostCS();
            try
            {
                string ledHostPath = VLED_CS_Utils.GetVLEDFilePath("LedHost.dll");
                ledHost.LoadLedHostCS(ledHostPath);
                App.Log("[GameLoop] LedHost loaded — calling LoadIni...");
                int iniResult = ledHost.LoadIni(ref vs);
                App.Log($"[GameLoop] LoadIni result={iniResult}  vs.rpm={vs.rpm}");
                ledHost.Init(ref vs);
                App.Log("[GameLoop] LedHost initialized");

                // Start the platter motor at the RPM from LedHost.ini — but only if
                // the pre-flight check actually confirmed hardware. LoadIni writes
                // vs.rpm but the motor stays stopped until SetRPM either way; in
                // Simulator mode we deliberately never call it.
                int targetRpm = ledHost.GetIntendedRPM();
                if (hardwareConfirmed && targetRpm > 0)
                {
                    App.Log($"[GameLoop] Starting motor at {targetRpm} RPM");
                    ledHost.SetRPM(ref vs, targetRpm);
                }
                else
                {
                    App.Log("[GameLoop] WARNING: intended RPM = 0 — check LedHost.ini rpm= setting");
                }
            }
            catch (Exception ex)
            {
                App.Log($"[GameLoop] LedHost init FAILED: {ex.Message}");
                return;
            }
            settings = RescueSettings(settings)!;

            // ── Step 5: initialize LedWin (timing pump + input) ──────────────
            // Window is created offscreen (−32000) — 1×1 pixel, never visible.
            // This is the VLEDStudio pattern: LedWin drives Breath() timing only;
            // the 2D preview comes entirely from our own Rend2D → pinned buffer.
            App.Log("[GameLoop] Initializing LedWinCS...");
            LedWinCS ledWin = new LedWinCS(VLED_CS_Utils.GetVLEDFilePath("LedWin.dll"));
            try
            {
                ledWin.LedWinInit(ProgramName, 1, 1, -32000, -32000);
                App.Log("[GameLoop] LedWin initialized (offscreen 1×1)");
            }
            catch (Exception ex)
            {
                App.Log($"[GameLoop] LedWin init failed: {ex.Message}");
                // Non-fatal in simulator — continue without LedWin timing
            }
            settings = RescueSettings(settings)!;
            App.Log($"[GameLoop] Settings after LedWin: {(settings == null ? "NULL — FATAL" : "OK")}");
            if (settings == null)
            {
                App.Log("[GameLoop] Cannot recover settings — aborting");
                return;
            }

            // Rescue the loop's references — the SDK init may have zeroed these
            // locals (same hazard as settings). Restore from the static area.
            game     ??= s_game!;
            lighting ??= s_lighting!;
            audio    ??= s_audio!;
            sprites  ??= s_sprites!;

            // Seed camera defaults from the SDK
            settings.EmuHAng = ledWin.GetEmuHAng();
            settings.EmuVAng = ledWin.GetEmuVAng();
            settings.EmuDist = ledWin.GetEmuDist();

            // ── Step 6: allocate pinned preview render buffer ─────────────────
            // We render at exactly the size the Avalonia PreviewImage control
            // reports via PreviewRequestW/H.  When the window is resized the
            // UI updates those fields and we reallocate next frame.
            int      previewW    = Math.Max(64, settings.PreviewRequestW);
            int      previewH    = Math.Max(64, settings.PreviewRequestH);
            byte[]   previewBuf  = new byte[previewW * previewH * 4];
            GCHandle previewPin  = GCHandle.Alloc(previewBuf, GCHandleType.Pinned);
            tiletype previewTile = MakePreviewTile(previewPin, previewW, previewH);

            // ── Step 6b: spin the platter up ──────────────────────────────────
            // Nothing used to request the motor at startup — it only ever moved when
            // someone pressed Motor On, so the display came up with a stationary
            // platter and no image. Requested rather than called directly so it goes
            // through the one SetRPM path in the loop below, which is already the
            // acknowledged, RPM-reporting one.
            //
            // Only with real hardware confirmed: in the simulator there is no platter,
            // and reporting a live RPM there would be a readout that means nothing.
            if (hardwareConfirmed)
            {
                settings.MotorRpmRequest = spec.DefaultMotorRpm;
                App.Log($"[GameLoop] Auto-starting motor at {spec.DefaultMotorRpm} RPM");
            }

            // ── Step 7: main game loop ────────────────────────────────────────
            try
            {
                settings.GameLoopRunning = true;
                App.Log("[GameLoop] Entering main loop");

                // Frame-rate cap timer — measures wall-clock per iteration so we
                // can sleep off any time spent running ahead of the 30 VPS budget.
                var frameTimer = Stopwatch.StartNew();
                // Work timer — measures the actual per-frame work (excludes the cap
                // sleep) and is surfaced as "latency" in the status bar.
                var workTimer  = new Stopwatch();

                // Unified input (keyboard + controller). Polled once per frame.
                var inputMgr = new InputManager();

                while (ledWin.Breath() == 0)
                {
                    // ── Optional 30-VPS cap ────────────────────────────────────
                    // The Voxon display tops out at ~30 volumes/sec. When enabled,
                    // sleep off the remainder of the 33.3 ms frame budget so we
                    // stop burning CPU on frames the hardware can never show.
                    if (settings.CapVps30)
                    {
                        const double frameMs = 1000.0 / 30.0;
                        double elapsedMs = frameTimer.Elapsed.TotalMilliseconds;
                        if (elapsedMs < frameMs)
                            Thread.Sleep((int)(frameMs - elapsedMs));
                    }
                    frameTimer.Restart();
                    workTimer.Restart();

                    float dt = ledWin.GetDeltaTime();

                    // ── Unified input snapshot — polls keyboard + controller ───
                    // (this also advances NativeInput edge state for the engine
                    // controls below: Escape, camera [ ] keys, etc.)
                    InputState input = inputMgr.Poll(ledWin);

                    // ── Motor RPM request from UI ──────────────────────────────
                    // UI writes MotorRpmRequest ≥ 0; we call SetRPM and clear it.
                    int motorReq = settings.MotorRpmRequest;
                    if (motorReq >= 0)
                    {
                        ledHost.SetRPM(ref vs, motorReq);
                        settings.LiveMotorRpm    = motorReq;
                        settings.MotorRpmRequest = -1;   // acknowledge
                    }

                    // ── Hard exit on Escape ────────────────────────────────────
                    if (NativeInput.OnDown(VX_KEYS.KB_Escape) == 1)
                        ledWin.QuitLoop();

                    // 'O' → toggle the frame profiler (CSV + 5s log summaries).
                    // See FrameProfiler.cs for what it captures and where it writes.
                    if (NativeInput.OnDown(VX_KEYS.KB_O) == 1)
                        FrameProfiler.Toggle();

                    // ── Apply per-frame vxl_state_t settings ───────────────────
                    ApplyVsSettings(ref vs, settings);

                    // ── Simulator camera: [ / ] rotate left / right ────────────
                    // 1.8 rad/s → full 360° in ~3.5 s while key is held
                    const float RotSpeed = 1.8f;
                    if (NativeInput.IsDown(VX_KEYS.KB_Square_Bracket_Open)  == 1)
                        settings.EmuHAng -= RotSpeed * dt;
                    if (NativeInput.IsDown(VX_KEYS.KB_Square_Bracket_Close) == 1)
                        settings.EmuHAng += RotSpeed * dt;
                    // Keep in [0, 2π] — avoids float drift over long sessions
                    settings.EmuHAng = (settings.EmuHAng % (2f * MathF.PI) + 2f * MathF.PI) % (2f * MathF.PI);

                    // ── Lighting: decay transients before the game can add more ─
                    using (FrameProfiler.Scope(FrameProfiler.Phase.LightingUpdate))
                        lighting.Update(dt);
                    sprites.Update(dt);

                    // ── Update game logic ──────────────────────────────────────
                    using (FrameProfiler.Scope(FrameProfiler.Phase.GameUpdate))
                        game.Update(in input, dt);

                    // ── Begin 3D rendering ─────────────────────────────────────
                    ledHost.FrameStart(ref vs);
                    DisplayVolume.Init(ref vs);   // reads hardware bounds once

                    // ── Lighting: apply UI config + collect active lights ───────
                    // Done by the engine so every game gets configured lighting.
                    using (FrameProfiler.Scope(FrameProfiler.Phase.LightingApply))
                    {
                        lighting.ApplyConfig(settings.Lighting);
                        lighting.BeginFrame();
                    }

                    // ── Draw the scene ─────────────────────────────────────────
                    using (FrameProfiler.Scope(FrameProfiler.Phase.GameDraw))
                        game.Draw(ledHost, ref vs);
                    sprites.Draw(ledHost, ref vs);

                    // ── End 3D rendering ───────────────────────────────────────
                    using (FrameProfiler.Scope(FrameProfiler.Phase.Submit))
                        ledHost.FrameEnd(ref vs);

                    // Push live VPS back to settings so the UI status bar can show it
                    settings.LiveVps = (float)ledWin.GetVPS();

                    // ── Resize preview buffer if the UI control changed size ────
                    int reqW = Math.Max(64, settings.PreviewRequestW);
                    int reqH = Math.Max(64, settings.PreviewRequestH);
                    if (reqW != previewW || reqH != previewH)
                    {
                        previewPin.Free();
                        previewW    = reqW;
                        previewH    = reqH;
                        previewBuf  = new byte[previewW * previewH * 4];
                        previewPin  = GCHandle.Alloc(previewBuf, GCHandleType.Pinned);
                        previewTile = MakePreviewTile(previewPin, previewW, previewH);
                    }

                    // ── Render 2D preview and deliver to Avalonia ──────────────
                    // Clear → Rend2D fills the buffer → callback updates the UI image.
                    using (FrameProfiler.Scope(FrameProfiler.Phase.Preview))
                    {
                        Array.Clear(previewBuf, 0, previewBuf.Length);
                        ledHost.Rend2D(ref vs, ref previewTile,
                            settings.EmuHAng, settings.EmuVAng, settings.EmuDist);
                        settings.OnPreviewFrame?.Invoke(previewBuf, previewW, previewH);
                    }

                    // ── Diagnostics for the status bar ─────────────────────────
                    settings.LiveFrameMs       = (float)workTimer.Elapsed.TotalMilliseconds;
                    // Real Voxon units have smaller bounds than the 4.0 sim default.
                    settings.HardwareConnected = DisplayVolume.HalfXY < 2.5f;

                    // Close out the frame profiler (no-op unless toggled on with 'O').
                    FrameProfiler.EndFrame(dt, settings.LiveVoxelCount, settings.LiveVps);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[GameLoop] Loop exception: {ex}");
            }
            finally
            {
                if (previewPin.IsAllocated) previewPin.Free();
                settings = RescueSettings(settings) ?? s_settings;
                if (settings != null) settings.GameLoopRunning = false;
                App.Log("[GameLoop] Cleaning up SDK...");
                try { (game ?? s_game)?.Dispose();   } catch { }
                try { (audio ?? s_audio)?.Dispose(); } catch { }
                try { ledHost.UnLoadLedHostCS(ref vs); ledHost.Dispose(); } catch { }
                try { ledWin.UninitWindow(); ledWin.Dispose();              } catch { }
                GC.KeepAlive(this);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static tiletype MakePreviewTile(GCHandle pin, int w, int h) =>
            new tiletype
            {
                first_pixel = pin.AddrOfPinnedObject(),
                pitch  = (nint)(w * 4),
                width  = (nint)w,
                height = (nint)h,
            };

        /// <summary>
        /// If the SDK zeroed the local variable, restore from the static copy.
        /// Call after every LedHostInit / LedWinInit block.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
        private static GameSettings? RescueSettings(GameSettings? local)
        {
            if (local != null) return local;
            App.Log("[GameLoop] RESCUE: settings was zeroed by SDK — restoring from static field");
            return s_settings;
        }

        /// <summary>
        /// Copy live GameSettings values into vxl_state_t each frame.
        /// Only the fields listed here are applied; expand as needed.
        /// </summary>
        private static void ApplyVsSettings(ref vxl_state_t vs, GameSettings s)
        {
            vs.gammapow = s.Gamma;
            vs.dithmode = s.DitherMode;
            vs.dithresh = s.DitherThreshold;
            vs.drawbord = s.ShowDebugBorder ? 1 : 0;
            vs.drawbilin = 0;
        }
    }
}
