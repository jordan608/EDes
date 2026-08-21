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
        SolderMask, Silkscreen, Paste,
        Outline, Drill, Mesh, Unknown,
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
    }

    public sealed class PcbLayer
    {
        public string       Name    = "";
        public PcbLayerKind Kind    = PcbLayerKind.Unknown;
        public bool         Visible = true;

        public readonly List<PcbSeg>    Segs    = new();
        public readonly List<PcbPad>    Pads    = new();
        public readonly List<PcbRegion> Regions = new();

        /// <summary>Narrowest drawn track on this layer (mm) — a DRC-lite readout.</summary>
        public float MinWidth = float.MaxValue;
        /// <summary>Total drawn track length (mm).</summary>
        public float TrackLength;

        public int ObjectCount => Segs.Count + Pads.Count + Regions.Count;

        /// <summary>Default colour for this layer kind (packed 0xRRGGBB).</summary>
        public int Colour => Kind switch
        {
            PcbLayerKind.CopperTop    => 0xFF5A3C,
            PcbLayerKind.CopperBottom => 0x3C8CFF,
            PcbLayerKind.CopperInner  => 0xC8A03C,
            PcbLayerKind.Silkscreen   => 0xF0F0F0,
            PcbLayerKind.SolderMask   => 0x2C7A4B,
            PcbLayerKind.Paste        => 0x9AA0A6,
            PcbLayerKind.Outline      => 0xFFE066,
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
        public DocKind Kind;
        public long    Bytes;
        public int     Pages;      // PDF page count when cheaply known, else 0
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

    public sealed class PcbBoard
    {
        public readonly List<PcbLayer>  Layers = new();
        public readonly List<PcbHole>   Holes  = new();
        public readonly List<MeshCloud> Meshes = new();

        /// <summary>Placed parts (from a centroid file), their BOM rows, and every
        /// non-geometry document found in the design folder tree.</summary>
        public readonly List<PcbComponent> Components = new();
        public readonly List<PcbBomLine>   BomLines   = new();
        public readonly List<PcbDocument>  Documents  = new();

        /// <summary>Folders walked during the last import, deepest paths first — shown
        /// so it is obvious which parts of a design tree contributed.</summary>
        public readonly List<string> SourceFolders = new();

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
            Components.Clear();
            BomLines.Clear();
            Documents.Clear();
            SourceFolders.Clear();
            Notes.Clear();
            SourceName = "(no board loaded)";
            MinX = MinY = float.MaxValue;
            MaxX = MaxY = float.MinValue;
        }

        /// <summary>Recompute the 2D bounding box over everything imported.</summary>
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

            foreach (var l in Layers)
            {
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

            foreach (var h in Holes)
            {
                Hit(h.X - h.Dia * 0.5f, h.Y - h.Dia * 0.5f);
                Hit(h.X + h.Dia * 0.5f, h.Y + h.Dia * 0.5f);
                if (h.Slot) Hit(h.X1, h.Y1);
            }

            foreach (var c in Components) Hit(c.X, c.Y);

            // Meshes carry their own 3D bounds; fold their XY footprint in so a
            // mesh-only import still fits the display.
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

        public float MinTrackWidth()
        {
            float min = float.MaxValue;
            foreach (var l in Layers)
                if (l.MinWidth < min) min = l.MinWidth;
            return min == float.MaxValue ? 0f : min;
        }

        public float MinDrill()
        {
            float min = float.MaxValue;
            foreach (var h in Holes)
                if (h.Dia < min) min = h.Dia;
            return min == float.MaxValue ? 0f : min;
        }

        public int CopperLayerCount()
        {
            int n = 0;
            foreach (var l in Layers)
                if (l.Kind is PcbLayerKind.CopperTop or PcbLayerKind.CopperInner
                           or PcbLayerKind.CopperBottom) n++;
            return n;
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
