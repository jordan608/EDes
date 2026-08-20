// ═══════════════════════════════════════════════════════════════════════════
//  Program.cs — Entry point
//
//  Threading model (CRITICAL — read before changing anything here):
//
//    Main STA thread  → Avalonia UI only
//    Background STA   → Voxon SDK + game loop only
//
//  The Voxon SDK corrupts managed stack frames during LedWinInit /
//  LedHostInit when it runs on the process main STA thread.
//  Moving the SDK to a background STA thread avoids this entirely.
//
//  Avalonia is started on the main thread via BuildAvaloniaApp().
//  App.axaml.cs then spawns the background game thread once Avalonia is ready.
//
//  Software rendering for Avalonia:
//    Win32RenderingMode.Software prevents D3D conflicts between Avalonia,
//    the Voxon SDK, and ComputeSharp which all want GPU access.
// ═══════════════════════════════════════════════════════════════════════════

using Avalonia;
using Avalonia.Win32;
using System;
using System.IO;
using System.Runtime.ExceptionServices;

namespace EDes
{
    internal class Program
    {
        // Crash log on the Desktop — always findable regardless of working directory.
        internal static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "edes_crash.log");

        // ── Entry point ────────────────────────────────────────────────────────
        // [STAThread] is required for both Avalonia AND the Voxon SDK.
        [STAThread]
        static void Main(string[] args)
        {
            SafeLog("=== EDes startup ===");

            // Log ALL first-chance exceptions (including access violations and SEH)
            // so crashes in the Voxon native DLLs are captured.
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            AppDomain.CurrentDomain.UnhandledException  += (_, e) =>
                SafeLog($"FATAL UnhandledException: {e.ExceptionObject}");

            SafeLog("Starting Avalonia on main STA thread...");
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                SafeLog("Avalonia exited cleanly.");
            }
            catch (Exception ex)
            {
                SafeLog($"[Main] Fatal: {ex}");
            }
        }

        // ── Avalonia app builder ───────────────────────────────────────────────
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                      .UsePlatformDetect()
                      // Software rendering avoids D3D conflicts with the Voxon SDK
                      // and ComputeSharp. Use this even if you have a GPU.
                      .With(new Win32PlatformOptions
                      {
                          RenderingMode = new[] { Win32RenderingMode.Software },
                      })
                      .LogToTrace();

        // ── Exception logging ──────────────────────────────────────────────────
        private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            var ex = e.Exception;
            // Only log exceptions that are likely meaningful — filter out
            // routine .NET internals that fire hundreds of times per second.
            bool isInteresting =
                ex is AccessViolationException          ||
                ex is System.Runtime.InteropServices.SEHException ||
                ex is NullReferenceException            ||
                ex.StackTrace?.Contains("EDes") == true;

            if (isInteresting)
                SafeLog($"[FirstChance] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        // Thread-safe log helper. FileShare.ReadWrite lets AV scanners read the file
        // concurrently without a sharing violation.
        internal static void SafeLog(string msg)
        {
            try
            {
                using var fs = new FileStream(LogPath, FileMode.Append,
                                              FileAccess.Write, FileShare.ReadWrite);
                using var sw = new System.IO.StreamWriter(fs, System.Text.Encoding.UTF8);
                sw.AutoFlush = true;
                sw.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
            }
            catch { /* never throw from the logger */ }
        }
    }
}
