// ═══════════════════════════════════════════════════════════════════════════
//  MainWindow.axaml.cs — Settings/preview window code-behind
//
//  Responsibilities:
//    • Build nav buttons and settings panels programmatically
//    • Receive the Rend2D preview buffer and display it as a WriteableBitmap
//    • Sync camera sliders ↔ GameSettings (bidirectional, with suppress flag)
//    • Motor start/stop, save/load/reset, auto-save on change
//    • Live status bar: VPS + hardware presence
//
//  Panel builder convention:
//    Every settings panel is a private method BuildXxxPanel() : Control.
//    Each adds rows using the helper methods (AddSlider, AddToggle, etc.).
//    This keeps each panel short and self-contained.
//
//  Threading:
//    OnPreviewFrame is called from the game thread.
//    It marshals to the UI thread with Dispatcher.UIThread.Post.
// ═══════════════════════════════════════════════════════════════════════════

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using Voxon;
using System.Linq;
using System.Runtime.InteropServices;

namespace EDes.UI
{
    public partial class MainWindow : Window
    {
        private readonly GameSettings    _s;
        private readonly DispatcherTimer _statusTimer;
        private DispatcherTimer?         _saveDebounce;

        // Shared settings-widget builder (engine tabs + game tabs use the same one).
        private readonly PanelBuilder _ui;

        // Preview bitmap (recreated if resolution changes)
        private WriteableBitmap? _previewBmp;
        private int              _bmpW, _bmpH;

        // Frame coalescing: at most one preview frame in flight on the UI thread.
        // The game loop produces frames far faster than the UI can draw them, so
        // we drop intermediate frames instead of letting the dispatcher queue grow.
        private volatile bool _previewPending;

        // Reusable snapshot buffer (reallocated only on resize). Avoids allocating
        // a multi-MB array on the Large Object Heap every frame. Safe to reuse
        // without locking because _previewPending serializes producer/consumer:
        // the game thread only writes it when no frame is in flight.
        private byte[]? _frameBuf;

        // Camera slider sync suppressor
        private bool _syncingCamera = false;

        // Camera controls — built into the Simulator panel, so these are null
        // whenever another tab is showing. Always null-check before use.
        private Slider?    _camYaw, _camTilt, _camZoom;
        private TextBlock? _camYawLabel, _camTiltLabel, _camZoomLabel;

        // Active nav panel key
        private string _activePanel = "Simulator";

        // True while the simulator preview holds keyboard focus. When set, game
        // keys are swallowed before they reach the settings controls.
        private bool _previewFocused;

        // Right-drag model rotation state.
        private bool  _rotating;
        private Point _lastPtr;

        // For the Profiles tab's demo-score button.
        private readonly Random _rand = new();

        // Keys the game consumes (read via GetAsyncKeyState). While the preview is
        // focused we mark these handled so they can't move sliders / click buttons.
        private static readonly HashSet<Key> GameKeys = new()
        {
            Key.Left, Key.Right, Key.Up, Key.Down,
            Key.W, Key.A, Key.S, Key.D,
            Key.LeftShift, Key.RightShift,
            Key.NumPad0, Key.Space, Key.Escape,
            Key.OemOpenBrackets, Key.OemCloseBrackets,
        };

        // Nav table — add more panels by extending this array
        private readonly (string Key, string Label, Func<Control> Builder)[] _navItems;

        private readonly IVoxonGame? _game;
        private readonly Color _accent;

        // ── Constructor ───────────────────────────────────────────────────────
        // Parameterless overload required by the Avalonia XAML compiler (AVLN3001).
        // Always use the (settings, game) overload at runtime.
        public MainWindow() : this(App.Settings, App.Game) { }

        public MainWindow(GameSettings settings, IVoxonGame? game)
        {
            _s = settings;
            _game = game;
            _ui = new PanelBuilder(DebounceSave);
            _accent = Color.FromUInt32(game?.Manifest.Accent ?? 0xFF00CCFF);
            InitializeComponent();

            // Branding from the game manifest (title bar + splash).
            if (game != null) Title = game.Manifest.Title;
            SetupSplash(game?.Manifest);

            // Pre-flight splash buttons — see VoxonPreflight.cs / GameLoop.cs.
            // Just write the choice; the game thread's IPreflightUi.PollChoice()
            // picks it up and clears it back to PreflightChoice.None.
            SplashRetryBtn.Click     += (_, _) => _s.SplashChoice = PreflightChoice.Retry;
            SplashSimulatorBtn.Click += (_, _) => _s.SplashChoice = PreflightChoice.Simulator;
            SplashQuitBtn.Click      += (_, _) => _s.SplashChoice = PreflightChoice.Quit;

            // ── Build nav table ───────────────────────────────────────────────
            // The "Game" tab is supplied by the active game; the others are engine.
            _navItems = new (string, string, Func<Control>)[]
            {
                ("Simulator", "Simulator", BuildSimulatorPanel),
                ("Lighting",  "Lighting",  BuildLightingPanel),
                ("Game",      "Game",      BuildGamePanel),
                ("Profiles",  "Profiles",  BuildProfilesPanel),
            };

            BuildNavButtons();
            ActivatePanel(_activePanel);   // builds the Simulator panel + camera sliders

            // ── Motor buttons ─────────────────────────────────────────────────
            BtnMotorStart.Click += (_, _) => _s.MotorRpmRequest = 600;
            BtnMotorStop .Click += (_, _) => _s.MotorRpmRequest = 0;

            // ── Ctrl+[ / Ctrl+] — zoom in / out ──────────────────────────────
            KeyDown += (_, e) =>
            {
                if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
                if (e.Key == Key.OemOpenBrackets)
                { _s.EmuDist = Math.Max(0.5f, _s.EmuDist - 0.4f); SyncCameraSliders(); e.Handled = true; }
                else if (e.Key == Key.OemCloseBrackets)
                { _s.EmuDist = Math.Min(20f,  _s.EmuDist + 0.4f); SyncCameraSliders(); e.Handled = true; }
            };

            // ── Window focus gating ───────────────────────────────────────────
            // NativeInput only reads keys when the window has OS focus.
            Activated   += (_, _) => NativeInput.WindowHasFocus = true;
            Deactivated += (_, _) => NativeInput.WindowHasFocus = false;

            // ── Simulator key capture ─────────────────────────────────────────
            // Click the preview to give it keyboard focus (pulling it off any
            // slider/button), so the game receives keys without the settings UI
            // reacting. The tunnelling handler swallows game keys while focused.
            PreviewBorder.GotFocus  += (_, _) => _previewFocused = true;
            PreviewBorder.LostFocus += (_, _) => _previewFocused = false;
            AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

            // ── Model test controls ───────────────────────────────────────────
            // Right-drag = rotate (yaw/pitch); wheel = scale; arrow keys move XY.
            PreviewBorder.PointerPressed += (_, e) =>
            {
                PreviewBorder.Focus();
                var pt = e.GetCurrentPoint(PreviewBorder);
                if (pt.Properties.IsRightButtonPressed)
                {
                    _rotating = true;
                    _lastPtr  = pt.Position;
                    e.Pointer.Capture(PreviewBorder);
                }
            };
            PreviewBorder.PointerMoved += (_, e) =>
            {
                if (!_rotating) return;
                var pos = e.GetPosition(PreviewBorder);
                _s.ModelYaw   += (float)((pos.X - _lastPtr.X) * 0.01);
                _s.ModelPitch  = Math.Clamp(_s.ModelPitch + (float)((pos.Y - _lastPtr.Y) * 0.01), -1.55f, 1.55f);
                _lastPtr = pos;
            };
            PreviewBorder.PointerReleased += (_, e) =>
            {
                _rotating = false;
                e.Pointer.Capture(null);
            };
            PreviewBorder.PointerWheelChanged += (_, e) =>
            {
                _s.ModelScale = Math.Clamp(_s.ModelScale * (float)(1.0 + e.Delta.Y * 0.1), 0.1f, 10f);
            };

            // Enable game input and focus the preview on open so keys drive the
            // game by default.
            Opened += (_, _) =>
            {
                NativeInput.InputEnabled = true;
                PreviewBorder.Focus();
            };

            // Suspend game input whenever a text box holds focus, so typed
            // characters (and the controller) can't bleed into the simulator.
            // GotFocus bubbles to the window from whichever control gains focus.
            GotFocus += (_, e) =>
                NativeInput.SuspendForTextEntry = e.Source is TextBox;

            // ── Preview size tracking ─────────────────────────────────────────
            // GameLoop reallocates the Rend2D buffer to match this size.
            PreviewImage.SizeChanged += (_, e) =>
            {
                int w = Math.Max(64, (int)e.NewSize.Width);
                int h = Math.Max(64, (int)e.NewSize.Height);
                _s.PreviewRequestW = w;
                _s.PreviewRequestH = h;
            };

            // ── 1-second status timer ─────────────────────────────────────────
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += OnStatusTick;
            _statusTimer.Start();
        }

        // ── OnPreviewFrame — called from the game thread ──────────────────────
        // Receives the raw BGRA pixel buffer from Rend2D.  We marshal to the
        // UI thread, create/resize the WriteableBitmap if needed, and blit.
        public void OnPreviewFrame(byte[] buf, int w, int h)
        {
            // Drop this frame if the UI hasn't finished the previous one. This caps
            // the dispatcher queue at a single pending frame, so latency can't build
            // up and input events aren't starved behind a backlog of preview posts.
            if (_previewPending) return;
            _previewPending = true;

            // Snapshot out of the shared buffer now — the game thread reuses and
            // clears it every iteration, so we can't read it later on the UI thread.
            // Reuse a persistent buffer to keep these multi-MB copies off the LOH.
            int bytes = w * h * 4;
            if (_frameBuf == null || _frameBuf.Length != bytes)
                _frameBuf = new byte[bytes];
            Array.Copy(buf, _frameBuf, Math.Min(buf.Length, bytes));
            byte[] snapshot = _frameBuf;

            // Background priority keeps the preview below user Input, so sliders and
            // other controls stay responsive even while frames are streaming in.
            Dispatcher.UIThread.Post(() =>
            {
                if (_previewBmp == null || _bmpW != w || _bmpH != h)
                {
                    _previewBmp = new WriteableBitmap(
                        new PixelSize(w, h), new Vector(96, 96),
                        PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    _bmpW = w; _bmpH = h;
                }

                using (var fb = _previewBmp.Lock())
                    Marshal.Copy(snapshot, 0, fb.Address, Math.Min(snapshot.Length, bytes));
                PreviewImage.Source = _previewBmp;
                PreviewImage.InvalidateVisual();
                _previewPending = false;
            }, DispatcherPriority.Background);
        }

        // ── Status bar tick ───────────────────────────────────────────────────
        private void OnStatusTick(object? s, EventArgs e)
        {
            VpsText.Text = $"VPS: {_s.LiveVps:F1}";

            bool hasHw = _s.HardwareConnected;
            MotorPanel.IsVisible = hasHw;

            // Display volume — bounds + XY:Z ratio indicate which unit is connected.
            float xy = DisplayVolume.HalfXY, z = DisplayVolume.HalfZ;
            float ratio = z > 0.01f ? xy / z : 0f;
            if (!_s.GameLoopRunning)
                DeviceText.Text = "● Waiting for game loop...";
            else
                DeviceText.Text = $"● Display {xy:F1}×{xy:F1}×{z:F1}  (XY:Z {ratio:F2})";
            DeviceText.Foreground = new SolidColorBrush(
                _s.GameLoopRunning ? Color.Parse("#FF66FF88") : Color.Parse("#FFAAAA66"));

            ModelText.Text   = $"Model: {_s.ModelSource}";
            VoxelText.Text   = $"Voxels: {_s.LiveVoxelCount:N0}";
            float ms = _s.LiveFrameMs;
            LatencyText.Text = $"Latency: {ms:F1} ms ({(ms > 0.01f ? 1000f / ms : 0f):F0} fps)";
            MotorText.Text   = hasHw ? $"Motor: {_s.LiveMotorRpm} RPM" : "";
            BoundsText.Text  = $"Lighting: {(_s.Lighting.UseGpu ? "GPU" : "CPU")}";

            // Dismiss the splash once the game loop is live.
            if (SplashOverlay.IsVisible && _s.GameLoopRunning)
                SplashOverlay.IsVisible = false;

            // Pre-flight status text + Retry/Simulator/Quit buttons (only relevant
            // while the splash is still up — see VoxonPreflight.cs).
            if (SplashOverlay.IsVisible)
            {
                SplashStatusText.Text       = _s.SplashStatus;
                SplashStatusText.Foreground = new SolidColorBrush(
                    _s.SplashWarning ? Color.Parse("#FFFFAA55") : Color.Parse("#FFCCCCCC"));
                SplashButtons.IsVisible = _s.SplashShowButtons;
            }

            // Sync camera labels from game-loop-updated angles
            SyncCameraSliders();
        }

        // ── Splash ─────────────────────────────────────────────────────────────
        // Branded splash shown until the game loop reports it's running. Loads the
        // manifest's splash art (preferred) or logo from next to the exe if present.
        private void SetupSplash(GameManifest? m)
        {
            SplashTitle.Text       = m?.Title ?? "EDes";
            SplashTitle.Foreground = new SolidColorBrush(_accent);

            string? img = m?.SplashPath ?? m?.LogoPath;
            if (!string.IsNullOrEmpty(img))
            {
                try
                {
                    string full = System.IO.Path.Combine(AppContext.BaseDirectory, img);
                    if (System.IO.File.Exists(full))
                        SplashImage.Source = new Avalonia.Media.Imaging.Bitmap(full);
                }
                catch { /* missing/invalid image — title-only splash */ }
            }
        }

        // ── Camera slider sync ────────────────────────────────────────────────
        private void SyncCameraSliders()
        {
            if (_camYaw == null) return;   // Simulator panel not currently showing
            _syncingCamera = true;
            _camYaw .Value = _s.EmuHAng;
            _camTilt!.Value = _s.EmuVAng;
            _camZoom!.Value = _s.EmuDist;
            _camYawLabel !.Text = $"{_s.EmuHAng:F2}";
            _camTiltLabel!.Text = $"{_s.EmuVAng:F2}";
            _camZoomLabel!.Text = $"{_s.EmuDist:F1}";
            _syncingCamera = false;
        }

        // ── Global key gate — keep game keys out of the settings UI ───────────
        // Tunnels before any control sees the key. While the preview is focused,
        // unmodified game keys are marked handled so sliders/buttons don't react.
        // Ctrl combos pass through (e.g. Ctrl+[ / ] zoom handled below).
        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_previewFocused) return;
            if ((e.KeyModifiers & KeyModifiers.Control) != 0) return;
            if (GameKeys.Contains(e.Key))
                e.Handled = true;
        }

        // ── Top bar buttons ───────────────────────────────────────────────────
        private void OnSaveClick (object? s, RoutedEventArgs e) => _s.Save();
        private void OnLoadClick (object? s, RoutedEventArgs e) { _s.Load(); RebuildActivePanel(); }
        private void OnResetClick(object? s, RoutedEventArgs e) { _s.Reset(); RebuildActivePanel(); }

        // ─────────────────────────────────────────────────────────────────────
        // Nav system
        // ─────────────────────────────────────────────────────────────────────

        private void BuildNavButtons()
        {
            NavPanel.Children.Clear();
            foreach (var (key, label, _) in _navItems)
            {
                var btn = new Button
                {
                    Content             = label,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Padding             = new Thickness(10, 6),
                    FontSize            = 12,
                    Background          = Brushes.Transparent,
                };
                string capturedKey = key;
                btn.Click += (_, _) => ActivatePanel(capturedKey);
                NavPanel.Children.Add(btn);
            }
            HighlightNavButton(_activePanel);
        }

        private void ActivatePanel(string key)
        {
            _activePanel = key;
            foreach (var (k, _, builder) in _navItems)
            {
                if (k != key) continue;
                SettingsPanelArea.Content = builder();
                break;
            }
            HighlightNavButton(key);
        }

        private void RebuildActivePanel() => ActivatePanel(_activePanel);

        private void HighlightNavButton(string activeKey)
        {
            int idx = 0;
            foreach (var (key, _, _) in _navItems)
            {
                if (NavPanel.Children[idx] is Button btn)
                {
                    bool active = key == activeKey;
                    btn.Background = active
                        ? new SolidColorBrush(Color.Parse("#FF0A2A4A"))
                        : Brushes.Transparent;
                    btn.Foreground = active
                        ? new SolidColorBrush(_accent)
                        : new SolidColorBrush(Color.Parse("#FFCCCCCC"));
                }
                idx++;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Settings panel builders
        // ─────────────────────────────────────────────────────────────────────
        //
        // Pattern for adding a new panel:
        //   1. Add a (key, label, BuildMyPanel) entry to _navItems above.
        //   2. Add a private Control BuildMyPanel() method below.
        //   3. Use the helper methods (AddSlider, AddToggle, etc.) to fill it.

        // ── Simulator panel ───────────────────────────────────────────────────
        // Everything that controls how the simulator/display renders: rendering
        // quality, frame-rate cap, background, hardware bounds, and camera.
        private Control BuildSimulatorPanel()
        {
            var stack = MakeScrollPanel();
            var group = new List<Expander>();

            var rend = AddSection(stack, "Rendering", group, expanded: true);
            AddSlider(rend, "Gamma",           0.5,  4.0, _s.Gamma,           v => _s.Gamma           = (float)v, "F2");
            AddIntToggle(rend, "Dithering",    _s.DitherMode != 0,             v => _s.DitherMode      = v ? 1 : 0);
            AddSlider(rend, "Dither threshold",0,    255, _s.DitherThreshold,  v => _s.DitherThreshold = (int)v,   "F0");
            AddToggle(rend, "Show debug border", _s.ShowDebugBorder,           v => _s.ShowDebugBorder = v);
            AddInfo(rend, "Voxel density re-meshes the model (higher = finer, more voxels).");
            AddSlider(rend, "Voxel density",  0.25, 3.0, _s.VoxelDensity,      v => _s.VoxelDensity    = (float)v, "F2");

            var perf = AddSection(stack, "Performance", group);
            AddInfo(perf, "Display tops out at 30 VPS. Cap the loop to save CPU/heat.");
            AddToggle(perf, "Cap to 30 VPS",     _s.CapVps30,                  v => _s.CapVps30        = v);

            var cam = AddSection(stack, "Simulator Camera", group);
            AddInfo(cam, "[ / ] rotate · Ctrl+[ / ] zoom");
            _camYaw  = AddCameraSlider(cam, "Yaw",  0,       6.2832, _s.EmuHAng, "F2",
                                       v => _s.EmuHAng = (float)v, out _camYawLabel);
            _camTilt = AddCameraSlider(cam, "Tilt", -1.5708, 1.5708, _s.EmuVAng, "F2",
                                       v => _s.EmuVAng = (float)v, out _camTiltLabel);
            _camZoom = AddCameraSlider(cam, "Zoom", 0.5,     20,     _s.EmuDist, "F1",
                                       v => _s.EmuDist = (float)v, out _camZoomLabel);

            var reset = new Button
            {
                Content             = "Reset Camera",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                FontSize            = 10,
                Padding             = new Thickness(4, 3),
                Margin              = new Thickness(10, 6, 10, 0),
            };
            reset.Click += (_, _) =>
            {
                _s.EmuHAng = 0f; _s.EmuVAng = 0f; _s.EmuDist = 4f;
                SyncCameraSliders();
            };
            cam.Children.Add(reset);

            return WrapInScroll(stack);
        }

        // ── Game panel ────────────────────────────────────────────────────────
        // Supplied by the active game (IVoxonGame.BuildSettingsPanel). Falls back
        // to an empty panel at design time when no game is wired.
        private Control BuildGamePanel()
            => _game?.BuildSettingsPanel(_ui) ?? WrapInScroll(MakeScrollPanel());

        // ── Profiles panel ────────────────────────────────────────────────────
        // Player profiles + high scores, persisted to players.json (App.Players).
        private Control BuildProfilesPanel()
        {
            var stack = MakeScrollPanel();
            var group = new List<Expander>();
            var pd    = App.Players;

            // ── Current profile ───────────────────────────────────────────────
            var cur = AddSection(stack, "Current Profile", group, expanded: true);

            var combo = new ComboBox
            {
                ItemsSource         = pd.Profiles.Select(p => p.Name).ToList(),
                SelectedItem        = pd.CurrentProfile,
                FontSize            = 11,
                Margin              = new Thickness(10, 2, 10, 4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string name && name != pd.CurrentProfile)
                { pd.SelectProfile(name); RebuildActivePanel(); }
            };
            cur.Children.Add(combo);

            var newName = new TextBox
            {
                Watermark = "New profile name",
                FontSize  = 11,
                MaxLength  = 24,
                Margin    = new Thickness(10, 2, 10, 2),
            };
            void AddProfile()
            {
                var n = newName.Text?.Trim();
                if (!string.IsNullOrEmpty(n)) { pd.SelectProfile(n); RebuildActivePanel(); }
            }
            // Commit on Enter (the value isn't applied per-keystroke).
            newName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddProfile(); e.Handled = true; } };
            cur.Children.Add(newName);
            AddButton(cur, "Add / select profile", AddProfile);

            var p = pd.Current;
            AddInfo(cur, $"Games played: {p.GamesPlayed}");
            AddInfo(cur, $"Best score:   {p.BestScore:N0}");
            AddInfo(cur, $"Total score:  {p.TotalScore:N0}");
            if (!string.IsNullOrEmpty(p.LastPlayed)) AddInfo(cur, $"Last played:  {p.LastPlayed}");

            // ── High scores ───────────────────────────────────────────────────
            var hs = AddSection(stack, "High Scores", group, expanded: true);
            if (pd.HighScores.Count == 0)
                AddInfo(hs, "No scores yet.");
            else
            {
                int rank = 1;
                foreach (var e in pd.HighScores)
                    AddInfo(hs, $"{rank++,2}.  {e.Name,-12} {e.Score,8:N0}   {e.Date}");
            }
            AddButton(hs, "Submit demo score", () =>
            {
                pd.SubmitScore(_rand.Next(100, 10000));
                RebuildActivePanel();
            });
            AddButton(hs, "Clear high scores", () =>
            {
                pd.ClearScores();
                RebuildActivePanel();
            });

            return WrapInScroll(stack);
        }

        // ── Lighting panel ────────────────────────────────────────────────────
        // Combined-additive model: global ambient/brightness + an optional
        // directional "sun" + four point-light spotlights, all independent.
        // Lighting applies to solid geometry only (particles/background are emissive).
        private Control BuildLightingPanel()
        {
            var stack = MakeScrollPanel();
            var group = new List<Expander>();
            var cfg   = _s.Lighting;   // snapshot for initial control values

            var glob = AddSection(stack, "Global", group, expanded: true);
            AddToggle(glob, "Enable lighting", cfg.Enabled,      v => ApplyLighting(c => c.Enabled = v));
            AddInfo(glob, "Off = flat passthrough (voxels keep their base colour).");
            AddSlider(glob, "Ambient",    0, 1.0, cfg.Ambient,    v => ApplyLighting(c => c.Ambient    = (float)v), "F2");
            AddSlider(glob, "Brightness", 0, 3.0, cfg.Brightness, v => ApplyLighting(c => c.Brightness = (float)v), "F2");
            AddToggle(glob, "Compute on GPU", cfg.UseGpu,         v => ApplyLighting(c => c.UseGpu = v));
            AddInfo(glob, "Offload shading to the GPU (ComputeSharp). Falls back to CPU if no DX12 device.");
            AddToggle(glob, "Boost dark colours", cfg.BoostDark, v => ApplyLighting(c => c.BoostDark = v));
            AddSlider(glob, "Boost strength", 0, 1.0, cfg.BoostStrength, v => ApplyLighting(c => c.BoostStrength = (float)v), "F2");
            AddToggle(glob, "Cull black voxels",  cfg.CullBlack, v => ApplyLighting(c => c.CullBlack = v));

            var sun = AddSection(stack, "Simple Lighting (Sun)", group);
            AddInfo(sun, "One directional light. Direction is a vector (auto-normalised).");
            AddToggle(sun, "Enable sun", cfg.SunEnabled,         v => ApplyLighting(c => c.SunEnabled = v));
            AddSlider(sun, "Direction X", -1, 1, cfg.SunDirX,    v => ApplyLighting(c => c.SunDirX = (float)v), "F2");
            AddSlider(sun, "Direction Y", -1, 1, cfg.SunDirY,    v => ApplyLighting(c => c.SunDirY = (float)v), "F2");
            AddSlider(sun, "Direction Z", -1, 1, cfg.SunDirZ,    v => ApplyLighting(c => c.SunDirZ = (float)v), "F2");
            AddSlider(sun, "Intensity",    0, 5, cfg.SunIntensity, v => ApplyLighting(c => c.SunIntensity = (float)v), "F2");
            AddInfo(sun, "Colour");
            AddRgb(sun, () => _s.Lighting.SunColor, nc => ApplyLighting(c => c.SunColor = nc));

            for (int i = 0; i < cfg.Spots.Length; i++)
            {
                int idx  = i;            // capture per-iteration index for the closures
                var sp   = cfg.Spots[idx];
                var sec  = AddSection(stack, $"Spotlight {idx + 1}", group);
                AddToggle(sec, "Enabled",     sp.Enabled,        v => ApplyLighting(c => c.Spots[idx].Enabled = v));
                AddSlider(sec, "Position X", -8, 8, sp.X,        v => ApplyLighting(c => c.Spots[idx].X = (float)v), "F1");
                AddSlider(sec, "Position Y", -8, 8, sp.Y,        v => ApplyLighting(c => c.Spots[idx].Y = (float)v), "F1");
                AddSlider(sec, "Position Z", -4, 8, sp.Z,        v => ApplyLighting(c => c.Spots[idx].Z = (float)v), "F1");
                AddSlider(sec, "Radius",    0.5, 40, sp.Radius,  v => ApplyLighting(c => c.Spots[idx].Radius    = (float)v), "F1");
                AddSlider(sec, "Intensity",   0, 5, sp.Intensity,v => ApplyLighting(c => c.Spots[idx].Intensity = (float)v), "F2");
                AddInfo(sec, "Colour");
                AddRgb(sec, () => _s.Lighting.Spots[idx].Color, nc => ApplyLighting(c => c.Spots[idx].Color = nc));
            }

            return WrapInScroll(stack);
        }

        // ── ApplyLighting — clone-mutate-swap the lighting config ─────────────
        // The game thread reads GameSettings.Lighting by reference, so we must
        // never mutate it in place. Clone, change the copy, swap atomically.
        private void ApplyLighting(Action<LightingConfig> mutate)
        {
            var c = _s.Lighting.Clone();
            mutate(c);
            _s.Lighting = c;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Panel helpers — thin delegators to the shared PanelBuilder (_ui) so the
        // engine tabs and game tabs build identical-looking, debounced-saving rows.
        // ─────────────────────────────────────────────────────────────────────

        private StackPanel  MakeScrollPanel() => _ui.Root();
        private ScrollViewer WrapInScroll(StackPanel p) => _ui.Wrap(p);

        private StackPanel AddSection(StackPanel root, string title,
                                      List<Expander> group, bool expanded = false)
            => _ui.AddSection(root, title, group, expanded);

        private void AddButton(StackPanel p, string label, Action onClick)
            => _ui.AddButton(p, label, onClick);

        private void AddInfo(StackPanel p, string text) => _ui.AddInfo(p, text);

        private void AddSlider(StackPanel p, string label,
                               double min, double max, double initial,
                               Action<double> onChange, string fmt = "F2")
            => _ui.AddSlider(p, label, min, max, initial, onChange, fmt);

        private void AddRgb(StackPanel p, Func<int> getColor, Action<int> setColor)
            => _ui.AddRgb(p, getColor, setColor);

        // ── AddCameraSlider — slider with bidirectional sync support ──────────
        // Like AddSlider, but the PropertyChanged handler respects _syncingCamera
        // (so SyncCameraSliders can push game-loop values back without feedback),
        // returns the slider, and hands back its value label via out param.
        // Camera angles aren't persisted, so this does NOT trigger DebounceSave.
        private Slider AddCameraSlider(StackPanel p, string label,
                                       double min, double max, double initial,
                                       string fmt, Action<double> onChange,
                                       out TextBlock valueLabel)
        {
            var lbl = new TextBlock
            {
                Text       = initial.ToString(fmt),
                FontSize   = 10,
                Opacity    = 0.70,
                Width      = 44,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var slider = new Slider
            {
                Minimum       = min,
                Maximum       = max,
                Value         = initial,
                TickFrequency = (max - min) / 200.0,
                Margin        = new Thickness(0, 0, 4, 0),
            };
            slider.PropertyChanged += (_, e) =>
            {
                if (_syncingCamera || e.Property.Name != nameof(Slider.Value)) return;
                lbl.Text = slider.Value.ToString(fmt);
                onChange(slider.Value);
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,44") };
            row.Children.Add(slider);
            row.Children.Add(lbl);
            Grid.SetColumn(lbl, 1);

            p.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11,
                Margin = new Thickness(10, 4, 10, 0),
            });
            p.Children.Add(new Border
            {
                Margin  = new Thickness(10, 0, 10, 2),
                Child   = row,
            });

            valueLabel = lbl;
            return slider;
        }

        private void AddToggle(StackPanel p, string label, bool initial, Action<bool> onChange)
            => _ui.AddToggle(p, label, initial, onChange);

        private void AddIntToggle(StackPanel p, string label, bool initial, Action<bool> onChange)
            => _ui.AddIntToggle(p, label, initial, onChange);

        // ── DebounceSave — auto-save 2s after the last change ─────────────────
        // Reuse a single timer: restarting it pushes the save out to 2s after the
        // most recent change. Allocating a new timer per change would churn the heap
        // during slider drags (DebounceSave fires on every Value change).
        private void DebounceSave()
        {
            if (_saveDebounce == null)
            {
                _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _saveDebounce.Tick += (_, _) =>
                {
                    _saveDebounce!.Stop();
                    _s.Save();                                   // engine settings
                    (_game?.Settings as IGameSettings)?.Save();  // game settings (game.json)
                };
            }
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }
    }
}
