// ═══════════════════════════════════════════════════════════════════════════
//  CadSceneRenderer.cs — a bare CAD assembly in the volume
//
//  Separate from PcbRenderer because the two answer different questions.
//  PcbRenderer fits a BOARD to the cylinder: it knows about layers, drills, nets
//  and a bounding circle. A Fusion assembly has none of that and must NOT be
//  auto-fitted, because Fusion owns position — so sharing that renderer would
//  have meant threading "do not fit" through a class built around fitting.
//
//  What IS shared is everything that would otherwise drift: TriangleGrouping for
//  the faces, CadLight for the shading, VoxelBatch for every voxel. This file is
//  only the placement and the draw order.
//
//  Visibility comes from Fusion (CadSolid.Visible, set from occurrence.isVisible,
//  which accounts for parent state). Hidden bodies are still loaded, so toggling
//  in Fusion costs no re-fetch and the legend does not lose its rows.
//
//  -Z is up here and Fusion's Z is up, so the flip happens in CadPlacement.Map
//  and nowhere else.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using EDes.Pcb;
using EDes.Sim;
using Voxon;

namespace EDes.Cad
{
    /// <summary>How to draw one solid.</summary>
    public enum CadDrawMode
    {
        /// <summary>Faces at full sample density.</summary>
        Solid = 0,
        /// <summary>Faces sampled sparsely. On a seven-colour display "fainter" has to mean
        /// "sparser" — there is no brightness axis — so this is what translucent looks like,
        /// and it is how the rest of the app already dims things.</summary>
        Ghost = 1,
        /// <summary>Not drawn, but still loaded and still listed.</summary>
        Hidden = 2,
    }

    public sealed class CadSceneRenderer
    {
        public int   FacesDrawn    { get; private set; }
        public int   TrianglesDrawn{ get; private set; }
        public int   SolidsDrawn   { get; private set; }

        /// <summary>Samples that fell outside the volume, as a fraction of those attempted.
        ///
        /// Reported because geometry placed outside the cylinder is simply not drawn, and
        /// without a number the operator cannot tell a wrong origin from a wrong model. The
        /// first draft of this bridge anchored the assembly to the CEILING, where a
        /// normal upward-growing model is almost entirely clipped — a readout would have
        /// made that obvious in a second rather than looking like a broken import.</summary>
        public float ClippedFraction { get; private set; }

        public void Draw(VoxelBatch batch, SceneCamera cam, IReadOnlyList<CadSolid> solids,
                         in CadPlacement place, in CadLight light,
                         float radius, float zHalf,
                         float density, float brightness,
                         Func<CadSolid, CadDrawMode>? modeOf = null)
        {
            FacesDrawn = TrianglesDrawn = SolidsDrawn = 0;
            ClippedFraction = 0f;

            long attempted = 0, outside = 0;

            float step = batch.Spacing / Math.Clamp(density <= 0f ? 0.6f : density, 0.1f, 2f);
            float brightMul = 1f / MathF.Max(0.05f, brightness <= 0f ? 1f : brightness);

            foreach (var solid in solids)
            {
                if (!solid.Visible) continue;

                var mode = modeOf?.Invoke(solid) ?? CadDrawMode.Solid;
                if (mode == CadDrawMode.Hidden) continue;

                // Ghost is a density multiplier, not a colour change: see CadDrawMode.
                float modeMul = mode == CadDrawMode.Ghost ? 4.5f : 1f;
                int   col     = solid.Colour;
                SolidsDrawn++;

                foreach (var face in solid.Faces)
                {
                    if (batch.BudgetHit) { Report(attempted, outside); return; }
                    FacesDrawn++;

                    for (int t = 0; t < face.TriCount; t++)
                    {
                        int i = t * 3;

                        // Shade per TRIANGLE, at its centroid. Still flat shading — one
                        // normal for the whole face — but a point light genuinely varies
                        // across a large face, and collapsing that to one value per face is
                        // what makes a point light look directional again.
                        float shade = light.Ambient;
                        if (face.HasNormalSet)
                        {
                            float mx = (face.X[i] + face.X[i + 1] + face.X[i + 2]) / 3f;
                            float my = (face.Y[i] + face.Y[i + 1] + face.Y[i + 2]) / 3f;
                            float mz = (face.Z[i] + face.Z[i + 1] + face.Z[i + 2]) / 3f;
                            shade = light.Shade(face.NX, face.NY, face.NZ, mx, my, mz, false);
                        }

                        float triStep = step / MathF.Max(0.12f, shade) * modeMul * brightMul;

                        point3d a = cam.Transform(place.Map(face.X[i],     face.Y[i],     face.Z[i]));
                        point3d b = cam.Transform(place.Map(face.X[i + 1], face.Y[i + 1], face.Z[i + 1]));
                        point3d c = cam.Transform(place.Map(face.X[i + 2], face.Y[i + 2], face.Z[i + 2]));

                        FillTri(batch, a, b, c, col, triStep, radius, zHalf,
                                ref attempted, ref outside);
                        TrianglesDrawn++;

                        if (batch.BudgetHit) { Report(attempted, outside); return; }
                    }
                }
            }

            Report(attempted, outside);
        }

        private void Report(long attempted, long outside)
            => ClippedFraction = attempted > 0 ? (float)((double)outside / attempted) : 0f;

        /// <summary>Point-fill one triangle on a barycentric lattice, counting what lands
        /// outside the volume.
        ///
        /// Rows are sized from the LONGEST edge so a long thin triangle still gets samples
        /// along its length — sizing from area bunches them at one end on the slivers that
        /// tessellation produces.</summary>
        private static void FillTri(VoxelBatch batch, point3d a, point3d b, point3d c,
                                    int col, float step, float radius, float zHalf,
                                    ref long attempted, ref long outside)
        {
            float longest = MathF.Max(Dist(a, b), MathF.Max(Dist(b, c), Dist(c, a)));
            if (longest <= 1e-6f)
            {
                attempted++;
                if (!CadPlacement.Inside(a, radius, zHalf)) outside++;
                batch.Add(a, col);
                return;
            }

            int n = Math.Clamp((int)MathF.Ceiling(longest / MathF.Max(1e-6f, step)), 1, 512);

            for (int r = 0; r <= n; r++)
            for (int s = 0; s <= n - r; s++)
            {
                float u = r / (float)n, v = s / (float)n, w = 1f - u - v;
                var p = new point3d(a.x * w + b.x * u + c.x * v,
                                    a.y * w + b.y * u + c.y * v,
                                    a.z * w + b.z * u + c.z * v);

                attempted++;
                if (!CadPlacement.Inside(p, radius, zHalf)) outside++;

                if (!batch.Add(p, col) && batch.BudgetHit) return;
            }
        }

        private static float Dist(point3d p, point3d q)
        {
            float dx = p.x - q.x, dy = p.y - q.y, dz = p.z - q.z;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
