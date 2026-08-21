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

        // ── Adaptive budget ───────────────────────────────────────────────────
        /// <summary>Cut the voxel budget temporarily while the view is MOVING and the
        /// display cannot keep up.
        ///
        /// Only while moving, on purpose: a still frame that renders slowly is one you are
        /// studying, and quietly throwing away half of it is the wrong answer. A frame you
        /// are dragging around is one where smoothness matters more than completeness.</summary>
        public volatile bool  AdaptiveBudget = true;

        /// <summary>Throttle below this VPS, recover above AdaptiveGoodVps. The gap is
        /// hysteresis: with one threshold the budget would oscillate around it, which reads
        /// as flicker rather than as a frame-rate save.</summary>
        public volatile float AdaptiveLowVps  = 10f;
        public volatile float AdaptiveGoodVps = 15f;

        /// <summary>Floor for the throttle, as a fraction of MaxVoxels. Not zero — the
        /// point is to stay usable while moving, not to blank the display.</summary>
        public volatile float AdaptiveFloor = 0.15f;
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
        /// <summary>One sensitivity for ALL THREE translation axes, one for ALL THREE
        /// rotation axes, one for the button zoom. Deliberately not per-axis: the puck
        /// should feel isotropic, so X/Y/Z must not be trimmable against each other.
        /// Units are world-units (or radians) per second at full deflection.</summary>
        public volatile float NavPanRate  = 9.0f;
        public volatile float NavRotRate  = 3.0f;
        public volatile float NavZoomRate = 2.0f;

        /// <summary>The raw count the puck reports at FULL deflection, for the three
        /// TRANSLATION axes and the three ROTATION axes respectively.
        ///
        /// The driver hands back raw counts, not a -1..1 signal, so these are what turn
        /// them into one and what make the rates above mean "world units per second".
        /// They are split because the puck does not report the same range for both
        /// groups — one shared value is what left translation and rotation feeling
        /// unequal. Within a group all three axes share a scale, so translation stays
        /// uniform in X, Y and Z by construction.
        ///
        /// 350 is the usual 3Dconnexion full-scale. The diagnostics block reports the
        /// peak actually seen per group, and Calibrate adopts it — deflect the puck hard
        /// in every direction first.</summary>
        public volatile float NavFullScaleTrans = 350f;
        public volatile float NavFullScaleRot   = 350f;

        /// <summary>Dead-zone as a FRACTION of full scale (0.08 = ignore the first 8%).
        /// A puck at rest still reports a few counts; without this they integrate into
        /// permanent scene drift. Motion is re-scaled past the threshold, so there is no
        /// jump at the edge.</summary>
        public volatile float NavDeadzone = 0.08f;

        /// <summary>Rotate about the scene's own axes (true) or the display's fixed ones.
        /// Local is what "turn the board over" means once the board is already tilted;
        /// global is easier when lining the board up with the volume itself.</summary>
        public volatile bool  NavLocalAxes = true;

        /// <summary>Lock rotation about an individual axis during normal camera mode.
        /// The axes are whichever frame NavLocalAxes selects, so a lock always means the
        /// axis it is named after. Inspection mode locks all three regardless.</summary>
        public volatile bool  LockRotX = false;
        public volatile bool  LockRotY = false;
        public volatile bool  LockRotZ = false;

        // ── Inspection mode ───────────────────────────────────────────────────
        /// <summary>Both puck buttons together toggle this. In inspection mode the puck's
        /// translation drives a probe inside the volume instead of panning the scene;
        /// rotation still turns the scene, so you can look around what you are probing.
        /// Everything dims except whatever the probe is over.</summary>
        public volatile bool  InspectMode = false;

        /// <summary>Probe position, in DISPLAY space, clamped to the volume. Display space
        /// rather than scene space on purpose: the probe is a physical pointer in the box,
        /// so it must stay put when the scene is rotated around it.</summary>
        public volatile float InspectX = 0f, InspectY = 0f, InspectZ = 0f;

        /// <summary>Brightness for everything the probe is NOT over. 0.75 = 25% darker.</summary>
        public volatile float InspectDim = 0.75f;

        /// <summary>Probe speed, world units per second at full deflection.</summary>
        public volatile float InspectRate = 3.0f;

        /// <summary>How far the probe reaches for something to select, world units. A
        /// reach rather than a hit test: traces are one voxel wide, so requiring the probe
        /// to be exactly on one would make selection almost impossible by hand.</summary>
        public volatile float InspectSnap = 0.6f;

        /// <summary>What the probe is over, formatted for the shell's overlay. Written by
        /// the game thread, read by the UI; string assignment is atomic.</summary>
        public string InspectInfo = "";

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
        /// <summary>Sampling density for pour OUTLINES (1 = the frame voxel spacing) and
        /// for the HATCH inside a filled pour (1 = a line every 6 voxels). Both are
        /// multipliers where higher means denser, and both are clamped in the renderer —
        /// a hatch finer than the voxel spacing is a solid fill wearing a hatch label.</summary>
        public volatile float  PcbPourDensity  = 1.0f;
        public volatile float  PcbHatchDensity = 1.0f;
        public volatile bool   PcbHoles      = true;
        public volatile bool   PcbVias       = false;   // via barrels through the stack
        /// <summary>A plated hole at or below this diameter counts as a via rather than
        /// a component through-hole. 0.7 mm covers ordinary vias while leaving even
        /// small component leads (0.8 mm+) classified as through-holes.</summary>
        public volatile float  PcbViaMaxDia  = 0.7f;    // mm

        /// <summary>Drawn via radius in VOXELS, the same for every via regardless of its
        /// real diameter. Real vias are sub-voxel at any board scale that fits the volume,
        /// so drawing them faithfully draws them invisibly.</summary>
        public volatile float  PcbViaSize    = 3.0f;
        public volatile bool   PcbMeshes     = true;
        public volatile bool   PcbCad        = true;    // STEP solids as edge wireframes
        public volatile float  PcbCadBright  = 1.0f;

        /// <summary>Directional shading for the STEP wireframe, using the adjacent-face
        /// normals StepParser recovers from planar faces. Ambient is a floor rather than
        /// an addition: at 0 an edge facing across the light goes fully dark and the shape
        /// breaks up, so the default keeps a readable minimum.
        ///
        /// The light direction is in the BOARD frame (Z up), not the display frame, so it
        /// stays fixed to the part as the scene is rotated — which is what makes the
        /// shading read as the part's own form rather than as a rotating gradient.</summary>
        public volatile bool   PcbCadLighting = true;

        /// <summary>Flat-shaded fill on the STEP model's planar faces, and how densely.
        ///
        /// Density is samples per voxel: 1.0 fills solid, lower is sparser. It defaults
        /// below 1 because the fill is the most expensive thing on a board -- roughly
        /// 42,000 voxels for the 1143 mm2 of planar face on a real 2-layer export at full
        /// density -- and because the display is transparent, so a solid fill also shows
        /// its own back faces through its front.</summary>
        public volatile bool   PcbCadSurfaces = true;
        public volatile float  PcbCadSurfaceDensity = 0.6f;

        /// <summary>Nudge the 3D model along Z, world units. 0 seats it on the topmost
        /// layer of the exploded stack, which is where it physically belongs — the Gerber
        /// layers are the board, the STEP model is what is mounted on it.</summary>
        public volatile float  PcbCadZOffset = 0f;
        public volatile float  PcbCadAmbient  = 0.35f;
        public volatile float  PcbCadLightX   = 0.3f;
        public volatile float  PcbCadLightY   = 0.2f;
        public volatile float  PcbCadLightZ   = 1.0f;
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

        /// <summary>Per-layer visibility and colour choices, keyed by layer file name:
        /// "name|RRGGBB|1;name||0" — an empty colour field means "keep the default".
        ///
        /// Persisted because a re-import rebuilds every layer from scratch, and the last
        /// board is re-imported on every launch. Without this, changing a layer colour
        /// would look like it silently failed the next time the app opened.</summary>
        public string PcbLayerPrefs = "";

        /// <summary>Set by the UI to ask the game thread to (re)import PcbPath.
        /// The game thread clears it — imports never run on the UI thread.</summary>
        public volatile bool PcbImportRequested = false;

        /// <summary>The file the import is working on right now, or "" when idle.
        ///
        /// Written by the game thread, read by the UI. Imports run on the game thread, so
        /// a slow file stalls rendering and the app looks hung; this is what turns that
        /// into "parsing big_assembly.step" so the difference between slow and stuck is
        /// visible. Reference assignment of a string is atomic, so no lock is needed.</summary>
        public string PcbImportStatus = "";

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
