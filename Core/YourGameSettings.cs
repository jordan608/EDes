// ═══════════════════════════════════════════════════════════════════════════
//  YourGameSettings.cs — STARTER STUB for your game's persisted settings
//
//  Copy/rename this alongside YourGame.cs. Add your own settings as volatile
//  fields (UI thread writes / game thread reads — a 1-frame torn read is fine).
//  The engine saves this automatically when settings change (via IGameSettings).
//
//  Stored at %AppData%/EDes/yourgame.json.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Text.Json;

namespace EDes
{
    public sealed class YourGameSettings : IGameSettings
    {
        // TODO: replace with your own settings.
        public volatile float ExampleValue = 50f;

        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EDes");
        private static readonly string PathName = Path.Combine(Dir, "yourgame.json");
        private static readonly JsonSerializerOptions Opts =
            new() { WriteIndented = true, IncludeFields = true };

        public static YourGameSettings Load()
        {
            try
            {
                if (File.Exists(PathName))
                {
                    var s = JsonSerializer.Deserialize<YourGameSettings>(File.ReadAllText(PathName), Opts);
                    if (s != null) return s;
                }
            }
            catch (Exception ex) { App.Log($"[YourGameSettings.Load] {ex.Message}"); }
            return new YourGameSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathName, JsonSerializer.Serialize(this, Opts));
            }
            catch (Exception ex) { App.Log($"[YourGameSettings.Save] {ex.Message}"); }
        }
    }
}
