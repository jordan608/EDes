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
            bool ok = PcbImporter.Import(path, _board, Math.Max(1000, _s.MeshPointBudget));
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

            if (IsDown(VX_KEYS.KB_Q)) _cam.Roll -= KeyRot * dt;
            if (IsDown(VX_KEYS.KB_E) && (EDesMode)_s.Mode != EDesMode.Scope) _cam.Roll += KeyRot * dt;

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
                var host = new NavState(true, 1,
                                        _navHost.dx, _navHost.dy, _navHost.dz,
                                        _navHost.ax, _navHost.ay, _navHost.az,
                                        0f, 0f, 0f, _navHost.but);
                _cam.ApplyNav(host, _lastDt, _s.NavPanRate, _s.NavRotRate, _s.NavZoomRate);
            }
            else if (_nav.Present)
            {
                _navSource = "LedWin GetNavAxisValue";
                _cam.ApplyNav(_nav, _lastDt, _s.NavPanRate, _s.NavRotRate, _s.NavZoomRate);
            }
            else
            {
                _navSource = "not detected";
            }
        }

        /// <summary>Everything known about the puck, for the settings panel and the
        /// in-volume readout. This is a diagnostic, so it shows the RAW numbers from
        /// both paths rather than a tidy summary.</summary>
        public string NavDiagnostics()
        {
            var sb = new StringBuilder();
            sb.Append("driving: ").Append(_navSource).Append('\n');
            sb.Append("LedWin  devices=").Append(_nav.Devices)
              .Append("  present=").Append(_nav.Present ? "yes" : "no").Append('\n');
            sb.Append($"  dir  X {_nav.Dx,7:0.000}  Y {_nav.Dy,7:0.000}  Z {_nav.Dz,7:0.000}\n");
            sb.Append($"  ang  P {_nav.Ax,7:0.000}  Y {_nav.Ay,7:0.000}  R {_nav.Az,7:0.000}\n");
            sb.Append($"  sum  X {_nav.Sx,7:0.000}  Y {_nav.Sy,7:0.000}  Z {_nav.Sz,7:0.000}\n");
            sb.Append("  buttons ").Append(_nav.Buttons)
              .Append("  L=").Append((_nav.Buttons & 1) != 0 ? "DOWN" : "up")
              .Append("  R=").Append((_nav.Buttons & 2) != 0 ? "DOWN" : "up").Append('\n');
            sb.Append("LedHost vxl_nav_read rc=").Append(_navHostRc).Append('\n');
            sb.Append($"  d    X {_navHost.dx,7:0.000}  Y {_navHost.dy,7:0.000}  Z {_navHost.dz,7:0.000}\n");
            sb.Append($"  a    X {_navHost.ax,7:0.000}  Y {_navHost.ay,7:0.000}  Z {_navHost.az,7:0.000}\n");
            sb.Append("  buttons ").Append(_navHost.but)
              .Append("  L=").Append((_navHost.but & 1) != 0 ? "DOWN" : "up")
              .Append("  R=").Append((_navHost.but & 2) != 0 ? "DOWN" : "up");
            return sb.ToString();
        }

        private static bool Down(VX_KEYS k)   => NativeInput.OnDown(k) == 1;
        private static bool IsDown(VX_KEYS k) => NativeInput.IsDown(k) == 1;

        // ── Draw ──────────────────────────────────────────────────────────────

        public void Draw(LedHostCS ledHost, ref vxl_state_t vs)
        {
            ReadBounds(ledHost, ref vs);
            ApplyNavigator(ledHost);

            _batch.BeginFrame(_s.MaxVoxels, _radius, _zHalf, _spacing);

            // Reserve vertical space BEFORE drawing anything: two header rows at the
            // top, and a footer sized to the rows this mode will actually need. Blocks
            // then draw into their own band and cannot collide. See Sim/Layout.cs.
            int headerRows = _s.ShowHudPanel ? 2 : 0;
            _layout = new FrameLayout(_zHalf, _step, headerRows, FooterRowsForMode());

            switch ((EDesMode)_s.Mode)
            {
                case EDesMode.Education: DrawEducation(); break;
                case EDesMode.Scope:     DrawScopeMode(); break;
                case EDesMode.Pcb:       DrawPcbMode();   break;
            }

            if (_s.ShowNavDiag)   DrawNavReadout();
            if (_s.ShowHudPanel)  DrawHudPanel();
            if (_s.ShowBackdrop)  DrawBackdrop();     // last: decoration is dropped first

            _batch.Flush(ledHost, ref vs);

            _lastVoxels  = _batch.Count;
            _lastDropped = _batch.Dropped;
            _engine.LiveVoxelCount = _lastVoxels;
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
            _textSize = MathF.Max(0.04f, _s.TextSize * (radius / 4f));
            _step     = Hud.LineStep(_textSize);
        }

        /// <summary>Footer rows this mode needs — reserved before anything is drawn so
        /// a readout can never land on top of the content above it.</summary>
        private int FooterRowsForMode()
        {
            if (!_s.ShowLabels) return 0;
            switch ((EDesMode)_s.Mode)
            {
                case EDesMode.Education:
                    return 3;                                    // law + totals + V=IR
                case EDesMode.Scope:
                    return 1 + (_s.ScopeMeasurements ? EnabledChannelCount() : 0);
                default:
                    return 2 + VisibleLayerRows()
                             + ((_s.PcbShowDocs && _board.Documents.Count > 0) ? 1 : 0)
                             + (_s.PcbCursor ? 1 : 0);
            }
        }

        private int EnabledChannelCount()
        {
            int n = 0;
            for (int ch = 0; ch < ScopeSource.MAX_CHANNELS; ch++)
                if ((_s.ScopeChannelMask & (1 << ch)) != 0 && ch < _scope.ChannelCount) n++;
            return Math.Max(1, n);
        }

        private int VisibleLayerRows()
        {
            int n = 0;
            for (int i = 0; i < _board.Layers.Count && n < 6; i++)
            {
                if (!_board.Layers[i].Visible) continue;
                if (_s.PcbIsolate >= 0 && _s.PcbIsolate != i) continue;
                n++;
            }
            return n;
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
            var f = _layout.Footer();
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
            var f = _layout.Footer();
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
                ShowMeshes   = _s.PcbMeshes,
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

            if (!_s.ShowLabels) return;

            var f = _layout.Footer();

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

            // Layer legend: which plane in the stack is which file.
            int shown = 0;
            for (int i = 0; i < _board.Layers.Count && shown < 6; i++)
            {
                var l = _board.Layers[i];
                if (!l.Visible) continue;
                if (_s.PcbIsolate >= 0 && _s.PcbIsolate != i) continue;
                _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize * 0.85f, l.Colour,
                                 l.Kind.ToString().ToUpperInvariant() + "  " + l.ObjectCount);
                shown++;
            }

            if (_s.PcbShowDocs && _board.Documents.Count > 0)
                _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize * 0.85f, Palette.TextDim,
                                 DesignInventoryLine());

            if (_s.PcbCursor)
                _hud.TextCentred(0f, _s.PlaneY, f.Row(), _textSize, Palette.TextHilite,
                    "CURSOR X " + _s.PcbCursorX.ToString("0.00") +
                    "  Y " + _s.PcbCursorY.ToString("0.00") + " MM");
        }

        /// <summary>One line summarising what the design package contains, so the
        /// display says whether the folder is complete, not just what is drawable.</summary>
        private string DesignInventoryLine()
        {
            int sch = 0, dwg = 0, net = 0, cad = 0, bom = 0, sheets = 0;
            foreach (var d in _board.Documents)
            {
                switch (d.Kind)
                {
                    case DocKind.Schematic: sch++; sheets += d.Pages; break;
                    case DocKind.Drawing:   dwg++; break;
                    case DocKind.Netlist:   net++; break;
                    case DocKind.Cad3D:     cad++; break;
                    case DocKind.Bom:       bom++; break;
                }
            }

            var sb = new StringBuilder();
            if (sch > 0) sb.Append("SCH ").Append(sch)
                           .Append(sheets > 0 ? "/" + sheets + "SH" : "").Append("  ");
            if (dwg > 0) sb.Append("DWG ").Append(dwg).Append("  ");
            if (cad > 0) sb.Append("3D ").Append(cad).Append("  ");
            if (net > 0) sb.Append("NET ").Append(net).Append("  ");
            if (bom > 0) sb.Append("BOM ").Append(_board.BomLines.Count).Append("  ");
            if (_board.Drc.Parsed)
                sb.Append("DRC ").Append(_board.Drc.Violations)
                  .Append('/').Append(_board.Drc.Rules).Append("  ");
            if (sb.Length == 0) sb.Append(_board.Documents.Count).Append(" DOCS");
            return sb.ToString().TrimEnd();
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
            var   st   = new TextStack(_layout.ContentTopZ, Hud.LineStep(size));

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

        public Control BuildSettingsPanel(PanelBuilder ui)
        {
            var stack = ui.Root();
            var group = new List<Expander>();

            BuildModeSection(ui, stack, group);
            BuildCircuitSection(ui, stack, group);
            BuildScopeSection(ui, stack, group);
            BuildPcbSection(ui, stack, group);
            BuildRenderSection(ui, stack, group);
            BuildCameraSection(ui, stack, group);
            BuildControlsSection(ui, stack, group);

            return ui.Wrap(stack);
        }

        private void BuildModeSection(PanelBuilder ui, StackPanel stack, List<Expander> group)
        {
            var sec = ui.AddSection(stack, "Mode", group, expanded: true);
            ui.AddInfo(sec, "Tab cycles modes in the volume.");
            ui.AddButton(sec, "Education — circuits + Ohm's law", () => _s.Mode = (int)EDesMode.Education);
            ui.AddButton(sec, "Oscilloscope — full-screen scope", () => _s.Mode = (int)EDesMode.Scope);
            ui.AddButton(sec, "PCB — board / layer stack",        () => _s.Mode = (int)EDesMode.Pcb);
            ui.AddLiveInfo(sec, () => "Active: " + (EDesMode)_s.Mode);
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
                            "single file. Meshes: STL / OBJ / PLY / GLB. STEP must be converted " +
                            "to STL first — see docs/PCB_IMPORT.md.");
            ui.AddTextBox(sec, "Path (folder or file)", _s.PcbPath, v => _s.PcbPath = v.Trim('"', ' '));
            ui.AddButton(sec, "Import / reload", () => _s.PcbImportRequested = true);
            ui.AddButton(sec, "Clear board", () =>
            {
                _s.PcbPath = "";
                _s.PcbImportRequested = true;
            });
            ui.AddLiveInfo(sec, () => BoardSummary, 1.5);

            ui.AddSlider(sec, "Layer spacing", 0.02, 1.5, _s.LayerSpacing, v => _s.LayerSpacing = (float)v, "F2");
            ui.AddSlider(sec, "Track width scale", 0.1, 6, _s.TrackScale, v => _s.TrackScale = (float)v, "F2");
            ui.AddSlider(sec, "Brightness", 0.2, 2.0, _s.PcbBrightness, v => _s.PcbBrightness = (float)v, "F2");
            ui.AddSlider(sec, "Isolate layer (-1 = all)", -1, 31, _s.PcbIsolate, v => _s.PcbIsolate = (int)v, "F0");
            ui.AddToggle(sec, "Pads",             _s.PcbPads,        v => _s.PcbPads = v);
            ui.AddToggle(sec, "Copper pours",     _s.PcbRegions,     v => _s.PcbRegions = v);
            ui.AddToggle(sec, "Hatch pours",      _s.PcbFillRegions, v => _s.PcbFillRegions = v);
            ui.AddToggle(sec, "Drills",           _s.PcbHoles,       v => _s.PcbHoles = v);
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

        private void BuildRenderSection(PanelBuilder ui, StackPanel stack, List<Expander> group)
        {
            var sec = ui.AddSection(stack, "Render budget & text", group);
            ui.AddInfo(sec, "Max voxels is a hard per-frame ceiling for EVERYTHING drawn, " +
                            "text included. Draw order is priority order: the backdrop is " +
                            "dropped first, then labels, then geometry.");
            ui.AddSlider(sec, "Max voxels / frame", 5000, VoxelBatch.MAX_CAPACITY, _s.MaxVoxels,
                         v => _s.MaxVoxels = (int)v, "F0");
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
            ui.AddSlider(sec, "Nav pan rate",  0.1, 10, _s.NavPanRate,  v => _s.NavPanRate  = (float)v, "F2");
            ui.AddSlider(sec, "Nav rotate rate", 0.1, 6, _s.NavRotRate, v => _s.NavRotRate  = (float)v, "F2");
            ui.AddSlider(sec, "Nav zoom rate", 0.1, 5, _s.NavZoomRate,  v => _s.NavZoomRate = (float)v, "F2");
            ui.AddButton(sec, "Reset scene camera", () => _cam.Reset());
            ui.AddLiveInfo(sec, () =>
                $"yaw {_cam.Yaw:0.00}  pitch {_cam.Pitch:0.00}  roll {_cam.Roll:0.00}  zoom {_cam.Zoom:0.00}");

            sec = ui.AddSection(stack, "SpaceNavigator diagnostics", group);
            ui.AddInfo(sec, "Live raw values from BOTH read paths, refreshed 5x/second. " +
                            "Press V in the volume for the same readout on the display. " +
                            "Move the puck: whichever block changes is the one that works.");
            ui.AddToggle(sec, "Readout in the volume (V)", _s.ShowNavDiag,
                         v => _s.ShowNavDiag = v);
            ui.AddLiveInfo(sec, NavDiagnostics, 0.2);
        }

        private void BuildControlsSection(PanelBuilder ui, StackPanel stack, List<Expander> group)
        {
            var sec = ui.AddSection(stack, "Controls", group);
            ui.AddInfo(sec,
                "GLOBAL   Tab mode - L labels - G backdrop - R reset camera - Esc quit\n" +
                "CAMERA   WASD orbit - Q/E roll - , / . zoom - SpaceNav 6DOF - left-drag preview\n" +
                "CIRCUIT  1-4 preset - left/right select resistor - up/down +-10% - -/= source volts - P pause\n" +
                "SCOPE    1-4 channels - up/down V/div - left/right trigger level - T trigger ch - E edge - P freeze\n" +
                "PCB      arrows move cursor (Shift = 0.1mm) - C cursor - H drills - P pads - F hatch - N/M isolate layer");
        }
    }
}
