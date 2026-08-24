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
    /// <summary>Cad is its own mode rather than a corner of the PCB viewer on purpose: a
    /// Fusion assembly has no layers, no drills and no nets, and it must NOT be auto-fitted
    /// to the cylinder because Fusion owns its position. Folding it into the board viewer
    /// would have meant threading "none of the above" through every board setting.</summary>
    public enum EDesMode { Education = 0, Scope = 1, Pcb = 2, Cad = 3 }

    /// <summary>The Fusion CAD tab's own both-buttons cycle — the PCB tab's probe stages
    /// (EDesInspect) mean nothing there, so it gets a cycle of its own on the same
    /// gesture instead of sharing one that would not make sense in this tab.</summary>
    public enum FusionInteraction
    {
        /// <summary>Normal camera driving — pan/rotate/zoom, same as every other mode.</summary>
        Normal = 0,
        /// <summary>The SpaceNav's translation axes move the cutting plane instead of
        /// panning the scene — see FusionCutEnabled/Axis/Fraction.</summary>
        CuttingPlane = 1,
        /// <summary>The SpaceNav's translation axes move a 3D cursor instead of panning the
        /// scene; whichever body the cursor is nearest to (or inside) is auto-picked.</summary>
        CursorProbe = 2,
    }

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
        // ── Fusion 360 bridge ─────────────────────────────────────────────────
        //
        // Localhost by DEFAULT even though Fusion may live on another machine, because the
        // add-in's socket has no authentication: binding it wider is a decision the operator
        // makes deliberately, on a network they trust, not something that happens quietly.

        /// <summary>Where the Fusion add-in is listening. A hostname or an IP.</summary>
        public          string FusionHost = "127.0.0.1";
        public volatile int    FusionPort = 47800;

        /// <summary>Tessellation tolerance asked of Fusion, in mm. The only real lever on
        /// cost: a rounded enclosure is millions of triangles at 0.05 and thousands at 0.4,
        /// and the two look identical at this display's resolution.</summary>
        public volatile float  FusionToleranceMm = 0.4f;

        /// <summary>Cap sent with the request. The add-in drops whole bodies to honour it
        /// rather than truncating one mid-surface, and reports what it dropped.</summary>
        public volatile int    FusionMaxTriangles = 300_000;

        /// <summary>Socket read timeout, in seconds — applies to EVERY read across the whole
        /// streamed transfer, so effectively "how long may the SLOWEST single body take to
        /// tessellate". 120s covers a genuinely complex part; raise it further for a large
        /// assembly with one especially heavy body rather than lowering the triangle cap or
        /// tolerance just to dodge a timeout on an otherwise-fine fetch.</summary>
        public volatile float  FusionTimeoutSeconds = 120f;

        /// <summary>Set by the UI to ask the game thread to fetch. Same one-way request
        /// pattern as PcbImportRequested — the UI thread must never touch a socket.</summary>
        public volatile bool   FusionFetchRequested = false;

        /// <summary>Poll the cheap revision token and re-fetch when the model changes.</summary>
        public volatile bool   FusionAutoRefresh = false;
        public volatile float  FusionPollSeconds = 0.4f;

        // ── Where the assembly lands ──────────────────────────────────────────
        //
        // Origin at (0, 0, +zHalf): the FLOOR, centre. This display is -Z-up, so +zHalf is
        // the bottom of the volume, and with Fusion's +Z mapped to display -Z the assembly
        // stands on the floor and grows upward through the full height. Anchoring at -zHalf
        // instead puts the origin on the CEILING and clips everything above it.
        //
        // FusionOriginZ is stored as a FRACTION of zHalf so it means the same thing on a VX2
        // and a VX2-XL, for the same reason the HUD anchor is a fraction.
        public volatile float  FusionOriginX = 0f;
        public volatile float  FusionOriginY = 0f;
        public volatile float  FusionOriginZFrac = 1f;      // +1 = the floor

        /// <summary>Display units per millimetre. Not auto-fitted: an auto-fit would rescale
        /// the model every time a component was added, which is exactly the control over
        /// placement that Fusion is supposed to hold. "Fit once" computes it and stops.</summary>
        public volatile float  FusionScale = 0.04f;

        public volatile float  FusionDensity    = 0.6f;
        public volatile float  FusionBrightness = 1.0f;
        public volatile bool   FusionGhost      = false;

        /// <summary>Per-body overrides, keyed by CadSolid.AssemblyPath — the same stable
        /// occurrence-path identity FusionWire already carries, so an override survives a
        /// re-fetch even though bodies arrive as a fresh array every time. A missing key
        /// means "use the default": FusionBodyColour absent means the body's own Colour
        /// (0x9FC5E8 for a Fusion body, since Fusion sends no appearance data); FusionBodyMode
        /// absent means CadDrawMode.Solid, or CadDrawMode.Ghost while FusionGhost is on.
        ///
        /// Same lock-guarded pattern as Resistors, for the same reason: the UI thread writes
        /// from the per-body colour/mode pickers, the game thread reads them every Draw, and
        /// Save enumerates them.</summary>
        public Dictionary<string, int> FusionBodyColour { get; set; } = new();
        /// <summary>CadDrawMode, stored as its int value so this dictionary needs no
        /// dependency on EDes.Cad from this file.</summary>
        public Dictionary<string, int> FusionBodyMode { get; set; } = new();

        private readonly object _fusionBodyLock = new();

        public void SetFusionBodyColour(string key, int colour)
        {
            lock (_fusionBodyLock) FusionBodyColour[key] = colour;
        }
        public bool TryGetFusionBodyColour(string key, out int colour)
        {
            lock (_fusionBodyLock) return FusionBodyColour.TryGetValue(key, out colour);
        }
        public void ClearFusionBodyColour(string key)
        {
            lock (_fusionBodyLock) FusionBodyColour.Remove(key);
        }

        public void SetFusionBodyMode(string key, int mode)
        {
            lock (_fusionBodyLock) FusionBodyMode[key] = mode;
        }
        public bool TryGetFusionBodyMode(string key, out int mode)
        {
            lock (_fusionBodyLock) return FusionBodyMode.TryGetValue(key, out mode);
        }

        // ── Fusion CAD: extra movable point lights + shadows ──────────────────
        //
        // Light 1 is the EXISTING PcbCadLighting/PcbCadPointLight/PcbCadLightX/Y/Z/
        // Fx/Fy/Fz/Range fields above, unchanged — those are shared with the PCB
        // viewer's own STEP-model overlay (CadLight.ForBoard), so widening THEM would
        // also relight the PCB tab. Lights 2-4 are Fusion-only and additive: PcbCadLighting
        // still gates the whole rig (no lights at all when it is off), but only the
        // Fusion CAD renderer ever reads Light2..4 or the two shadow toggles below.
        //
        // Absolute Fusion millimetres, not PcbCadLightFx/Fy/Fz's board-relative
        // fractions — "move it around" sliders are more legible as plain numbers than
        // as fractions of a bounding box the operator cannot see while typing.
        public volatile bool  FusionLight2On;
        public volatile float FusionLight2X, FusionLight2Y;
        public volatile float FusionLight2Z = 50f;
        public volatile float FusionLight2Range;

        public volatile bool  FusionLight3On;
        public volatile float FusionLight3X = 50f, FusionLight3Y;
        public volatile float FusionLight3Z = 50f;
        public volatile float FusionLight3Range;

        public volatile bool  FusionLight4On;
        public volatile float FusionLight4X, FusionLight4Y = 50f;
        public volatile float FusionLight4Z = 50f;
        public volatile float FusionLight4Range;

        /// <summary>Whether a body's OWN geometry can shadow itself — the concave side of
        /// a bracket reading darker than the side facing the light, say. Off by default:
        /// the shadow test is a coarse approximation (see CadSceneRenderer's ShadowMap),
        /// and self-shadowing is where its bucket-resolution error shows up soonest.</summary>
        public volatile bool  FusionSelfShadow  = false;

        /// <summary>Whether one body can block light from reaching ANOTHER body — global,
        /// not per-body, because a shadow is a relationship between two bodies and there is
        /// no single body it could be "a setting of".</summary>
        public volatile bool  FusionCastShadows = false;

        /// <summary>When on and a body is picked, every OTHER body draws Hidden regardless
        /// of its own render-mode override — the picked body's own mode (Lit/Flat/
        /// Wireframe) is left alone, so isolating does not also flatten how it looks.</summary>
        public volatile bool  FusionIsolatePicked = false;

        // ── Fusion CAD: both-buttons interaction mode ──────────────────────────
        //
        // int, not FusionInteraction, for the same reason every other enum-backed setting
        // here is an int: GameSettings is JSON round-tripped, and storing the numeric value
        // directly means a future rename of the enum's members cannot silently break a
        // saved settings file the way renaming a string-serialized enum would.
        public volatile int   FusionInteractionMode = (int)FusionInteraction.Normal;

        /// <summary>Whether the cutting plane is actually applied to the render. Separate
        /// from FusionInteractionMode: entering CuttingPlane mode turns this on, but leaving
        /// the mode (to go back to normal orbiting) does not turn it back off — the point of
        /// slicing through a model is usually to keep LOOKING at the slice afterward, not
        /// only while the puck is actively moving the plane.</summary>
        public volatile bool  FusionCutEnabled  = false;
        /// <summary>0 = X, 1 = Y, 2 = Z. Z (Fusion's up axis, the usual print build
        /// direction) is the default because "simulate slice buildup" is the tool's main
        /// use case.</summary>
        public volatile int   FusionCutAxis     = 2;
        /// <summary>0..1 along the assembly's OWN extent on that axis — a fraction rather
        /// than an absolute mm value, so "half printed" means the same thing on a tiny
        /// bracket and a full-size enclosure without the operator doing the arithmetic.</summary>
        public volatile float FusionCutFraction = 1f;
        /// <summary>True keeps the LOW side of the plane (already-printed layers building
        /// up from the bed); false keeps the high side.</summary>
        public volatile bool  FusionCutKeepLow  = true;
        public volatile int   FusionCutHighlightColour = 0xFF3020;
        /// <summary>How thick a band around the plane counts as "the cut face" for the
        /// highlight colour, in mm.</summary>
        public volatile float FusionCutHighlightBandMm = 0.5f;

        /// <summary>The probe cursor's position, in Fusion's OWN millimetres — the same
        /// frame every body's bounding box is already in, so "nearest body" is a plain
        /// distance comparison with no placement/camera math involved.</summary>
        public volatile float FusionCursorX, FusionCursorY, FusionCursorZ;

        /// <summary>0 = assembled; each whole unit pushes every body an additional
        /// assembly-diagonal's worth further from the assembly's own centre, along that
        /// body's own direction from it — a fraction of the WHOLE assembly's size rather
        /// than an absolute mm value, for the same reason FusionCutFraction is a fraction:
        /// one number means the same thing on a tiny bracket and a full enclosure.</summary>
        public volatile float FusionExplodeAmount = 0f;

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
        /// <summary>Gentler than the SDK template's reference values (9 / 3 / 2): a
        /// sustained full deflection at the old NavRotRate spun the scene about 172
        /// degrees a second, which read as "way too sensitive" for anything needing
        /// precision. Still live-adjustable in the settings panel and by the
        /// calibration button below — this is a starting point, not a hard number.</summary>
        public volatile float NavPanRate  = 4.0f;
        public volatile float NavRotRate  = 1.2f;
        public volatile float NavZoomRate = 1.0f;

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

        /// <summary>Serialize under the same locks the mutators take — both of them, since
        /// this one JsonSerializer.Serialize(this, ...) call walks Resistors AND the two
        /// FusionBody dictionaries together. Used by Save.</summary>
        internal string SerializeLocked(JsonSerializerOptions opts)
        {
            lock (_resistorLock)
            lock (_fusionBodyLock)
                return JsonSerializer.Serialize(this, opts);
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
