// ═══════════════════════════════════════════════════════════════════════════
//  MainWindow.axaml.cs — Settings/preview window code-behind
//
//  Responsibilities:
//    • Build the mode headers (from IVoxonGame.Modes) and the two settings panels
//    • Receive the Rend2D preview buffer and display it as a WriteableBitmap
//    • Sync camera sliders ↔ GameSettings (bidirectional, with suppress flag)
//    • Motor start/stop, save/load/reset, auto-save on change
//    • Live status bar: VPS + hardware presence
//
//  Layout:
//    The window has exactly two settings panels and no tab strip. The LEFT panel is
//    the active game mode own settings (IVoxonGame.BuildSettingsPanel, rebuilt on
//    every mode change); the RIGHT panel is the Voxon display settings. Mode choice
//    lives in the header row above both.
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
using System.Text;
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

        // Mode headers, in IVoxonGame.Modes order. Index = mode index.
        private readonly List<Button> _modeHeaders = new();

        // Last mode the UI drew. The game thread can change the mode on its own (Tab
        // in the volume), so the status tick compares against this and rebuilds the
        // headers + left panel when they disagree. Without it the volume and the
        // window would sit there showing different modes.
        private int _shownMode = -1;

        // True while the simulator preview holds keyboard focus. When set, game
        // keys are swallowed before they reach the settings controls.
        private bool _previewFocused;

        // Right-drag model rotation state.
        private bool  _rotating;      // right-drag: rotate the model / scene content
        private bool  _orbiting;      // left-drag: orbit the simulator camera
        private Point _lastPtr;

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

            // ── Panels ────────────────────────────────────────────────────────
            // The display panel is built once — nothing in it is mode-specific. The
            // mode panel is rebuilt on every mode change by RefreshModePanel.
            DisplayPanelArea.Content = BuildDisplayPanel();

            BuildModeHeaders();
            RefreshModePanel();

            // ── Motor buttons ─────────────────────────────────────────────────
            // The same spec constant GameLoop auto-starts from. Hardcoding 600 here
            // meant pressing Motor On SLOWED a VX2-XL down from its 900 RPM default.
            BtnMotorStart.Click += (_, _) =>
                _s.MotorRpmRequest = VoxonHardwareCheck.VX2XL.DefaultMotorRpm;
            BtnMotorStop .Click += (_, _) => _s.MotorRpmRequest = 0;

            // ── Ctrl+[ / Ctrl+] — zoom in / out ──────────────────────────────
            KeyDown += (_, e) =>
            {
                if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
                if (e.Key == Key.OemOpenBrackets)
                { _s.EmuDist = Math.Max(0.5f, _s.EmuDist - 0.4f); e.Handled = true; }
                else if (e.Key == Key.OemCloseBrackets)
                { _s.EmuDist = Math.Min(20f,  _s.EmuDist + 0.4f); e.Handled = true; }
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

            // ── Preview mouse controls ────────────────────────────────────────
            //   LEFT-drag   orbit the SIMULATOR camera (EmuHAng / EmuVAng) — i.e.
            //               walk around the volume, exactly as if you moved around
            //               the physical display.
            //   RIGHT-drag  rotate the model / scene content (ModelYaw / ModelPitch).
            //   Wheel       scale the model; Ctrl+wheel pulls the simulator camera
            //               in and out (EmuDist).
            // The two drags are deliberately separate: conflating "move the viewer"
            // with "move the thing" makes it impossible to line a scene up.
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
                else if (pt.Properties.IsLeftButtonPressed)
                {
                    _orbiting = true;
                    _lastPtr  = pt.Position;
                    e.Pointer.Capture(PreviewBorder);
                }
            };
            PreviewBorder.PointerMoved += (_, e) =>
            {
                if (!_rotating && !_orbiting) return;
                var pos = e.GetPosition(PreviewBorder);
                double dx = pos.X - _lastPtr.X, dy = pos.Y - _lastPtr.Y;

                if (_rotating)
                {
                    _s.ModelYaw   += (float)(dx * 0.01);
                    _s.ModelPitch  = Math.Clamp(_s.ModelPitch + (float)(dy * 0.01), -1.55f, 1.55f);
                }
                else
                {
                    // Both axes are negated so the drag feels like grabbing the volume
                    // and turning it, rather than walking the camera around the outside
                    // of it. Those two readings move the view in opposite directions and
                    // the grab-the-object one is what matches the preview.
                    float h = _s.EmuHAng - (float)(dx * 0.01);
                    _s.EmuHAng = (h % (2f * MathF.PI) + 2f * MathF.PI) % (2f * MathF.PI);
                    _s.EmuVAng = Math.Clamp(_s.EmuVAng - (float)(dy * 0.01), -1.4f, 1.4f);
                }
                _lastPtr = pos;
            };
            PreviewBorder.PointerReleased += (_, e) =>
            {
                _rotating = false;
                _orbiting = false;
                e.Pointer.Capture(null);
            };
            PreviewBorder.PointerWheelChanged += (_, e) =>
            {
                bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
                if (ctrl)
                    _s.EmuDist = Math.Clamp(_s.EmuDist * (float)(1.0 - e.Delta.Y * 0.1), 0.5f, 40f);
                else
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
            BoundsText.Text  = $"Mode: {ActiveModeName()}";

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

            // The volume Tab key changes the mode behind the window back — notice it.
            if (_game != null && _game.ActiveMode != _shownMode) RefreshModePanel();

            RefreshLegend();
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

        // ── Legend overlay ────────────────────────────────────────────────────
        // Rebuilt only when the CONTENT changes, not every tick. The status timer fires
        // several times a second and this sits over a live preview, so rebuilding the
        // visual tree unconditionally would churn layout for no visible difference.
        private string _legendKey = "\u0001";

        private void RefreshLegend()
        {
            var rows = _game?.Legend;
            if (rows == null || rows.Count == 0)
            {
                if (LegendPanel.IsVisible) LegendPanel.IsVisible = false;
                _legendKey = "";
                return;
            }

            var sb = new StringBuilder(rows.Count * 16);
            foreach (var r in rows)
                sb.Append(r.Label).Append('|').Append(r.Colour).Append(r.Hidden ? '-' : '+')
                  .Append(';');
            string key = sb.ToString();
            if (key == _legendKey) { LegendPanel.IsVisible = true; return; }
            _legendKey = key;

            LegendItems.Children.Clear();
            foreach (var r in rows)
            {
                var swatch = new Border
                {
                    Width = 11, Height = 11,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromRgb(
                        (byte)((r.Colour >> 16) & 0xFF),
                        (byte)((r.Colour >> 8)  & 0xFF),
                        (byte)(r.Colour         & 0xFF))),
                    BorderBrush = new SolidColorBrush(Color.Parse("#50FFFFFF")),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    // A hidden layer keeps its colour but loses its solidity, so the row
                    // still says WHICH colour that layer is when it comes back.
                    Opacity = r.Hidden ? 0.25 : 1.0,
                };

                var label = new TextBlock
                {
                    Text       = r.Label,
                    FontSize   = 10,
                    Opacity    = r.Hidden ? 0.45 : 0.95,
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };

                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(swatch);
                row.Children.Add(label);
                LegendItems.Children.Add(row);
            }
            LegendPanel.IsVisible = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mode headers
        // ─────────────────────────────────────────────────────────────────────
        //
        // The headers come from IVoxonGame.Modes, so the shell never learns what
        // "Education" or "PCB" mean — it lights one and asks the game to rebuild its
        // panel. Clicking a header does two things that must stay together: it changes
        // the mode the volume renders AND swaps the settings shown underneath it.

        private void BuildModeHeaders()
        {
            ModeHeaderPanel.Children.Clear();
            _modeHeaders.Clear();

            var modes = _game?.Modes;
            if (modes == null || modes.Count == 0) return;

            for (int i = 0; i < modes.Count; i++)
            {
                int index = i;                  // capture per-iteration for the closure
                var btn = new Button
                {
                    Content         = modes[i],
                    FontSize        = 14,
                    FontWeight      = FontWeight.SemiBold,
                    Padding         = new Thickness(18, 10),
                    Background      = Brushes.Transparent,
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    BorderBrush     = Brushes.Transparent,
                };
                btn.Click += (_, _) => SelectMode(index);
                ModeHeaderPanel.Children.Add(btn);
                _modeHeaders.Add(btn);
            }
        }

        private void SelectMode(int index)
        {
            if (_game == null) return;
            _game.ActiveMode = index;
            RefreshModePanel();
            DebounceSave();
        }

        /// <summary>Rebuild the left panel for whatever mode is active now and light the
        /// matching header. Safe to call when nothing changed.</summary>
        private void RefreshModePanel()
        {
            if (_game == null)
            {
                GamePanelArea.Content = WrapInScroll(MakeScrollPanel());
                return;
            }

            _shownMode            = _game.ActiveMode;
            GamePanelArea.Content = _game.BuildSettingsPanel(_ui);
            GamePanelTitle.Text   = ActiveModeName() + " settings";
            HighlightModeHeader(_shownMode);
        }

        private string ActiveModeName()
        {
            var modes = _game?.Modes;
            if (modes == null || modes.Count == 0) return "Settings";
            return modes[Math.Clamp(_game!.ActiveMode, 0, modes.Count - 1)];
        }

        private void HighlightModeHeader(int activeIndex)
        {
            for (int i = 0; i < _modeHeaders.Count; i++)
            {
                bool active = i == activeIndex;
                _modeHeaders[i].Foreground = active
                    ? new SolidColorBrush(_accent)
                    : new SolidColorBrush(Color.Parse("#FF888899"));
                _modeHeaders[i].BorderBrush = active
                    ? new SolidColorBrush(_accent)
                    : Brushes.Transparent;
                _modeHeaders[i].Background = active
                    ? new SolidColorBrush(Color.Parse("#FF0A2A4A"))
                    : Brushes.Transparent;
            }
        }

        /// <summary>Rebuild both panels — used after Load/Reset, which can change every
        /// control value at once.</summary>
        private void RebuildActivePanel()
        {
            DisplayPanelArea.Content = BuildDisplayPanel();
            RefreshModePanel();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Settings panel builders
        // ─────────────────────────────────────────────────────────────────────
        //
        // ── Display panel (right side) ─────────────────────────────────────────
        // Everything that controls how the Voxon display / simulator renders: quality,
        // the frame-rate cap, and the simulator camera. Nothing here is mode-specific,
        // which is why it gets its own permanent panel instead of competing with the
        // mode settings for the same strip of window.
        private Control BuildDisplayPanel()
        {
            var stack = MakeScrollPanel();

            // One flat list, no accordion. This panel is short enough to read whole, and
            // a collapsed Expander hides that a setting exists at all — which matters
            // more here than saving a few hundred pixels.
            //
            // No camera rows either: the preview window IS the camera control (left-drag
            // to turn the volume, wheel and Ctrl+wheel to zoom), so sliders duplicating
            // it were a second source of truth for the same three numbers.
            var rend = _ui.AddHeader(stack, "Rendering");
            AddSlider(rend, "Gamma",            0.5,  4.0, _s.Gamma,           v => _s.Gamma           = (float)v, "F2");
            AddIntToggle(rend, "Dithering",     _s.DitherMode != 0,            v => _s.DitherMode      = v ? 1 : 0);
            AddSlider(rend, "Dither threshold", 0,    255, _s.DitherThreshold, v => _s.DitherThreshold = (int)v,   "F0");
            AddToggle(rend, "Show debug border", _s.ShowDebugBorder,           v => _s.ShowDebugBorder = v);
            AddSlider(rend, "Voxel density",    0.25, 3.0, _s.VoxelDensity,    v => _s.VoxelDensity    = (float)v, "F2");
            AddInfo(rend, "Voxel density re-meshes the model (higher = finer, more voxels).");

            var view = _ui.AddHeader(stack, "View");
            AddInfo(view, "Drag the preview to turn the volume. Wheel zooms the model, " +
                          "Ctrl+wheel and Ctrl+[ / ] zoom the camera.");
            AddButton(view, "Reset camera", () =>
            {
                _s.EmuHAng = 0f; _s.EmuVAng = 0f; _s.EmuDist = 4f;
            });

            return WrapInScroll(stack);
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
