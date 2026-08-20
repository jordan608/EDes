// ═══════════════════════════════════════════════════════════════════════════
//  App.axaml.cs — Avalonia application bootstrap
//
//  Responsibilities:
//    1. Create the shared GameSettings instance (lives for the full session).
//    2. Create and show the main settings/preview window.
//    3. Wire the preview-frame callback BEFORE starting the game thread.
//    4. Spawn the game loop on a background STA thread.
//
//  IMPORTANT: Do not start the game loop before setting OnPreviewFrame.
//  The game loop will call it immediately on its first rendered frame.
// ═══════════════════════════════════════════════════════════════════════════

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Threading;
using EDes.UI;

namespace EDes
{
    public class App : Application
    {
        // ── Single shared settings instance ───────────────────────────────────
        // Both threads access this. UI thread writes; game thread reads.
        // All fields are volatile — no locking needed for scalar values.
        public static GameSettings Settings { get; } = new GameSettings();

        // ── Player profiles + high scores (persisted to players.json) ──────────
        public static PlayerStore Players { get; private set; } = new PlayerStore();

        // ── The active game module. Swap this line to ship a different game. ───
        // Created lightweight here; the engine calls Init() on the game thread.
        public static IVoxonGame Game { get; private set; } = null!;

        // Strong reference prevents the GC from collecting the GameLoop object
        // while its background thread is still running.
        private GameLoop? _gameLoop;

        public override void Initialize()
        {
            Program.SafeLog("App.Initialize()");
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            Program.SafeLog("OnFrameworkInitializationCompleted()");

            // Load saved settings from disk (falls back to defaults if no file exists)
            Settings.Load();

            // Load player profiles + high scores (separate file).
            Players = PlayerStore.Load();

            // Log any unhandled Avalonia UI-thread exceptions without crashing
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Program.SafeLog($"[UI Thread Exception] {e.Exception}");
                e.Handled = true;
            };

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Create the game module (lightweight — Init() runs on the game thread).
                Game = new EDesApp(Settings);

                // Create the settings/preview window (shows the game's tab + branding)
                var window = new MainWindow(Settings, Game);
                desktop.MainWindow = window;

                // ── Wire preview callback BEFORE starting the game thread ─────
                // The game loop invokes this after every Rend2D call, handing
                // the pixel buffer to the UI to display in the preview image.
                // This is set once and never changes, so no locking is needed.
                Settings.OnPreviewFrame = window.OnPreviewFrame;

                // The pre-flight screen (VoxonPreflight, run on the game thread
                // before any SDK call) has no Avalonia reference of its own — this
                // is its only way to shut the app down if the operator chooses Quit.
                Settings.RequestShutdown = () => desktop.Shutdown();

                // ── Start game loop on background STA thread ──────────────────
                // Background STA is required — the Voxon SDK uses COM STA internally.
                // AboveNormal priority keeps frame timing consistent even when
                // Windows is doing background work.
                _gameLoop = new GameLoop(Settings, Game);
                var gameThread = new Thread(() =>
                {
                    try   { _gameLoop.RunOnCurrentThread(); }
                    catch (Exception ex) { Program.SafeLog($"[GameLoop Fatal] {ex}"); }
                })
                {
                    IsBackground = true,
                    Name         = "VoxonGameLoop",
                    Priority     = ThreadPriority.AboveNormal,
                };
                gameThread.SetApartmentState(ApartmentState.STA);
                gameThread.Start();
                Program.SafeLog("Game thread started.");
            }

            base.OnFrameworkInitializationCompleted();
        }

        // Convenience logger so other classes can log without a Program reference
        internal static void Log(string msg) => Program.SafeLog(msg);
    }
}
