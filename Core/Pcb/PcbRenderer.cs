// ═══════════════════════════════════════════════════════════════════════════
//  PcbRenderer.cs — a board in the volume
//
//  The point of showing a PCB volumetrically is the stack: copper, silk, mask
//  and drills separated in Z so you can see which layer a track is on and where
//  a via actually lands, instead of decoding a flat overlay of eight colours.
//  So the renderer's job is: fit the board to the display, spread the layers
//  along Z (-Z is up), and draw each layer's geometry on its own plane.
//
//  Fitting uses the board's bounding CIRCLE, not its bounding box, because the
//  display volume is a cylinder — a board fitted by width alone pokes out of
//  the round wall when you rotate it 45 degrees.
//
//  Voxel economy (this is where a board will eat the whole budget if you let it):
//    • tracks are point-sampled lines, widened to at most 3 parallel passes and
//      only when the real copper width is wider than a couple of voxels;
//    • copper pours draw as outlines (optionally hatched) — a filled pour is
//      tens of thousands of voxels that all read as one solid slab anyway;
//    • pads fill only when they are small; large pads draw as rings;
//    • mesh clouds are decimated by a stride, never truncated, so reducing the
//      budget thins the model evenly instead of cutting half the board off.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using EDes.Sim;
using Voxon;

namespace EDes.Pcb
{
    /// <summary>Render-time options — all live UI settings, none baked into the board.</summary>
    public struct PcbViewOptions
    {
        public float LayerSpacing;     // world units between layers (before clamping)
        public float TrackScale;       // multiplier on copper width
        public bool  ShowPads;
        public bool  ShowRegions;
        public bool  FillRegions;      // hatch pours instead of outlining them
        public bool  ShowHoles;
        public bool  ShowVias;         // via barrels between the copper they connect
        public float ViaMaxDiaMm;      // plated holes at or under this are vias
        public float ViaDisplayVoxels; // drawn radius, in voxels — same for every via
        public float PourDensity;      // 1 = default outline sampling, higher = tighter
        public float HatchDensity;     // 1 = a hatch line every 6 voxels
        public bool  ShowMeshes;
        public bool  ShowCad;          // STEP solids, as edge wireframes
        public float CadBrightness;    // separate from Brightness: CAD sits above the board
        public bool  CadLighting;      // shade edges by their adjacent-face normals
        public bool  CadSurfaces;     // flat-shaded fill on the planar faces
        public float CadSurfaceDensity;// 1 = one sample per voxel; lower is sparser
        public float CadZOffset;       // nudge the 3D model along Z, world units

        // ── Inspection ────────────────────────────────────────────────────────
        public bool  Inspect;          // probe active: dim everything it is not over

        /// <summary>0 all layers, 1 copper + outline (signal), 2 outline only (component).
        /// A VIEW filter, never a change to the user's own layer toggles — those are
        /// persisted, and clobbering them would lose the operator's setup every time they
        /// glanced at an inspector.</summary>
        public int   LayerFilter;
        public bool  HideParts;        // component-inspector hides copper; signal hides parts
        public float ProbeX, ProbeY, ProbeZ;   // probe position, DISPLAY space
        public float DimFactor;        // brightness for everything not under the probe
        public float SnapRange;        // how far the probe reaches for a target, world units
        public float Pulse;            // 0..1 cyan<->white phase for the net highlight

        /// <summary>Explicitly picked net, or -1. Overrides whatever the probe is over: it
        /// was chosen by name, where a hover is wherever the pointer happens to be.</summary>
        public int    PickedNet;
        /// <summary>Explicitly picked component designator, or empty.</summary>
        public string PickedDesignator;
        public float CadAmbient;       // floor brightness, so unlit edges never vanish
        public float CadLightX, CadLightY, CadLightZ;   // light direction, board frame

        /// <summary>Treat the light as a POINT at CadLightX/Y/Z rather than a direction.
        ///
        /// A directional light gives every face on the board the same L, so two identical
        /// parts at opposite corners shade identically and the board reads flat. A point
        /// light gives each face its own L and its own distance, which is what makes one
        /// side of a component brighter than the other and what makes the model look like
        /// it is sitting in a room rather than in a diagram.</summary>
        public bool  CadPointLight;

        /// <summary>Where the point light is, as a fraction of the board's own half-extents
        /// (X, Y) and height (Z). 0,0,2 is centred two board-heights above it; 1,1,1 is over
        /// one corner. Fractions rather than millimetres so the same setting means the same
        /// thing on a 16 mm sensor board and a 300 mm backplane.</summary>
        public float CadLightFx, CadLightFy, CadLightFz;

        /// <summary>Falloff distance as a fraction of the board diagonal. At exactly this
        /// distance the light is half strength. 0 or less disables falloff, leaving a point
        /// light with direction but no distance -- useful when the falloff is fighting the
        /// seven-colour palette and you only want the directional cue.</summary>
        public float CadLightRange;

        /// <summary>Draw a marker where the point light is. Otherwise its position is only
        /// visible through its effect, which makes it very hard to aim.</summary>
        public bool  CadShowLight;
        public bool  ShowCursor;
        public float CursorXmm, CursorYmm;
        public float Brightness;
        public int   IsolateLayer;     // -1 = all layers, else only this index
        public bool  ShowComponents;   // markers from the placement file
        public bool  ShowLabels;       // designators next to those markers
        public int   LabelLimit;       // skip labels entirely above this part count
        public float TextSize;         // label size, in display units
    }

    /// <summary>What the inspection probe is currently over. Reused between frames
    /// rather than reallocated — this is rebuilt every frame the probe is active.</summary>
    public sealed class InspectHit
    {
        public bool   Hit;
        public string Kind  = "";     // "trace", "component", "solid"
        public int    Index = -1;
        public string Title = "";
        public readonly List<string> Lines = new();

        /// <summary>Net the hit belongs to, or -1. Set for traces only.</summary>
        public int Net = -1;

        public void Clear()
        {
            Hit = false; Kind = ""; Index = -1; Title = "";
            Net = -1;
            Lines.Clear();
        }
    }

    public sealed class PcbRenderer
    {
        /// <summary>Vias draw copper-amber so they read as plated conductor and are
        /// unmistakable against the grey drill bores.</summary>
        private const int VIA_COLOUR = 0xE8A020;

        /// <summary>Blind and buried vias draw cooler, so a barrel that stops short reads
        /// as deliberate rather than as a clipping bug.</summary>
        private const int BLIND_VIA_COLOUR = 0x40D0E8;

        /// <summary>How many voxels wide a highlighted net draws. Thick enough to read as
        /// "this one", not so thick that a ground net floods the board.</summary>
        private const float HIGHLIGHT_VOXELS = 5f;

        /// <summary>Minimum Z extent for through-board features, so they stay visible when
        /// layer spacing is zero or near it. See where it is assigned in Draw.</summary>
        private float _featureZ = 0.1f;

        /// <summary>Z of each copper layer for the current frame's layout. Rebuilt every
        /// Draw because layer visibility and spacing are live UI settings.</summary>
        private readonly System.Collections.Generic.List<float> _copperZ = new();

        /// <summary>What the probe found this frame. Valid after Draw.</summary>
        public InspectHit Probe { get; } = new InspectHit();

        // Resolved once per Draw, then consulted by every draw pass so exactly one thing
        // is at full brightness.
        private int _hoverLayer = -1, _hoverSolid = -1, _hoverComponent = -1;
        private int _hoverNet = -1;

        /// <summary>Where the probe snapped to, in DISPLAY space, so the app can draw the
        /// leader line from the probe to it. Valid when ProbeHasTarget.</summary>
        public bool    ProbeHasTarget { get; private set; }
        public point3d ProbeTarget    { get; private set; }

        /// <summary>Net the probe is on, for the highlight and the readout.</summary>
        public int HoverNet => _hoverNet;

        // ── Board-to-world mapping from the last Draw (the app quotes it in the HUD) ──
        public float Scale   { get; private set; } = 1f;   // world units per mm
        public float Spacing { get; private set; }         // world units between layers
        public int   VisibleLayers { get; private set; }

        /// <summary>Draw the board. Returns immediately if there is nothing loaded.</summary>
        public void Draw(VoxelBatch batch, Sim.Hud hud, SceneCamera cam, PcbBoard board,
                         in PcbViewOptions opt, float radius, float zHalf)
        {
            if (!board.HasGeometry) return;

            // ── Fit: bounding circle into the cylinder, with margin ───────────
            float diag = MathF.Sqrt(board.WidthMm * board.WidthMm + board.HeightMm * board.HeightMm);
            Scale = diag > 1e-3f ? radius * 0.88f / (0.5f * diag) : 1f;

            // ── Stack: never taller than the usable Z range ───────────────────
            int layerCount = 0;
            foreach (var l in board.Layers) if (l.Visible) layerCount++;
            VisibleLayers = layerCount;
            int slots = Math.Max(1, layerCount);
            // Zero and NEGATIVE are both allowed. Zero collapses the stack into one
            // plane, which is the right view when you want the board as fabricated rather
            // than exploded; negative reverses it, so the bottom layer draws on top --
            // which is exactly what you want when reading the board from underneath.
            //
            // The height limit still applies, but to the MAGNITUDE: MathF.Min alone would
            // have let a large negative through unclamped, since -50 is less than any
            // positive ceiling.
            float maxSpan = zHalf * 1.5f / slots;
            Spacing = MathF.Sign(opt.LayerSpacing) *
                      MathF.Min(MathF.Abs(opt.LayerSpacing), maxSpan);

            // Minimum Z extent for features that are physically THROUGH the board -- drill
            // bores, via barrels, the cursor, component markers. At spacing 0 every layer
            // shares a plane, and scaling those off the spacing would collapse them to
            // zero length: a drill would become a single voxel and a via would vanish
            // entirely, which is not what "put the layers together" should mean.
            _featureZ = MathF.Max(MathF.Abs(Spacing), batch.Spacing * 4f);

            float cx = board.CentreX, cy = board.CentreY;
            float z0 = -(slots - 1) * 0.5f * Spacing;      // first layer highest (-Z is up)

            ResolveProbe(cam, board, opt, z0);

            // Z of every copper layer, in stack order. Vias are defined against COPPER,
            // not against the visible stack — a 2-layer board can easily have 14 visible
            // layers once silk, mask, paste and mechanical are counted, and spanning
            // those made every barrel stick far out of the board.
            BuildCopperZ(board, z0, Spacing, opt);

            int index = 0;
            for (int li = 0; li < board.Layers.Count; li++)
            {
                var layer = board.Layers[li];
                if (!LayerShown(layer, opt, li)) continue;

                float z    = z0 + index * Spacing;
                int   col  = layer.Colour;          // snapped at the batch; see Palette
                // De-emphasis and brightness both act on SPACING now, for the same reason:
                // colour scaling cannot express either on a seven-colour display.
                float dimS = DimSpacing(_hoverLayer == li)
                           / MathF.Max(0.05f, opt.Brightness);
                index++;

                DrawLayer(batch, cam, layer, col, z, cx, cy, opt, dimS);
                if (batch.BudgetHit) return;
            }

            // AFTER the layers so it lands on top of them, and BEFORE the vias and the
            // rest so a tight budget cannot eat the one thing the operator selected.
            if (_hoverNet >= 0) DrawNetHighlight(batch, cam, board, opt, cx, cy, z0);

            if (opt.ShowVias)  DrawVias(batch, cam, board, opt, cx, cy, z0, Spacing, slots);
            if (opt.ShowHoles) DrawHoles(batch, cam, board, cx, cy, z0, Spacing, slots, opt);
            if (opt.ShowCad && !opt.HideParts) DrawCadSolids(batch, cam, board, opt, cx, cy, z0);
            if (opt.ShowCad && opt.CadSurfaces && !opt.HideParts)
                DrawCadFaces(batch, cam, board, opt, cx, cy, z0);
            if (opt.ShowMeshes && !opt.HideParts) DrawMeshes(batch, cam, board, cx, cy, opt);
            if (opt.ShowComponents && !opt.HideParts)
                DrawComponents(batch, hud, cam, board, opt, cx, cy, z0, slots);
            if (opt.ShowCursor) DrawCursor(batch, cam, board, opt, cx, cy, z0, Spacing, slots);
        }

        // ── One layer ─────────────────────────────────────────────────────────
        private void DrawLayer(VoxelBatch batch, SceneCamera cam, PcbLayer layer, int col, float z,
                               float cx, float cy, in PcbViewOptions opt, float dimS)
        {
            // Tracks
            foreach (var s in layer.Segs)
            {
                float wWorld = s.W * Scale * MathF.Max(0.1f, opt.TrackScale);
                int   passes = Math.Clamp((int)(wWorld / batch.Spacing), 1, 3);

                // Offset the extra passes perpendicular to the track so width reads.
                float dx = s.X1 - s.X0, dy = s.Y1 - s.Y0;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                float px = len > 1e-6f ? -dy / len : 0f;
                float py = len > 1e-6f ?  dx / len : 0f;

                for (int k = 0; k < passes; k++)
                {
                    float off = (k - (passes - 1) * 0.5f) * batch.Spacing;
                    PatternLine(batch,
                        cam.Transform(Wx(s.X0, cx) + px * off, Wy(s.Y0, cy) + py * off, z),
                        cam.Transform(Wx(s.X1, cx) + px * off, Wy(s.Y1, cy) + py * off, z),
                        col, dimS, layer.Pattern);
                }
                if (batch.BudgetHit) return;
            }

            // Pads
            if (opt.ShowPads)
            {
                int padCol = Palette.Scale(col, 1.25f);
                foreach (var p in layer.Pads)
                {
                    float w = p.W * Scale, h = p.H * Scale;
                    float x = Wx(p.X, cx), y = Wy(p.Y, cy);

                    switch (p.Shape)
                    {
                        case PadShape.Rect:
                            RectXY(batch, cam, x, y, z, w, h, padCol);
                            break;
                        case PadShape.Obround:
                            CircleXY(batch, cam, x, y, z, MathF.Min(w, h) * 0.5f, padCol,
                                     fill: MathF.Min(w, h) < batch.Spacing * 6f);
                            RectXY(batch, cam, x, y, z, w, h, Palette.Scale(padCol, 0.7f));
                            break;
                        default:
                            CircleXY(batch, cam, x, y, z, w * 0.5f, padCol,
                                     fill: w < batch.Spacing * 6f);
                            break;
                    }
                    if (batch.BudgetHit) return;
                }
            }

            // Copper pours / filled regions
            if (opt.ShowRegions)
            {
                int regCol = Palette.Scale(col, 0.55f);
                foreach (var r in layer.Regions)
                {
                    if (r.Count < 2) continue;

                    // Density is expressed as a multiplier on how CLOSE the samples are,
                    // so higher reads as denser. VoxelBatch.Line wants the inverse (a
                    // spacing multiplier), hence the reciprocal.
                    float outlineMul = 1f / Math.Clamp(opt.PourDensity <= 0f ? 1f
                                                                            : opt.PourDensity,
                                                       0.1f, 8f);
                    for (int i = 0; i < r.Count; i++)
                    {
                        int j = (i + 1) % r.Count;
                        batch.Line(cam.Transform(Wx(r.X[i], cx), Wy(r.Y[i], cy), z),
                                   cam.Transform(Wx(r.X[j], cx), Wy(r.Y[j], cy), z),
                                   regCol, outlineMul);
                        if (batch.BudgetHit) return;
                    }
                    if (opt.FillRegions) HatchRegion(batch, cam, r, cx, cy, z,
                                                     Palette.Scale(col, 0.35f), opt);
                    if (batch.BudgetHit) return;
                }
            }
        }

        /// <summary>A line stroked solid, dashed or dotted.
        ///
        /// The second axis of layer identity. Seven colours cannot label twelve layer
        /// kinds across two sides of a board, so colour says WHAT a layer is and pattern
        /// says which side or which of a colour-sharing pair — dotted yellow is the pad
        /// master where solid yellow is the outline, and bottom-side layers dash where
        /// their top-side counterparts do not.
        ///
        /// Dash lengths are in VOXELS, not millimetres, so a dash never falls below the
        /// display's resolution and collapse into a solid line at one board scale while
        /// looking right at another.</summary>
        private static void PatternLine(VoxelBatch batch, point3d a, point3d b,
                                        int col, float spacingMul, int pattern)
        {
            if (pattern <= 0) { batch.Line(a, b, col, spacingMul); return; }

            float dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
            float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len <= 1e-6f) { batch.Add(a, col); return; }

            // dashed: 6 on, 4 off.   dotted: 2 on, 4 off.
            float on  = batch.Spacing * (pattern == 1 ? 6f : 2f);
            float off = batch.Spacing * 4f;
            float period = on + off;
            if (period <= 1e-6f) { batch.Line(a, b, col, spacingMul); return; }

            for (float t = 0f; t < len; t += period)
            {
                float t1 = MathF.Min(t + on, len);
                float f0 = t / len, f1 = t1 / len;
                batch.Line(new point3d(a.x + dx * f0, a.y + dy * f0, a.z + dz * f0),
                           new point3d(a.x + dx * f1, a.y + dy * f1, a.z + dz * f1),
                           col, spacingMul);
                if (batch.BudgetHit) return;
            }
        }

        /// <summary>Scanline hatch of a polygon — a pour reads as filled for a fraction
        /// of the voxels a solid fill would cost.</summary>
        /// <summary>Cross-hatch a copper pour: two families of parallel lines at right
        /// angles, run diagonally.
        ///
        /// A pour is the largest object on a board, and filling it — even as a
        /// single-direction hatch dense enough to read as solid — puts a whole PLANE of
        /// voxels into the volume. On a transparent display that is close to the worst
        /// thing you can draw: it costs more budget than everything else combined, and it
        /// veils every layer behind it, which is exactly what you were looking through the
        /// display to see.
        ///
        /// A lattice reads unmistakably as "copper is here" while leaving most of the plane
        /// empty to look through. DIAGONAL rather than axis-aligned because tracks, pads
        /// and the outline are overwhelmingly orthogonal — a 45 degree lattice cannot be
        /// mistaken for routing, where a 0/90 one competes with it.</summary>
        private void HatchRegion(VoxelBatch batch, SceneCamera cam, PcbRegion r,
                                 float cx, float cy, float z, int col,
                                 in PcbViewOptions opt)
        {
            // Base of 10 voxels between lines rather than 6, because there are now TWO
            // families: at the old spacing a cross-hatch would cost twice what the single
            // hatch did. Density scales the gap, clamped because a step below the voxel
            // spacing is a solid fill in disguise — it would cost a fill's budget while
            // still being described as a hatch.
            float density = Math.Clamp(opt.HatchDensity <= 0f ? 1f : opt.HatchDensity, 0.1f, 8f);
            float stepMm  = batch.Spacing * (10f / density) / MathF.Max(1e-6f, Scale);

            const float R = 0.70710678f;      // cos = sin = 45 degrees
            HatchPass(batch, cam, r, cx, cy, z, col, stepMm, R,  R);
            if (batch.BudgetHit) return;
            HatchPass(batch, cam, r, cx, cy, z, col, stepMm, R, -R);
        }

        /// <summary>One family of parallel scanlines running along (ux, uy).
        ///
        /// The line runs along u and steps along the perpendicular v — the same even-odd
        /// crossing fill as an axis-aligned hatch, expressed in a rotated frame, so one
        /// implementation covers every angle instead of a special case per direction.</summary>
        private void HatchPass(VoxelBatch batch, SceneCamera cam, PcbRegion r,
                               float cx, float cy, float z, int col, float stepMm,
                               float ux, float uy)
        {
            float vx = -uy, vy = ux;

            float minP = float.MaxValue, maxP = float.MinValue;
            for (int i = 0; i < r.Count; i++)
            {
                float proj = r.X[i] * vx + r.Y[i] * vy;
                if (proj < minP) minP = proj;
                if (proj > maxP) maxP = proj;
            }
            if (maxP <= minP || stepMm <= 1e-9f) return;

            // 256, not 64: a diagonal through a complex pour crosses far more edges than a
            // horizontal one, and the old limit stopped collecting partway — which drops
            // spans out of the MIDDLE of the fill rather than failing visibly.
            Span<float> along = stackalloc float[256];

            for (float level = minP; level <= maxP; level += stepMm)
            {
                int hits = 0;
                for (int i = 0; i < r.Count && hits < along.Length; i++)
                {
                    int j = (i + 1) % r.Count;
                    float p0 = r.X[i] * vx + r.Y[i] * vy;
                    float p1 = r.X[j] * vx + r.Y[j] * vy;
                    if (!((level >= p0 && level < p1) || (level >= p1 && level < p0))) continue;

                    float t  = (level - p0) / (p1 - p0);
                    float hx = r.X[i] + (r.X[j] - r.X[i]) * t;
                    float hy = r.Y[i] + (r.Y[j] - r.Y[i]) * t;
                    along[hits++] = hx * ux + hy * uy;
                }

                for (int a = 1; a < hits; a++)
                for (int b = a; b > 0 && along[b - 1] > along[b]; b--)
                    (along[b - 1], along[b]) = (along[b], along[b - 1]);

                for (int k = 0; k + 1 < hits; k += 2)
                {
                    // Back to board space: P = along * u + level * v.
                    float ax = along[k]     * ux + level * vx;
                    float ay = along[k]     * uy + level * vy;
                    float bx = along[k + 1] * ux + level * vx;
                    float by = along[k + 1] * uy + level * vy;

                    batch.Line(cam.Transform(Wx(ax, cx), Wy(ay, cy), z),
                               cam.Transform(Wx(bx, cx), Wy(by, cy), z), col);
                    if (batch.BudgetHit) return;
                }
            }
        }

        // ── Drills ────────────────────────────────────────────────────────────
        // A hole is drawn as what it physically is: a bore through the whole stack.
        private void DrawHoles(VoxelBatch batch, SceneCamera cam, PcbBoard board,
                               float cx, float cy, float z0, float spacing, int slots,
                               in PcbViewOptions opt)
        {
            // Overhang from _featureZ, not from spacing, so a coplanar stack still shows
            // a bore rather than a dot.
            float over = _featureZ * 0.35f;
            float zTop = MathF.Min(z0, z0 + (slots - 1) * spacing) - over;
            float zBot = MathF.Max(z0, z0 + (slots - 1) * spacing) + over;

            foreach (var h in board.Holes)
            {
                // Vias are the same Excellon hits, so without this every via would be
                // drawn twice — once grey as a drill, once as a barrel — and the grey
                // pass would win on colour wherever they overlapped.
                if (opt.ShowVias && PcbBoard.IsVia(h, opt.ViaMaxDiaMm)) continue;

                int col = h.Plated ? 0xC0C0C0 : 0x707070;
                float x = Wx(h.X, cx), y = Wy(h.Y, cy);
                float r = MathF.Max(h.Dia * 0.5f * Scale, batch.Spacing);

                // Bore: 4 walls at the hole radius, plus the centre line for small holes.
                if (r > batch.Spacing * 1.5f)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        float a = k * MathF.PI * 0.5f;
                        float ox = MathF.Cos(a) * r, oy = MathF.Sin(a) * r;
                        batch.Line(cam.Transform(x + ox, y + oy, zTop),
                                   cam.Transform(x + ox, y + oy, zBot), col);
                    }
                }
                else
                {
                    batch.Line(cam.Transform(x, y, zTop), cam.Transform(x, y, zBot), col);
                }

                if (h.Slot)
                {
                    float x1 = Wx(h.X1, cx), y1 = Wy(h.Y1, cy);
                    batch.Line(cam.Transform(x, y, zTop), cam.Transform(x1, y1, zTop), col);
                    batch.Line(cam.Transform(x, y, zBot), cam.Transform(x1, y1, zBot), col);
                    batch.Line(cam.Transform(x1, y1, zTop), cam.Transform(x1, y1, zBot), col);
                }
                if (batch.BudgetHit) return;
            }
        }

        // ── Vias ──────────────────────────────────────────────────────────────
        // A via exists to be a CONTINUOUS conductor between layers, so the barrel is
        // drawn as ONE line spanning the full stack — from the topmost layer plane to
        // the bottommost — rather than as a segment per gap.
        //
        // That is what makes it independent of layer separation. VoxelBatch.Line
        // point-samples at the frame's voxel spacing, so stretching LayerSpacing makes
        // the barrel longer, never dotted, and there is no seam at a layer boundary
        // because there is no join there to leak. Per-gap segments would look identical
        // at the default spacing and fall apart as soon as the stack was pulled open.
        //
        // Budget note: a barrel costs (stack height / voxel spacing) voxels, so a
        // via-dense board is easily tens of thousands of voxels. It is drawn BEFORE the
        // drills deliberately — under a tight budget the drills thin out first.
        private void DrawVias(VoxelBatch batch, SceneCamera cam, PcbBoard board,
                              in PcbViewOptions opt, float cx, float cy,
                              float z0, float spacing, int slots)
        {
            if (_copperZ.Count == 0) return;   // no copper: nothing for a via to connect

            int through = Palette.Scale(VIA_COLOUR, opt.Brightness);
            int blind   = Palette.Scale(BLIND_VIA_COLOUR, opt.Brightness);
            int copperCount = _copperZ.Count;

            foreach (var h in board.Holes)
            {
                if (!PcbBoard.IsVia(h, opt.ViaMaxDiaMm)) continue;

                // The span, in copper layers. Unstated means through, which is the safe
                // reading: showing a connection that exists beats hiding one.
                PcbBoard.ViaSpan(h, copperCount, out int first, out int last);

                float zTop = _copperZ[first - 1];
                float zBot = _copperZ[last - 1];
                if (zBot < zTop) (zTop, zBot) = (zBot, zTop);

                // A via connecting coplanar copper has zero height, and a zero-length
                // barrel is a single voxel -- the connection would be there in the data and
                // invisible on the display. Given a minimum height it still reads as a via
                // standing between the layers it joins, whatever the spacing.
                if (zBot - zTop < _featureZ)
                {
                    float mid = (zTop + zBot) * 0.5f;
                    zTop = mid - _featureZ * 0.5f;
                    zBot = mid + _featureZ * 0.5f;
                }

                int col = h.IsBlind(copperCount) ? blind : through;

                float x = Wx(h.X, cx), y = Wy(h.Y, cy);

                // Vias are drawn at a FIXED radius, identical for every via, rather than
                // at true scale. At true scale they are invisible: a 0.3 mm via on a 35 mm
                // board works out at 0.027 world units, under the 0.03 voxel spacing, so
                // the wall and rings were skipped and all that survived was a bare centre
                // line one voxel wide. A via is a topological feature — you need to see
                // THAT it is there and what it connects, not how wide it is — so a
                // legible constant beats a faithful sub-voxel one. True diameters are
                // still what classify a via and what the readout quotes.
                float r = batch.Spacing * Math.Clamp(opt.ViaDisplayVoxels, 0.5f, 12f);

                // The conductor itself: always drawn, always the full span height.
                // Everything below is decoration and may be skipped or budget-cut
                // without ever breaking the connection this line represents.
                batch.Line(cam.Transform(x, y, zTop), cam.Transform(x, y, zBot), col);

                if (r > batch.Spacing * 1.5f)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        float a  = k * MathF.PI * 0.5f;
                        float ox = MathF.Cos(a) * r, oy = MathF.Sin(a) * r;
                        batch.Line(cam.Transform(x + ox, y + oy, zTop),
                                   cam.Transform(x + ox, y + oy, zBot), col);
                    }

                    // A ring where the barrel meets each copper layer it connects — and
                    // only those. Ringing a layer the via does not reach would draw a
                    // connection that is not there.
                    for (int i = first - 1; i <= last - 1; i++)
                        CircleXY(batch, cam, x, y, _copperZ[i], r, col, fill: false);
                }

                if (batch.BudgetHit) return;
            }
        }

        // ── CAD solids (STEP) ─────────────────────────────────────────────────
        // Edges, not surfaces. On a transparent display a filled or densely-sampled
        // surface shows its own back faces through its front and the part reads as fog,
        // so the wireframe is not a cheap approximation of the real thing here — it is
        // the more legible rendering, and it happens to cost a fraction of the voxels.
        //
        // Z uses the same convention as mesh clouds: the model frame has Z up, the
        // display has -Z up, so heights are negated on the way through.
        private void DrawCadSolids(VoxelBatch batch, SceneCamera cam, PcbBoard board,
                                   in PcbViewOptions opt, float cx, float cy, float z0)
        {
            // The 3D model sits ON TOP of the exploded Gerber stack, not through the middle
            // of it. Anchored at z=0 the model rose from the stack's CENTRE, which put the
            // TOP silkscreen above the components — physically backwards, since silkscreen
            // is printed on the board underneath the parts. z0 is the topmost layer plane,
            // and -Z is up, so subtracting height from it lifts the model clear.
            float zBase = z0 + opt.CadZOffset;
            // Brightness as DENSITY. Scaling the colour cannot brighten anything (see
            // Palette), but packing more voxels into the same area genuinely does look
            // brighter on a volumetric display -- so the slider keeps its meaning and
            // starts working again instead of being a no-op.
            float bright   = opt.CadBrightness > 0f ? opt.CadBrightness : opt.Brightness;
            float brightMul = 1f / MathF.Max(0.05f, bright);

            var light = CadLight.Build(opt, board);
            if (opt.CadShowLight) DrawLightMarker(batch, cam, light, cx, cy, zBase);

            for (int si = 0; si < board.Solids.Count; si++)
            {
                var solid = board.Solids[si];
                if (!solid.Visible || solid.Edges.Count == 0) continue;

                // Colour is snapped at the batch, so scaling it here would achieve
                // nothing; de-emphasis is the spacing multiplier folded in below.
                int   baseCol = solid.Colour;
                float solidDim = DimSpacing(_hoverSolid == si);

                foreach (var e in solid.Edges)
                {
                    // Two-sided |N·L|: the SIGN is meaningless on a display with no
                    // viewpoint, where every face is seen from both sides at once, so the
                    // magnitude is what carries the shape.
                    //
                    // Expressed as SPACING, not brightness. A darker colour is not
                    // available (see Palette): edges facing across the light are drawn
                    // sparser instead, which reads as recessive and costs less budget
                    // rather than the same.
                    float mul = 1f;
                    if (opt.CadLighting && e.HasNormal)
                    {
                        // Shaded at the edge's MIDPOINT. A point light varies along a long
                        // edge, but an edge is one polyline drawn at one spacing, so it gets
                        // one sample; the midpoint is the least wrong single choice.
                        int mid = e.Count / 2;
                        float shade = light.Shade(e.NX, e.NY, e.NZ,
                                                  e.X[mid], e.Y[mid], e.Z[mid], true);
                        mul = 1f / MathF.Max(0.15f, shade);
                    }
                    mul *= solidDim * brightMul;
                    int col = baseCol;

                    // A polyline, so consecutive points are joined — drawing the points
                    // alone would dot a long straight edge into two lonely voxels.
                    for (int i = 1; i < e.Count; i++)
                    {
                        var a = cam.Transform(Wx(e.X[i - 1], cx), Wy(e.Y[i - 1], cy),
                                              zBase - e.Z[i - 1] * Scale);
                        var b = cam.Transform(Wx(e.X[i], cx), Wy(e.Y[i], cy),
                                              zBase - e.Z[i] * Scale);
                        batch.Line(a, b, col, mul);
                    }
                    if (batch.BudgetHit) return;
                }
            }
        }

        // ── Net highlight ─────────────────────────────────────────────────────
        // The whole net, thick, pulsing cyan to white. Thickness comes from repeated
        // passes offset PERPENDICULAR to each segment in its own layer plane — the same
        // trick the normal track drawing uses, because there is no line-width to set on a
        // display that draws points.
        //
        // The net came from PcbNets, which derives connectivity geometrically: plain
        // Gerber carries no net names, so "the same trace" means "the copper this is
        // physically joined to, through its vias" rather than a name lookup.
        private void DrawNetHighlight(VoxelBatch batch, SceneCamera cam, PcbBoard board,
                                      in PcbViewOptions opt, float cx, float cy, float z0)
        {
            var nets = board.Nets;
            if (nets == null) return;

            // Cyan to white and back. Only the red and green channels move — blue is
            // already full in both, so lerping it would be a no-op that reads as noise.
            float t = Math.Clamp(opt.Pulse, 0f, 1f);
            int rg = (int)(60f + 195f * t);
            int col = (rg << 16) | (rg << 8) | 0xFF;

            int passes = Math.Clamp((int)MathF.Round(HIGHLIGHT_VOXELS), 1, 9);

            int index = 0;
            for (int li = 0; li < board.Layers.Count; li++)
            {
                var layer = board.Layers[li];
                if (!LayerShown(layer, opt, li)) continue;
                float z = z0 + index * Spacing;
                index++;

                for (int i = 0; i < layer.Segs.Count; i++)
                {
                    if (nets.SegNet(li, i) != _hoverNet) continue;

                    var sg = layer.Segs[i];
                    float ax = Wx(sg.X0, cx), ay = Wy(sg.Y0, cy);
                    float bx = Wx(sg.X1, cx), by = Wy(sg.Y1, cy);

                    // Perpendicular in the layer plane, normalised. A zero-length segment
                    // (a pad drawn as a degenerate draw) has no direction, so it just gets
                    // the centre pass.
                    float dx = bx - ax, dy = by - ay;
                    float len = MathF.Sqrt(dx * dx + dy * dy);
                    float nx = len > 1e-6f ? -dy / len : 0f;
                    float ny = len > 1e-6f ?  dx / len : 0f;

                    for (int k = 0; k < passes; k++)
                    {
                        float off = (k - (passes - 1) * 0.5f) * batch.Spacing;
                        batch.Line(cam.Transform(ax + nx * off, ay + ny * off, z),
                                   cam.Transform(bx + nx * off, by + ny * off, z), col);
                    }
                    if (batch.BudgetHit) return;
                }

                // Pads on the net too — a net that stopped at its pads would look broken
                // exactly where it matters, at the component it connects to.
                for (int i = 0; i < layer.Pads.Count; i++)
                {
                    if (nets.PadNet(li, i) != _hoverNet) continue;
                    var pd = layer.Pads[i];
                    float r = MathF.Max(pd.W, pd.H) * 0.5f * Scale;
                    CircleXY(batch, cam, Wx(pd.X, cx), Wy(pd.Y, cy), z,
                             MathF.Max(r, batch.Spacing * 2f), col, fill: true);
                    if (batch.BudgetHit) return;
                }
            }
        }

        // ── The light ─────────────────────────────────────────────────────────
        //
        // One place that answers "how lit is this point, facing this way", so the edge
        // pass and the surface pass cannot drift apart. They differ only in whether the
        // normal's SIGN counts, which is a property of what is being shaded rather than
        // of the light: an edge is shared by two faces pointing opposite ways so its sign
        // is meaningless, while a face has one outward normal and the sign is exactly what
        // makes it turn away.
        private readonly struct CadLight
        {
            public readonly bool  Point;
            public readonly float X, Y, Z;        // mm in the board frame
            public readonly float InvRange2;      // 0 = no distance falloff
            public readonly float Ambient;
            public readonly bool  On;

            private CadLight(bool on, bool point, float x, float y, float z,
                             float invRange2, float ambient)
            {
                On = on; Point = point; X = x; Y = y; Z = z;
                InvRange2 = invRange2; Ambient = ambient;
            }

            public static CadLight Build(in PcbViewOptions opt, PcbBoard board)
            {
                float ambient = Math.Clamp(opt.CadAmbient, 0f, 1f);
                if (!opt.CadLighting) return new CadLight(false, false, 0, 0, 1, 0f, ambient);

                if (!opt.CadPointLight)
                {
                    // Normalise once, not per edge. A zero vector would divide by zero and
                    // blacken the whole model, so it falls back to lighting from above.
                    float dx = opt.CadLightX, dy = opt.CadLightY, dz = opt.CadLightZ;
                    float l = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (l < 1e-6f) { dx = 0f; dy = 0f; dz = 1f; l = 1f; }
                    return new CadLight(true, false, dx / l, dy / l, dz / l, 0f, ambient);
                }

                // Fractions -> millimetres, using the board's own size.
                float halfW = MathF.Max(0.5f, board.WidthMm  * 0.5f);
                float halfH = MathF.Max(0.5f, board.HeightMm * 0.5f);
                float tall  = MathF.Max(1f, TallestSolid(board));

                float px = board.CentreX + opt.CadLightFx * halfW;
                float py = board.CentreY + opt.CadLightFy * halfH;
                float pz = opt.CadLightFz * tall;

                // Falloff distance from the board diagonal, so "range 1" means "about one
                // board away" whatever size the board is.
                float diag  = MathF.Sqrt(halfW * halfW * 4f + halfH * halfH * 4f);
                float range = opt.CadLightRange * diag;
                float inv2  = range > 1e-3f ? 1f / (range * range) : 0f;

                return new CadLight(true, true, px, py, pz, inv2, ambient);
            }

            /// <summary>Tallest point of any solid, in mm. The board itself carries no Z
            /// bound -- it is a 2D artwork stack -- so the height that the light's Z
            /// fraction scales against has to come from the 3D models sitting on it.</summary>
            private static float TallestSolid(PcbBoard board)
            {
                float top = 0f;
                foreach (var s in board.Solids)
                    if (s.MaxZ > top && s.MaxZ < 1e6f) top = s.MaxZ;
                return top;
            }

            /// <summary>0..1 shade for a surface at (px,py,pz) mm facing (nx,ny,nz).
            /// twoSided ignores the normal's sign; see the note above.</summary>
            public float Shade(float nx, float ny, float nz,
                               float px, float py, float pz, bool twoSided)
            {
                if (!On) return Ambient;

                float lx = X, ly = Y, lz = Z, att = 1f;
                if (Point)
                {
                    lx = X - px; ly = Y - py; lz = Z - pz;
                    float d2 = lx * lx + ly * ly + lz * lz;
                    if (d2 < 1e-9f) return 1f;                 // sitting inside the lamp
                    float d = MathF.Sqrt(d2);
                    lx /= d; ly /= d; lz /= d;
                    if (InvRange2 > 0f) att = 1f / (1f + d2 * InvRange2);
                }

                float ndl = nx * lx + ny * ly + nz * lz;
                ndl = twoSided ? MathF.Abs(ndl) : MathF.Max(0f, ndl);
                return Ambient + (1f - Ambient) * ndl * att;
            }
        }

        /// <summary>A small cross where the point light is, so its position can be aimed
        /// by eye instead of by guessing at three numbers.</summary>
        private void DrawLightMarker(VoxelBatch batch, SceneCamera cam, in CadLight light,
                                     float cx, float cy, float zBase)
        {
            if (!light.On || !light.Point) return;

            var p = cam.Transform(Wx(light.X, cx), Wy(light.Y, cy), zBase - light.Z * Scale);
            float r = batch.Spacing * 3f;
            batch.Blob(p, r, Palette.Yellow);
            // Arms, so it reads as a marker rather than as a stray voxel of the model.
            for (int a = 0; a < 3; a++)
            {
                var q = p; var w = p;
                float arm = r * 2.5f;
                if (a == 0) { q.x -= arm; w.x += arm; }
                if (a == 1) { q.y -= arm; w.y += arm; }
                if (a == 2) { q.z -= arm; w.z += arm; }
                batch.Line(q, w, Palette.Yellow);
            }
        }

        // ── CAD surfaces: flat shading ────────────────────────────────────────
        // Flat shading in the strict sense: ONE normal per face, so every sample on a
        // face takes the same brightness and the facets read as facets. That is what
        // makes a solid legible here — a smooth-shaded gradient would fight the display,
        // which has no viewpoint for a highlight to be consistent with.
        //
        // Lighting uses SIGNED max(0, N.L), unlike the edge pass which uses |N.L|. The
        // difference is deliberate: an edge is shared by two faces pointing opposite ways
        // so its sign is meaningless, whereas a face has one outward normal, and the sign
        // is exactly what makes a face turned away from the light go dark. That darkening
        // IS the shadowing — it is self-shading from the face angle, not cast shadows;
        // casting would need occlusion tests, and an occluder means nothing on a display
        // you can see straight through.
        //
        // Cost: the fill is by far the most expensive thing on the board. Measured on a
        // real 2-layer export, 1143 mm^2 of planar face is ~42,000 voxels at full density,
        // so the density knob is not decoration.
        private void DrawCadFaces(VoxelBatch batch, SceneCamera cam, PcbBoard board,
                                  in PcbViewOptions opt, float cx, float cy, float z0)
        {
            float zBase = z0 + opt.CadZOffset;   // same anchor as the wireframe
            // Brightness as DENSITY. Scaling the colour cannot brighten anything (see
            // Palette), but packing more voxels into the same area genuinely does look
            // brighter on a volumetric display -- so the slider keeps its meaning and
            // starts working again instead of being a no-op.
            float bright   = opt.CadBrightness > 0f ? opt.CadBrightness : opt.Brightness;
            float brightMul = 1f / MathF.Max(0.05f, bright);

            var light = CadLight.Build(opt, board);
            float density = Math.Clamp(opt.CadSurfaceDensity <= 0f ? 0.6f
                                                                  : opt.CadSurfaceDensity,
                                       0.1f, 2f);
            float step = batch.Spacing / density;

            for (int si = 0; si < board.Solids.Count; si++)
            {
                var solid = board.Solids[si];
                if (!solid.Visible || solid.Faces.Count == 0) continue;

                int   baseCol  = solid.Colour;
                float solidDim = DimSpacing(_hoverSolid == si);

                foreach (var face in solid.Faces)
                {
                    // SIGNED max(0, N·L) here, unlike the edge pass: an edge is shared
                    // by two faces pointing opposite ways so its sign means nothing,
                    // whereas a face has one outward normal and the sign is exactly what
                    // makes a face turned away from the light recede.
                    //
                    // And again as DENSITY: a lit face is sampled at the full step, a face
                    // facing away is sampled sparsely. That is what flat shading looks like
                    // on a display with seven colours and no brightness axis — it is
                    // dithering, which is the honest way to get tone out of one bit.
                    // Still FLAT shading -- one normal for the whole face, so facets read
                    // as facets. But the shade is evaluated per TRIANGLE, at its centroid,
                    // because a point light genuinely does vary across a large face and
                    // collapsing that to one value per face is what makes a point light
                    // look directional. With a directional light every triangle of a face
                    // gets the same answer anyway, so this costs nothing there.
                    bool  lit    = opt.CadLighting && face.HasNormalSet;
                    int   col    = baseCol;
                    float dimMul = solidDim * brightMul;

                    for (int t = 0; t < face.TriCount; t++)
                    {
                        int i = t * 3;
                        float shade = light.Ambient;
                        if (lit)
                        {
                            float mx = (face.X[i] + face.X[i + 1] + face.X[i + 2]) / 3f;
                            float my = (face.Y[i] + face.Y[i + 1] + face.Y[i + 2]) / 3f;
                            float mz = (face.Z[i] + face.Z[i + 1] + face.Z[i + 2]) / 3f;
                            shade = light.Shade(face.NX, face.NY, face.NZ, mx, my, mz, false);
                        }
                        float faceStep = step / MathF.Max(0.12f, shade) * dimMul;
                        var a = cam.Transform(Wx(face.X[i],     cx), Wy(face.Y[i],     cy), zBase - face.Z[i]     * Scale);
                        var b = cam.Transform(Wx(face.X[i + 1], cx), Wy(face.Y[i + 1], cy), zBase - face.Z[i + 1] * Scale);
                        var c = cam.Transform(Wx(face.X[i + 2], cx), Wy(face.Y[i + 2], cy), zBase - face.Z[i + 2] * Scale);
                        FillTri(batch, a, b, c, col, faceStep);
                        if (batch.BudgetHit) return;
                    }
                }
            }
        }

        /// <summary>Point-fill one triangle on a barycentric lattice.
        ///
        /// Rows are chosen from the LONGEST edge, so a long thin triangle still gets
        /// samples along its length instead of a handful bunched at one end — sizing from
        /// area would do exactly that on the sliver triangles ear clipping produces.</summary>
        private static void FillTri(VoxelBatch batch, point3d a, point3d b, point3d c,
                                    int col, float step)
        {
            float ab = Dist(a, b), bc = Dist(b, c), ca = Dist(c, a);
            float longest = MathF.Max(ab, MathF.Max(bc, ca));
            if (longest <= 1e-6f) { batch.Add(a, col); return; }

            int n = (int)MathF.Ceiling(longest / MathF.Max(1e-6f, step));
            n = Math.Clamp(n, 1, 512);

            for (int r = 0; r <= n; r++)
            for (int s = 0; s <= n - r; s++)
            {
                float u = r / (float)n;
                float v = s / (float)n;
                float w = 1f - u - v;
                if (!batch.Add(a.x * w + b.x * u + c.x * v,
                               a.y * w + b.y * u + c.y * v,
                               a.z * w + b.z * u + c.z * v, col) && batch.BudgetHit) return;
            }
        }

        private static float Dist(point3d p, point3d q)
        {
            float dx = p.x - q.x, dy = p.y - q.y, dz = p.z - q.z;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // ── Mechanical meshes ─────────────────────────────────────────────────
        private void DrawMeshes(VoxelBatch batch, SceneCamera cam, PcbBoard board,
                                float cx, float cy, in PcbViewOptions opt)
        {
            foreach (var m in board.Meshes)
            {
                if (!m.Visible || m.Count == 0) continue;

                // Decimate evenly rather than truncating, so a tight budget thins
                // the whole model instead of showing half a board.
                int remaining = Math.Max(1, batch.Limit - batch.Count);
                int stride    = Math.Max(1, m.Count / Math.Max(1, remaining));
                int col       = Palette.Scale(m.Colour, opt.Brightness);

                for (int i = 0; i < m.Count; i += stride)
                {
                    // Mesh Z is height in the board frame; -Z is up on the display.
                    var p = cam.Transform(Wx(m.X[i], cx), Wy(m.Y[i], cy), -m.Z[i] * Scale);
                    if (!batch.Add(p, col) && batch.BudgetHit) return;
                }
            }
        }

        // ── Placed components ─────────────────────────────────────────────────
        // A marker per part, on the side it is mounted, with a stub showing rotation
        // (pin 1 / part orientation) and optionally its designator. This is the thing
        // a flat gerber viewer cannot do: parts sitting ON the board, in 3D, labelled.
        private void DrawComponents(VoxelBatch batch, Sim.Hud hud, SceneCamera cam, PcbBoard board,
                                    in PcbViewOptions opt, float cx, float cy, float z0, int slots)
        {
            // Top parts sit just above the top layer, bottom parts just below the last.
            float lastZ   = z0 + (slots - 1) * Spacing;
            float zTop    = MathF.Min(z0, lastZ) - _featureZ * 0.55f;
            float zBottom = MathF.Max(z0, lastZ) + _featureZ * 0.55f;

            float arm   = MathF.Max(batch.Spacing * 2f, 0.8f * Scale);   // ~1.6 mm part
            bool  label = opt.ShowLabels && board.Components.Count <= Math.Max(1, opt.LabelLimit);

            foreach (var c in board.Components)
            {
                if (batch.BudgetHit) return;

                float x = Wx(c.X, cx), y = Wy(c.Y, cy);
                float z = c.Bottom ? zBottom : zTop;
                int   col = c.Bottom ? 0x5AA0FF : 0xFFC24A;

                // A cross, so the exact centroid is readable at any zoom.
                batch.Line(cam.Transform(x - arm, y, z), cam.Transform(x + arm, y, z), col);
                batch.Line(cam.Transform(x, y - arm, z), cam.Transform(x, y + arm, z), col);

                // Rotation stub: the part's own +X direction.
                float rad = c.Rotation * MathF.PI / 180f;
                batch.Line(cam.Transform(x, y, z),
                           cam.Transform(x + MathF.Cos(rad) * arm * 1.8f,
                                         y + MathF.Sin(rad) * arm * 1.8f, z),
                           Palette.Scale(col, 0.7f));

                if (!label) continue;

                string text = c.Designator;
                if (c.Value.Length > 0 && text.Length + c.Value.Length <= 12)
                    text += " " + c.Value;
                hud.Text(new Voxon.point3d(x + arm * 1.2f, y, z - opt.TextSize * 0.4f),
                         opt.TextSize * 0.8f, col, text, cam);
            }
        }

        // ── Measurement cursor ────────────────────────────────────────────────
        private void DrawCursor(VoxelBatch batch, SceneCamera cam, PcbBoard board,
                                in PcbViewOptions opt, float cx, float cy,
                                float z0, float spacing, int slots)
        {
            float x = Wx(opt.CursorXmm, cx), y = Wy(opt.CursorYmm, cy);
            // Spans the stack whichever way it runs, with a minimum height so the cursor
            // is still a line and not a point when the layers are coplanar.
            float lastZ = z0 + (slots - 1) * spacing;
            float zTop  = MathF.Min(z0, lastZ) - _featureZ;
            float zBot  = MathF.Max(z0, lastZ) + _featureZ;
            float arm  = batch.Radius * 0.06f;

            batch.Line(cam.Transform(x, y, zTop), cam.Transform(x, y, zBot), Palette.TextHilite);
            batch.Line(cam.Transform(x - arm, y, z0), cam.Transform(x + arm, y, z0), Palette.TextHilite);
            batch.Line(cam.Transform(x, y - arm, z0), cam.Transform(x, y + arm, z0), Palette.TextHilite);
        }

        /// <summary>Z of each copper layer, in stack order, for the layout just chosen.
        ///
        /// Only VISIBLE layers occupy a slot (that is how Draw assigns z), so this walks
        /// the same sequence Draw does rather than recomputing from layer indices — if the
        /// two ever disagreed, vias would land between layers instead of on them.
        /// A copper layer that is hidden gets the nearest visible slot, so a via still
        /// terminates somewhere sensible instead of vanishing.</summary>
        private void BuildCopperZ(PcbBoard board, float z0, float spacing,
                                  in PcbViewOptions opt)
        {
            _copperZ.Clear();

            int index = 0;
            float lastZ = z0;
            for (int li = 0; li < board.Layers.Count; li++)
            {
                var layer = board.Layers[li];
                bool copper = layer.Kind is PcbLayerKind.CopperTop
                                          or PcbLayerKind.CopperInner
                                          or PcbLayerKind.CopperBottom;
                if (!LayerShown(layer, opt, li))
                {
                    if (copper) _copperZ.Add(lastZ);
                    continue;
                }

                float z = z0 + index * spacing;
                lastZ = z;
                index++;
                if (copper) _copperZ.Add(z);
            }
        }

        /// <summary>Is this layer drawn at all this frame?
        ///
        /// THE one place that decides. Draw, BuildCopperZ and ResolveProbe all walk the
        /// stack assigning slot Z by position, so if any of them disagreed about which
        /// layers count, vias would terminate between layers and the probe would test
        /// against planes nothing was drawn on — both invisible until someone measured.</summary>
        private static bool LayerShown(PcbLayer layer, in PcbViewOptions opt, int li)
        {
            if (!layer.Visible) return false;
            if (opt.IsolateLayer >= 0 && opt.IsolateLayer != li) return false;

            bool copper = layer.Kind is PcbLayerKind.CopperTop or PcbLayerKind.CopperInner
                                      or PcbLayerKind.CopperBottom;
            bool outline = layer.Kind is PcbLayerKind.Outline;

            return opt.LayerFilter switch
            {
                1 => copper || outline,      // signal inspector
                2 => outline,                // component inspector
                _ => true,
            };
        }

        /// <summary>De-emphasis as a SPACING multiplier: 1 for the thing under the probe,
        /// larger for everything else, so unselected geometry is drawn SPARSER rather than
        /// darker.
        ///
        /// It has to work this way. Brightness is not a dimension on a seven-colour
        /// display — Palette.Snap sends a dimmed colour straight back to where it started,
        /// so the old brightness-based dimming became a no-op the moment snapping was
        /// added. Density is the dimension that survives, and it has the useful side
        /// effect of GIVING BACK budget for the thing you are actually looking at rather
        /// than spending the same on what you are not.</summary>
        private float DimSpacing(bool hovered)
        {
            if (!_inspecting || hovered) return 1f;
            // 0.75 dim -> 1.33x spacing, i.e. about half the voxels in 2D.
            return 1f / MathF.Max(0.05f, _dimFactor);
        }

        /// <summary>Kept for pads and outlines, which are drawn as shapes rather than
        /// sampled lines and so have no spacing to stretch. Returns 1 or 0: below the
        /// threshold an unselected shape is skipped entirely, which is the only "dimmer"
        /// a fixed-colour shape has.</summary>
        private bool DimVisible(bool hovered)
            => !_inspecting || hovered || _dimFactor > 0.35f;

        private bool  _inspecting;
        private float _dimFactor = 0.75f;

        /// <summary>Work out what the probe is pointing at.
        ///
        /// The probe lives in DISPLAY space, but everything it might be pointing at is
        /// defined in board space — so this inverse-transforms the probe rather than
        /// forward-transforming every candidate. That is both cheaper and exact, since the
        /// camera basis is orthonormal.
        ///
        /// Order matters: a STEP solid wins over a placement marker (it is the real body,
        /// and it can borrow the marker's data through its designator), and both win over
        /// a layer, which is the fallback because the probe is ALWAYS within half a slot of
        /// some layer and would otherwise mask everything else.</summary>
        private void ResolveProbe(SceneCamera cam, PcbBoard board,
                                  in PcbViewOptions opt, float z0)
        {
            _dimFactor  = Math.Clamp(opt.DimFactor <= 0f ? 0.75f : opt.DimFactor, 0f, 1f);
            _hoverLayer = _hoverSolid = _hoverComponent = -1;
            _hoverNet   = -1;
            ProbeHasTarget = false;
            Probe.Clear();

            // An explicit pick applies WITHOUT inspection mode, so a net chosen from the
            // list on screen lights up in the normal camera view too. Dimming follows the
            // same rule: if something is selected, everything else recedes.
            bool havePick = ApplyPick(board, opt);
            _inspecting = opt.Inspect || havePick;

            if (!opt.Inspect) return;

            var scene = cam.InverseTransform(opt.ProbeX, opt.ProbeY, opt.ProbeZ);
            float cx = board.CentreX, cy = board.CentreY;
            float snap = opt.SnapRange > 1e-4f ? opt.SnapRange : 0.6f;

            // Only TRACES and PARTS are selectable. Vias and whole layers are excluded on
            // purpose: a layer is always within half a slot of the probe so it would win
            // every time and mask everything, and a via is a feature OF a net rather than
            // a thing you inspect — selecting its net gets you the via anyway.
            float bestD = float.MaxValue;
            int   bestKind = 0;              // 1 = trace, 2 = part, 3 = solid
            int   bestLayer = -1, bestSeg = -1, bestItem = -1;
            point3d bestPt = default;

            // ── Traces ────────────────────────────────────────────────────────
            int index = 0;
            for (int li = 0; li < board.Layers.Count; li++)
            {
                var layer = board.Layers[li];
                if (!LayerShown(layer, opt, li)) continue;
                float lz = z0 + index * Spacing;
                index++;
                if (layer.Kind is not (PcbLayerKind.CopperTop or PcbLayerKind.CopperInner
                                       or PcbLayerKind.CopperBottom)) continue;

                for (int i = 0; i < layer.Segs.Count; i++)
                {
                    var sg = layer.Segs[i];
                    float ax = Wx(sg.X0, cx), ay = Wy(sg.Y0, cy);
                    float bx = Wx(sg.X1, cx), by = Wy(sg.Y1, cy);

                    float d = PointToSegment(scene.x, scene.y, scene.z,
                                             ax, ay, lz, bx, by, lz,
                                             out float px, out float py, out float pz);
                    if (d >= bestD) continue;
                    bestD = d; bestKind = 1;
                    bestLayer = li; bestSeg = i;
                    bestPt = new point3d(px, py, pz);
                }
            }

            // ── Parts: STEP bodies, then placement markers ────────────────────
            float zBase = z0 + opt.CadZOffset;
            for (int i = 0; i < board.Solids.Count && !opt.HideParts; i++)
            {
                var sol = board.Solids[i];
                if (!sol.Visible || !sol.HasGeometry) continue;

                // Distance to the solid's box centre in scene space, which is enough for
                // snapping — the probe only has to pick a winner, not measure it.
                float sx = Wx((sol.MinX + sol.MaxX) * 0.5f, cx);
                float sy = Wy((sol.MinY + sol.MaxY) * 0.5f, cy);
                float sz = zBase - (sol.MinZ + sol.MaxZ) * 0.5f * Scale;
                float d = Dist3(scene.x, scene.y, scene.z, sx, sy, sz);
                if (d >= bestD) continue;
                bestD = d; bestKind = 3; bestItem = i;
                bestPt = new point3d(sx, sy, sz);
            }

            for (int i = 0; i < board.Components.Count && !opt.HideParts; i++)
            {
                var c = board.Components[i];
                float sx = Wx(c.X, cx), sy = Wy(c.Y, cy);
                float sz = z0 - _featureZ * 0.55f;
                float d = Dist3(scene.x, scene.y, scene.z, sx, sy, sz);
                if (d >= bestD) continue;
                bestD = d; bestKind = 2; bestItem = i;
                bestPt = new point3d(sx, sy, sz);
            }

            if (bestKind == 0 || bestD > snap)
            {
                // An explicit pick is left standing: the probe finding nothing is not a
                // reason to drop a selection the user made deliberately.
                Probe.Lines.Add("nothing within reach");
                return;
            }

            ProbeTarget    = cam.Transform(bestPt);
            ProbeHasTarget = true;

            if (bestKind == 1)
            {
                var layer = board.Layers[bestLayer];
                int net = board.Nets?.SegNet(bestLayer, bestSeg) ?? -1;
                _hoverLayer = bestLayer;
                _hoverNet   = net;

                Probe.Hit   = true;
                Probe.Kind  = "trace";
                Probe.Index = bestSeg;
                Probe.Net   = net;
                Probe.Title = board.Nets?.Name(net) ?? "trace";

                // One line, side by side. Four stacked rows for four short values wasted
                // most of a very limited text band, and the values are read together
                // anyway — the layer only means something next to the width.
                var sg = layer.Segs[bestSeg];
                string side = layer.Bottom ? " (bottom)" : "";
                Probe.Lines.Add($"{Probe.Title}   {layer.Kind}{side}   " +
                                $"w {sg.W:0.000} mm   l {sg.Length:0.00} mm");
                return;
            }

            if (bestKind == 3)
            {
                var sol = board.Solids[bestItem];
                _hoverSolid = bestItem;
                Probe.Hit   = true;
                Probe.Kind  = "part";
                Probe.Index = bestItem;
                Probe.Title = sol.Name.Length > 0 ? sol.Name : "STEP solid";
                // Edge and face counts are a parser statistic, not something anyone
                // inspecting a board wants to read off the display.
                Probe.Lines.Add($"size       {sol.MaxX - sol.MinX:0.00} x " +
                                $"{sol.MaxY - sol.MinY:0.00} x {sol.MaxZ - sol.MinZ:0.00} mm");
                if (sol.Designator.Length > 0) AddComponentInfo(board, sol.Designator);
                return;
            }

            var comp = board.Components[bestItem];
            _hoverComponent = bestItem;
            Probe.Hit   = true;
            Probe.Kind  = "part";
            Probe.Index = bestItem;
            Probe.Title = comp.Designator;
            AddComponentInfo(board, comp.Designator);
        }

        /// <summary>Resolve an explicit pick into the same hover fields the probe uses, so
        /// one highlight-and-dim path serves both. Returns whether anything was picked.</summary>
        private bool ApplyPick(PcbBoard board, in PcbViewOptions opt)
        {
            bool any = false;

            if (opt.PickedNet >= 0)
            {
                _hoverNet = opt.PickedNet;
                any = true;
            }

            if (!string.IsNullOrEmpty(opt.PickedDesignator))
            {
                // Match the STEP body first, then the placement marker. The body is the
                // thing you can actually see, so highlighting it is what the user meant;
                // the marker is the fallback for a part with no 3D model.
                for (int i = 0; i < board.Solids.Count; i++)
                {
                    if (!string.Equals(board.Solids[i].Designator, opt.PickedDesignator,
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    _hoverSolid = i;
                    any = true;
                    break;
                }

                if (_hoverSolid < 0)
                    for (int i = 0; i < board.Components.Count; i++)
                    {
                        if (!string.Equals(board.Components[i].Designator, opt.PickedDesignator,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        _hoverComponent = i;
                        any = true;
                        break;
                    }
            }
            return any;
        }

        private static float Dist3(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = ax - bx, dy = ay - by, dz = az - bz;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Distance from a point to a segment, and the closest point on it — so
        /// the leader line lands ON the trace rather than at its nearest endpoint.</summary>
        private static float PointToSegment(float px, float py, float pz,
                                            float ax, float ay, float az,
                                            float bx, float by, float bz,
                                            out float qx, out float qy, out float qz)
        {
            float dx = bx - ax, dy = by - ay, dz = bz - az;
            float len2 = dx * dx + dy * dy + dz * dz;
            float t = len2 > 1e-12f
                      ? ((px - ax) * dx + (py - ay) * dy + (pz - az) * dz) / len2
                      : 0f;
            t = Math.Clamp(t, 0f, 1f);
            qx = ax + dx * t; qy = ay + dy * t; qz = az + dz * t;
            return Dist3(px, py, pz, qx, qy, qz);
        }

        private void AddComponentInfo(PcbBoard board, string designator)
        {
            foreach (var c in board.Components)
            {
                if (!string.Equals(c.Designator, designator, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (c.Value.Length > 0)     Probe.Lines.Add($"value      {c.Value}");
                if (c.Footprint.Length > 0) Probe.Lines.Add($"footprint  {c.Footprint}");
                Probe.Lines.Add($"placed     {c.X:0.00}, {c.Y:0.00} mm  rot {c.Rotation:0}deg  " +
                                $"{(c.Bottom ? "bottom" : "top")}");
                break;
            }
        }

        // ── Board-to-world helpers ────────────────────────────────────────────
        // Board X maps to display X; board Y maps to display Y (depth). The board
        // therefore lies flat in the volume and the stack grows along Z.
        private float Wx(float mmX, float cx) => (mmX - cx) * Scale;
        private float Wy(float mmY, float cy) => (mmY - cy) * Scale;

        private static void RectXY(VoxelBatch batch, SceneCamera cam, float x, float y, float z,
                                   float w, float h, int col)
        {
            float hw = w * 0.5f, hh = h * 0.5f;
            batch.Line(cam.Transform(x - hw, y - hh, z), cam.Transform(x + hw, y - hh, z), col);
            batch.Line(cam.Transform(x + hw, y - hh, z), cam.Transform(x + hw, y + hh, z), col);
            batch.Line(cam.Transform(x + hw, y + hh, z), cam.Transform(x - hw, y + hh, z), col);
            batch.Line(cam.Transform(x - hw, y + hh, z), cam.Transform(x - hw, y - hh, z), col);
        }

        private static void CircleXY(VoxelBatch batch, SceneCamera cam, float x, float y, float z,
                                     float r, int col, bool fill)
        {
            if (r < batch.Spacing) { batch.Add(cam.Transform(x, y, z), col); return; }

            int segs = Math.Clamp((int)(2f * MathF.PI * r / batch.Spacing), 8, 256);
            for (int i = 0; i < segs; i++)
            {
                float a = i * 2f * MathF.PI / segs;
                var p = cam.Transform(x + MathF.Cos(a) * r, y + MathF.Sin(a) * r, z);
                if (!batch.Add(p, col) && batch.BudgetHit) return;
            }

            if (!fill) return;
            for (float rr = batch.Spacing; rr < r; rr += batch.Spacing)
            {
                int segs2 = Math.Clamp((int)(2f * MathF.PI * rr / batch.Spacing), 6, 256);
                for (int i = 0; i < segs2; i++)
                {
                    float a = i * 2f * MathF.PI / segs2;
                    var p = cam.Transform(x + MathF.Cos(a) * rr, y + MathF.Sin(a) * rr, z);
                    if (!batch.Add(p, col) && batch.BudgetHit) return;
                }
            }
        }
    }
}
