// ═══════════════════════════════════════════════════════════════════════════
//  GameSettings.cs — Shared state between the UI thread and game thread
//
//  Threading model:
//    UI thread WRITES fields.  Game thread READS fields.
//    Never call SDK functions from the UI thread.
//
//  Scalar fields → volatile   (torn reads within 1 frame are acceptable)
//  Arrays/objects → atomic reference swap  (copy, modify, assign)
//
//  To update an array from the UI thread:
//    var old  = MyArray;                      // atomic read
//    var copy = (MyType[])old.Clone();        // copy
//    copy[index] = newValue;                  // modify copy
//    MyArray = copy;                          // atomic swap — game thread sees
//                                             // fully old or fully new, never partial
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Voxon;

namespace EDes
{
    public sealed class GameSettings
    {
        // ── Visual quality ──────────────────────────────────────────────────
        // These map directly to vxl_state_t fields — applied each frame in GameLoop.
        public volatile float Gamma           = 2.0f;    // display gamma (0.5–4.0)
        public volatile int   DitherMode      = 1;       // 0=off, 1=on
        public volatile int   DitherThreshold = 128;     // 0–255
        public volatile bool  ShowDebugBorder = false;   // draw display volume border
        public volatile float VoxelDensity    = 1.0f;    // voxelization density (re-meshes model)

        // ── Frame-rate cap ──────────────────────────────────────────────────
        // The Voxon display can't show volumes faster than ~30/s. When enabled,
        // the game loop sleeps off any time it's running ahead of a 30 VPS budget,
        // saving CPU/heat and avoiding render work for frames nothing will see.
        public volatile bool  CapVps30        = true;

        // Game-specific settings (e.g. player speed, particle budget) live in the
        // game's own settings object — see DemoGameSettings. Engine settings stay here.

        // ── Audio ────────────────────────────────────────────────────────────
        public volatile float SfxVolume       = 1.0f;   // 0..1 one-shot SFX
        public volatile float MusicVolume      = 0.6f;   // 0..1 looping music

        // ── Model transform (test controls) ──────────────────────────────────
        // Right-drag rotates (yaw/pitch), mouse wheel scales, arrow keys move XY.
        // Written by the UI (mouse) and game thread (keys); read in DrawModel.
        public volatile float ModelYaw   = 0f;   // radians, around vertical (Z)
        public volatile float ModelPitch = 0f;   // radians, around horizontal (X)
        public volatile float ModelScale = 1f;   // user scale multiplier

        // ── Simulator camera ─────────────────────────────────────────────────
        // Written by GameLoop ([/] keys), read by GameLoop (Rend2D) and UI (sliders).
        public volatile float EmuHAng = 0f;   // horizontal yaw  (radians)
        public volatile float EmuVAng = 0f;   // vertical tilt   (radians)
        public volatile float EmuDist = 4f;   // viewing distance (world units)

        // ── Preview buffer size request ───────────────────────────────────────
        // UI writes the pixel size of the PreviewImage control.
        // Game loop reads it and reallocates the render buffer when it changes.
        public volatile int PreviewRequestW = 640;
        public volatile int PreviewRequestH = 500;

        // ── Motor control ─────────────────────────────────────────────────────
        // UI sets MotorRpmRequest ≥ 0. Game loop reads, calls SetRPM, clears to -1.
        // -1 = idle (no pending request)   0 = stop   >0 = start at this RPM
        public volatile int MotorRpmRequest = -1;
        public volatile int LiveMotorRpm    = 0;   // game loop writes current RPM

        // ── Transient signals ─────────────────────────────────────────────────
        // UI sets LaunchRequested when the user clicks "Play".
        // Game loop reads it once and clears it.
        public volatile bool LaunchRequested  = false;
        public volatile bool GameLoopRunning  = false;  // game loop sets true/false

        // ── Live readout (game loop writes, UI polls at ~1 Hz) ───────────────
        public volatile float LiveVps        = 0f;
        public volatile int   LiveScore      = 0;
        public volatile int   LiveLives      = 3;

        // ── Diagnostics (game loop writes, status bar reads) ──────────────────
        public volatile int   LiveVoxelCount    = 0;      // voxels drawn this frame
        public volatile float LiveFrameMs       = 0f;     // per-frame work time (ms)
        public volatile bool  HardwareConnected = false;  // real Voxon device present
        public          string ModelSource      = "(loading)";  // GLB filename or fallback

        // ── Preview callback ──────────────────────────────────────────────────
        // Set once by App.axaml.cs before the game thread starts.
        // Game loop calls it after every Rend2D. Never changes after startup.
        // No locking needed — set-once pattern.
        public Action<byte[], int, int>? OnPreviewFrame;

        // ── Pre-flight splash bridge (see VoxonPreflight.cs / VoxonHardwareCheck.cs) ──
        // GameLoop drives VoxonPreflight.Run() BEFORE SDK init, bridged to the splash
        // overlay through these fields (its IPreflightUi doesn't know about Avalonia).
        // GameLoop (background thread) writes Status/Warning/ShowButtons; the UI
        // thread reads them once/tick. The UI writes Choice on a button click;
        // GameLoop polls it and clears back to PreflightChoice.None.
        public volatile string SplashStatus      = "Starting…";
        public volatile bool   SplashWarning      = false;
        public volatile bool   SplashShowButtons  = false;
        public volatile int    SplashChoice       = PreflightChoice.None;

        // Set once by App.axaml.cs to the desktop lifetime's Shutdown — GameLoop
        // calls this if the operator chooses Quit on the pre-flight screen (it has
        // no Avalonia reference of its own; this is its only way off the boot thread).
        public Action? RequestShutdown;

        // ── Lighting (atomic reference swap — see LightingConfig) ──────────────
        // Unlike the scalar fields above, lighting is a whole object. The UI clones
        // the current config, mutates the copy, and assigns it back; the game thread
        // reads the reference once per frame. Reference assignment is atomic, so the
        // game thread never sees a half-updated config. Do NOT mutate Lighting in
        // place from the UI — always clone-and-swap.
        private LightingConfig _lighting = new LightingConfig();
        public LightingConfig Lighting
        {
            get => _lighting;
            set => _lighting = value;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Persistence
        // ─────────────────────────────────────────────────────────────────────

        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EDes");
        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        // IncludeFields lets System.Text.Json (de)serialize LightingConfig, whose
        // members are public fields rather than properties.
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            IncludeFields = true,
        };

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var dict = new Dictionary<string, object>
                {
                    // Add every field you want persisted here.
                    // Key = string used in Load(). Value = current field value.
                    ["Gamma"]           = Gamma,
                    ["DitherMode"]      = DitherMode,
                    ["DitherThreshold"] = DitherThreshold,
                    ["ShowDebugBorder"] = ShowDebugBorder,
                    ["VoxelDensity"]    = VoxelDensity,
                    ["CapVps30"]        = CapVps30,
                    ["SfxVolume"]       = SfxVolume,
                    ["MusicVolume"]     = MusicVolume,
                    ["Lighting"]        = Lighting,
                };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dict, JsonOpts));
            }
            catch (Exception ex) { App.Log($"[Settings.Save] {ex.Message}"); }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(SettingsPath));
                if (dict == null) return;

                // Helper lambdas — return the stored value or the default if missing.
                // This means adding new fields to Save() is safe; old settings files
                // just use the default value for the new field.
                float F(string k, float d) => dict.TryGetValue(k, out var v) ? v.GetSingle()  : d;
                int   I(string k, int   d) => dict.TryGetValue(k, out var v) ? v.GetInt32()   : d;
                bool  B(string k, bool  d) => dict.TryGetValue(k, out var v) ? v.GetBoolean() : d;

                Gamma           = F("Gamma",           2.0f);
                DitherMode      = I("DitherMode",      1);
                DitherThreshold = I("DitherThreshold", 128);
                ShowDebugBorder = B("ShowDebugBorder",  false);
                VoxelDensity    = F("VoxelDensity",     1.0f);
                CapVps30        = B("CapVps30",         true);
                SfxVolume       = F("SfxVolume",       1.0f);
                MusicVolume     = F("MusicVolume",     0.6f);

                if (dict.TryGetValue("Lighting", out var lv))
                {
                    var cfg = lv.Deserialize<LightingConfig>(JsonOpts);
                    if (cfg != null)
                    {
                        // Guard against an old/short file leaving Spots null or undersized.
                        if (cfg.Spots == null || cfg.Spots.Length == 0)
                            cfg.Spots = new LightingConfig().Spots;
                        Lighting = cfg;
                    }
                }
            }
            catch (Exception ex) { App.Log($"[Settings.Load] {ex.Message}"); }
        }

        public void Reset()
        {
            // Copy all defaults from a fresh instance — single source of truth.
            var d = new GameSettings();
            Gamma           = d.Gamma;
            DitherMode      = d.DitherMode;
            DitherThreshold = d.DitherThreshold;
            ShowDebugBorder = d.ShowDebugBorder;
            VoxelDensity    = d.VoxelDensity;
            CapVps30        = d.CapVps30;
            SfxVolume       = d.SfxVolume;
            MusicVolume     = d.MusicVolume;
            Lighting        = new LightingConfig();
        }
    }
}
