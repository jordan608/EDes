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

            _scope.Configure(_s.ScopeUsb, _s.ScopePort, _s.ScopeBaud);
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

            // The preview window's left-drag drives the SIMULATOR camera directly
            // (GameSettings.EmuHAng/EmuVAng, read by Rend2D) — that is "walk around
            // the volume". This scene camera is the other half: it moves the content.
            if (_s.NavEnabled)
                _cam.ApplyNav(input.Nav, dt, _s.NavPanRate, _s.NavRotRate, _s.NavZoomRate);
        }

        private static bool Down(VX_KEYS k)   => NativeInput.OnDown(k) == 1;
        private static bool IsDown(VX_KEYS k) => NativeInput.IsDown(k) == 1;

        // ── Draw ──────────────────────────────────────────────────────────────

        public void Draw(LedHostCS ledHost, ref vxl_state_t vs)
        {
            ReadBounds(ledHost, ref vs);

            _batch.BeginFrame(_s.MaxVoxels, _radius, _zHalf, _spacing);

            switch ((EDesMode)_s.Mode)
            {
                case EDesMode.Education: DrawEducation(); break;
                case EDesMode.Scope:     DrawScopeMode(); break;
                case EDesMode.Pcb:       DrawPcbMode();   break;
            }

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
            float aspect = ledHost.GetAspectRatioX(ref vs);
            float radius = aspect > 0.1f ? aspect : 4f;
            if (vs.boundr > 0.1f) radius = MathF.Min(radius, vs.boundr);

            float zHalf = vs.boundz > 0.1f
                        ? vs.boundz
                        : MathF.Min(ledHost.GetAspectRatioZ(ref vs), radius * 0.5f);

            // 6% margin so nothing sits exactly on the wall of the volume.
            _radius = radius * 0.94f;
            _zHalf  = MathF.Max(0.2f, zHalf * 0.94f);

            // One point per real voxel at density 1.0; the density slider scales it.
            float pitch   = vs.xsiz > 8 ? 2f * radius / vs.xsiz : 0.03f;
            float density = Math.Clamp(_engine.VoxelDensity, 0.25f, 3f);
            _spacing = MathF.Max(0.004f, pitch / density);
        }

        // ── Mode: education ───────────────────────────────────────────────────
        private void DrawEducation()
        {
            _scene.RecomputeIfDirty(_radius, _zHalf);
            _circuit.Draw(_batch, _hud, _cam, _scene, _anim, _s.ShowLabels, _s.TextSize, _zHalf);

            // Ohm's-law teaching block + circuit totals, on the readout plane.
            if (_s.ShowLabels)
            {
                float step = Hud.LineStep(_s.TextSize);
                float z    = -_zHalf * 0.04f;
                var p      = _scene.Active;

                _hud.TextCentred(0f, _s.PlaneY, z, _s.TextSize, Palette.TextHilite, p.Law);
                z += step;
                _hud.TextCentred(0f, _s.PlaneY, z, _s.TextSize, Palette.Text,
                    "RT " + Hud.Eng(_scene.TotalResistance, "R") +
                    "   IT " + Hud.Eng(_scene.TotalCurrent, "A") +
                    "   PT " + Hud.Eng(_scene.TotalPower, "W"));
                z += step;
                _hud.TextCentred(0f, _s.PlaneY, z, _s.TextSize, Palette.TextDim,
                    "V " + Hud.Eng(_scene.SourceVolts, "V") + "  =  I X R");
            }

            // The scope keeps a strip at the bottom of the volume in this mode, so a
            // measured signal can be compared against the circuit above it.
            DrawScopePanel(_zHalf * 0.34f, _zHalf * 0.72f, _radius * 0.72f);
        }

        // ── Mode: scope ───────────────────────────────────────────────────────
        private void DrawScopeMode()
            => DrawScopePanel(-_zHalf * 0.45f, _zHalf * 0.45f, _radius * 0.82f);

        private void DrawScopePanel(float zTop, float zBottom, float halfWidth)
        {
            var panel = new ScopePanel
            {
                Y       = _s.PlaneY,
                X0      = -halfWidth,
                X1      =  halfWidth,
                ZTop    = zTop,
                ZBottom = zBottom,
            };

            _scopeRenderer.NoteColumns((int)(panel.Width / _spacing));
            _scopeRenderer.Draw(_batch, _hud, _scope, panel,
                                _s.ScopeVoltsPerDiv, (uint)_s.ScopeChannelMask,
                                _s.ScopeTriggerCh, _s.ScopeTriggerLevel, _s.ScopeTriggerRising,
                                _s.ScopeMeasurements && _s.ShowLabels, _s.TextSize);
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
            };

            _pcb.Draw(_batch, _cam, _board, opt, _radius, _zHalf);

            if (!_s.ShowLabels) return;

            float step = Hud.LineStep(_s.TextSize);
            float z    = _zHalf * 0.55f;

            if (!_board.HasGeometry)
            {
                _hud.TextCentred(0f, _s.PlaneY, z, _s.TextSize, Palette.Warning,
                                 "NO BOARD LOADED - SET A PATH IN THE PCB TAB");
                return;
            }

            _hud.TextCentred(0f, _s.PlaneY, z, _s.TextSize, Palette.Text,
                _board.WidthMm.ToString("0.0") + " X " + _board.HeightMm.ToString("0.0") + " MM   " +
                _board.CopperLayerCount() + " CU   " + _board.Holes.Count + " HOLES");
            z += step;

            _hud.TextCentred(0f, _s.PlaneY, z, _s.TextSize, Palette.TextDim,
                "MIN TRACK " + _board.MinTrackWidth().ToString("0.000") + "MM   " +
                "MIN DRILL " + _board.MinDrill().ToString("0.000") + "MM");
            z += step;

            // Layer legend: which plane in the stack is which file.
            int shown = 0;
            for (int i = 0; i < _board.Layers.Count && shown < 6; i++)
            {
                var l = _board.Layers[i];
                if (!l.Visible) continue;
                if (_s.PcbIsolate >= 0 && _s.PcbIsolate != i) continue;
                _hud.TextCentred(0f, _s.PlaneY, z, _s.TextSize * 0.85f, l.Colour,
                                 l.Kind.ToString().ToUpperInvariant() + "  " + l.ObjectCount);
                z += step * 0.85f;
                shown++;
            }

            if (_s.PcbCursor)
                _hud.TextCentred(0f, _s.PlaneY, z, _s.TextSize, Palette.TextHilite,
                    "CURSOR X " + _s.PcbCursorX.ToString("0.00") +
                    "  Y " + _s.PcbCursorY.ToString("0.00") + " MM");
        }

        // ── Shared HUD ────────────────────────────────────────────────────────
        private void DrawHudPanel()
        {
            float size = _s.TextSize;
            float step = Hud.LineStep(size);
            float top  = -_zHalf * 0.92f;

            string mode = ((EDesMode)_s.Mode) switch
            {
                EDesMode.Education => "EDUCATION  " + _scene.Active.Name,
                EDesMode.Scope     => "OSCILLOSCOPE",
                _                  => "PCB  " + _board.SourceName.ToUpperInvariant(),
            };
            _hud.TextCentred(0f, _s.PlaneY, top, size, Palette.Text, mode);

            // Voxel budget readout — the single most useful number when tuning a
            // scene for this display, so it is on the glass, not just in the UI.
            string budget = _lastVoxels + " VOX";
            if (_lastDropped > 0) budget += "  +" + _lastDropped + " DROPPED";
            _hud.TextCentred(0f, _s.PlaneY, top + step, size * 0.8f,
                             _lastDropped > 0 ? Palette.Warning : Palette.TextDim, budget);
        }

        /// <summary>Grid floor + three orthogonal rings: cheap orientation cues that
        /// stop the scene reading as objects floating in a void.</summary>
        private void DrawBackdrop()
        {
            float r = _radius * 0.92f;
            float floorZ = _zHalf * 0.90f;
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
            var sec = ui.AddSection(stack, "Oscilloscope", group);
            ui.AddInfo(sec, "USB: any device streaming ASCII samples, one line per sample set " +
                            "(CSV = one channel per column). Off = synthetic signal.");
            ui.AddToggle(sec, "Read from USB serial", _s.ScopeUsb, v => _s.ScopeUsb = v);
            ui.AddTextBox(sec, "Serial port", _s.ScopePort, v => _s.ScopePort = v.Trim());
            ui.AddButton(sec, "Next detected port", () =>
            {
                var ports = ScopeSource.AvailablePorts();
                if (ports.Length == 0) return;
                int at = Array.IndexOf(ports, _s.ScopePort);
                _s.ScopePort = ports[(at + 1) % ports.Length];
            });
            ui.AddLiveInfo(sec, () =>
            {
                var ports = ScopeSource.AvailablePorts();
                return "Ports: " + (ports.Length == 0 ? "(none)" : string.Join(", ", ports)) +
                       "\nStatus: " + _scope.Status +
                       $"\n{_scope.ChannelCount} ch @ {_scope.SampleRateHz:0} Hz";
            });

            ui.AddSlider(sec, "Baud", 9600, 1000000, _s.ScopeBaud, v => _s.ScopeBaud = (int)v, "F0");
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
