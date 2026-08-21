// ═══════════════════════════════════════════════════════════════════════════
//  EDesApp.cs — the application (IVoxonGame)
//
//  Three modes over one shared scene infrastructure:
//
//    EDUCATION  Four built-in circuits solved live (Ohm's law + series/parallel),
//               drawn as a 3D schematic with heat-coloured resistors, animated
//               current flow, per-component readouts, and the scope strip below.
//    SCOPE      The oscilloscope full-size on the y = PlaneY plane: up to four
//               channels from USB (or a synthetic signal), software trigger,
//               and a bench-style measurement row.
//    PCB        An imported board (Gerber + Excellon + mechanical meshes) with
//               its layers spread along Z, drills bored through the stack, and
//               a fabrication/DRC-lite readout.
//
//  What is shared, and why it lives here rather than in each mode:
//    • ONE voxel batch per frame  → a single DrawVox_Batch call, one hard
//      max-voxel ceiling, and one place that clips to the real display bounds.
//    • Bounds come from the SDK every frame (GetAspectRatioX / vs.boundr and
//      vs.boundz), never hardcoded, so the same build fits a VX2 and a VX2-XL.
//    • ONE camera. Every point goes through SceneCamera.Transform, driven by the
//      SpaceNavigator, the keyboard, the controller, and the preview window's
//      left-drag — so all four feel like the same control.
//    • Draw order IS priority order: mode content, then HUD text, then the
//      backdrop. When the budget runs out the decoration is what disappears.
//
//  Axes: -Z is up, X is the layout's left/right, Y is depth. The HUD and scope
//  live on the constant-Y plane PlaneY (default 0.1) and are NOT camera-
//  transformed, so readouts stay legible while the scene is flown around.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Controls;
using EDes.Pcb;
using EDes.Sim;
using EDes.UI;
using Voxon;

namespace EDes
{
    public sealed class EDesApp : IVoxonGame
    {
        // ── Persisted app state + engine settings ─────────────────────────────
        private readonly EDesSettings _s;
        private GameSettings _engine = null!;

        // ── Scene infrastructure ──────────────────────────────────────────────
        private readonly VoxelBatch      _batch   = new();
        private readonly SceneCamera     _cam     = new();
        private          Hud             _hud     = null!;
        private readonly CircuitScene    _scene   = new();
        private readonly CircuitRenderer _circuit = new();
        private readonly ScopeSource     _scope   = new();
        private readonly ScopeRenderer   _scopeRenderer = new();
        private readonly PcbBoard        _board   = new();
        private readonly PcbRenderer     _pcb     = new();

        // ── Live frame state ──────────────────────────────────────────────────
        private float _radius = 4f, _zHalf = 2f, _spacing = 0.03f;

        /// <summary>Height of one 5x7 glyph cell as a fraction of the text size — the
        /// constant VoxelFont.EmitGlyphs uses. Named here so the legibility floor is
        /// derived from the renderer's real metric rather than a guess about it.</summary>
        private const float GLYPH_CELL_FRACTION = 0.18f;

        /// <summary>True when the requested text size was raised to stay legible, so the
        /// panel can say so instead of appearing to ignore the slider.</summary>
        private bool _textFloored;
        private float _textSize = 0.2f, _step = 0.31f;   // display-scaled text metrics
        private FrameLayout _layout;                     // this frame's vertical plan

        // ── SpaceNavigator ────────────────────────────────────────────────────
        // Two independent read paths, because they do not always both work:
        //   LedWin  GetNavCount / GetNavAxisValue  (polled in InputManager)
        //   LedHost vxl_nav_read                   (polled here, in Draw)
        // The engine never called vxl_nav_read, which is the documented way to read
        // the puck — so on many machines LedWin's copy simply never updated. Whichever
        // path reports motion drives the camera; both are shown in the diagnostics.
        private NavState    _nav;              // last LedWin read
        private vxl_nav_t   _navHost;          // last LedHost vxl_nav_read
        private int         _navHostRc = -999; // its return code (0 = ok on this SDK)
        private bool        _navHostUsable;
        private string      _navSource = "none";
        private NavState    _navLive;          // conditioned (-1..1, dead-zoned) signal
        private int         _prevNavButtons;   // for both-buttons edge detection
        private float       _navPeakTrans;     // largest raw translation count seen
        private float       _navPeakRot;       // largest raw rotation count seen
        private float       _lastDt = 1f / 30f;
        private float _anim;                       // flow-animation clock
        private int   _lastVoxels, _lastDropped;

        /// <summary>Import summary for the settings panel (assigned atomically).</summary>
        public string BoardSummary { get; private set; } = "No board loaded.";
        public string ScopeStatus  => _scope.Status;

        public GameManifest Manifest { get; } = new GameManifest
        {
            Title   = "EDes — Electronics Design Explorer",
            Version = "0.2",
            Accent  = 0xFF33FF99,
        };

        public object? Settings => _s;

        public EDesApp(GameSettings engineSettings)
        {
            _engine = engineSettings;
            _s      = EDesSettings.Load();
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Init(GameContext ctx)
        {
            _engine = ctx.Settings;
            _hud    = new Hud(_batch);
            Sync();

            // The last board is reloaded here, on the game thread, before SDK init.
            if (_s.PcbImportRequested) ImportBoard();
            App.Log($"[EDesApp] Init complete — mode {(EDesMode)_s.Mode}");
        }

        public void Dispose() => _scope.Dispose();

        // ── Update ────────────────────────────────────────────────────────────

        public void Update(in InputState input, float dt)
        {
            _nav    = input.Nav;
            _lastDt = dt;

            HandleKeys(dt);
            DriveCamera(input, dt);
            Sync();
            RebuildLegend(dt);
            RebuildPickList(dt);
            TrackViewMotion();

            if (_s.PcbImportRequested) ImportBoard();

            if (!_s.FlowPaused) _anim += dt * MathF.Max(0f, _s.FlowSpeed);

            // Synthetic amplitude tracks the circuit so the scope shows something
            // related to what the user is looking at rather than an arbitrary sine.
            float amplitude = (float)Math.Max(0.1, _scene.SourceVolts * 0.5);
            _scope.Poll(dt, amplitude, MathF.Max(0.1f, _s.SynthFreqHz));
        }

        /// <summary>Push UI/persisted settings into the live objects. One direction
        /// only: keys and the UI both write settings, settings drive the scene.</summary>
        private void Sync()
        {
            _scene.SetPreset(_s.PresetIndex);
            _scene.SetSourceVolts(_s.SourceVolts);
            _scene.SetResistor(0, _s.R1);
            _scene.SetResistor(1, _s.R2);
            _scene.SetResistor(2, _s.R3);

            _scope.Configure((ScopeInput)Math.Clamp(_s.ScopeMode, 0, 3),
                             _s.ScopePort, _s.ScopeBaud,
                             _s.ScopeHost, _s.ScopeTcpPort, _s.ScopeVisaResource, _s.ScopePollHz);
            _scope.Paused = _s.ScopeFrozen;

            VoxelFont.Thickness = Math.Clamp(_s.TextWeight, 0.5f, 3f);
            _hud.Font = (HudFont)Math.Clamp(_s.FontIndex, 0, 2);
        }

        private void ImportBoard()
        {
            _s.PcbImportRequested = false;
            string path = _s.PcbPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                _board.Clear();
                BoardSummary = "No path set.";
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = PcbImporter.Import(path, _board, Math.Max(1000, _s.MeshPointBudget),
                                         f => _s.PcbImportStatus = f,
                                         new PcbImporter.StepOptions
                                         {
                                             Tessellate  = _s.PcbTessellate,
                                             ToleranceMm = _s.PcbTessellateTol,
                                             Command     = _s.PcbTessellator,
                                         });

            // Before anything draws: an import resets every layer to its defaults, so the
            // user's own visibility and colour choices have to be laid back over the top.
            ApplyLayerPrefs();
            sw.Stop();

            var sb = new StringBuilder();
            sb.Append(ok ? _board.SourceName : "Import failed");
            sb.Append("  (").Append(sw.ElapsedMilliseconds).Append(" ms)\n");
            foreach (var n in _board.Notes) sb.Append("- ").Append(n).Append('\n');
            BoardSummary = sb.ToString();
            App.Log($"[EDesApp] PCB import '{path}': {(ok ? "ok" : "failed")} in {sw.ElapsedMilliseconds} ms");
        }

        // ── Input ─────────────────────────────────────────────────────────────

        private void HandleKeys(float dt)
        {
            if (Down(VX_KEYS.KB_Tab))
                _s.Mode = (_s.Mode + 1) % 3;

            if (Down(VX_KEYS.KB_V)) _s.ShowNavDiag  = !_s.ShowNavDiag;
            if (Down(VX_KEYS.KB_L)) _s.ShowLabels   = !_s.ShowLabels;
            if (Down(VX_KEYS.KB_G)) _s.ShowBackdrop = !_s.ShowBackdrop;
            if (Down(VX_KEYS.KB_R)) _cam.Reset();

            switch ((EDesMode)_s.Mode)
            {
                case EDesMode.Education: EducationKeys(); break;
                case EDesMode.Scope:     ScopeKeys();     break;
                case EDesMode.Pcb:       PcbKeys();       break;
            }
        }

        private void EducationKeys()
        {
            if (Down(VX_KEYS.KB_1)) _s.PresetIndex = 0;
            if (Down(VX_KEYS.KB_2)) _s.PresetIndex = 1;
            if (Down(VX_KEYS.KB_3)) _s.PresetIndex = 2;
            if (Down(VX_KEYS.KB_4)) _s.PresetIndex = 3;

            if (Down(VX_KEYS.KB_Arrow_Left))  _scene.SelectNext(-1);
            if (Down(VX_KEYS.KB_Arrow_Right)) _scene.SelectNext(+1);

            // Up/Down retune the selected resistor by 10% per press. The scene owns
            // the selection, so write the result back into the persisted slider value.
            int sel = _scene.Selected;
            if (Down(VX_KEYS.KB_Arrow_Up))   ScaleResistor(sel, 1.1);
            if (Down(VX_KEYS.KB_Arrow_Down)) ScaleResistor(sel, 1.0 / 1.1);

            if (Down(VX_KEYS.KB_Minus))  _s.SourceVolts = Math.Clamp(_s.SourceVolts - 1f, 1f, 24f);
            if (Down(VX_KEYS.KB_Equals)) _s.SourceVolts = Math.Clamp(_s.SourceVolts + 1f, 1f, 24f);
            if (Down(VX_KEYS.KB_P))      _s.FlowPaused = !_s.FlowPaused;
        }

        private void ScaleResistor(int index, double factor)
        {
            float v = index switch { 0 => _s.R1, 1 => _s.R2, _ => _s.R3 };
            v = (float)Math.Clamp(v * factor, 1, 10_000);
            switch (index)
            {
                case 0: _s.R1 = v; break;
                case 1: _s.R2 = v; break;
                default: _s.R3 = v; break;
            }
        }

        private void ScopeKeys()
        {
            if (Down(VX_KEYS.KB_1)) _s.ScopeChannelMask ^= 1 << 0;
            if (Down(VX_KEYS.KB_2)) _s.ScopeChannelMask ^= 1 << 1;
            if (Down(VX_KEYS.KB_3)) _s.ScopeChannelMask ^= 1 << 2;
            if (Down(VX_KEYS.KB_4)) _s.ScopeChannelMask ^= 1 << 3;

            if (Down(VX_KEYS.KB_Arrow_Up))   _s.ScopeVoltsPerDiv = Math.Clamp(_s.ScopeVoltsPerDiv / 1.25f, 0.01f, 50f);
            if (Down(VX_KEYS.KB_Arrow_Down)) _s.ScopeVoltsPerDiv = Math.Clamp(_s.ScopeVoltsPerDiv * 1.25f, 0.01f, 50f);
            if (Down(VX_KEYS.KB_Arrow_Left))  _s.ScopeTriggerLevel -= _s.ScopeVoltsPerDiv * 0.25f;
            if (Down(VX_KEYS.KB_Arrow_Right)) _s.ScopeTriggerLevel += _s.ScopeVoltsPerDiv * 0.25f;

            if (Down(VX_KEYS.KB_T)) _s.ScopeTriggerCh = _s.ScopeTriggerCh >= ScopeSource.MAX_CHANNELS - 1
                                                      ? -1 : _s.ScopeTriggerCh + 1;
            if (Down(VX_KEYS.KB_E)) _s.ScopeTriggerRising = !_s.ScopeTriggerRising;
            if (Down(VX_KEYS.KB_P)) _s.ScopeFrozen = !_s.ScopeFrozen;
        }

        private void PcbKeys()
        {
            float step = IsDown(VX_KEYS.KB_Shift_Left) ? 0.1f : 1.0f;   // mm per press
            if (Down(VX_KEYS.KB_Arrow_Left))  { _s.PcbCursorX -= step; _s.PcbCursor = true; }
            if (Down(VX_KEYS.KB_Arrow_Right)) { _s.PcbCursorX += step; _s.PcbCursor = true; }
            if (Down(VX_KEYS.KB_Arrow_Up))    { _s.PcbCursorY += step; _s.PcbCursor = true; }
            if (Down(VX_KEYS.KB_Arrow_Down))  { _s.PcbCursorY -= step; _s.PcbCursor = true; }

            if (Down(VX_KEYS.KB_C)) _s.PcbCursor  = !_s.PcbCursor;
            if (Down(VX_KEYS.KB_H)) _s.PcbHoles   = !_s.PcbHoles;
            if (Down(VX_KEYS.KB_P)) _s.PcbPads    = !_s.PcbPads;
            if (Down(VX_KEYS.KB_F)) _s.PcbFillRegions = !_s.PcbFillRegions;

            int layers = _board.Layers.Count;
            if (layers > 0)
            {
                if (Down(VX_KEYS.KB_N)) _s.PcbIsolate = _s.PcbIsolate <= -1 ? layers - 1 : _s.PcbIsolate - 1;
                if (Down(VX_KEYS.KB_M)) _s.PcbIsolate = _s.PcbIsolate >= layers - 1 ? -1 : _s.PcbIsolate + 1;
            }
        }

        /// <summary>Camera from every source at once: keyboard, controller right
        /// stick, and the SpaceNavigator.</summary>
        private void DriveCamera(in InputState input, float dt)
        {
            const float KeyRot = 1.2f, KeyZoom = 0.9f;

            // Pushed here as well as in the puck path: DriveCamera runs FIRST, so relying
            // on the puck path to have set the policy would leave one frame of keyboard
            // rotation escaping the locks every time one was toggled.
            ApplyCameraLocks();

            float yaw = 0, pitch = 0;
            if (IsDown(VX_KEYS.KB_A)) yaw   -= KeyRot * dt;
            if (IsDown(VX_KEYS.KB_D)) yaw   += KeyRot * dt;
            if (IsDown(VX_KEYS.KB_W)) pitch -= KeyRot * dt;
            if (IsDown(VX_KEYS.KB_S)) pitch += KeyRot * dt;

            // Controller right stick orbits (the left stick stays free for the game
            // layer); triggers zoom.
            yaw   += input.LookX * KeyRot * dt;
            pitch += input.LookY * KeyRot * dt;
            _cam.Orbit(yaw, pitch);

            if (IsDown(VX_KEYS.KB_Q)) _cam.RollBy(-KeyRot * dt);
            if (IsDown(VX_KEYS.KB_E) && (EDesMode)_s.Mode != EDesMode.Scope) _cam.RollBy(KeyRot * dt);

            if (IsDown(VX_KEYS.KB_Comma))     _cam.ZoomBy(1f - KeyZoom * dt);
            if (IsDown(VX_KEYS.KB_Full_Stop)) _cam.ZoomBy(1f + KeyZoom * dt);
            if (MathF.Abs(input.MoveZ) > 0.01f) _cam.ZoomBy(1f + input.MoveZ * KeyZoom * dt);

            // NOTE: the SpaceNavigator is applied in Draw (ApplyNavigator), because the
            // LedHost read needs the ledHost/vxl_state_t the engine only hands us there.
            // The preview window's left-drag drives the SIMULATOR camera directly
            // (GameSettings.EmuHAng/EmuVAng, read by Rend2D) — that is "walk around
            // the volume". This scene camera is the other half: it moves the content.
        }

        /// <summary>Read the puck through LedHost as well as LedWin, then drive the
        /// camera from whichever path actually has data.</summary>
        private void ApplyNavigator(LedHostCS ledHost)
        {
            // LedHost path: vxl_nav_read fills the struct directly from the driver.
            try
            {
                if (ledHost.NavRead != null)
                {
                    var nav = new vxl_nav_t();
                    _navHostRc = ledHost.NavRead(0, ref nav);
                    _navHost   = nav;
                    _navHostUsable = MathF.Abs(nav.dx) + MathF.Abs(nav.dy) + MathF.Abs(nav.dz) +
                                     MathF.Abs(nav.ax) + MathF.Abs(nav.ay) + MathF.Abs(nav.az)
                                     > 1e-4f || nav.but != 0;
                }
                else _navHostRc = -1;      // delegate not exported by this LedHost build
            }
            catch { _navHostRc = -2; }

            if (!_s.NavEnabled) { _navSource = "disabled"; return; }

            if (_navHostUsable)
            {
                _navSource = "LedHost vxl_nav_read";
                Drive(new NavState(true, 1,
                                   _navHost.dx, _navHost.dy, _navHost.dz,
                                   _navHost.ax, _navHost.ay, _navHost.az,
                                   0f, 0f, 0f, _navHost.but));
            }
            else if (_nav.Present)
            {
                _navSource = "LedWin GetNavAxisValue";
                Drive(_nav);
            }
            else
            {
                _navSource = "not detected";
                _navLive   = default;
            }

            // Raw driver counts are useless as a camera rate — normalise and dead-zone
            // first (see NavState.Condition). _navLive is kept so the diagnostics can
            // show the conditioned signal beside the raw one, which is the only way to
            // watch the dead-zone actually swallow the puck's resting noise.
            void Drive(in NavState raw)
            {
                _navPeakTrans = MathF.Max(_navPeakTrans, raw.PeakTranslation);
                _navPeakRot   = MathF.Max(_navPeakRot,   raw.PeakRotation);
                _navLive = raw.Condition(_s.NavFullScaleTrans, _s.NavFullScaleRot, _s.NavDeadzone);

                // Both buttons together switch mode, on the RISING edge only — held down,
                // a level test would flip modes every frame for as long as they were held.
                bool bothNow  = (raw.Buttons & 0x3) == 0x3;
                bool bothPrev = (_prevNavButtons & 0x3) == 0x3;
                if (bothNow && !bothPrev) ToggleInspect();
                _prevNavButtons = raw.Buttons;

                ApplyCameraLocks();

                // Zoom is on the individual buttons, and both-at-once already cancels
                // there, so the mode switch cannot also zoom.
                _cam.ApplyNav(_navLive, _lastDt, _s.NavPanRate, _s.NavRotRate, _s.NavZoomRate,
                              allowPan: !_s.InspectMode);

                if (_s.InspectMode) DriveProbe(_navLive, _lastDt);
            }
        }

        /// <summary>Push the rotation policy onto the camera. Called from the input path
        /// every frame rather than only when a toggle changes, so the camera cannot be left
        /// holding a stale policy after a settings load or reset.</summary>
        private void ApplyCameraLocks()
        {
            _cam.LocalAxes = _s.NavLocalAxes;

            // Inspection mode locks rotation outright. The probe is positioned in DISPLAY
            // space and deliberately not camera-transformed, so a scene that kept turning
            // would drag the board out from under a pointer that had not moved — the
            // reading would change while the operator held still.
            _cam.RotationLocked = _s.InspectMode;

            _cam.LockRotX = _s.LockRotX;
            _cam.LockRotY = _s.LockRotY;
            _cam.LockRotZ = _s.LockRotZ;
        }

        // ── Pick list ─────────────────────────────────────────────────────────
        // Rebuilt on the same timer as the legend, for the same reason: what belongs in it
        // depends on the loaded board and several toggles, and a dirty flag that misses one
        // shows a stale list -- which is worse than none, because it will be clicked.
        private volatile PickRow[] _picks = Array.Empty<PickRow>();
        private float _picksAge;

        public IReadOnlyList<PickRow> PickList => _picks;
        public string PickedKey => _s.PickedKey;

        /// <summary>Select from the list, or clear. Clicking the active row clears it, so
        /// one control both selects and deselects rather than needing a separate button.</summary>
        public void Pick(string key)
        {
            _s.PickedKey = string.Equals(_s.PickedKey, key, StringComparison.Ordinal)
                           ? "" : (key ?? "");
        }

        private int PickedNetId()
        {
            string k = _s.PickedKey;
            if (!k.StartsWith("net:", StringComparison.Ordinal)) return -1;
            return int.TryParse(k.AsSpan(4), out int id) ? id : -1;
        }

        private string PickedDesignator()
        {
            string k = _s.PickedKey;
            return k.StartsWith("comp:", StringComparison.Ordinal) ? k.Substring(5) : "";
        }

        private void RebuildPickList(float dt)
        {
            _picksAge += dt;
            if (_picksAge < 0.5f) return;
            _picksAge = 0f;

            if ((EDesMode)_s.Mode != EDesMode.Pcb || !_board.HasGeometry)
            {
                if (_picks.Length > 0) _picks = Array.Empty<PickRow>();
                return;
            }

            var rows = new List<PickRow>();

            var nets = _board.Nets;
            if (nets != null)
            {
                // Biggest nets first. On a derived netlist the large ones are the power and
                // ground planes, which are what you most often want to see the extent of,
                // and a list ordered by an arbitrary id would bury them.
                var order = new List<int>(nets.NetCount);
                for (int n = 0; n < nets.NetCount; n++) if (nets.Size(n) > 1) order.Add(n);
                order.Sort((a, b) => nets.Size(b).CompareTo(nets.Size(a)));

                foreach (int n in order)
                    rows.Add(new PickRow("Nets", nets.Name(n),
                                         nets.Size(n) + " obj", "net:" + n, 0x40E0E0));
            }

            // Designators from the STEP bodies first, then any placed part without a body,
            // so a part is listed once whichever source knows about it.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sol in _board.Solids)
            {
                string d = sol.Designator.Length > 0 ? sol.Designator : sol.Name;
                if (d.Length == 0 || !seen.Add(d)) continue;
                rows.Add(new PickRow("Components", d, "3D", "comp:" + d, sol.Colour));
            }
            foreach (var c in _board.Components)
            {
                if (c.Designator.Length == 0 || !seen.Add(c.Designator)) continue;
                rows.Add(new PickRow("Components", c.Designator,
                                     c.Value.Length > 0 ? c.Value : "placed",
                                     "comp:" + c.Designator, 0xC8C8D0));
            }

            _picks = rows.ToArray();
        }

        /// <summary>Advance the inspection stage: camera, signal, component, camera...
        ///
        /// A cycle rather than a toggle because the two inspectors answer different
        /// questions and each hides what the other needs — there is no one view that
        /// serves both, so they have to be separate stops.</summary>
        private void ToggleInspect()
        {
            _s.InspectStage = (_s.InspectStage + 1) % 3;

            if (_s.InspectMode)
            {
                // Start at the centre rather than wherever it was left: the volume may have
                // been re-fitted or a different board loaded since, and a probe resuming
                // off in a corner reads as a broken control.
                _s.InspectX = 0f; _s.InspectY = _s.PlaneY; _s.InspectZ = 0f;
            }
            else _s.InspectInfo = "";

            App.Log($"[EDesApp] {(EDesInspect)_s.InspectStage} inspector");
        }

        /// <summary>Move the probe, clamped inside the physical volume.
        ///
        /// Motion is in GLOBAL (display) coordinates, never the board's. The puck axes map
        /// straight onto the display axes with the camera taking no part, so pushing right
        /// moves the probe right on the display no matter how the board happens to be
        /// oriented. Driving it in board space instead would mean the same push went a
        /// different direction for every orientation, which is unusable for a pointer —
        /// and it is also why rotation is locked while inspecting.
        ///
        /// Clamped to the CYLINDER, not a box: the display is round in XY, so a box clamp
        /// would let the probe sit in a corner where nothing is ever drawn — it would
        /// simply vanish. Bounds come from the live values read this frame, so the clamp
        /// follows the hardware rather than a hardcoded 4.0/2.0.</summary>
        private void DriveProbe(in NavState nav, float dt)
        {
            float rate = _s.InspectRate * dt;
            float x = _s.InspectX + nav.Dx * rate;
            float y = _s.InspectY + nav.Dy * rate;
            float z = _s.InspectZ + nav.Dz * rate;

            float rMax = _radius * 0.94f;
            float rr   = MathF.Sqrt(x * x + y * y);
            if (rr > rMax && rr > 1e-6f) { x *= rMax / rr; y *= rMax / rr; }
            z = Math.Clamp(z, -_zHalf * 0.94f, _zHalf * 0.94f);

            _s.InspectX = x; _s.InspectY = y; _s.InspectZ = z;
        }

        /// <summary>True when an inspector is active AND the probe is actually on
        /// something. Both halves matter: with nothing selected the normal readouts are
        /// still the most useful thing on the display.</summary>
        private bool ProbeHasSelection => _s.InspectMode && _pcb.Probe.Hit;

        /// <summary>Leader line from the probe to whatever it snapped to.
        ///
        /// Both ends are already in display space — the probe by definition, the target
        /// because the renderer transformed it — so this must NOT be camera-transformed
        /// again. Drawn thin and dim so it reads as a pointer rather than as board
        /// geometry.</summary>
        private void DrawProbeLeader()
        {
            if (!_pcb.ProbeHasTarget) return;
            _batch.Line(new point3d(_s.InspectX, _s.InspectY, _s.InspectZ),
                        _pcb.ProbeTarget, 0x6080A0, 2f);
        }

        /// <summary>The probe itself, plus its readout. Drawn in DISPLAY space and NOT
        /// camera-transformed: it is a physical pointer in the box, so rotating the scene
        /// must move the board past the probe, not carry the probe along with it.</summary>
        private void DrawProbe()
        {
            var at = new point3d(_s.InspectX, _s.InspectY, _s.InspectZ);
            float r = MathF.Max(_spacing * 2.5f, _radius * 0.012f);
            _batch.Blob(at, r, 0xFFFFFF);

            // Cross-hair arms, so the probe's depth is readable — a lone blob on a
            // transparent display gives no cue about where it sits in Y.
            float arm = r * 3f;
            _batch.Line(new point3d(at.x - arm, at.y, at.z), new point3d(at.x + arm, at.y, at.z), 0xB0B0C0);
            _batch.Line(new point3d(at.x, at.y - arm, at.z), new point3d(at.x, at.y + arm, at.z), 0xB0B0C0);
            _batch.Line(new point3d(at.x, at.y, at.z - arm), new point3d(at.x, at.y, at.z + arm), 0xB0B0C0);
        }

        /// <summary>Probe readout, top-left of the volume on the constant-Y HUD plane.
        /// Deliberately NOT camera-transformed, like the other readouts, so it stays
        /// legible while the scene turns.</summary>
        private void DrawProbeReadout()
        {
            var probe = _pcb.Probe;

            float size = _textSize * 0.85f;
            float x    = -_radius * 0.95f;

            // Shares the frame's single top-of-display cursor rather than picking its own
            // fraction of the height — a hand-picked -0.92 * zHalf is exactly how two
            // blocks end up in the same voxels once one of them grows a line.
            ref TextStack st = ref _topText;

            string title = ((EDesInspect)_s.InspectStage) switch
            {
                EDesInspect.Signal    => "SIGNAL INSPECTOR",
                EDesInspect.Component => "COMPONENT INSPECTOR",
                _                     => "INSPECTION MODE",
            };
            _hud.Text(new point3d(x, _s.PlaneY, st.Row()), size, Palette.TextHilite, title);

            if (!probe.Hit)
            {
                _hud.Text(new point3d(x, _s.PlaneY, st.Row()), size, Palette.TextDim,
                          "move the probe over a layer or part");
                _s.InspectInfo = "Inspection mode\nNothing under the probe.";
                return;
            }

            _hud.Text(new point3d(x, _s.PlaneY, st.Row()), size, Palette.Trace,
                      probe.Kind.ToUpperInvariant() + "  " + probe.Title);
            foreach (string line in probe.Lines)
                _hud.Text(new point3d(x, _s.PlaneY, st.Row()), size, Palette.Text, line);

            var sb = new StringBuilder();
            sb.Append(probe.Kind.ToUpperInvariant()).Append("  ").Append(probe.Title).Append('\n');
            foreach (string line in probe.Lines) sb.Append(line).Append('\n');
            _s.InspectInfo = sb.ToString();
        }

        private static bool Down(VX_KEYS k)   => NativeInput.OnDown(k) == 1;
        private static bool IsDown(VX_KEYS k) => NativeInput.IsDown(k) == 1;

        // ── Draw ──────────────────────────────────────────────────────────────

        public void Draw(LedHostCS ledHost, ref vxl_state_t vs)
        {
            ReadBounds(ledHost, ref vs);
            ApplyNavigator(ledHost);

            _batch.BeginFrame(EffectiveVoxelBudget(), _radius, _zHalf, _spacing);

            // Reserve vertical space BEFORE drawing anything: two header rows at the
            // top, and a footer sized to the rows this mode will actually need. Blocks
            // then draw into their own band and cannot collide. See Sim/Layout.cs.
            int headerRows = _s.ShowHudPanel ? 2 : 0;
            _layout = new FrameLayout(_zHalf, _step, headerRows, ReadoutRowsForMode());

            // ONE cursor for every text block this frame, handed out first-come. That is
            // what keeps two blocks from writing into the same rows now that they all
            // share the top band instead of being split between the two ends.
            _topText = _layout.Readout();

            switch ((EDesMode)_s.Mode)
            {
                case EDesMode.Education: DrawEducation(); break;
                case EDesMode.Scope:     DrawScopeMode(); break;
                case EDesMode.Pcb:       DrawPcbMode();   break;
            }

            // Probe and its readout come BEFORE the HUD panel and the backdrop: when the
            // probe is what you are driving, it is the last thing that should disappear
            // to a tight budget, not the first.
            if (_s.InspectMode)
            {
                DrawProbeLeader();
                DrawProbe();
                DrawProbeReadout();
            }
            else if (_s.InspectInfo.Length > 0) _s.InspectInfo = "";

            if (_s.ShowNavDiag)   DrawNavReadout();
            // The title/voxel panel is suppressed while the probe has something, so the
            // selection sits at the very top of the display instead of under two rows of
            // information nobody is reading at that moment. The text band is only a few
            // rows tall, so anything kept is something else lost.
            if (_s.ShowHudPanel && !ProbeHasSelection) DrawHudPanel();
            if (_s.ShowBackdrop)  DrawBackdrop();     // last: decoration is dropped first

            _batch.Flush(ledHost, ref vs);

            _lastVoxels  = _batch.Count;
            _lastDropped = _batch.Dropped;
            _engine.LiveVoxelCount = _lastVoxels;
        }

        // ── Adaptive budget ───────────────────────────────────────────────────
        // Tracked individually rather than as a combined signature: summing the seven
        // values into one number would let two simultaneous changes cancel and report the
        // view as still while it was moving.
        private float _lastPanX, _lastPanY, _lastPanZ, _lastZoom;
        private float _lastYaw, _lastPitch, _lastRoll;
        private float _budgetScale = 1f;
        private bool  _viewMoving;

        /// <summary>The budget to actually draw with this frame.
        ///
        /// Decay and recovery are per-SECOND, not per-frame. Per-frame rates would make
        /// the throttle behave differently at 30 VPS than at 5 — and 5 is precisely when it
        /// matters, so the slow case would get the weakest response. Recovery is
        /// deliberately gentler than decay: rescue quickly, come back carefully, or the
        /// budget pumps up and down across the threshold.</summary>
        private int EffectiveVoxelBudget()
        {
            if (!_s.AdaptiveBudget)
            {
                _budgetScale = 1f;
                return _s.MaxVoxels;
            }

            float vps   = _engine?.LiveVps ?? 0f;
            float floor = Math.Clamp(_s.AdaptiveFloor, 0.02f, 1f);
            float dt    = MathF.Max(1e-4f, _lastDt);

            // A VPS of 0 means the loop has not measured one yet; treat it as healthy
            // rather than throttling the very first frames to the floor.
            bool struggling = vps > 0.01f && vps < _s.AdaptiveLowVps;
            bool recovered  = vps <= 0.01f || vps > _s.AdaptiveGoodVps;

            if (_viewMoving && struggling) _budgetScale -= dt * 1.5f;
            else if (recovered || !_viewMoving) _budgetScale += dt * 0.5f;

            _budgetScale = Math.Clamp(_budgetScale, floor, 1f);
            return Math.Max(1_000, (int)(_s.MaxVoxels * _budgetScale));
        }

        /// <summary>Has the view moved since the last frame? Any of pan, zoom or
        /// orientation counts.</summary>
        private void TrackViewMotion()
        {
            const float eps = 1e-5f;
            _viewMoving =
                MathF.Abs(_cam.PanX  - _lastPanX)  > eps ||
                MathF.Abs(_cam.PanY  - _lastPanY)  > eps ||
                MathF.Abs(_cam.PanZ  - _lastPanZ)  > eps ||
                MathF.Abs(_cam.Zoom  - _lastZoom)  > eps ||
                MathF.Abs(_cam.Yaw   - _lastYaw)   > eps ||
                MathF.Abs(_cam.Pitch - _lastPitch) > eps ||
                MathF.Abs(_cam.Roll  - _lastRoll)  > eps;

            _lastPanX = _cam.PanX; _lastPanY = _cam.PanY; _lastPanZ = _cam.PanZ;
            _lastZoom = _cam.Zoom;
            _lastYaw  = _cam.Yaw;  _lastPitch = _cam.Pitch; _lastRoll = _cam.Roll;
        }

        /// <summary>Read the display's true extents from the SDK every frame. The
        /// volume is a cylinder: radius in X/Y from GetAspectRatioX, half-height in Z.
        /// vs.boundr/boundz are preferred when the DLL has filled them in.</summary>
        private void ReadBounds(LedHostCS ledHost, ref vxl_state_t vs)
        {
            // The SDK aspect ratios ARE the volume, and the origin is its centre:
            //     -GetAspectRatioX .. +GetAspectRatioX   across  (radius)
            //     -GetAspectRatioZ .. +GetAspectRatioZ   vertical (-Z is up)
            // Both are read every frame so one build fills a VX2 or a VX2-XL.
            float radius = ledHost.GetAspectRatioX(ref vs);
            float zHalf  = ledHost.GetAspectRatioZ(ref vs);
            if (radius <= 0.1f) radius = 4f;                  // pre-init frame
            if (zHalf  <= 0.1f) zHalf  = radius;

            // 2% inset only, so a glyph's last voxel never lands exactly on the wall.
            _radius = radius * 0.98f;
            _zHalf  = MathF.Max(0.2f, zHalf * 0.98f);

            // One point per real voxel at density 1.0; the density slider scales it.
            float pitch   = vs.xsiz > 8 ? 2f * radius / vs.xsiz : 0.03f;
            float density = Math.Clamp(_engine.VoxelDensity, 0.25f, 3f);
            _spacing = MathF.Max(0.004f, pitch / density);

            // Text scales with the display so a VX2 is not covered in giant glyphs
            // and a VX2-XL is not covered in unreadable specks.
            //
            // The floor is derived from the VOXEL SPACING, not picked. The 5x7 glyphs use
            // a cell of size * 0.18 in height, so a glyph is only legible while one cell
            // is at least a voxel or so across — below that every lit cell lands on the
            // same voxel as its neighbour and the character collapses into a blob. The old
            // fixed floor of 0.04 was roughly a QUARTER of a voxel per cell at default
            // density, i.e. far past unreadable, so text could be configured into
            // illegibility and look like a rendering fault.
            float minSize = _spacing / GLYPH_CELL_FRACTION *
                            MathF.Max(1f, _s.MinTextCellVoxels);
            _textSize = MathF.Max(minSize, _s.TextSize * (radius / 4f));
            _step     = Hud.LineStep(_textSize);
            _textFloored = _textSize > _s.TextSize * (radius / 4f) + 1e-6f;
        }

        /// <summary>The single top-of-display text cursor for this frame. Every text block
        /// draws from it, which is what stops two of them landing in the same rows now that
        /// they all share one band instead of being split top and bottom.</summary>
        private TextStack _topText;

        /// <summary>Rows the top band needs this frame — reserved BEFORE anything draws,
        /// so geometry can never start inside the text.</summary>
        private int ReadoutRowsForMode()
        {
            // The optional blocks share the same top band, so their rows have to be
            // reserved here as well — otherwise the geometry band starts inside them.
            int extra = 0;
            // Trimmed from nine: a trace is now one line and a part is at most four, so
            // reserving nine pushed the geometry down for rows that never get drawn.
            if (_s.InspectMode) extra += 6;
            if (_s.ShowNavDiag) extra += 5;

            // With a selection the mode's own readout is suppressed, so its rows must not
            // be reserved either — reserving space for text that will not be drawn is the
            // same mistake as drawing it.
            if (!_s.ShowLabels || ProbeHasSelection) return extra;
            switch ((EDesMode)_s.Mode)
            {
                case EDesMode.Education:
                    return extra + 3;                                    // law + totals + V=IR
                case EDesMode.Scope:
                    return extra + 1 + (_s.ScopeMeasurements ? EnabledChannelCount() : 0);
                default:
                    // Two summary rows plus the cursor row when it is on. The layer legend
                    // and document inventory are no longer drawn, so reserving their rows
                    // would push the geometry down for text that never appears.
                    return extra + 2 + (_s.PcbCursor ? 1 : 0);
            }
        }

        private int EnabledChannelCount()
        {
            int n = 0;
            for (int ch = 0; ch < ScopeSource.MAX_CHANNELS; ch++)
                if ((_s.ScopeChannelMask & (1 << ch)) != 0 && ch < _scope.ChannelCount) n++;
            return Math.Max(1, n);
        }

        // ── Mode: education ───────────────────────────────────────────────────
        private void DrawEducation()
        {
            // Upper 55% of the content band is the circuit, lower is the scope strip,
            // with a one-row gutter between them.
            _layout.SplitContent(0.55f, 1.0f,
                                 out float cTop, out float cBottom,
                                 out float sTop, out float sBottom);

            // The circuit loop needs headroom inside itself for the component labels
            // (3 rows) plus the power bulge, so it is laid out from the real band.
            _scene.RecomputeIfDirty(_radius, cTop + _step * 0.6f, cBottom, _step);
            _circuit.Draw(_batch, _hud, _cam, _scene, _anim, _s.ShowLabels, _textSize, _step);

            DrawScopePanel(sTop, sBottom, _radius * 0.74f);

            if (!_s.ShowLabels) return;

            // Footer: the law this circuit demonstrates, then its solved totals.
            ref TextStack f = ref _topText;
            _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize, Palette.TextHilite,
                             _scene.Active.Law);
            _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize, Palette.Text,
                "RT " + Hud.Eng(_scene.TotalResistance, "R") +
                "   IT " + Hud.Eng(_scene.TotalCurrent, "A") +
                "   PT " + Hud.Eng(_scene.TotalPower, "W"));
            _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize, Palette.TextDim,
                "V " + Hud.Eng(_scene.SourceVolts, "V") + "   =   I X R");
        }

        // ── Mode: scope ───────────────────────────────────────────────────────
        // The scope owns the whole content band here: full width, full height.
        private void DrawScopeMode()
        {
            DrawScopePanel(_layout.ContentTopZ, _layout.ContentBottomZ, _radius * 0.88f);

            if (!_s.ShowLabels) return;
            ref TextStack f = ref _topText;
            _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize * 0.85f, Palette.TextDim,
                _scope.Identity.Length > 0 ? _scope.Identity : _scope.Status);
            if (_s.ScopeMeasurements) DrawScopeMeasurements(ref f);
        }

        private void DrawScopePanel(float zTop, float zBottom, float halfWidth)
        {
            // One row above the face carries the source/scale header; the measurement
            // rows go in the reserved footer, not below the face.
            var panel = new ScopePanel
            {
                Y       = _s.PlaneY,
                X0      = -halfWidth,
                X1      =  halfWidth,
                ZTop    = zTop + _step,      // the face starts one row below the header
                ZBottom = zBottom,
                HeaderZ = zTop,
            };

            _scopeRenderer.NoteColumns((int)(panel.Width / _spacing));
            _scopeRenderer.Draw(_batch, _hud, _scope, panel,
                                _s.ScopeVoltsPerDiv, (uint)_s.ScopeChannelMask,
                                _s.ScopeTriggerCh, _s.ScopeTriggerLevel, _s.ScopeTriggerRising,
                                _s.ShowLabels, _textSize);
        }

        /// <summary>Per-channel measurement rows, drawn into the reserved footer.</summary>
        private void DrawScopeMeasurements(ref TextStack f)
        {
            int channels = Math.Clamp(_scope.ChannelCount, 1, ScopeSource.MAX_CHANNELS);
            for (int ch = 0; ch < channels; ch++)
            {
                if ((_s.ScopeChannelMask & (1 << ch)) == 0) continue;
                var st = _scopeRenderer.Stats[ch];
                _hud.Text(new point3d(-_radius * 0.88f, _s.PlaneY, f.Row()), _textSize,
                          ScopeRenderer.ChannelColour(ch),
                          "CH" + (ch + 1) +
                          "  VPP " + Hud.Eng(st.Vpp, "V") +
                          "  RMS " + Hud.Eng(st.Vrms, "V") +
                          "  F "   + Hud.Eng(st.FreqHz, "HZ") +
                          "  DUTY " + st.DutyPct.ToString("0") + "PCT");
            }
        }

        // ── Mode: PCB ─────────────────────────────────────────────────────────
        private void DrawPcbMode()
        {
            var opt = new PcbViewOptions
            {
                LayerSpacing = _s.LayerSpacing,
                TrackScale   = _s.TrackScale,
                ShowPads     = _s.PcbPads,
                ShowRegions  = _s.PcbRegions,
                FillRegions  = _s.PcbFillRegions,
                ShowHoles    = _s.PcbHoles,
                ShowVias     = _s.PcbVias,
                ViaMaxDiaMm  = _s.PcbViaMaxDia,
                ViaDisplayVoxels = _s.PcbViaSize,
                PourDensity  = _s.PcbPourDensity,
                HatchDensity = _s.PcbHatchDensity,
                ShowMeshes   = _s.PcbMeshes,
                ShowCad       = _s.PcbCad,
                CadBrightness = _s.PcbCadBright,
                CadLighting   = _s.PcbCadLighting,
                CadSurfaces   = _s.PcbCadSurfaces,
                CadSurfaceDensity = _s.PcbCadSurfaceDensity,
                CadZOffset    = _s.PcbCadZOffset,
                Inspect       = _s.InspectMode,
                PickedNet        = PickedNetId(),
                PickedDesignator = PickedDesignator(),
                // Signal shows copper + outline; component shows outline only and hides
                // every part-derived thing. Both are VIEW filters — the operator's own
                // layer toggles are persisted and must survive a glance at an inspector.
                LayerFilter   = _s.InspectStage == (int)EDesInspect.Signal ? 1
                              : _s.InspectStage == (int)EDesInspect.Component ? 2 : 0,
                HideParts     = _s.InspectStage == (int)EDesInspect.Signal,
                ProbeX        = _s.InspectX,
                ProbeY        = _s.InspectY,
                ProbeZ        = _s.InspectZ,
                DimFactor     = _s.InspectDim,
                SnapRange     = _s.InspectSnap,
                // Slow deliberately: a fast pulse on a volumetric display reads as flicker
                // rather than as emphasis, and this runs for as long as something is
                // selected rather than as a brief confirmation.
                Pulse         = 0.5f + 0.5f * MathF.Sin(_anim * MathF.PI * 2f / 2.4f),
                CadAmbient    = _s.PcbCadAmbient,
                CadLightX     = _s.PcbCadLightX,
                CadLightY     = _s.PcbCadLightY,
                CadLightZ     = _s.PcbCadLightZ,
                ShowCursor   = _s.PcbCursor,
                CursorXmm    = _s.PcbCursorX,
                CursorYmm    = _s.PcbCursorY,
                Brightness   = _s.PcbBrightness,
                IsolateLayer = _s.PcbIsolate,
                ShowComponents = _s.PcbComponents,
                ShowLabels     = _s.PcbComponentLabels && _s.ShowLabels,
                LabelLimit     = _s.PcbLabelLimit,
                TextSize       = _textSize,
            };

            _pcb.Draw(_batch, _hud, _cam, _board, opt, _radius, _zHalf);

            if (!_s.ShowLabels || ProbeHasSelection) return;

            ref TextStack f = ref _topText;

            if (!_board.HasGeometry)
            {
                _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize, Palette.Warning,
                                 "NO BOARD LOADED - SET A PATH IN THE PCB TAB");
                return;
            }

            _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize, Palette.Text,
                _board.WidthMm.ToString("0.0") + " X " + _board.HeightMm.ToString("0.0") + " MM   " +
                _board.CopperLayerCount() + " CU   " + _board.Holes.Count + " HOLES" +
                (_board.Components.Count > 0 ? "   " + _board.Components.Count + " PARTS" : ""));

            _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize, Palette.TextDim,
                "MIN TRACK " + _board.MinTrackWidth().ToString("0.000") + "MM   " +
                "MIN DRILL " + _board.MinDrill().ToString("0.000") + "MM");

            // The layer legend and the document inventory used to print here. Both are
            // now redundant: the window's legend overlay shows every layer with its
            // colour and a visibility box, and the import inventory lists the documents
            // in full. Reprinting them in the volume spent most of a very short text band
            // on information that is better presented on screen.

            if (_s.PcbCursor)
                _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize, Palette.TextHilite,
                    "CURSOR X " + _s.PcbCursorX.ToString("0.00") +
                    "  Y " + _s.PcbCursorY.ToString("0.00") + " MM");
        }

        // ── Shared HUD ────────────────────────────────────────────────────────
        private void DrawHudPanel()
        {
            float size = _textSize;

            string mode = ((EDesMode)_s.Mode) switch
            {
                EDesMode.Education => "EDUCATION  " + _scene.Active.Name,
                EDesMode.Scope     => "OSCILLOSCOPE",
                _                  => "PCB  " + _board.SourceName.ToUpperInvariant(),
            };
            _hud.TextCentred(0f, _s.PlaneY, _layout.HeaderZ, size, Palette.Text, mode);

            // Voxel budget readout — the single most useful number when tuning a
            // scene for this display, so it is on the glass, not just in the UI.
            string budget = _lastVoxels + " / " + _s.MaxVoxels + " VOX";
            if (_lastDropped > 0) budget += "   +" + _lastDropped + " DROPPED";
            _hud.TextCentred(0f, _s.PlaneY, _layout.SubHeaderZ, size * 0.8f,
                             _lastDropped > 0 ? Palette.Warning : Palette.TextDim, budget);
        }

        /// <summary>SpaceNavigator readout in the volume: detection, the three
        /// translation axes, the three rotation axes, and both buttons — so the puck
        /// can be checked at the display instead of on the PC screen.</summary>
        private void DrawNavReadout()
        {
            float size = _textSize * 0.8f;
            float x    = -_radius * 0.92f;
            ref TextStack st = ref _topText;   // shared top-of-display cursor

            bool detected = _nav.Present || _navHostUsable || _nav.Devices > 0;
            _hud.Text(new point3d(x, _s.PlaneY, st.Row()), size,
                      detected ? Palette.Trace : Palette.Warning,
                      "SPACENAV " + (detected ? "DETECTED" : "NOT DETECTED") +
                      "  DEV " + _nav.Devices + "  SRC " + _navSource.ToUpperInvariant());

            _hud.Text(new point3d(x, _s.PlaneY, st.Row()), size, Palette.Text,
                      "LW  X " + F(_nav.Dx) + "  Y " + F(_nav.Dy) + "  Z " + F(_nav.Dz) +
                      "   RX " + F(_nav.Ax) + "  RY " + F(_nav.Ay) + "  RZ " + F(_nav.Az));

            _hud.Text(new point3d(x, _s.PlaneY, st.Row()), size, Palette.Text,
                      "LH  X " + F(_navHost.dx) + "  Y " + F(_navHost.dy) + "  Z " + F(_navHost.dz) +
                      "   RX " + F(_navHost.ax) + "  RY " + F(_navHost.ay) + "  RZ " + F(_navHost.az) +
                      "   RC " + _navHostRc);

            int buttons = _nav.Buttons | _navHost.but;
            _hud.Text(new point3d(x, _s.PlaneY, st.Row()), size,
                      buttons != 0 ? Palette.TextHilite : Palette.TextDim,
                      "BTN L " + ((buttons & 1) != 0 ? "DOWN" : "UP") +
                      "   BTN R " + ((buttons & 2) != 0 ? "DOWN" : "UP") +
                      "   MASK " + buttons);
        }

        private static string F(float v) => v.ToString("0.00");

        /// <summary>Grid floor + three orthogonal rings: cheap orientation cues that
        /// stop the scene reading as objects floating in a void.</summary>
        private void DrawBackdrop()
        {
            float r = _radius * 0.96f;
            float floorZ = _zHalf * 0.99f;      // the actual floor of the volume
            var up = new point3d(1, 0, 0);
            var rt = new point3d(0, 1, 0);

            for (int i = 1; i <= 3; i++)
                _batch.Ring(_cam.Transform(0, 0, floorZ), r * i / 3f, up, rt, Palette.GridFloor, 4f);

            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4f;
                _batch.Line(_cam.Transform(0, 0, floorZ),
                            _cam.Transform(MathF.Cos(a) * r, MathF.Sin(a) * r, floorZ),
                            Palette.GridFloor, 4f);
            }

            // Three great circles at the volume wall, one per plane.
            float gr = MathF.Min(r, _zHalf * 0.95f);
            _batch.Ring(_cam.Transform(0, 0, 0), gr, new point3d(1, 0, 0), new point3d(0, 1, 0), Palette.Globe, 4f);
            _batch.Ring(_cam.Transform(0, 0, 0), gr, new point3d(1, 0, 0), new point3d(0, 0, 1), Palette.Globe, 4f);
            _batch.Ring(_cam.Transform(0, 0, 0), gr, new point3d(0, 1, 0), new point3d(0, 0, 1), Palette.Globe, 4f);
        }

        // ── Settings tab ──────────────────────────────────────────────────────

        // Immutable snapshot handed to the shell, swapped by reference. Rebuilt on the
        // GAME thread; read on the UI thread. Never mutated in place — see IVoxonGame.Legend.
        private volatile LegendRow[] _legend = Array.Empty<LegendRow>();
        private float _legendAge;

        public IReadOnlyList<LegendRow> Legend => _legend;

        /// <summary>The probe readout, mirrored onto the shell's preview overlay.</summary>
        public string StatusOverlay => _s.InspectMode ? _s.InspectInfo : "";

        /// <summary>Rebuild the legend, at most a few times a second.
        ///
        /// Time-based rather than dirty-flagged on purpose: what belongs in the legend
        /// depends on the mode, the loaded board, per-layer visibility, the isolate
        /// setting and several toggles, and a dirty flag that misses one of those shows
        /// the user a stale legend — which is worse than no legend, because they will
        /// believe it. One small array twice a second is not worth outsmarting.</summary>
        private void RebuildLegend(float dt)
        {
            _legendAge += dt;
            if (_legendAge < 0.4f) return;
            _legendAge = 0f;

            var rows = new List<LegendRow>();

            if ((EDesMode)_s.Mode == EDesMode.Pcb && _board.HasGeometry)
            {
                for (int li = 0; li < _board.Layers.Count; li++)
                {
                    var layer = _board.Layers[li];
                    bool isolatedOut = _s.PcbIsolate >= 0 && _s.PcbIsolate != li;
                    bool shown = layer.Visible && !isolatedOut;
                    rows.Add(new LegendRow($"{layer.Kind}  {layer.Name}", layer.Colour, !shown,
                                           key: "layer:" + layer.Name,
                                           canToggle: true, canRecolour: true));
                }

                if (_s.PcbVias && _board.Holes.Count > 0)
                {
                    int copper = _board.CopperLayerCount();
                    int blind = 0, through = 0;
                    foreach (var h in _board.Holes)
                    {
                        if (!PcbBoard.IsVia(h, _s.PcbViaMaxDia)) continue;
                        if (h.IsBlind(copper)) blind++; else through++;
                    }
                    if (through > 0)
                        rows.Add(new LegendRow($"via (through) x{through}", 0xE8A020,
                                               key: "vias", canToggle: true));
                    if (blind > 0)
                        rows.Add(new LegendRow($"via (blind/buried) x{blind}", 0x40D0E8,
                                               key: "vias", canToggle: true));
                }

                if (_board.Solids.Count > 0)
                    rows.Add(new LegendRow($"STEP model x{_board.Solids.Count}", 0x9FC5E8,
                                           !_s.PcbCad, key: "cad", canToggle: true));

                if (_board.Meshes.Count > 0)
                    rows.Add(new LegendRow($"mesh x{_board.Meshes.Count}", 0x66D9C0,
                                           !_s.PcbMeshes, key: "mesh", canToggle: true));
            }

            _legend = rows.ToArray();
        }

        // ── Legend write-back ─────────────────────────────────────────────────
        // Called on the UI THREAD. Layers are looked up by name rather than index because
        // an import can replace the whole list between the snapshot the shell drew and the
        // click coming back; a stale index would silently hit the wrong layer, whereas a
        // stale name simply finds nothing.

        public void SetLegendVisible(string key, bool visible)
        {
            if (key == "vias") { _s.PcbVias   = visible; SaveLayerPrefs(); return; }
            if (key == "cad")  { _s.PcbCad    = visible; SaveLayerPrefs(); return; }
            if (key == "mesh") { _s.PcbMeshes = visible; SaveLayerPrefs(); return; }

            var layer = FindLayer(key);
            if (layer == null) return;
            layer.Visible = visible;
            SaveLayerPrefs();
        }

        public void SetLegendColour(string key, int colour)
        {
            var layer = FindLayer(key);
            if (layer == null) return;
            layer.ColourOverride = colour & 0xFFFFFF;
            SaveLayerPrefs();
        }

        private PcbLayer? FindLayer(string key)
        {
            if (!key.StartsWith("layer:", StringComparison.Ordinal)) return null;
            string name = key.Substring(6);
            // Snapshot the count first: the game thread may be mid-import.
            var layers = _board.Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (i >= layers.Count) break;
                if (string.Equals(layers[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return layers[i];
            }
            return null;
        }

        /// <summary>Flatten the per-layer choices into the settings string.
        ///
        /// Persisted because a re-import rebuilds every layer from defaults, and the last
        /// board is re-imported on every launch — so without this, recolouring a layer
        /// would appear to work and then silently revert.</summary>
        private void SaveLayerPrefs()
        {
            var sb = new StringBuilder();
            var layers = _board.Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                sb.Append(l.Name.Replace(';', '_').Replace('|', '_')).Append('|');
                if (l.ColourOverride.HasValue) sb.Append(l.ColourOverride.Value.ToString("X6"));
                sb.Append('|').Append(l.Visible ? '1' : '0').Append(';');
            }
            _s.PcbLayerPrefs = sb.ToString();
        }

        /// <summary>Re-apply saved choices to a freshly imported board.</summary>
        private void ApplyLayerPrefs()
        {
            string prefs = _s.PcbLayerPrefs;
            if (string.IsNullOrEmpty(prefs)) return;

            foreach (string entry in prefs.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = entry.Split('|');
                if (bits.Length < 3) continue;

                foreach (var l in _board.Layers)
                {
                    if (!string.Equals(l.Name, bits[0], StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (bits[1].Length > 0 &&
                        int.TryParse(bits[1], System.Globalization.NumberStyles.HexNumber,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out int c))
                        l.ColourOverride = c & 0xFFFFFF;
                    l.Visible = bits[2] == "1";
                    break;
                }
            }
        }

        /// <summary>The mode headers the shell draws across the top of the window.
        /// Order must match EDesMode.</summary>
        public IReadOnlyList<string> Modes { get; } = new[] { "Education", "Oscilloscope", "PCB" };

        /// <summary>Which header is lit. The shell writes this on a click and then
        /// rebuilds the panel below, so the setting is the single source of truth for
        /// both the volume and the UI — there is no second copy to drift.</summary>
        public int ActiveMode
        {
            get => Math.Clamp(_s.Mode, 0, Modes.Count - 1);
            set => _s.Mode = Math.Clamp(value, 0, Modes.Count - 1);
        }

        /// <summary>Only the sections that belong to the mode on screen, plus the ones
        /// that apply everywhere. The shell rebuilds this whenever the mode changes, so
        /// a mode's settings are never buried under two other modes' accordions.</summary>
        public Control BuildSettingsPanel(PanelBuilder ui)
        {
            var stack = ui.Root();
            var group = new List<Expander>();

            switch ((EDesMode)ActiveMode)
            {
                case EDesMode.Education: BuildCircuitSection(ui, stack, group); break;
                case EDesMode.Scope:     BuildScopeSection(ui, stack, group);   break;
                case EDesMode.Pcb:       BuildPcbSection(ui, stack, group);     break;
            }

            BuildRenderSection(ui, stack, group);
            BuildCameraSection(ui, stack, group);

            return ui.Wrap(stack);
        }

        private void BuildCircuitSection(PanelBuilder ui, StackPanel stack, List<Expander> group)
        {
            var sec = ui.AddSection(stack, "Circuit (Education)", group);
            ui.AddInfo(sec, "Four built-in topologies. Keys 1-4 in the volume.");
            for (int i = 0; i < _scene.Presets.Length; i++)
            {
                int idx = i;
                ui.AddButton(sec, $"{i + 1}. {_scene.Presets[i].Name}", () => _s.PresetIndex = idx);
            }
            ui.AddSlider(sec, "Source voltage (V)", 1, 24, _s.SourceVolts, v => _s.SourceVolts = (float)v, "F1");
            ui.AddSlider(sec, "R1 (ohms)", 1, 10000, _s.R1, v => _s.R1 = (float)v, "F0");
            ui.AddSlider(sec, "R2 (ohms)", 1, 10000, _s.R2, v => _s.R2 = (float)v, "F0");
            ui.AddSlider(sec, "R3 (ohms)", 1, 10000, _s.R3, v => _s.R3 = (float)v, "F0");
            ui.AddSlider(sec, "Flow animation speed", 0, 4, _s.FlowSpeed, v => _s.FlowSpeed = (float)v, "F2");
            ui.AddToggle(sec, "Pause current flow", _s.FlowPaused, v => _s.FlowPaused = v);
            ui.AddLiveInfo(sec, () =>
                $"Rt {_scene.TotalResistance:0.##} ohm   It {_scene.TotalCurrent * 1000:0.##} mA   " +
                $"Pt {_scene.TotalPower:0.###} W");
        }

        private void BuildScopeSection(PanelBuilder ui, StackPanel stack, List<Expander> group)
        {
            var sec = ui.AddSection(stack, "Oscilloscope — input", group);
            ui.AddInfo(sec, "Synthetic needs nothing. Serial reads an ASCII sample stream " +
                            "(CSV = one channel per column). SCPI talks to a bench instrument: " +
                            "TCP needs no driver at all, USBTMC needs an installed VISA runtime. " +
                            "See docs/SCOPE_USB.md.");
            ui.AddButton(sec, "Source: Synthetic",         () => _s.ScopeMode = (int)ScopeInput.Synthetic);
            ui.AddButton(sec, "Source: Serial (ASCII)",    () => _s.ScopeMode = (int)ScopeInput.Serial);
            ui.AddButton(sec, "Source: SCPI over TCP",     () => _s.ScopeMode = (int)ScopeInput.ScpiTcp);
            ui.AddButton(sec, "Source: SCPI over USBTMC",  () => _s.ScopeMode = (int)ScopeInput.ScpiVisa);
            ui.AddLiveInfo(sec, () =>
                $"Source: {(ScopeInput)Math.Clamp(_s.ScopeMode, 0, 3)}\nStatus: {_scope.Status}" +
                (_scope.Identity.Length > 0 ? "\n" + _scope.Identity : "") +
                $"\n{_scope.ChannelCount} ch @ {_scope.SampleRateHz:0} Hz" +
                (_scope.Overruns > 0 ? $"   ({_scope.Overruns} bad reads)" : ""));

            ui.AddTextBox(sec, "Instrument IP (SCPI/TCP)", _s.ScopeHost, v => _s.ScopeHost = v.Trim());
            ui.AddSlider(sec, "TCP port", 1, 65535, _s.ScopeTcpPort, v => _s.ScopeTcpPort = (int)v, "F0");
            ui.AddTextBox(sec, "VISA resource (blank = first USB)", _s.ScopeVisaResource,
                          v => _s.ScopeVisaResource = v.Trim());
            ui.AddButton(sec, "Next VISA resource", () =>
            {
                var res = ScopeSource.VisaResources();
                if (res.Length == 0) return;
                int at = Array.IndexOf(res, _s.ScopeVisaResource);
                _s.ScopeVisaResource = res[(at + 1) % res.Length];
            });
            ui.AddSlider(sec, "SCPI acquisitions / second", 0.5, 30, _s.ScopePollHz,
                         v => _s.ScopePollHz = (float)v, "F1");
            ui.AddLiveInfo(sec, () =>
            {
                var res = ScopeSource.VisaResources();
                return "VISA runtime: " + (ScopeSource.VisaRuntimeAvailable ? "present" : "NOT installed") +
                       "\nVISA instruments: " + (res.Length == 0 ? "(none)" : string.Join("\n  ", res));
            }, 3.0);

            ui.AddTextBox(sec, "Serial port", _s.ScopePort, v => _s.ScopePort = v.Trim());
            ui.AddButton(sec, "Next detected serial port", () =>
            {
                var ports = ScopeSource.AvailablePorts();
                if (ports.Length == 0) return;
                int at = Array.IndexOf(ports, _s.ScopePort);
                _s.ScopePort = ports[(at + 1) % ports.Length];
            });
            ui.AddLiveInfo(sec, () =>
            {
                var ports = ScopeSource.AvailablePorts();
                return "Serial ports: " + (ports.Length == 0 ? "(none)" : string.Join(", ", ports));
            }, 3.0);
            ui.AddSlider(sec, "Baud", 9600, 1000000, _s.ScopeBaud, v => _s.ScopeBaud = (int)v, "F0");
            sec = ui.AddSection(stack, "Oscilloscope — display", group);
            ui.AddSlider(sec, "Volts / division", 0.01, 20, _s.ScopeVoltsPerDiv,
                         v => _s.ScopeVoltsPerDiv = (float)v, "F2");
            for (int ch = 0; ch < ScopeSource.MAX_CHANNELS; ch++)
            {
                int bit = 1 << ch;
                ui.AddToggle(sec, $"Channel {ch + 1}", (_s.ScopeChannelMask & bit) != 0,
                             v => _s.ScopeChannelMask = v ? _s.ScopeChannelMask | bit
                                                          : _s.ScopeChannelMask & ~bit);
            }
            ui.AddSlider(sec, "Trigger channel (-1 = free run)", -1, 3, _s.ScopeTriggerCh,
                         v => _s.ScopeTriggerCh = (int)v, "F0");
            ui.AddSlider(sec, "Trigger level (V)", -20, 20, _s.ScopeTriggerLevel,
                         v => _s.ScopeTriggerLevel = (float)v, "F2");
            ui.AddToggle(sec, "Trigger on rising edge", _s.ScopeTriggerRising,
                         v => _s.ScopeTriggerRising = v);
            ui.AddToggle(sec, "Freeze acquisition", _s.ScopeFrozen, v => _s.ScopeFrozen = v);
            ui.AddToggle(sec, "Show measurements", _s.ScopeMeasurements, v => _s.ScopeMeasurements = v);
            ui.AddSlider(sec, "Synthetic frequency (Hz)", 0.5, 500, _s.SynthFreqHz,
                         v => _s.SynthFreqHz = (float)v, "F1");
            ui.AddLiveInfo(sec, () =>
            {
                var sb = new StringBuilder();
                for (int ch = 0; ch < ScopeSource.MAX_CHANNELS; ch++)
                {
                    if ((_s.ScopeChannelMask & (1 << ch)) == 0) continue;
                    var st = _scopeRenderer.Stats[ch];
                    sb.Append($"CH{ch + 1}  Vpp {st.Vpp:0.###}  RMS {st.Vrms:0.###}  " +
                              $"f {st.FreqHz:0.#} Hz  duty {st.DutyPct:0}%\n");
                }
                return sb.Length == 0 ? "(no channels enabled)" : sb.ToString().TrimEnd();
            });
        }

        private void BuildPcbSection(PanelBuilder ui, StackPanel stack, List<Expander> group)
        {
            var sec = ui.AddSection(stack, "PCB import", group);
            ui.AddInfo(sec, "Point this at a fabrication output FOLDER (Gerbers + drill) or a " +
                            "single file. Meshes: STL / OBJ / PLY / GLB. STEP (.step/.stp) is " +
                            "read directly as an edge wireframe — no conversion needed. " +
                            "See docs/PCB_IMPORT.md.");
            ui.AddTextBox(sec, "Path (folder or file)", _s.PcbPath, v => _s.PcbPath = v.Trim('"', ' '));
            ui.AddButton(sec, "Import / reload", () => _s.PcbImportRequested = true);

            // Toggling this RE-IMPORTS. Tessellation happens at import time, so flipping
            // the flag on its own would change nothing on screen until the next import —
            // which reads as a dead button rather than as a setting that needs applying.
            ui.AddButton(sec, "STEP mode: wireframe <-> tessellated STL", () =>
            {
                _s.PcbTessellate = !_s.PcbTessellate;
                if (_s.PcbPath.Length > 0) _s.PcbImportRequested = true;
            });
            ui.AddLiveInfo(sec, () =>
            {
                if (!_s.PcbTessellate)
                    return "STEP: WIREFRAME — exact edges, planar faces filled, curved "
                         + "faces left empty (no external tool needed)";

                string tool = StepConverter.Discover(_s.PcbTessellator, out string how);
                return tool.Length > 0
                    ? "STEP: TESSELLATED STL — curved surfaces filled and lit, via "
                      + System.IO.Path.GetFileName(tool)
                    : "STEP: TESSELLATED requested, but " + how;
            }, 1.0);
            ui.AddButton(sec, "Clear board", () =>
            {
                _s.PcbPath = "";
                _s.PcbImportRequested = true;
            });
            ui.AddLiveInfo(sec, () => BoardSummary, 1.5);

            // Every file, including the ignored ones. Refreshed twice a second so the
            // IMPORTING line is live rather than a snapshot from before the stall.
            ui.AddInfo(sec, "Files found by the last import — anything the viewer did not " +
                            "pick up will be in the NOT USED list with the reason. A file " +
                            "that appears nowhere was never enumerated at all.");
            ui.AddLiveInfo(sec, ImportInventory, 0.5);

            ui.AddSlider(sec, "Layer spacing", 0.02, 1.5, _s.LayerSpacing, v => _s.LayerSpacing = (float)v, "F2");
            ui.AddSlider(sec, "Track width scale", 0.1, 6, _s.TrackScale, v => _s.TrackScale = (float)v, "F2");
            ui.AddSlider(sec, "Brightness (voxel density)", 0.2, 3.0, _s.PcbBrightness,
                         v => _s.PcbBrightness = (float)v, "F2");
            ui.AddInfo(sec, "The display shows seven colours — red, green, blue, cyan, " +
                            "magenta, yellow, white — and nothing else, so brightness is " +
                            "not a dimension it has. It is DENSITY instead: more voxels in " +
                            "the same area genuinely does look brighter, and a dark colour " +
                            "would just be invisible while costing the same budget. Layers " +
                            "that must share a colour are told apart by pattern (solid, " +
                            "dashed, dotted) rather than by shade.");
            ui.AddSlider(sec, "Isolate layer (-1 = all)", -1, 31, _s.PcbIsolate, v => _s.PcbIsolate = (int)v, "F0");
            ui.AddToggle(sec, "Pads",             _s.PcbPads,        v => _s.PcbPads = v);
            ui.AddToggle(sec, "Copper pours",     _s.PcbRegions,     v => _s.PcbRegions = v);
            ui.AddToggle(sec, "Cross-hatch pours", _s.PcbFillRegions, v => _s.PcbFillRegions = v);
            ui.AddToggle(sec, "Drills",           _s.PcbHoles,       v => _s.PcbHoles = v);
            ui.AddToggle(sec, "Vias",             _s.PcbVias,        v => _s.PcbVias = v);
            ui.AddSlider(sec, "Pour outline density", 0.2, 4.0, _s.PcbPourDensity,
                         v => _s.PcbPourDensity = (float)v, "F2");
            ui.AddSlider(sec, "Hatch density", 0.2, 4.0, _s.PcbHatchDensity,
                         v => _s.PcbHatchDensity = (float)v, "F2");
            ui.AddInfo(sec, "Higher is denser. Hatch only applies with filled pours on. " +
                            "Pours and hatch are usually the biggest voxel consumers on a " +
                            "board, so these two are the first knobs to turn when the " +
                            "budget runs out and the backdrop starts disappearing.");
            ui.AddToggle(sec, "STEP / CAD wireframe", _s.PcbCad,     v => _s.PcbCad = v);
            ui.AddSlider(sec, "CAD brightness", 0.2, 2.0, _s.PcbCadBright,
                         v => _s.PcbCadBright = (float)v, "F2");
            ui.AddSlider(sec, "Tessellation detail (mm)", 0.05, 3.0, _s.PcbTessellateTol,
                         v => _s.PcbTessellateTol = (float)v, "F2");
            ui.AddTextBox(sec, "Tessellator command (blank = auto)", _s.PcbTessellator,
                          v => _s.PcbTessellator = v.Trim('"', ' '));
            ui.AddLiveInfo(sec, () =>
            {
                string tool = StepConverter.Discover(_s.PcbTessellator, out string how);
                return tool.Length > 0
                    ? "tessellator: " + System.IO.Path.GetFileName(tool) + "  (" + how + ")"
                    : how;
            }, 2.0);
            ui.AddInfo(sec, "StepParser fills PLANAR faces without any external tool, but a " +
                            "round part is mostly curved faces — those need a real geometry " +
                            "kernel, so they are tessellated by gmsh or FreeCAD instead of " +
                            "guessed at. Converted once and cached; the exact STEP edges are " +
                            "kept either way. Smaller detail = smoother = more triangles. " +
                            "Re-import after changing these.");
            ui.AddToggle(sec, "CAD flat-shaded surfaces", _s.PcbCadSurfaces,
                         v => _s.PcbCadSurfaces = v);
            ui.AddSlider(sec, "Surface fill density", 0.1, 2.0, _s.PcbCadSurfaceDensity,
                         v => _s.PcbCadSurfaceDensity = (float)v, "F2");
            ui.AddSlider(sec, "CAD Z offset", -3.0, 3.0, _s.PcbCadZOffset,
                         v => _s.PcbCadZOffset = (float)v, "F2");
            ui.AddInfo(sec, "0 seats the 3D model on the topmost layer of the stack, which " +
                            "is where it belongs — the Gerbers are the board, the STEP model " +
                            "is what is mounted on it.");
            ui.AddToggle(sec, "CAD lighting", _s.PcbCadLighting, v => _s.PcbCadLighting = v);
            ui.AddSlider(sec, "CAD ambient", 0, 1.0, _s.PcbCadAmbient,
                         v => _s.PcbCadAmbient = (float)v, "F2");
            ui.AddSlider(sec, "Light X", -1, 1, _s.PcbCadLightX, v => _s.PcbCadLightX = (float)v, "F2");
            ui.AddSlider(sec, "Light Y", -1, 1, _s.PcbCadLightY, v => _s.PcbCadLightY = (float)v, "F2");
            ui.AddSlider(sec, "Light Z", -1, 1, _s.PcbCadLightZ, v => _s.PcbCadLightZ = (float)v, "F2");
            ui.AddInfo(sec, "Edges are shaded by the faces meeting at them, which only " +
                            "planar faces can supply — edges between curved surfaces stay " +
                            "unlit rather than mis-lit. The light is fixed to the BOARD, " +
                            "so shading stays put as the scene rotates. Ambient is a floor: " +
                            "at 0 edges facing across the light go black.");
            ui.AddLiveInfo(sec, () =>
            {
                if (_board.Solids.Count == 0) return "no STEP solids loaded";
                int linked = 0, pts = 0;
                foreach (var s in _board.Solids)
                {
                    if (s.Designator.Length > 0) linked++;
                    pts += s.PointCount;
                }
                int lit = 0, edges = 0;
                foreach (var s in _board.Solids)
                {
                    edges += s.Edges.Count;
                    lit   += s.NormalCount;
                }
                int tris = 0, faces = 0;
                foreach (var s in _board.Solids)
                    foreach (var fc in s.Faces) { faces++; tris += fc.TriCount; }
                return $"{_board.Solids.Count} CAD solid(s), {linked} matched to a designator, "
                     + $"{pts} edge point(s), {lit}/{edges} edge(s) shadeable, "
                     + $"{faces} planar face(s) / {tris} triangle(s)";
            }, 0.5);
            ui.AddSlider(sec, "Via max diameter (mm)", 0.1, 2.0, _s.PcbViaMaxDia,
                         v => _s.PcbViaMaxDia = (float)v, "F2");
            ui.AddSlider(sec, "Via drawn size (voxels)", 1.0, 10.0, _s.PcbViaSize,
                         v => _s.PcbViaSize = (float)v, "F1");
            ui.AddInfo(sec, "Every via draws at this one size whatever its real diameter — " +
                            "a real 0.3 mm via is smaller than a voxel and would be " +
                            "invisible drawn to scale. Max diameter above still uses the " +
                            "TRUE size to decide what counts as a via.");
            ui.AddLiveInfo(sec, () =>
            {
                int vias = _board.ViaCount(_s.PcbViaMaxDia);
                return $"{vias} of {_board.Holes.Count} holes classified as vias "
                     + $"(plated, not slotted, <= {_s.PcbViaMaxDia:0.00} mm)";
            }, 0.5);
            ui.AddToggle(sec, "Mechanical meshes", _s.PcbMeshes,     v => _s.PcbMeshes = v);
            ui.AddToggle(sec, "Components (placement file)", _s.PcbComponents,
                         v => _s.PcbComponents = v);
            ui.AddToggle(sec, "Component designators", _s.PcbComponentLabels,
                         v => _s.PcbComponentLabels = v);
            ui.AddSlider(sec, "Label limit (parts)", 10, 2000, _s.PcbLabelLimit,
                         v => _s.PcbLabelLimit = (int)v, "F0");
            ui.AddToggle(sec, "Design inventory readout", _s.PcbShowDocs, v => _s.PcbShowDocs = v);
            ui.AddToggle(sec, "Measurement cursor", _s.PcbCursor,    v => _s.PcbCursor = v);
            ui.AddSlider(sec, "Cursor X (mm)", -200, 200, _s.PcbCursorX, v => _s.PcbCursorX = (float)v, "F2");
            ui.AddSlider(sec, "Cursor Y (mm)", -200, 200, _s.PcbCursorY, v => _s.PcbCursorY = (float)v, "F2");
            ui.AddSlider(sec, "Mesh point budget", 5000, 300000, _s.MeshPointBudget,
                         v => _s.MeshPointBudget = (int)v, "F0");
            ui.AddLiveInfo(sec, () =>
            {
                if (!_board.HasGeometry) return "(no geometry)";
                var sb = new StringBuilder();
                sb.Append($"Board {_board.WidthMm:0.0} x {_board.HeightMm:0.0} mm\n");
                sb.Append($"Fit scale {_pcb.Scale:0.###} world/mm, stack {_pcb.Spacing:0.###}\n");
                foreach (var l in _board.Layers)
                    sb.Append($"{l.Kind,-13} {l.Name}  segs {l.Segs.Count} pads {l.Pads.Count} " +
                              $"pours {l.Regions.Count}\n");
                foreach (var g in _board.DrillTable())
                    sb.Append($"drill {g.Dia:0.000} mm x {g.Count}\n");
                foreach (var m in _board.Meshes)
                    sb.Append($"mesh {m.Name}: {m.Count} pts\n");
                if (_board.Components.Count > 0)
                    sb.Append($"parts: {_board.Components.Count} " +
                              $"({_board.ComponentsOnSide(false)} top / " +
                              $"{_board.ComponentsOnSide(true)} bottom)\n");
                if (_board.BomLines.Count > 0)
                    sb.Append($"bom rows: {_board.BomLines.Count}\n");
                foreach (var d in _board.Documents)
                    sb.Append($"{d.Kind,-10} {d.Display}" +
                              (d.Pages > 0 ? $"  ({d.Pages} pages)" : "") + "\n");
                if (_board.Drc.Parsed)
                {
                    sb.Append($"drc: {_board.Drc.Violations} violation(s) over " +
                              $"{_board.Drc.Rules} rule(s)\n");
                    foreach (var fail in _board.Drc.Failing) sb.Append("  ! ").Append(fail).Append('\n');
                }
                if (_board.SourceFolders.Count > 0)
                    sb.Append($"folders walked: {_board.SourceFolders.Count}\n");
                return sb.ToString().TrimEnd();
            }, 2.0);
        }

        /// <summary>Every file the last import looked at and what it decided, newest
        /// import only. This is the answer to "did it even find my STEP file?" — a file
        /// that is absent from this list was never enumerated, which is a different
        /// problem from one that was found and rejected.</summary>
        private string ImportInventory()
        {
            string busy = _s.PcbImportStatus;
            if (busy.Length > 0)
                return "IMPORTING: " + busy + "\n(the game thread is busy; large STEP " +
                       "assemblies can take a while)";

            if (_board.ImportLog.Count == 0)
                return "No import has run yet. Set a folder or file and press Import.";

            var sb = new StringBuilder();
            sb.Append($"{_board.ImportLog.Count} file(s) examined in {_board.ImportMs} ms\n");

            // Used files first, then everything skipped — the skipped list is the one
            // people actually need when something is missing, so it must not be buried
            // or truncated away.
            AppendGroup(sb, "USED", true);
            AppendGroup(sb, "NOT USED", false);
            return sb.ToString();
        }

        private static string SizeText(long bytes)
            => bytes >= 1024L * 1024L ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
             : bytes >= 1024L         ? $"{bytes / 1024.0:0.#} kB"
                                      : $"{bytes} B";

        private void AppendGroup(StringBuilder sb, string title, bool used)
        {
            int n = 0;
            foreach (var f in _board.ImportLog) if (f.Used == used) n++;
            if (n == 0) return;

            sb.Append('\n').Append(title).Append(" (").Append(n).Append(")\n");
            string lastFolder = "\u0001";
            foreach (var f in _board.ImportLog)
            {
                if (f.Used != used) continue;
                if (f.Folder != lastFolder)
                {
                    lastFolder = f.Folder;
                    sb.Append("  ").Append(f.Folder.Length > 0 ? f.Folder : ".").Append("/\n");
                }
                sb.Append("    ").Append(f.Role.PadRight(10))
                  .Append(f.Name);
                if (f.Bytes > 0) sb.Append("  ").Append(SizeText(f.Bytes));
                if (f.Ms >= 50)  sb.Append("  ").Append(f.Ms).Append(" ms");
                if (f.Detail.Length > 0) sb.Append("\n                ").Append(f.Detail);
                sb.Append('\n');
            }
        }

        private void BuildRenderSection(PanelBuilder ui, StackPanel stack, List<Expander> group)
        {
            var sec = ui.AddSection(stack, "Render budget & text", group);
            ui.AddInfo(sec, "Max voxels is a hard per-frame ceiling for EVERYTHING drawn, " +
                            "text included. Draw order is priority order: the backdrop is " +
                            "dropped first, then labels, then geometry.");
            ui.AddSlider(sec, "Max voxels / frame", 5000, VoxelBatch.MAX_CAPACITY, _s.MaxVoxels,
                         v => _s.MaxVoxels = (int)v, "F0");
            ui.AddSlider(sec, "Min voxels per glyph cell", 1.0, 4.0, _s.MinTextCellVoxels,
                         v => _s.MinTextCellVoxels = (float)v, "F1");
            ui.AddLiveInfo(sec, () =>
            {
                float cell = _textSize * 0.18f / MathF.Max(1e-6f, _spacing);
                return $"text size {_textSize:0.000} -> {cell:0.0} voxels per glyph cell"
                     + (_textFloored ? "   (raised to stay legible)" : "");
            }, 0.5);
            ui.AddInfo(sec, "Glyphs are 5x7 cells, so legibility is set by how many voxels " +
                            "ONE CELL covers, not by the size in world units — which is why " +
                            "the floor is in voxels and follows the display and the density. " +
                            "Below about one voxel per cell adjacent cells share voxels and " +
                            "the character becomes a blob. A smaller glyph grid would let " +
                            "text shrink further but 3x5 characters are harder to read than " +
                            "small 5x7 ones, so the 5x7 Bold font plus this floor is the " +
                            "better trade on a low-resolution display.");
            ui.AddToggle(sec, "Reduce voxels while moving if slow", _s.AdaptiveBudget,
                         v => _s.AdaptiveBudget = v);
            ui.AddSlider(sec, "Throttle below VPS", 2, 30, _s.AdaptiveLowVps,
                         v => _s.AdaptiveLowVps = (float)v, "F1");
            ui.AddSlider(sec, "Recover above VPS", 2, 30, _s.AdaptiveGoodVps,
                         v => _s.AdaptiveGoodVps = (float)v, "F1");
            ui.AddSlider(sec, "Throttle floor (fraction)", 0.05, 1.0, _s.AdaptiveFloor,
                         v => _s.AdaptiveFloor = (float)v, "F2");
            ui.AddInfo(sec, "The budget is only cut while the view is MOVING — a still " +
                            "frame that renders slowly is one you are studying, and " +
                            "silently dropping half of it would be the wrong answer. " +
                            "Recovery is slower than the cut so the budget does not pump " +
                            "up and down across the threshold.");
            ui.AddLiveInfo(sec, () =>
                $"budget scale {_budgetScale * 100f:0}%  ->  {(int)(_s.MaxVoxels * _budgetScale):N0} vox"
                + $"   ({(_viewMoving ? "moving" : "still")}, {_engine?.LiveVps ?? 0f:0.0} VPS)", 0.3);
            ui.AddSlider(sec, "Voxel density (shared with Simulator tab)", 0.25, 3.0,
                         _engine.VoxelDensity, v => _engine.VoxelDensity = (float)v, "F2");
            ui.AddSlider(sec, "Text size", 0.05, 0.6, _s.TextSize, v => _s.TextSize = (float)v, "F2");
            ui.AddSlider(sec, "Text weight", 0.5, 3.0, _s.TextWeight, v => _s.TextWeight = (float)v, "F2");
            ui.AddButton(sec, "Cycle font (Classic / Blocky / Bold)",
                         () => _s.FontIndex = (_s.FontIndex + 1) % 3);
            ui.AddToggle(sec, "Labels & readouts", _s.ShowLabels,   v => _s.ShowLabels = v);
            ui.AddToggle(sec, "Title / voxel readout", _s.ShowHudPanel, v => _s.ShowHudPanel = v);
            ui.AddToggle(sec, "Backdrop (grid + rings)", _s.ShowBackdrop, v => _s.ShowBackdrop = v);
            ui.AddSlider(sec, "Readout plane Y", -1.0, 1.0, _s.PlaneY, v => _s.PlaneY = (float)v, "F2");
            ui.AddLiveInfo(sec, () =>
                $"Drawn {_lastVoxels} / {_s.MaxVoxels} voxels" +
                (_lastDropped > 0 ? $", {_lastDropped} dropped" : "") +
                $"\nVolume radius {_radius:0.00}, half-height {_zHalf:0.00}, step {_spacing:0.0000}");
        }

        private void BuildCameraSection(PanelBuilder ui, StackPanel stack, List<Expander> group)
        {
            var sec = ui.AddSection(stack, "Camera & SpaceNavigator", group);
            ui.AddInfo(sec, "Left-drag the preview to orbit the simulator camera; " +
                            "Ctrl+wheel zooms it. WASD orbits the scene, Q/E rolls, " +
                            "comma/period zooms, R resets.");
            ui.AddToggle(sec, "SpaceNavigator enabled", _s.NavEnabled, v => _s.NavEnabled = v);
            ui.AddInfo(sec, "ONE sensitivity for all three translation axes and ONE for all " +
                            "three rotation axes — so the puck feels the same in X, Y and Z, and " +
                            "rotation can be tuned independently of translation.");
            ui.AddSlider(sec, "Translation sensitivity (units/s)", 0.1, 40, _s.NavPanRate,
                         v => _s.NavPanRate = (float)v, "F2");
            ui.AddSlider(sec, "Rotation sensitivity (rad/s)", 0.1, 20, _s.NavRotRate,
                         v => _s.NavRotRate = (float)v, "F2");
            ui.AddSlider(sec, "Button zoom rate", 0.1, 10, _s.NavZoomRate,
                         v => _s.NavZoomRate = (float)v, "F2");
            ui.AddSlider(sec, "Translation full scale (raw counts)", 1, 1000, _s.NavFullScaleTrans,
                         v => _s.NavFullScaleTrans = (float)v, "F0");
            ui.AddSlider(sec, "Rotation full scale (raw counts)", 1, 1000, _s.NavFullScaleRot,
                         v => _s.NavFullScaleRot = (float)v, "F0");
            ui.AddSlider(sec, "Nav dead-zone (fraction)", 0, 0.5, _s.NavDeadzone,
                         v => _s.NavDeadzone = (float)v, "F3");
            ui.AddInfo(sec, "The driver reports RAW counts, not -1..1, and NOT the same range for " +
                            "translation as for rotation. The two full-scale values convert them, " +
                            "which is what puts all six axes on a common footing — set those FIRST " +
                            "(deflect hard in every direction, then Calibrate) and only then trim " +
                            "the two sensitivities. Dead-zone stops the scene drifting at rest.");
            ui.AddButton(sec, "Calibrate both full scales from observed peaks", () =>
            {
                if (_navPeakTrans > 1f) _s.NavFullScaleTrans = _navPeakTrans;
                if (_navPeakRot   > 1f) _s.NavFullScaleRot   = _navPeakRot;
            });
            ui.AddButton(sec, "Reset observed peaks", () => { _navPeakTrans = 0f; _navPeakRot = 0f; });
            ui.AddButton(sec, "Reset scene camera", () => _cam.Reset());
            ui.AddInfo(sec, "Lock an axis to stop it rotating in normal camera mode — " +
                            "useful for keeping a board flat while turning it. The axes " +
                            "follow the LOCAL/GLOBAL choice below. Inspection mode locks " +
                            "all three on its own, because the probe would otherwise have " +
                            "the board slide out from under it.");
            ui.AddToggle(sec, "Lock X rotation", _s.LockRotX, v => _s.LockRotX = v);
            ui.AddToggle(sec, "Lock Y rotation", _s.LockRotY, v => _s.LockRotY = v);
            ui.AddToggle(sec, "Lock Z rotation", _s.LockRotZ, v => _s.LockRotZ = v);
            ui.AddLiveInfo(sec, () =>
            {
                if (_s.InspectMode) return "rotation LOCKED (inspection mode)";
                bool any = _s.LockRotX || _s.LockRotY || _s.LockRotZ;
                if (!any) return "all three axes free";
                return "locked: " + (_s.LockRotX ? "X " : "") + (_s.LockRotY ? "Y " : "")
                                  + (_s.LockRotZ ? "Z" : "");
            }, 0.3);

            ui.AddButton(sec, "Rotate about: LOCAL / GLOBAL axes", () =>
            {
                _s.NavLocalAxes = !_s.NavLocalAxes;
                _cam.LocalAxes  = _s.NavLocalAxes;
            });
            ui.AddLiveInfo(sec, () =>
                "rotating about " + (_s.NavLocalAxes
                    ? "the MODEL's local axes (turns with the board)"
                    : "the DISPLAY's global axes (fixed to the volume)"), 0.3);

            ui.AddInfo(sec, "Press BOTH puck buttons to switch between Camera mode and " +
                            "Inspection mode. In inspection mode the puck moves a probe " +
                            "through the volume, everything dims except what the probe is " +
                            "over, and its details appear top-left.");
            ui.AddButton(sec, "Cycle Camera / Signal / Component inspector", ToggleInspect);
            ui.AddSlider(sec, "Probe speed", 0.2, 12, _s.InspectRate,
                         v => _s.InspectRate = (float)v, "F2");
            ui.AddSlider(sec, "Dim for unhovered (1 = no dimming)", 0.1, 1.0, _s.InspectDim,
                         v => _s.InspectDim = (float)v, "F2");
            ui.AddSlider(sec, "Probe snap reach", 0.1, 2.0, _s.InspectSnap,
                         v => _s.InspectSnap = (float)v, "F2");
            ui.AddInfo(sec, "The probe reaches for the nearest TRACE or PART and draws a " +
                            "line to it — vias and layers are not selectable, since a " +
                            "layer is always under the probe and a via comes with its net " +
                            "anyway. Selecting a trace lights the whole net it is joined " +
                            "to, through its vias, pulsing cyan.");
            ui.AddLiveInfo(sec, () =>
            {
                var nets = _board.Nets;
                if (nets == null || nets.NetCount == 0) return "no copper connectivity built";
                return $"{nets.NetCount} net(s) derived from copper geometry"
                     + (_pcb.HoverNet >= 0
                        ? $"   selected: {nets.Name(_pcb.HoverNet)} "
                          + $"({nets.Size(_pcb.HoverNet)} object(s))"
                        : "");
            }, 0.3);
            ui.AddLiveInfo(sec, () => _s.InspectMode
                ? $"{(EDesInspect)_s.InspectStage} inspector   probe "
                  + $"{_s.InspectX:0.00}, {_s.InspectY:0.00}, {_s.InspectZ:0.00}"
                : "CAMERA mode", 0.3);
            ui.AddLiveInfo(sec, () =>
                $"yaw {_cam.Yaw:0.00}  pitch {_cam.Pitch:0.00}  roll {_cam.Roll:0.00}  zoom {_cam.Zoom:0.00}");

        }

        /// <summary>The control reference the shell pins to the bottom of the window.
        ///
        /// Mode-specific rather than the whole list at once: showing every mode's keys
        /// means the four fifths that do nothing right now are competing with the fifth
        /// that does. GLOBAL and CAMERA always apply, so they always show.</summary>
        public string ControlsHelp
        {
            get
            {
                string modeKeys = (EDesMode)_s.Mode switch
                {
                    EDesMode.Education =>
                        "1-4 preset   left/right select resistor   up/down +-10%\n" +
                        "-/= source volts   P pause flow",
                    EDesMode.Scope =>
                        "1-4 channels   up/down V/div   left/right trigger level\n" +
                        "T trigger ch   E edge   P freeze",
                    _ =>
                        "arrows move cursor (Shift = 0.1mm)   C cursor\n" +
                        "H drills   P pads   F hatch   N/M isolate layer",
                };

                return "GLOBAL\n" +
                       "Tab mode   L labels   G backdrop   R reset camera   Esc quit\n" +
                       "\nCAMERA\n" +
                       "WASD orbit   Q/E roll   , / . zoom   left-drag preview\n" +
                       "SpaceNav 6DOF   both puck buttons = inspection mode\n" +
                       "\n" + ((EDesMode)_s.Mode).ToString().ToUpperInvariant() + "\n" +
                       modeKeys;
            }
        }
    }
}
