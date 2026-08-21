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

        /// <summary>Averaged normal of the faces meeting at this edge, for shading.
        ///
        /// Lighting is N·L, and an edge has a tangent rather than a normal — so on its
        /// own a wireframe cannot be lit. What makes it possible is that the parser has
        /// to walk the FACES anyway to reach the edges, and a STEP PLANE states its
        /// normal exactly and for free. Averaging the (usually two) adjacent face normals
        /// gives an edge something to shade against.
        ///
        /// HasNormal is false when neither neighbour was a planar face — curved surfaces
        /// contribute nothing here because their normal varies along the edge, and a
        /// single sampled value would shade the edge wrongly along most of its length.
        /// Those edges render unlit rather than mis-lit.</summary>
        public float NX, NY, NZ;
        public bool  HasNormal;
    }

    /// <summary>One PLANAR face, triangulated, for flat shading.
    ///
    /// Only planar faces get one. A trimmed cylinder or NURBS patch needs a real
    /// geometry kernel to tessellate, and guessing produces geometry that is confidently
    /// wrong rather than merely absent — so those faces stay unfilled and are counted in
    /// a note. A planar face is different: its boundary is already tessellated as edges,
    /// so filling it is polygon triangulation, not surface evaluation.
    ///
    /// Vertices are 3 per triangle, flat-packed. Flat shading means one normal for the
    /// whole face, which is exactly what a plane has.</summary>
    public sealed class CadFace
    {
        public float[] X = System.Array.Empty<float>();
        public float[] Y = System.Array.Empty<float>();
        public float[] Z = System.Array.Empty<float>();
        public int     TriCount;
        public float   NX, NY, NZ;

        /// <summary>True once a usable normal has been computed. Distinguishes "faces the
        /// +X direction" from "no normal was available", which are different for lighting
        /// and would otherwise both read as a zero vector.</summary>
        public bool    HasNormalSet;
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

        /// <summary>Triangulated planar faces, for the optional flat-shaded fill. Empty
        /// when the solid has no planar faces, or when surfaces were not requested.</summary>
        public readonly List<CadFace> Faces = new();

        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        /// <summary>Total polyline points across every edge — the honest measure of what
        /// this solid costs to draw, since each point becomes a line sample.</summary>
        public int PointCount;

        /// <summary>How many of this solid's edges got a usable shading normal.</summary>
        public int NormalCount;

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

        public int TotalNormals
        {
            get { int n = 0; foreach (var s in Solids) n += s.NormalCount; return n; }
        }

        public int TotalTriangles
        {
            get
            {
                int n = 0;
                foreach (var s in Solids) foreach (var f in s.Faces) n += f.TriCount;
                return n;
            }
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
