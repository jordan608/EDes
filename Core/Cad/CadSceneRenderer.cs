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
        /// <summary>"Lit" in the per-body picker: faces at full density, sample density
        /// varying with the point light's shade (see TriStep) — the only way this display
        /// can show a light's direction, since it has no brightness axis.</summary>
        Solid = 0,
        /// <summary>Faces sampled sparsely. On a seven-colour display "fainter" has to mean
        /// "sparser" — there is no brightness axis — so this is what translucent looks like,
        /// and it is how the rest of the app already dims things. A global toggle
        /// (FusionGhost), not one of the four per-body picker states.</summary>
        Ghost = 1,
        /// <summary>Not drawn, but still loaded and still listed.</summary>
        Hidden = 2,
        /// <summary>"Flat" in the per-body picker: faces filled at a single, UNLIT density —
        /// no shade-driven variation, so the body reads as one uniform solid colour rather
        /// than being brighter on the side facing the light.</summary>
        Flat = 3,
        /// <summary>"Wireframe" in the per-body picker: triangle EDGES only, unlit. Note this
        /// draws every tessellation triangle's edges, not a clean B-rep outline — a Fusion
        /// body carries no boundary-edge data (see CadEdge, which only STEP imports get), so
        /// a large flat face's internal diagonals show. Still useful to see through a body.</summary>
        Wireframe = 4,
    }

    /// <summary>A single infinite plane that sections the assembly — the "3D print slice
    /// buildup" tool. Defined in Fusion's OWN millimetres and axes, not display space, so
    /// the cut stays put on the physical part regardless of how the scene is panned,
    /// rotated or zoomed: a print's build direction does not change because someone
    /// spun the display to look at it from another angle.
    ///
    /// Tested at TRIANGLE granularity (one centroid check per triangle, reusing the same
    /// centroid TriStep already computes for lighting) rather than per sample point — a
    /// jagged cut at triangle resolution is a fair trade for staying a single cheap check
    /// per triangle instead of needing raw AND transformed coordinates side by side deep
    /// inside FillTri/WireTri.</summary>
    public readonly struct CutPlane
    {
        public readonly bool  Enabled;
        public readonly float Nx, Ny, Nz;    // unit normal, Fusion mm
        public readonly float D;             // plane: dot(N,P) = D
        public readonly bool  KeepPositiveSide;
        public readonly int   HighlightColour;
        public readonly float HighlightBandMm;

        public static readonly CutPlane Off = default;

        public CutPlane(float nx, float ny, float nz, float d, bool keepPositiveSide,
                        int highlightColour, float highlightBandMm)
        {
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9f) { nx = 0f; ny = 0f; nz = 1f; len = 1f; }
            Enabled          = true;
            Nx = nx / len; Ny = ny / len; Nz = nz / len;
            D                = d;
            KeepPositiveSide = keepPositiveSide;
            HighlightColour  = highlightColour;
            HighlightBandMm  = MathF.Max(0f, highlightBandMm);
        }

        /// <summary>Signed distance of a point from the plane, along the normal.</summary>
        public float SignedDistance(float x, float y, float z) => Nx * x + Ny * y + Nz * z - D;

        /// <summary>Should a triangle centred at (x,y,z) be drawn at all, and — if so — is
        /// it close enough to the cut to draw as the highlighted "cut face" instead of the
        /// body's own colour?</summary>
        public bool Keep(float x, float y, float z, out bool nearCut)
        {
            float dist = SignedDistance(x, y, z);
            nearCut = MathF.Abs(dist) <= HighlightBandMm;
            return KeepPositiveSide ? dist >= 0f : dist <= 0f;
        }
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

        /// <summary>1 = every face sampled at full requested density; less than 1 means the
        /// whole model was coarsened UNIFORMLY to fit the voxel budget — never some bodies
        /// at full density and others dropped, which is what a per-body or per-triangle
        /// cutoff would do. Reported so "the model looks sparse" reads as "too big for the
        /// budget at this zoom" rather than a broken import.</summary>
        public float DensityScale { get; private set; } = 1f;

        /// <summary>Single-light convenience overload — every existing caller (and every
        /// existing test) that only ever had one light keeps working unchanged.</summary>
        public void Draw(VoxelBatch batch, SceneCamera cam, IReadOnlyList<CadSolid> solids,
                         in CadPlacement place, in CadLight light,
                         float radius, float zHalf,
                         float density, float brightness,
                         Func<CadSolid, CadDrawMode>? modeOf = null,
                         Func<CadSolid, int>? colorOf = null)
            => Draw(batch, cam, solids, place, new[] { light }, radius, zHalf, density,
                   brightness, modeOf, colorOf, selfShadow: false, castShadowsOnOthers: false,
                   cut: CutPlane.Off, explodeOf: null);

        /// <summary>The multi-light path: up to a handful of independent point/directional
        /// lights, combined by taking the BRIGHTEST unoccluded one per triangle (not summed
        /// — summing would double-count the ambient floor each light already carries, and
        /// "brightest light wins" is the standard cheap multi-light approximation anyway).
        ///
        /// selfShadow/castShadowsOnOthers gate a coarse, approximate occlusion test (see
        /// ShadowMap) built once per light, only when either is on — shadows cost roughly
        /// one extra pass over every triangle per enabled point light, so they are free when
        /// both toggles are off.</summary>
        public void Draw(VoxelBatch batch, SceneCamera cam, IReadOnlyList<CadSolid> solids,
                         in CadPlacement place, IReadOnlyList<CadLight> lights,
                         float radius, float zHalf,
                         float density, float brightness,
                         Func<CadSolid, CadDrawMode>? modeOf = null,
                         Func<CadSolid, int>? colorOf = null,
                         bool selfShadow = false, bool castShadowsOnOthers = false,
                         CutPlane cut = default,
                         Func<CadSolid, (float dx, float dy, float dz)>? explodeOf = null)
        {
            FacesDrawn = TrianglesDrawn = SolidsDrawn = 0;
            ClippedFraction = 0f;
            DensityScale = 1f;

            long attempted = 0, outside = 0;

            float step = batch.Spacing / Math.Clamp(density <= 0f ? 0.6f : density, 0.1f, 2f);
            float brightMul = 1f / MathF.Max(0.05f, brightness <= 0f ? 1f : brightness);

            // Both CadPlacement.MapLinear and SceneCamera.Transform are pure similarity
            // transforms (a uniform scale, plus — for Transform — an orthonormal rotation and
            // a pan, neither of which changes lengths). So an edge's length AFTER both equals
            // its raw Fusion-mm length times this one scalar, and the estimate pass below
            // never has to touch the camera at all — which is also what keeps this whole
            // scheme cheap: O(triangles), not O(voxels), same order as the draw it is sizing.
            float worldScale = place.Scale * cam.Zoom;

            // Shadow maps: one coarse occlusion structure per light, built ONCE up front —
            // not part of either pass below — so both passes query the same maps rather than
            // risking two builds drifting apart. Skipped entirely (null) when neither shadow
            // toggle is on, so the O(triangles)-per-light build cost is zero when unused.
            ShadowMap[]? maps = null;
            if (selfShadow || castShadowsOnOthers)
            {
                maps = new ShadowMap[lights.Count];
                for (int L = 0; L < lights.Count; L++)
                    maps[L] = ShadowMap.Build(solids, modeOf, lights[L]);
            }

            // Pass 1 — estimate the FULL-density sample count for every visible face, using
            // the same step (including the shade-driven density lights rely on) that pass 2
            // will use. This is what lets a big model coarsen EVENLY: one global scale-down
            // applied to every triangle, instead of an early body spending the whole budget
            // and a later one being cut off mid-face or dropped outright. Deliberately
            // OPTIMISTIC about shadows (maps: null here even when they exist) — a triangle
            // that turns out to be shadowed only needs FEWER samples than estimated, which
            // is a safe direction to be wrong in for a budget estimate.
            long estimatedTotal = 0;

            for (int bi = 0; bi < solids.Count; bi++)
            {
                var solid = solids[bi];
                if (!solid.Visible) continue;
                var mode = modeOf?.Invoke(solid) ?? CadDrawMode.Solid;
                if (mode == CadDrawMode.Hidden) continue;
                float modeMul = mode == CadDrawMode.Ghost ? 4.5f : 1f;
                bool wire = mode == CadDrawMode.Wireframe;
                bool flat = mode == CadDrawMode.Flat;

                foreach (var face in solid.Faces)
                    for (int t = 0; t < face.TriCount; t++)
                    {
                        int i = t * 3;
                        if (cut.Enabled && !TriKept(face, i, cut, out _)) continue;

                        float triStep = TriStep(face, i, bi, lights, null, false, false,
                                                step, modeMul, brightMul, flat);
                        estimatedTotal += wire
                            ? WireSampleCount(face, i, worldScale, triStep)
                            : SampleCount(SampleN(RawLongestEdge(face, i) * worldScale, triStep));
                    }
            }

            int remaining = Math.Max(0, batch.Limit - batch.Count);
            if (estimatedTotal > remaining && estimatedTotal > 0)
                // Samples per triangle grow roughly with the SQUARE of 1/step (a triangular
                // lattice, not a line), so halving the total needs step up by sqrt(2), not 2.
                DensityScale = Math.Clamp(MathF.Sqrt(remaining / (float)estimatedTotal),
                                          0.02f, 1f);

            float coarsen = DensityScale > 1e-6f ? 1f / DensityScale : 1f;

            // Pass 2 — the actual draw, at `coarsen` times the base step everywhere. No
            // per-body or per-triangle cutoff: batch.BudgetHit stays only as a safety net
            // against the estimate's own approximation (e.g. samples that land outside the
            // volume don't cost budget but did cost an estimate slot), not the normal path.
            for (int bi = 0; bi < solids.Count; bi++)
            {
                var solid = solids[bi];
                if (!solid.Visible) continue;

                var mode = modeOf?.Invoke(solid) ?? CadDrawMode.Solid;
                if (mode == CadDrawMode.Hidden) continue;

                if (batch.BudgetHit) { Report(attempted, outside); return; }

                float modeMul = mode == CadDrawMode.Ghost ? 4.5f : 1f;
                bool  wire    = mode == CadDrawMode.Wireframe;
                bool  flat    = mode == CadDrawMode.Flat;
                int   col     = colorOf?.Invoke(solid) ?? solid.Colour;
                SolidsDrawn++;

                // A per-body constant offset, in Fusion mm — added to the RAW vertex before
                // MapLinear, so an exploded body still scales and rotates with the rest of
                // the assembly, just displaced. Deliberately NOT fed to lighting, shadows or
                // the cut plane: those all read face.X/Y/Z directly for the shared reason
                // that shading/cutting agree with each other, and an exploded view is a
                // display aid layered on top, not a real change to the model.
                (float ex, float ey, float ez) = explodeOf?.Invoke(solid) ?? (0f, 0f, 0f);

                foreach (var face in solid.Faces)
                {
                    if (batch.BudgetHit) { Report(attempted, outside); return; }
                    FacesDrawn++;

                    for (int t = 0; t < face.TriCount; t++)
                    {
                        int i = t * 3;
                        bool nearCut = false;
                        if (cut.Enabled && !TriKept(face, i, cut, out nearCut)) continue;
                        int triCol = nearCut ? cut.HighlightColour : col;

                        float triStep = TriStep(face, i, bi, lights, maps, selfShadow,
                                                castShadowsOnOthers, step, modeMul, brightMul,
                                                flat) * coarsen;

                        // MapLinear (scale+flip) goes through the rotating camera; Anchor
                        // (the assembly's floor spot) is added back afterwards, so rotating
                        // or panning the scene moves the whole assembly as a rigid body
                        // instead of swinging it around the display's own origin. See
                        // CadPlacement's header for why this split exists.
                        point3d a = place.Anchor(cam.Transform(place.MapLinear(face.X[i] + ex,     face.Y[i] + ey,     face.Z[i] + ez)));
                        point3d b = place.Anchor(cam.Transform(place.MapLinear(face.X[i + 1] + ex, face.Y[i + 1] + ey, face.Z[i + 1] + ez)));
                        point3d c = place.Anchor(cam.Transform(place.MapLinear(face.X[i + 2] + ex, face.Y[i + 2] + ey, face.Z[i + 2] + ez)));

                        if (wire)
                            WireTri(batch, a, b, c, triCol, triStep, radius, zHalf,
                                    ref attempted, ref outside);
                        else
                            FillTri(batch, a, b, c, triCol, triStep, radius, zHalf,
                                    ref attempted, ref outside);
                        TrianglesDrawn++;

                        if (batch.BudgetHit) { Report(attempted, outside); return; }
                    }
                }
            }

            Report(attempted, outside);
        }

        /// <summary>Shade per TRIANGLE, at its centroid, folded into a sample step — the
        /// brightest UNOCCLUDED light wins (see the multi-light Draw() overload for why "max"
        /// rather than "sum"). Still flat shading — one normal for the whole face — but a
        /// point light genuinely varies across a large face, and collapsing that to one value
        /// per face is what makes a point light look directional again. Takes raw
        /// (pre-transform) coordinates, since shading does not depend on placement or the
        /// camera.
        ///
        /// <paramref name="unlit"/> skips lighting entirely — CadDrawMode.Flat's whole point
        /// is a body that reads as one uniform density regardless of which way each face
        /// happens to point or how it is lit. <paramref name="maps"/> null means "do not test
        /// occlusion at all" (the density-estimate pass's optimistic shortcut), independently
        /// of whether the caller ultimately wants shadows.</summary>
        private static float TriStep(CadFace face, int i, int bodyIndex,
                                     IReadOnlyList<CadLight> lights, ShadowMap[]? maps,
                                     bool selfShadow, bool castOnOthers,
                                     float step, float modeMul, float brightMul,
                                     bool unlit = false)
        {
            float shade = 1f;
            if (!unlit)
            {
                shade = lights.Count > 0 ? lights[0].Ambient : 0.35f;
                if (face.HasNormalSet)
                {
                    float mx = (face.X[i] + face.X[i + 1] + face.X[i + 2]) / 3f;
                    float my = (face.Y[i] + face.Y[i + 1] + face.Y[i + 2]) / 3f;
                    float mz = (face.Z[i] + face.Z[i + 1] + face.Z[i + 2]) / 3f;

                    for (int L = 0; L < lights.Count; L++)
                    {
                        var lt = lights[L];
                        if (!lt.On) continue;

                        float s = lt.Shade(face.NX, face.NY, face.NZ, mx, my, mz, false);
                        if (s <= shade) continue;   // cannot raise the max — skip the test

                        if (maps != null && lt.Point && (selfShadow || castOnOthers))
                        {
                            float dx = mx - lt.X, dy = my - lt.Y, dz = mz - lt.Z;
                            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                            if (maps[L].Occluded(dx, dy, dz, dist, bodyIndex,
                                                 selfShadow, castOnOthers))
                                continue;            // this light does not reach here
                        }
                        shade = s;
                    }
                }
            }
            return step / MathF.Max(0.12f, shade) * modeMul * brightMul;
        }

        /// <summary>A coarse, cheap stand-in for a real shadow map: the sphere of directions
        /// around one light, bucketed, each bucket remembering only the NEAREST triangle
        /// centroid seen in that direction and which body it belonged to.
        ///
        /// Approximate by design. Two things this can get wrong, both accepted trade-offs for
        /// staying O(triangles) per light instead of needing a real spatial index: fine
        /// detail is lost wherever two different triangles share a bucket, and only ONE
        /// occluder is remembered per bucket rather than a full sorted list — so with
        /// self-shadowing OFF, a body can occasionally read as lit when a DIFFERENT body is
        /// actually blocking it, if that body's own (ignored) geometry happened to be the
        /// nearest thing in the same bucket. Rare in practice: it needs two bodies stacked in
        /// almost the same direction from the light.</summary>
        private struct ShadowMap
        {
            private const int ThetaBuckets = 24, PhiBuckets = 12;
            private const int BucketCount = ThetaBuckets * PhiBuckets;

            private float[] _minDist;
            private int[]   _bodyIndex;

            public static ShadowMap Build(IReadOnlyList<CadSolid> solids,
                                          Func<CadSolid, CadDrawMode>? modeOf, in CadLight light)
            {
                var map = new ShadowMap
                {
                    _minDist   = new float[BucketCount],
                    _bodyIndex = new int[BucketCount],
                };
                Array.Fill(map._minDist, float.MaxValue);
                Array.Fill(map._bodyIndex, -1);
                if (!light.On || !light.Point) return map;   // an off/directional light casts no shadow here

                for (int bi = 0; bi < solids.Count; bi++)
                {
                    var solid = solids[bi];
                    if (!solid.Visible) continue;
                    var mode = modeOf?.Invoke(solid) ?? CadDrawMode.Solid;
                    if (mode == CadDrawMode.Hidden) continue;

                    foreach (var face in solid.Faces)
                        for (int t = 0; t < face.TriCount; t++)
                        {
                            int i = t * 3;
                            float cx = (face.X[i] + face.X[i + 1] + face.X[i + 2]) / 3f;
                            float cy = (face.Y[i] + face.Y[i + 1] + face.Y[i + 2]) / 3f;
                            float cz = (face.Z[i] + face.Z[i + 1] + face.Z[i + 2]) / 3f;
                            float dx = cx - light.X, dy = cy - light.Y, dz = cz - light.Z;
                            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                            int b = Bucket(dx, dy, dz);
                            if (dist < map._minDist[b]) { map._minDist[b] = dist; map._bodyIndex[b] = bi; }
                        }
                }
                return map;
            }

            private static int Bucket(float dx, float dy, float dz)
            {
                float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-6f) return 0;
                float theta = MathF.Atan2(dy, dx);                       // -pi .. pi
                float phi   = MathF.Acos(Math.Clamp(dz / len, -1f, 1f)); // 0 .. pi
                int ti = Math.Clamp((int)((theta + MathF.PI) / (2f * MathF.PI) * ThetaBuckets),
                                    0, ThetaBuckets - 1);
                int pi = Math.Clamp((int)(phi / MathF.PI * PhiBuckets), 0, PhiBuckets - 1);
                return pi * ThetaBuckets + ti;
            }

            /// <summary>Is the point at (dx,dy,dz) FROM the light, at distance `dist` and
            /// belonging to body `bodyIndex`, blocked from that light?</summary>
            public readonly bool Occluded(float dx, float dy, float dz, float dist,
                                          int bodyIndex, bool selfShadow, bool castOnOthers)
            {
                int b = Bucket(dx, dy, dz);
                int occluder = _bodyIndex[b];
                if (occluder < 0) return false;

                float nearest = _minDist[b];
                // A generous relative bias: the bucket is coarse, so triangles across a
                // range of distances share one bucket, and without slack the triangle that
                // SET the bucket's own minimum could still fail the check against itself.
                float bias = MathF.Max(0.05f, nearest * 0.03f);
                if (dist <= nearest + bias) return false;

                return occluder == bodyIndex ? selfShadow : castOnOthers;
            }
        }

        /// <summary>Cut-plane test for triangle `i`, at its centroid, in raw (pre-transform)
        /// Fusion mm — the same reason lighting is tested there too: the plane's own
        /// definition is in that space, and shading and cutting agree on "where" a triangle
        /// is for the same reason they should never disagree about anything else geometric.</summary>
        private static bool TriKept(CadFace face, int i, in CutPlane cut, out bool nearCut)
        {
            float mx = (face.X[i] + face.X[i + 1] + face.X[i + 2]) / 3f;
            float my = (face.Y[i] + face.Y[i + 1] + face.Y[i + 2]) / 3f;
            float mz = (face.Z[i] + face.Z[i + 1] + face.Z[i + 2]) / 3f;
            return cut.Keep(mx, my, mz, out nearCut);
        }

        /// <summary>The three raw (pre-transform) edge lengths of triangle `i` — shared by
        /// RawLongestEdge (fill estimate) and WireSampleCount (wireframe estimate) so the two
        /// never compute this differently.</summary>
        private static void RawEdges(CadFace face, int i, out float ab, out float bc, out float ca)
        {
            float abx = face.X[i + 1] - face.X[i],     aby = face.Y[i + 1] - face.Y[i],     abz = face.Z[i + 1] - face.Z[i];
            float bcx = face.X[i + 2] - face.X[i + 1], bcy = face.Y[i + 2] - face.Y[i + 1], bcz = face.Z[i + 2] - face.Z[i + 1];
            float cax = face.X[i] - face.X[i + 2],     cay = face.Y[i] - face.Y[i + 2],     caz = face.Z[i] - face.Z[i + 2];
            ab = MathF.Sqrt(abx * abx + aby * aby + abz * abz);
            bc = MathF.Sqrt(bcx * bcx + bcy * bcy + bcz * bcz);
            ca = MathF.Sqrt(cax * cax + cay * cay + caz * caz);
        }

        /// <summary>Longest edge of triangle `i` in the face's OWN (raw, pre-transform)
        /// coordinates — the fill estimate pass's only reason to touch this triangle at all.</summary>
        private static float RawLongestEdge(CadFace face, int i)
        {
            RawEdges(face, i, out float ab, out float bc, out float ca);
            return MathF.Max(ab, MathF.Max(bc, ca));
        }

        /// <summary>Mirrors FillTri's own subdivision count, so the estimate pass predicts
        /// exactly what the draw pass will attempt.</summary>
        private static int SampleN(float longest, float step)
            => Math.Clamp((int)MathF.Ceiling(longest / MathF.Max(1e-6f, step)), 1, 512);

        /// <summary>Points in a barycentric lattice subdivided n ways: (n+1)(n+2)/2.</summary>
        private static long SampleCount(int n) => (long)(n + 1) * (n + 2) / 2;

        /// <summary>Mirrors WireTri's own per-edge subdivision, in RAW (pre-transform, hence
        /// worldScale) coordinates — the wireframe estimate's counterpart to SampleCount.</summary>
        private static long WireSampleCount(CadFace face, int i, float worldScale, float step)
        {
            RawEdges(face, i, out float ab, out float bc, out float ca);
            return (SampleN(ab * worldScale, step) + 1)
                 + (SampleN(bc * worldScale, step) + 1)
                 + (SampleN(ca * worldScale, step) + 1);
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

        /// <summary>CadDrawMode.Wireframe: the triangle's three edges, point-sampled the same
        /// way FillTri samples a face — no reliance on VoxelBatch.Line, because this needs
        /// the same attempted/outside counters FillTri keeps for ClippedFraction, and the
        /// same caller-supplied `step` (already carrying density/coarsen/brightness) rather
        /// than VoxelBatch's own fixed spacing.</summary>
        private static void WireTri(VoxelBatch batch, point3d a, point3d b, point3d c,
                                    int col, float step, float radius, float zHalf,
                                    ref long attempted, ref long outside)
        {
            WireEdge(batch, a, b, col, step, radius, zHalf, ref attempted, ref outside);
            if (batch.BudgetHit) return;
            WireEdge(batch, b, c, col, step, radius, zHalf, ref attempted, ref outside);
            if (batch.BudgetHit) return;
            WireEdge(batch, c, a, col, step, radius, zHalf, ref attempted, ref outside);
        }

        private static void WireEdge(VoxelBatch batch, point3d a, point3d b, int col, float step,
                                     float radius, float zHalf, ref long attempted, ref long outside)
        {
            float len = Dist(a, b);
            int n = Math.Clamp((int)MathF.Ceiling(len / MathF.Max(1e-6f, step)), 1, 2048);

            for (int k = 0; k <= n; k++)
            {
                float t = k / (float)n;
                var p = new point3d(a.x + (b.x - a.x) * t,
                                    a.y + (b.y - a.y) * t,
                                    a.z + (b.z - a.z) * t);

                attempted++;
                if (!CadPlacement.Inside(p, radius, zHalf)) outside++;

                if (!batch.Add(p, col) && batch.BudgetHit) return;
            }
        }
    }
}
