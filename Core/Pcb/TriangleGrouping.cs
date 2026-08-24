// ═══════════════════════════════════════════════════════════════════════════
//  TriangleGrouping.cs — triangle soup to CadFace groups
//
//  Extracted from StlMesh so the FILE path and the WIRE path share one
//  implementation. Two readers of the same kind of data drifting apart is how
//  the same model ends up shaded one way when it arrives from disk and another
//  when it arrives from Fusion — the same reason the display-space cylinder
//  lives in VoxelBatch rather than in whichever renderer wanted it first.
//
//  Two decisions carry the result, and both are the reason this is worth sharing
//  rather than reimplementing:
//
//    • Normals are recomputed from the WINDING, and a supplied normal is used
//      only to decide orientation. Stored normals turn out to be wrong often
//      enough — in STL files demonstrably — that trusting them is not worth the
//      bytes. It is also why the Fusion bridge does not transmit them at all.
//
//    • Triangles are grouped by QUANTISED direction. A tessellated cylinder is
//      hundreds of facets differing by fractions of a degree; exact grouping
//      would give one group per triangle, which is the cost this exists to
//      avoid. ~1.4° buckets keep a converted assembly affordable.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;

namespace EDes.Pcb
{
    /// <summary>One triangle, with whatever normal its source claimed (zero if none).</summary>
    public struct Tri
    {
        public float AX, AY, AZ, BX, BY, BZ, CX, CY, CZ;
        public float NX, NY, NZ;
    }

    public static class TriangleGrouping
    {
        /// <summary>Per axis, so about 1.4 degrees of direction per bucket.</summary>
        private const int BUCKETS = 64;

        /// <summary>Group triangles by direction into CadFaces. Degenerate triangles are
        /// dropped rather than grouped — they draw nothing and would poison a group's
        /// averaged normal with a NaN.</summary>
        public static List<CadFace> Group(List<Tri> tris)
        {
            var groups = new Dictionary<long, List<Tri>>();

            foreach (var t in tris)
            {
                if (!FaceNormal(t, out float nx, out float ny, out float nz)) continue;

                long key = ((long)(int)MathF.Round(nx * BUCKETS) << 40)
                         ^ ((long)(int)MathF.Round(ny * BUCKETS) << 20)
                         ^  (long)(int)MathF.Round(nz * BUCKETS);

                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Tri>();
                list.Add(t);
            }

            var faces = new List<CadFace>(groups.Count);
            foreach (var kv in groups)
            {
                var list = kv.Value;
                var face = new CadFace
                {
                    X = new float[list.Count * 3],
                    Y = new float[list.Count * 3],
                    Z = new float[list.Count * 3],
                    TriCount = list.Count,
                };

                float sx = 0, sy = 0, sz = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    int b = i * 3;
                    face.X[b] = t.AX; face.Y[b] = t.AY; face.Z[b] = t.AZ;
                    face.X[b + 1] = t.BX; face.Y[b + 1] = t.BY; face.Z[b + 1] = t.BZ;
                    face.X[b + 2] = t.CX; face.Y[b + 2] = t.CY; face.Z[b + 2] = t.CZ;

                    FaceNormal(t, out float tnx, out float tny, out float tnz);
                    sx += tnx; sy += tny; sz += tnz;
                }

                float len = MathF.Sqrt(sx * sx + sy * sy + sz * sz);
                if (len > 1e-9f)
                {
                    face.NX = sx / len; face.NY = sy / len; face.NZ = sz / len;
                    face.HasNormalSet = true;
                }
                faces.Add(face);
            }
            return faces;
        }

        /// <summary>Normal from the winding, oriented by the stored one when it is usable.
        /// Returns false for a zero-area triangle.</summary>
        public static bool FaceNormal(in Tri t, out float nx, out float ny, out float nz)
        {
            float ux = t.BX - t.AX, uy = t.BY - t.AY, uz = t.BZ - t.AZ;
            float vx = t.CX - t.AX, vy = t.CY - t.AY, vz = t.CZ - t.AZ;

            nx = uy * vz - uz * vy;
            ny = uz * vx - ux * vz;
            nz = ux * vy - uy * vx;

            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12f) { nx = ny = nz = 0; return false; }
            nx /= len; ny /= len; nz /= len;

            // If the source's normal is usable and points the other way, the winding is the
            // one that is wrong, so flip to match the exporter's intent.
            float sl = MathF.Sqrt(t.NX * t.NX + t.NY * t.NY + t.NZ * t.NZ);
            if (sl > 1e-6f && (nx * t.NX + ny * t.NY + nz * t.NZ) / sl < -0.5f)
            { nx = -nx; ny = -ny; nz = -nz; }

            return true;
        }
    }
}
