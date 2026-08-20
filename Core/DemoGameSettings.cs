// ═══════════════════════════════════════════════════════════════════════════
//  DemoGameSettings.cs — Per-game settings for DemoGame (its own JSON)
//
//  Game-specific settings live with the game, separate from the engine's
//  GameSettings. Persisted to %AppData%/EDes/game.json.
//
//  The engine saves the active game's settings whenever it debounces a settings
//  change — it calls Save() through IGameSettings (see MainWindow.DebounceSave).
//  Loading is the game's job (it knows the concrete type) — see DemoGame ctor.
//
//  Fields are volatile (UI thread writes / game thread reads), matching the
//  GameSettings concurrency model — a 1-frame torn read is acceptable.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Text.Json;

namespace EDes
{
    /// <summary>Lets the engine persist a game's settings without knowing its type.</summary>
    public interface IGameSettings { void Save(); }

    public sealed class DemoGameSettings : IGameSettings
    {
        public volatile float PlayerSpeed    = 3.0f;   // world units / second
        public volatile int   ParticleBudget = 600;    // max particles drawn / frame

        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EDes");
        private static readonly string PathName = Path.Combine(Dir, "game.json");
        private static readonly JsonSerializerOptions Opts =
            new() { WriteIndented = true, IncludeFields = true };

        public static DemoGameSettings Load()
        {
            try
            {
                if (File.Exists(PathName))
                {
                    var s = JsonSerializer.Deserialize<DemoGameSettings>(File.ReadAllText(PathName), Opts);
                    if (s != null) return s;
                }
            }
            catch (Exception ex) { App.Log($"[DemoGameSettings.Load] {ex.Message}"); }
            return new DemoGameSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathName, JsonSerializer.Serialize(this, Opts));
            }
            catch (Exception ex) { App.Log($"[DemoGameSettings.Save] {ex.Message}"); }
        }
    }
}
