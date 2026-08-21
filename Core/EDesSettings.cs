// ═══════════════════════════════════════════════════════════════════════════
//  EDesSettings.cs — the app's own persisted state (%AppData%/EDes/edes.json)
//
//  Same threading discipline as GameSettings: the UI thread writes, the game
//  thread reads, scalars are volatile and a one-frame torn read is acceptable.
//  Strings are reference-assigned (atomic) and never mutated in place.
//
//  The engine saves this automatically (IGameSettings) on the debounced settings
//  save, so nothing here needs an explicit Save() call from the UI.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Text.Json;

namespace EDes
{
    public enum EDesMode { Education = 0, Scope = 1, Pcb = 2 }

    public sealed class EDesSettings : IGameSettings
    {
        // ── Mode ──────────────────────────────────────────────────────────────
        public volatile int Mode = (int)EDesMode.Education;

        // ── Global render budget / style ───────────────────────────────────────
        /// <summary>Hard per-frame voxel ceiling. Everything the app draws is counted
        /// against this, HUD text included.</summary>
        public volatile int   MaxVoxels   = 150_000;
        public volatile float TextSize    = 0.20f;
        public volatile int   FontIndex   = 2;      // 0 Classic, 1 Blocky, 2 Bold
        public volatile float TextWeight  = 1.0f;
        public volatile bool  ShowLabels  = true;
        public volatile bool  ShowBackdrop = true;  // grid floor + orientation rings
        public volatile bool  ShowHudPanel = true;  // title / totals / voxel readout
        public volatile float PlaneY       = 0.1f;  // the HUD + scope plane

        // ── Camera / SpaceNavigator ───────────────────────────────────────────
        public volatile bool  NavEnabled  = true;
        public volatile bool  ShowNavDiag = false;   // SpaceNav readout in the volume (key V)
        public volatile float NavPanRate  = 2.5f;
        public volatile float NavRotRate  = 1.5f;
        public volatile float NavZoomRate = 1.2f;

        /// <summary>The raw count the puck reports at FULL deflection. The driver hands
        /// back raw counts, not a -1..1 signal, so this is what turns them into one and
        /// what makes the three rates above mean "world units per second". 350 is the
        /// usual 3Dconnexion full-scale; if a build already normalises, set this to 1.
        /// The diagnostics block reports the peak actually seen — deflect the puck hard
        /// on every axis and set this to that number.</summary>
        public volatile float NavFullScale = 350f;

        /// <summary>Dead-zone as a FRACTION of full scale (0.08 = ignore the first 8%).
        /// A puck at rest still reports a few counts; without this they integrate into
        /// permanent scene drift. Motion is re-scaled past the threshold, so there is no
        /// jump at the edge.</summary>
        public volatile float NavDeadzone = 0.08f;

        // ── Education mode: the circuit ───────────────────────────────────────
        public volatile int    PresetIndex = 0;
        // float, not double: volatile requires a 32-bit type, and 24-bit float
        // precision is far beyond what a resistor value needs.
        public volatile float  SourceVolts = 12.0f;
        public volatile float  R1 = 100f, R2 = 220f, R3 = 470f;
        public volatile float  FlowSpeed  = 1.0f;
        public volatile bool   FlowPaused = false;

        // ── Scope ─────────────────────────────────────────────────────────────
        /// <summary>0 Synthetic, 1 Serial (ASCII stream), 2 SCPI over TCP (LXI socket),
        /// 3 SCPI over USBTMC (needs a VISA runtime). See ScopeInput.</summary>
        public volatile int    ScopeMode         = 0;
        public          string ScopePort         = "";            // e.g. "COM4"
        public volatile int    ScopeBaud         = 115200;
        public          string ScopeHost         = "";            // instrument IP for SCPI/TCP
        public volatile int    ScopeTcpPort      = 5555;          // Rigol/LXI raw socket
        public          string ScopeVisaResource = "";            // blank = first USB instrument
        public volatile float  ScopePollHz       = 10f;           // SCPI acquisitions/second
        public volatile float  ScopeVoltsPerDiv  = 1.0f;
        public volatile int    ScopeChannelMask  = 0xF;
        public volatile int    ScopeTriggerCh    = 0;             // -1 = free run
        public volatile float  ScopeTriggerLevel = 0f;
        public volatile bool   ScopeTriggerRising = true;
        public volatile bool   ScopeFrozen       = false;
        public volatile bool   ScopeMeasurements = true;
        public volatile float  SynthFreqHz       = 50f;

        // ── PCB ───────────────────────────────────────────────────────────────
        public          string PcbPath       = "";     // file or fabrication folder
        public volatile float  LayerSpacing  = 0.35f;
        public volatile float  TrackScale    = 1.0f;
        public volatile bool   PcbPads       = true;
        public volatile bool   PcbRegions    = true;
        public volatile bool   PcbFillRegions = false;
        public volatile bool   PcbHoles      = true;
        public volatile bool   PcbVias       = false;   // via barrels through the stack
        /// <summary>A plated hole at or below this diameter counts as a via rather than
        /// a component through-hole. 0.7 mm covers ordinary vias while leaving even
        /// small component leads (0.8 mm+) classified as through-holes.</summary>
        public volatile float  PcbViaMaxDia  = 0.7f;    // mm
        public volatile bool   PcbMeshes     = true;
        public volatile bool   PcbCursor     = false;
        public volatile float  PcbCursorX    = 0f;     // mm, board coordinates
        public volatile float  PcbCursorY    = 0f;
        public volatile float  PcbBrightness = 1.0f;
        public volatile int    PcbIsolate    = -1;     // -1 = show all layers
        public volatile int    MeshPointBudget = 80_000;
        public volatile bool   PcbComponents      = true;    // markers from the placement file
        public volatile bool   PcbComponentLabels = true;    // designators beside them
        public volatile int    PcbLabelLimit      = 150;     // skip labels above this part count
        public volatile bool   PcbShowDocs        = true;    // document inventory in the volume

        /// <summary>Set by the UI to ask the game thread to (re)import PcbPath.
        /// The game thread clears it — imports never run on the UI thread.</summary>
        public volatile bool PcbImportRequested = false;

        // ── Persistence ───────────────────────────────────────────────────────

        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EDes");
        private static readonly string PathName = Path.Combine(Dir, "edes.json");
        private static readonly JsonSerializerOptions Opts =
            new() { WriteIndented = true, IncludeFields = true };

        public static EDesSettings Load()
        {
            try
            {
                if (File.Exists(PathName))
                {
                    var s = JsonSerializer.Deserialize<EDesSettings>(File.ReadAllText(PathName), Opts);
                    if (s != null)
                    {
                        s.PcbImportRequested = s.PcbPath.Length > 0;   // reload the last board
                        return s;
                    }
                }
            }
            catch (Exception ex) { App.Log($"[EDesSettings.Load] {ex.Message}"); }
            return new EDesSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathName, JsonSerializer.Serialize(this, Opts));
            }
            catch (Exception ex) { App.Log($"[EDesSettings.Save] {ex.Message}"); }
        }
    }
}
