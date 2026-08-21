// ═══════════════════════════════════════════════════════════════════════════
//  CadModel.cs — what a STEP import produces
//
//  A volumetric display is transparent: there is no occlusion, so a filled or
//  densely-sampled surface shows its own back faces through its front and the
//  model reads as fog. The thing that survives that and still looks like CAD is
//  the EDGE set — the B-rep feature curves.
//
//  So a STEP import lands here as one CadSolid per B-rep solid, each holding its
//  edges already tessellated to polylines in board millimetres. No surfaces, no
//  interior, nothing to fill. Surfaces can be added later as a separate opt-in
//  layer (see StepParser's header) without disturbing any of this.
//
//  Coordinates match MeshLoader's convention: the board frame, millimetres,
//  Z = height with positive up. PcbRenderer maps that onto the display's
//  -Z-is-up convention, exactly as it already does for mesh clouds.
// ═══════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;

namespace EDes.Pcb
{
    /// <summary>One B-rep edge, tessellated to a polyline. Straight edges are two
    /// points; arcs and splines get as many as their curvature needs.</summary>
    public sealed class CadEdge
    {
        public float[] X = System.Array.Empty<float>();
        public float[] Y = System.Array.Empty<float>();
        public float[] Z = System.Array.Empty<float>();
        public int     Count;
    }

    /// <summary>One solid from the STEP assembly — a component body, or the board.</summary>
    public sealed class CadSolid
    {
        /// <summary>The PRODUCT name out of the assembly tree. On an Altium export this
        /// is the designator ("R1", "U1") for a placed part, or a manufacturer part
        /// number for a vendor model, or "Board"/"PCB" for the board itself.</summary>
        public string Name = "";

        /// <summary>Designator this solid was matched to in the placement/BOM data, or
        /// empty when nothing matched. Kept separate from Name so an unmatched solid is
        /// obviously unmatched rather than silently renamed.</summary>
        public string Designator = "";

        /// <summary>The full assembly chain that led to this solid, outermost first.
        ///
        /// Needed because the designator and the geometry live at DIFFERENT levels of a
        /// real export: Altium nests the vendor body inside a per-placement node, so the
        /// leaf product is called something like CRCW060310R5FKEC while "R2" sits one
        /// level up. Matching has to be able to look at the whole chain, not just the
        /// leaf, or every part gets identified by its manufacturer part number.</summary>
        public string AssemblyPath = "";

        /// <summary>Colour from the STEP presentation entities, or a default.</summary>
        public int  Colour  = 0x9FC5E8;
        public bool Visible = true;

        public readonly List<CadEdge> Edges = new();

        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        /// <summary>Total polyline points across every edge — the honest measure of what
        /// this solid costs to draw, since each point becomes a line sample.</summary>
        public int PointCount;

        public bool HasGeometry => Edges.Count > 0;
    }

    /// <summary>Everything one STEP file produced, plus what could not be handled.</summary>
    public sealed class CadModel
    {
        public string SourceName = "";
        public readonly List<CadSolid> Solids = new();

        /// <summary>Unsupported constructs, counted rather than silently dropped — the
        /// same contract GerberParser follows.</summary>
        public readonly List<string> Notes = new();

        public int  SolidCount => Solids.Count;
        public bool HasGeometry
        {
            get
            {
                foreach (var s in Solids) if (s.HasGeometry) return true;
                return false;
            }
        }

        public int TotalEdges
        {
            get { int n = 0; foreach (var s in Solids) n += s.Edges.Count; return n; }
        }

        public int TotalPoints
        {
            get { int n = 0; foreach (var s in Solids) n += s.PointCount; return n; }
        }

        public float MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue;
        public float MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue;

        public float WidthMm  => MaxX > MinX ? MaxX - MinX : 0f;
        public float DepthMm  => MaxY > MinY ? MaxY - MinY : 0f;
        public float HeightMm => MaxZ > MinZ ? MaxZ - MinZ : 0f;

        public void RecomputeBounds()
        {
            MinX = MinY = MinZ = float.MaxValue;
            MaxX = MaxY = MaxZ = float.MinValue;
            foreach (var s in Solids)
            {
                if (!s.HasGeometry) continue;
                if (s.MinX < MinX) MinX = s.MinX;
                if (s.MinY < MinY) MinY = s.MinY;
                if (s.MinZ < MinZ) MinZ = s.MinZ;
                if (s.MaxX > MaxX) MaxX = s.MaxX;
                if (s.MaxY > MaxY) MaxY = s.MaxY;
                if (s.MaxZ > MaxZ) MaxZ = s.MaxZ;
            }
            if (MinX > MaxX) { MinX = MinY = MinZ = MaxX = MaxY = MaxZ = 0f; }
        }
    }
}
