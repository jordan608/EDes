// ═══════════════════════════════════════════════════════════════════════════
//  PcbBoard.cs — the imported board, in millimetres
//
//  One flat, renderer-friendly model shared by every importer (Gerber,
//  Excellon, mesh). Geometry is stored ONCE in board units (mm, Y up in the
//  board's own 2D frame) and mapped to display coordinates at draw time, so:
//
//    • re-fitting to a different display size costs nothing (no re-parse),
//    • the analysis numbers (board size, min trace width, drill diameters)
//      stay in the units a PCB engineer actually checks them in,
//    • layer stacking / exploding is a render-time decision, not baked in.
//
//  Everything is parallel-ish lists of small structs rather than an object
//  graph: the render loop iterates them and nothing else.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;

namespace EDes.Pcb
{
    public enum PcbLayerKind
    {
        CopperTop, CopperInner, CopperBottom,
        SolderMask, Silkscreen, Paste, PadMaster,
        Outline, Mechanical, Drill, Mesh, Unknown,
    }

    /// <summary>A drawn track or line: a segment with a width (mm).</summary>
    public struct PcbSeg
    {
        public float X0, Y0, X1, Y1, W;
        public PcbSeg(float x0, float y0, float x1, float y1, float w)
        { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; W = w; }

        public float Length => MathF.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
    }

    public enum PadShape : byte { Circle = 0, Rect = 1, Obround = 2, Polygon = 3 }

    /// <summary>A flashed aperture — a pad.</summary>
    public struct PcbPad
    {
        public float     X, Y, W, H;
        public PadShape  Shape;
        public PcbPad(float x, float y, float w, float h, PadShape shape)
        { X = x; Y = y; W = w; H = h; Shape = shape; }
    }

    /// <summary>A filled contour (copper pour, keepout, board outline region).</summary>
    public sealed class PcbRegion
    {
        public readonly List<float> X = new();
        public readonly List<float> Y = new();
        public int Count => X.Count;
    }

    /// <summary>A drilled hole or routed slot, from the Excellon file.</summary>
    public struct PcbHole
    {
        public float X, Y, Dia;
        public bool  Slot;
        public float X1, Y1;      // slot end (valid when Slot)
        public bool  Plated;

        /// <summary>Which COPPER layers this hole connects, 1-based in stack order
        /// (1 = top copper). 0 on either side means "not stated", which is treated as a
        /// through hole spanning the whole copper stack.
        ///
        /// Excellon does not carry this. It arrives either from a Gerber X2
        /// TF.FileFunction attribute embedded in the drill header, or from the layer pair
        /// in the file name that Altium and KiCad both use for blind/buried drills. Both
        /// are best-effort, and a hole with no information must render as a through hole
        /// rather than guess a span -- guessing wrong hides a connection that exists.</summary>
        public int SpanFrom, SpanTo;

        /// <summary>True only when a real layer pair was stated AND it does not reach both
        /// outer copper layers. Used for labelling, never for geometry.</summary>
        public bool IsBlind(int copperCount)
            => SpanFrom > 0 && SpanTo > 0 && copperCount > 1 &&
               !(Math.Min(SpanFrom, SpanTo) == 1 && Math.Max(SpanFrom, SpanTo) == copperCount);
    }

    public sealed class PcbLayer
    {
        public string       Name    = "";
        public PcbLayerKind Kind    = PcbLayerKind.Unknown;
        public bool         Visible = true;

        /// <summary>Which side of the board this layer belongs to.
        ///
        /// Needed because PcbLayerKind does NOT distinguish sides for silkscreen, mask or
        /// paste — there is one Silkscreen kind for both. Without this the stack order put
        /// every silkscreen layer at the same height, so the BOTTOM silkscreen sorted to
        /// the very top of the stack and appeared above the components.</summary>
        public bool         Bottom;

        public readonly List<PcbSeg>    Segs    = new();
        public readonly List<PcbPad>    Pads    = new();
        public readonly List<PcbRegion> Regions = new();

        /// <summary>Narrowest drawn track on this layer (mm) — a DRC-lite readout.</summary>
        public float MinWidth = float.MaxValue;
        /// <summary>Total drawn track length (mm).</summary>
        public float TrackLength;

        public int ObjectCount => Segs.Count + Pads.Count + Regions.Count;

        // Net names from Gerber X2 %TO.N attributes. SPARSE dictionaries rather than
        // arrays because the attributes are optional and usually absent — most exports
        // carry none at all, and a name array per layer would then be pure overhead.
        private Dictionary<int, string>? _segNets;
        private Dictionary<int, string>? _padNets;

        /// <summary>True if this layer carried any real net names.</summary>
        public bool HasNetNames => _segNets != null || _padNets != null;

        public void SetSegNetName(int segIndex, string net)
        {
            if (string.IsNullOrEmpty(net)) return;
            (_segNets ??= new Dictionary<int, string>())[segIndex] = net;
        }

        public void SetPadNetName(int padIndex, string net)
        {
            if (string.IsNullOrEmpty(net)) return;
            (_padNets ??= new Dictionary<int, string>())[padIndex] = net;
        }

        public string SegNetName(int segIndex)
            => _segNets != null && _segNets.TryGetValue(segIndex, out var n) ? n : "";

        public string PadNetName(int padIndex)
            => _padNets != null && _padNets.TryGetValue(padIndex, out var n) ? n : "";

        /// <summary>User-chosen colour, or null to use the kind's default. Separate from
        /// the default rather than overwriting it so "reset to default" stays possible and
        /// so a re-import can tell a deliberate choice from an untouched layer.</summary>
        public int? ColourOverride;

        /// <summary>Colour actually drawn (packed 0xRRGGBB).</summary>
        public int Colour => ColourOverride ?? DefaultColour;

        /// <summary>Colour for this layer kind, ignoring any override.</summary>
        public int DefaultColour => Kind switch
        {
            PcbLayerKind.CopperTop    => 0xFF5A3C,
            PcbLayerKind.CopperBottom => 0x3C8CFF,
            PcbLayerKind.CopperInner  => 0xC8A03C,
            PcbLayerKind.Silkscreen   => 0xF0F0F0,
            PcbLayerKind.SolderMask   => 0x2C7A4B,
            PcbLayerKind.Paste        => 0x9AA0A6,
            PcbLayerKind.PadMaster    => 0xD08A5A,
            PcbLayerKind.Outline      => 0xFFE066,
            PcbLayerKind.Mechanical   => 0x7A6ACF,
            PcbLayerKind.Drill        => 0x808080,
            PcbLayerKind.Mesh         => 0x66D9C0,
            _                         => 0x8899AA,
        };
    }

    /// <summary>A placed component, from a pick-and-place / centroid file. XY is the
    /// part centroid in board millimetres; Bottom says which side it is mounted on.</summary>
    public struct PcbComponent
    {
        public string Designator;      // R1, C14, U3 ...
        public string Value;           // 10k, 100nF, STM32F401 ...
        public string Footprint;       // 0603, SOIC-8 ...
        public float  X, Y;            // mm, board coordinates
        public float  Rotation;        // degrees
        public bool   Bottom;          // mounted on the bottom side
    }

    /// <summary>One BOM row (a part and the designators that use it).</summary>
    public struct PcbBomLine
    {
        public string Designators;
        public string Value;
        public string Footprint;
        public int    Quantity;
    }

    /// <summary>Any file in the design folder that is not geometry: a schematic PDF,
    /// a mechanical drawing, a netlist, a datasheet, a readme. EDes cannot render a
    /// vector schematic in the volume, but it CAN tell you the design package is
    /// complete and what is in it — which is most of what an inventory is for.</summary>
    public enum DocKind { Schematic, Drawing, Netlist, Bom, Placement, Datasheet, Cad3D, Archive, Other }

    public struct PcbDocument
    {
        public string  Path;
        public string  Name;
        public string  Folder;     // parent folder — in a fixed export tree this IS the meaning
        public DocKind Kind;
        public long    Bytes;
        public int     Pages;      // PDF page count when cheaply known, else 0

        /// <summary>"Schematic Prints/board.PDF" — every PDF in an Altium output tree is
        /// named after the project, so the folder is what tells them apart.</summary>
        public string Display => Folder.Length > 0 ? Folder + "/" + Name : Name;
    }

    /// <summary>A sampled point cloud from a mesh file (STL / OBJ / GLB / PLY).</summary>
    public sealed class MeshCloud
    {
        public string  Name = "";
        public float[] X = Array.Empty<float>();
        public float[] Y = Array.Empty<float>();
        public float[] Z = Array.Empty<float>();
        public int     Count;
        public bool    Visible = true;
        public int     Colour  = 0x66D9C0;
        /// <summary>Model bounds in its own units (mm assumed for STL/STEP exports).</summary>
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    /// <summary>Summary of a design-rule-check report. Parsed rather than merely
    /// catalogued, because "0 violations across 17 rules" is exactly the kind of
    /// fact this display should be able to state about a board.</summary>
    public sealed class DrcSummary
    {
        public string       File       = "";
        public int          Rules;
        public int          Violations;
        /// <summary>Rules that actually failed, "name: n" — usually empty, and the
        /// only part worth reading when it is not.</summary>
        public readonly List<string> Failing = new();
        public bool Parsed;
    }

    /// <summary>What the importer decided about one file it found.
    ///
    /// Recorded for EVERY file, including the ones it ignored, because "the viewer did
    /// not find my STEP file" and "the viewer found it and failed to parse it" and "the
    /// viewer skipped it as a duplicate" are three different problems with three
    /// different fixes, and without a per-file record they are indistinguishable.</summary>
    public struct ImportedFile
    {
        public string Name;
        public string Folder;    // relative to the imported root
        public string Role;      // Gerber, Drill, STEP, Mesh, Placement, BOM, Document...
        public string Detail;    // what came out of it, or why nothing did
        public long   Bytes;
        public int    Ms;        // time spent on this file
        public bool   Used;      // false = ignored, skipped or failed
    }

    public sealed class PcbBoard
    {
        /// <summary>Every file the importer looked at, in the order it looked.</summary>
        public readonly List<ImportedFile> ImportLog = new();

        /// <summary>Wall-clock milliseconds for the whole import.</summary>
        public int ImportMs;

        public readonly List<PcbLayer>  Layers = new();
        public readonly List<PcbHole>   Holes  = new();
        public readonly List<MeshCloud> Meshes = new();

        /// <summary>Derived copper connectivity, or null until it is built. Rebuilt after
        /// an import rather than on demand: it depends on every copper layer and every
        /// hole, so building it lazily from a draw call would mean doing it mid-frame.</summary>
        public PcbNets? Nets { get; set; }

        /// <summary>CAD solids from STEP imports, as edge wireframes. Kept separate from
        /// Meshes because they are drawn differently on purpose: a mesh becomes a surface
        /// point cloud, a CAD solid becomes its feature edges.</summary>
        public readonly List<CadSolid> Solids = new();

        /// <summary>Placed parts (from a centroid file), their BOM rows, and every
        /// non-geometry document found in the design folder tree.</summary>
        public readonly List<PcbComponent> Components = new();
        public readonly List<PcbBomLine>   BomLines   = new();
        public readonly List<PcbDocument>  Documents  = new();

        /// <summary>Folders walked during the last import, deepest paths first — shown
        /// so it is obvious which parts of a design tree contributed.</summary>
        public readonly List<string> SourceFolders = new();

        /// <summary>The design-rule-check report, if the tree contained one.</summary>
        public DrcSummary Drc { get; set; } = new DrcSummary();

        public string SourceName = "(no board loaded)";
        /// <summary>Import warnings/notes — surfaced in the settings panel.</summary>
        public readonly List<string> Notes = new();

        // ── Bounds in board units (mm) ────────────────────────────────────────
        public float MinX { get; private set; } = float.MaxValue;
        public float MinY { get; private set; } = float.MaxValue;
        public float MaxX { get; private set; } = float.MinValue;
        public float MaxY { get; private set; } = float.MinValue;

        public bool  HasGeometry => MaxX > MinX && MaxY > MinY;
        public float WidthMm     => HasGeometry ? MaxX - MinX : 0f;
        public float HeightMm    => HasGeometry ? MaxY - MinY : 0f;
        public float CentreX     => (MinX + MaxX) * 0.5f;
        public float CentreY     => (MinY + MaxY) * 0.5f;

        public PcbLayer GetOrAddLayer(string name, PcbLayerKind kind)
        {
            foreach (var l in Layers)
                if (l.Name == name) return l;
            var layer = new PcbLayer { Name = name, Kind = kind };
            Layers.Add(layer);
            return layer;
        }

        public void Clear()
        {
            Layers.Clear();
            Holes.Clear();
            Meshes.Clear();
            Solids.Clear();
            Nets = null;
            ImportLog.Clear();
            ImportMs = 0;
            Components.Clear();
            BomLines.Clear();
            Documents.Clear();
            SourceFolders.Clear();
            Drc = new DrcSummary();
            Notes.Clear();
            SourceName = "(no board loaded)";
            MinX = MinY = float.MaxValue;
            MaxX = MaxY = float.MinValue;
        }

        /// <summary>Recompute the 2D bounding box.
        ///
        /// What counts is deliberate: if the design has a board OUTLINE layer, the
        /// outline IS the board and nothing else may enlarge it. Mechanical layers
        /// carry dimension lines, title blocks and notes that sit well outside the
        /// board — on a real Altium export they made a 15 x 30 mm board measure
        /// 140 x 61 mm, which then scaled the whole thing down to a speck on the
        /// display. Hidden layers never contribute either.</summary>
        public void ComputeBounds()
        {
            MinX = MinY = float.MaxValue;
            MaxX = MaxY = float.MinValue;

            void Hit(float x, float y)
            {
                if (x < MinX) MinX = x;
                if (y < MinY) MinY = y;
                if (x > MaxX) MaxX = x;
                if (y > MaxY) MaxY = y;
            }

            bool haveOutline = false;
            foreach (var l in Layers)
                if (l.Kind == PcbLayerKind.Outline && l.ObjectCount > 0) { haveOutline = true; break; }

            foreach (var l in Layers)
            {
                if (!l.Visible) continue;
                if (haveOutline && l.Kind != PcbLayerKind.Outline) continue;
                if (!haveOutline && l.Kind == PcbLayerKind.Mechanical) continue;

                foreach (var s in l.Segs)
                {
                    float h = s.W * 0.5f;
                    Hit(s.X0 - h, s.Y0 - h); Hit(s.X0 + h, s.Y0 + h);
                    Hit(s.X1 - h, s.Y1 - h); Hit(s.X1 + h, s.Y1 + h);
                }
                foreach (var p in l.Pads)
                {
                    Hit(p.X - p.W * 0.5f, p.Y - p.H * 0.5f);
                    Hit(p.X + p.W * 0.5f, p.Y + p.H * 0.5f);
                }
                foreach (var r in l.Regions)
                    for (int i = 0; i < r.Count; i++) Hit(r.X[i], r.Y[i]);
            }

            // Drills and parts are inside the outline by definition; only fold them in
            // when there is no outline to trust.
            if (!haveOutline)
            {
                foreach (var h in Holes)
                {
                    Hit(h.X - h.Dia * 0.5f, h.Y - h.Dia * 0.5f);
                    Hit(h.X + h.Dia * 0.5f, h.Y + h.Dia * 0.5f);
                    if (h.Slot) Hit(h.X1, h.Y1);
                }

                foreach (var c in Components) Hit(c.X, c.Y);
            }

            // Meshes carry their own 3D bounds; fold their XY footprint in so a
            // mesh-only import still fits the display.
            if (!haveOutline)
                foreach (var m in Meshes)
                {
                    Hit(m.MinX, m.MinY);
                    Hit(m.MaxX, m.MaxY);
                }
        }

        // ── Analysis ──────────────────────────────────────────────────────────

        public struct DrillGroup
        {
            public float Dia;
            public int   Count;
        }

        /// <summary>Drill diameters grouped and counted, largest first — the table a
        /// fab house quotes from, and the quickest sanity check on a board.</summary>
        public List<DrillGroup> DrillTable()
        {
            var groups = new List<DrillGroup>();
            foreach (var h in Holes)
            {
                bool merged = false;
                for (int i = 0; i < groups.Count; i++)
                {
                    if (MathF.Abs(groups[i].Dia - h.Dia) > 0.005f) continue;
                    var g = groups[i];
                    g.Count++;
                    groups[i] = g;
                    merged = true;
                    break;
                }
                if (!merged) groups.Add(new DrillGroup { Dia = h.Dia, Count = 1 });
            }
            groups.Sort((a, b) => b.Dia.CompareTo(a.Dia));
            return groups;
        }

        /// <summary>Narrowest track on a COPPER layer. Silk, mask and mechanical layers
        /// routinely use a 1-mil aperture to draw outlines, which is not a track and must
        /// not be reported as the board's minimum trace width.</summary>
        public float MinTrackWidth()
        {
            float min = float.MaxValue;
            foreach (var l in Layers)
            {
                if (l.Kind is not (PcbLayerKind.CopperTop or PcbLayerKind.CopperInner
                                   or PcbLayerKind.CopperBottom)) continue;
                if (l.Segs.Count == 0) continue;      // pads only: no tracks to measure
                if (l.MinWidth < min) min = l.MinWidth;
            }
            return min == float.MaxValue ? 0f : min;
        }

        /// <summary>Is this hole a via rather than a component through-hole?
        ///
        /// Excellon carries no via flag — a via and a lead hole are both just a tool
        /// diameter and a coordinate — so this has to be a classification, and it is
        /// deliberately a conservative one: plated (an unplated hole conducts nothing
        /// and cannot be a via), not a slot (routed slots are mechanical), and at or
        /// under the caller's diameter cut-off. The default cut-off of 0.7 mm sits in
        /// the gap between real vias and the smallest component leads (~0.8 mm), so
        /// raising it starts pulling in through-hole pads.</summary>
        public static bool IsVia(in PcbHole h, float maxDiaMm)
            => h.Plated && !h.Slot && h.Dia > 0f && h.Dia <= maxDiaMm;

        /// <summary>How many holes the above classifies as vias — quoted in the panel
        /// so the diameter cut-off can be set by watching the count, not by guessing.</summary>
        public int ViaCount(float maxDiaMm)
        {
            int n = 0;
            foreach (var h in Holes) if (IsVia(h, maxDiaMm)) n++;
            return n;
        }

        /// <summary>Resolve which copper layers a via actually spans, 1-based and ordered.
        ///
        /// Split out of the renderer so it can be tested: getting this wrong is invisible
        /// in a screenshot but wrong on the board. Rules, in order:
        ///   • an unstated end means the outer copper on that side, i.e. a through via —
        ///     showing a connection that exists beats hiding one;
        ///   • the pair is sorted, so a "4-1" file reads the same as "1-4";
        ///   • both ends are clamped into the real stack, because a drill file may name
        ///     more layers than the Gerbers present (a 4-layer drill set imported with
        ///     only two copper layers found).</summary>
        public static void ViaSpan(in PcbHole h, int copperCount, out int first, out int last)
        {
            if (copperCount < 1) { first = last = 1; return; }

            first = h.SpanFrom > 0 ? h.SpanFrom : 1;
            last  = h.SpanTo   > 0 ? h.SpanTo   : copperCount;
            if (first > last) (first, last) = (last, first);
            first = Math.Clamp(first, 1, copperCount);
            last  = Math.Clamp(last,  1, copperCount);
        }

        /// <summary>Copper layers in stack order, as indices into Layers. This is the
        /// mapping a via span is expressed in — index 0 is top copper.</summary>
        public List<int> CopperStack()
        {
            var order = new List<int>();
            for (int i = 0; i < Layers.Count; i++)
                if (Layers[i].Kind is PcbLayerKind.CopperTop or PcbLayerKind.CopperInner
                                     or PcbLayerKind.CopperBottom)
                    order.Add(i);
            return order;
        }

        public float MinDrill()
        {
            float min = float.MaxValue;
            foreach (var h in Holes)
                if (h.Dia < min) min = h.Dia;
            return min == float.MaxValue ? 0f : min;
        }

        /// <summary>Copper layers in the stack. Pad-master layers (Altium .GPT/.GPB) are
        /// composites of the pads already on the copper layers, not extra layers — counting
        /// them reports a 2-layer board as 4-layer.</summary>
        public int CopperLayerCount()
        {
            bool top = false, bottom = false;
            int inner = 0;
            foreach (var l in Layers)
            {
                switch (l.Kind)
                {
                    case PcbLayerKind.CopperTop:    top = true; break;
                    case PcbLayerKind.CopperBottom: bottom = true; break;
                    case PcbLayerKind.CopperInner:  inner++; break;
                }
            }
            return (top ? 1 : 0) + (bottom ? 1 : 0) + inner;
        }

        public int ComponentsOnSide(bool bottom)
        {
            int n = 0;
            foreach (var c in Components) if (c.Bottom == bottom) n++;
            return n;
        }

        public int TotalObjects()
        {
            int n = Holes.Count + Components.Count;
            foreach (var l in Layers) n += l.ObjectCount;
            foreach (var m in Meshes) n += m.Count;
            return n;
        }
    }
}
