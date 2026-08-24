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
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EDes
{
    public enum EDesMode { Education = 0, Scope = 1, Pcb = 2 }

    /// <summary>The inspection stages the both-buttons gesture cycles through.</summary>
    public enum EDesInspect
    {
        /// <summary>Normal camera driving; no probe.</summary>
        Off = 0,
        /// <summary>Copper and board outline only — for tracing signals.</summary>
        Signal = 1,
        /// <summary>Parts and board outline only — for identifying components.</summary>
        Component = 2,
    }

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

        /// <summary>Minimum voxels per glyph CELL, which is what sets the real text floor.
        /// A 5x7 glyph needs about one voxel per cell to be readable at all and two to be
        /// comfortable; below one, adjacent lit cells share voxels and the character turns
        /// into a blob. Expressed in voxels rather than world units so it holds on both a
        /// VX2 and a VX2-XL, and at any voxel density.</summary>
        public volatile float MinTextCellVoxels = 1.6f;
        public volatile int   FontIndex   = 2;      // 0 Classic, 1 Blocky, 2 Bold
        public volatile float TextWeight  = 1.0f;
        public volatile bool  ShowLabels  = true;
        public volatile bool  ShowHudPanel = true;  // title / totals / voxel readout
        // -- HUD text anchor, as FRACTIONS of the live display extents ---------
        //
        // Fractions rather than world units, because the anchor has to mean the same
        // thing on a VX2 and a VX2-XL. -1 is the left/top edge, +1 the right/bottom,
        // 0 the centre; the app multiplies by the radius and half-height it read from
        // the SDK this frame, so one setting lands in the same visual place on any unit.
        //
        // This replaces the old absolute PlaneY (0.1 world units) -- a VX2 measurement
        // hard-coded into a setting, which would have sat in a different relative place
        // on every other display size.

        /// <summary>Horizontal anchor of the left-aligned text column, as a fraction of
        /// the usable half-width AT the HUD plane. -1 = hard left. Deliberately a fraction
        /// of the half-width at the plane rather than of the radius: the volume is a
        /// cylinder, so the leftmost point at the HUD plane's y is INSIDE the radius, and
        /// scaling the radius would push the first glyph column out of the volume where it
        /// is clipped -- text that silently loses its first character.</summary>
        public volatile float HudFracX = -1.0f;

        /// <summary>Depth of the HUD/scope plane, as a fraction of the radius. The old
        /// absolute default was 0.1 world units against a 4-unit radius, i.e. 0.025.</summary>
        public volatile float HudFracY = 0.025f;

        /// <summary>Vertical anchor of the first text row, as a fraction of the display's
        /// half-height. -1 = the very top (remember -Z is up). Clamped in ReadBounds so
        /// the band can never start above the top of the volume.</summary>
        public volatile float HudFracZ = -1.0f;

        /// <summary>Vertical half-height of the volume as a fraction of its radius, used
        /// ONLY when the SDK does not report a usable bound of its own.
        ///
        /// It exists because LedHostCS.GetAspectRatioZ returns xsiz/64 -- the same value as
        /// GetAspectRatioX, with a comment saying so. That is the RADIUS, not the vertical
        /// half-height, so trusting it told this app the volume was twice as tall as it is
        /// and every text row was anchored roughly double its intended height. 0.5 matches
        /// a VX2 (radius 4, half-height 2). See EDesApp.ReadBounds.</summary>
        public volatile float ZHalfRatio = 0.5f;

        // ── Camera / SpaceNavigator ───────────────────────────────────────────
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

        /// <summary>Flip all three puck translation axes at once, rotation untouched.
        ///
        /// Exists because which direction feels right depends on whether you read the puck
        /// as moving the MODEL or as moving your VIEWPOINT, and those two readings are
        /// opposite on every translation axis. That is a preference, not a fact, so it is a
        /// switch rather than a default someone has to live with.</summary>
        public volatile bool  NavInvertTranslation = false;

        /// <summary>Which puck axis drives which scene axis (see NavAxisMap). Selectable
        /// because the correct binding is a property of the hardware and the hand holding
        /// it, not something this code can know -- the SDK's own axis NAMES are wrong on
        /// this puck.</summary>
        public volatile int   NavAxisMap = 0;

        /// <summary>Draw the axis triad the SpaceNavigator is driving, in the volume. On by
        /// default while there is a mapping to choose: a mapping you cannot see is one you
        /// have to discover by pushing the puck and watching what happens.</summary>
        public volatile bool  ShowNavAxes = true;

        /// <summary>Lock rotation about an individual axis during normal camera mode.
        /// The axes are whichever frame NavLocalAxes selects, so a lock always means the
        /// axis it is named after. Inspection mode locks all three regardless.</summary>
        public volatile bool  LockRotX = false;
        public volatile bool  LockRotY = false;
        public volatile bool  LockRotZ = false;

        // ── Inspection mode ───────────────────────────────────────────────────
        /// <summary>Which inspector is active: 0 camera, 1 signal, 2 component.
        ///
        /// Both puck buttons together CYCLE through them. A cycle rather than a toggle
        /// because the two inspectors answer different questions — one about copper, one
        /// about parts — and each hides what the other needs, so there is no single view
        /// that serves both.
        ///
        /// In any inspector the puck's translation drives a probe instead of panning;
        /// rotation is locked so the board cannot slide out from under it. Everything dims
        /// except whatever the probe is over. See EDesInspect.</summary>
        public volatile int   InspectStage = 0;

        /// <summary>Convenience over InspectStage: any inspector at all.</summary>
        public bool InspectMode => InspectStage != 0;

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

        /// <summary>A LATCHED selection made from the pick list, as "net:<id>" or
        /// "comp:<designator>", or empty.
        ///
        /// Separate from what the probe is hovering, and deliberately so: a pick made by
        /// name has to survive letting go of the puck and moving the view, which a
        /// hover-derived selection cannot. When both exist the pick wins — it was chosen
        /// explicitly, where a hover is wherever the pointer happens to be.</summary>
        public string PickedKey = "";

        /// <summary>What the probe is over, formatted for the shell's overlay. Written by
        /// the game thread, read by the UI; string assignment is atomic.</summary>
        public string InspectInfo = "";

        // ── Education mode: the circuit ───────────────────────────────────────
        public volatile int    PresetIndex = 0;
        // float, not double: volatile requires a 32-bit type, and 24-bit float
        // precision is far beyond what a resistor value needs.
        public volatile float  SourceVolts = 12.0f;
        /// <summary>Resistor values, keyed "presetIndex:resistorIndex".
        ///
        /// Replaces three fixed R1/R2/R3 fields. Those capped every circuit at three
        /// tunable parts -- so the four-resistor Wheatstone bridge could not be set up at
        /// all -- and they were SHARED across presets, so the sliders for a one-resistor
        /// circuit were dead controls that silently rewrote another circuit's values.
        ///
        /// Per preset as well as per resistor, so switching circuits does not carry a 26k
        /// divider leg into a 100-ohm parallel demo. A missing key means "use the
        /// preset's own default", which is what makes new presets need no migration.</summary>
        public Dictionary<string, float> Resistors { get; set; } = new();

        /// <summary>Guards every touch of Resistors, including the save's serialization.
        ///
        /// It needs one because a plain Dictionary here crossed threads three ways: the
        /// GAME thread writes it from the volume's arrow keys (EDesApp.ScaleResistor), the
        /// UI thread writes it from the typed boxes and the reset button, and Save
        /// enumerates it. Concurrent inserts during a bucket resize can lose a write,
        /// throw, or corrupt the chain so a later lookup spins forever -- and enumerating
        /// during an insert throws inside Save, which is caught, so settings would silently
        /// stop persisting.
        ///
        /// The three fixed volatile float fields this replaced were safe by construction;
        /// swapping them for a collection quietly gave that up. A lock rather than a
        /// ConcurrentDictionary because Save has to serialize a CONSISTENT snapshot, which
        /// a concurrent collection does not offer.
        ///
        /// Private, so System.Text.Json does not serialize it even with IncludeFields.</summary>
        private readonly object _resistorLock = new();

        /// <summary>Store one resistor value. Safe from either thread.</summary>
        public void SetResistorOhms(string key, float ohms)
        {
            lock (_resistorLock) Resistors[key] = ohms;
        }

        /// <summary>Read one resistor value. Safe from either thread.</summary>
        public bool TryGetResistorOhms(string key, out float ohms)
        {
            lock (_resistorLock) return Resistors.TryGetValue(key, out ohms);
        }

        /// <summary>Forget one resistor value, so the preset's own default applies.</summary>
        public void ClearResistorOhms(string key)
        {
            lock (_resistorLock) Resistors.Remove(key);
        }

        /// <summary>Serialize under the same lock the mutators take. Used by Save.</summary>
        internal string SerializeLocked(JsonSerializerOptions opts)
        {
            lock (_resistorLock) return JsonSerializer.Serialize(this, opts);
        }
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
        /// <summary>Cross-hatch copper pours. ON by default, because outline-only made
        /// them effectively invisible: the full-board ground pour's outline runs 0.58 mm
        /// inside the board edge -- about three voxels -- so it lands on top of the outline
        /// layer and reads as the board perimeter rather than as a plane of copper. The
        /// hatch costs about 7,100 voxels for the 965 mm2 of pour on a real 2-layer board,
        /// roughly 5% of the default budget, which is a cheap price for the largest
        /// feature on the board being visible at all.</summary>
        public volatile bool   PcbFillRegions = true;
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
        /// <summary>Convert STEP to a mesh with an external tessellator so CURVED faces
        /// can be filled and lit. StepParser fills planar faces without a kernel, but a
        /// round part is mostly curved faces, so it comes out as a sparse cage with nothing
        /// to light. Off means wireframe-only, which is still perfectly usable.</summary>
        /// <summary>Show the schematic sheet instead of the board. OFF by default: the
        /// board is what this mode is for, and a schematic filling the volume on import
        /// would be a surprise rather than a feature.</summary>
        public volatile bool   ShowSchematic = false;

        /// <summary>Which sheet, when the print has more than one.</summary>
        public volatile int    SchematicSheet = 0;

        /// <summary>Draw the sheet's labels. Separate from the toggle above because on a
        /// zoomed-out sheet most labels are below the legible size and skipped anyway --
        /// turning them off entirely saves the budget they were spending on the few that
        /// did qualify.</summary>
        public volatile bool   SchematicText = true;

        public volatile bool   PcbTessellate = true;

        /// <summary>Max element size for the tessellator, mm. Smaller is smoother and costs
        /// triangles; this is the knob for a model that arrives too coarse or too heavy.</summary>
        /// Default 1.0, chosen from measurement rather than taste: on a real 2-layer
        /// export clmax 0.4 gives 44,158 triangles (~132,000 voxels, which is most of the
        /// 150,000 default budget spent on surfaces alone), 1.0 gives 11,978 (~36,000) and
        /// 2.0 gives 6,414. 1.0 leaves room for the board underneath it.
        public volatile float  PcbTessellateTol = 1.0f;

        /// <summary>Explicit tessellator command, or empty to auto-discover (gmsh, then
        /// FreeCAD). Present so an unusual or commercial toolchain can be used without a
        /// code change.</summary>
        public          string PcbTessellator = "";

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

        // -- Point light ------------------------------------------------------
        /// <summary>Light the CAD from a POSITION rather than a direction. On by default:
        /// a directional light gives every face on the board the same L, so two identical
        /// parts at opposite corners shade identically and the whole board reads flat. A
        /// point light is what makes one side of a component brighter than the other.</summary>
        public volatile bool   PcbCadPointLight = true;

        /// <summary>Light position as fractions of the board's own half-width, half-height
        /// and tallest part -- not millimetres, so the same numbers put the lamp in the same
        /// relative place on a 16 mm sensor board and a 300 mm backplane. Default: off one
        /// corner, well above the board.</summary>
        public volatile float  PcbCadLightFx = 0.8f;
        public volatile float  PcbCadLightFy = -0.8f;
        public volatile float  PcbCadLightFz = 3.0f;

        /// <summary>Half-strength distance, as a fraction of the board diagonal. 0 turns
        /// falloff off entirely, leaving direction without distance.</summary>
        public volatile float  PcbCadLightRange = 1.2f;

        /// <summary>Draw a marker at the lamp. On by default -- three position numbers with
        /// no visible referent are very hard to aim.</summary>
        public volatile bool   PcbCadShowLight = true;
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
                // Through SerializeLocked, so the Resistors dictionary cannot be
                // enumerated while the game thread is inserting into it.
                File.WriteAllText(PathName, SerializeLocked(Opts));
            }
            catch (Exception ex) { App.Log($"[EDesSettings.Save] {ex.Message}"); }
        }
    }
}
