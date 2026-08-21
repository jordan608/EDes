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
        public float PourDensity;      // 1 = default outline sampling, higher = tighter
        public float HatchDensity;     // 1 = a hatch line every 6 voxels
        public bool  ShowMeshes;
        public bool  ShowCad;          // STEP solids, as edge wireframes
        public float CadBrightness;    // separate from Brightness: CAD sits above the board
        public bool  CadLighting;      // shade edges by their adjacent-face normals
        public float CadAmbient;       // floor brightness, so unlit edges never vanish
        public float CadLightX, CadLightY, CadLightZ;   // light direction, board frame
        public bool  ShowCursor;
        public float CursorXmm, CursorYmm;
        public float Brightness;
        public int   IsolateLayer;     // -1 = all layers, else only this index
        public bool  ShowComponents;   // markers from the placement file
        public bool  ShowLabels;       // designators next to those markers
        public int   LabelLimit;       // skip labels entirely above this part count
        public float TextSize;         // label size, in display units
    }

    public sealed class PcbRenderer
    {
        /// <summary>Vias draw copper-amber so they read as plated conductor and are
        /// unmistakable against the grey drill bores.</summary>
        private const int VIA_COLOUR = 0xE8A020;

        /// <summary>Blind and buried vias draw cooler, so a barrel that stops short reads
        /// as deliberate rather than as a clipping bug.</summary>
        private const int BLIND_VIA_COLOUR = 0x40D0E8;

        /// <summary>Z of each copper layer for the current frame's layout. Rebuilt every
        /// Draw because layer visibility and spacing are live UI settings.</summary>
        private readonly System.Collections.Generic.List<float> _copperZ = new();

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
            Spacing = MathF.Min(opt.LayerSpacing, zHalf * 1.5f / slots);

            float cx = board.CentreX, cy = board.CentreY;
            float z0 = -(slots - 1) * 0.5f * Spacing;      // first layer highest (-Z is up)

            // Z of every copper layer, in stack order. Vias are defined against COPPER,
            // not against the visible stack — a 2-layer board can easily have 14 visible
            // layers once silk, mask, paste and mechanical are counted, and spanning
            // those made every barrel stick far out of the board.
            BuildCopperZ(board, z0, Spacing);

            int index = 0;
            for (int li = 0; li < board.Layers.Count; li++)
            {
                var layer = board.Layers[li];
                if (!layer.Visible) continue;
                if (opt.IsolateLayer >= 0 && opt.IsolateLayer != li) { index++; continue; }

                float z   = z0 + index * Spacing;
                int   col = Palette.Scale(layer.Colour, opt.Brightness);
                index++;

                DrawLayer(batch, cam, layer, col, z, cx, cy, opt);
                if (batch.BudgetHit) return;
            }

            if (opt.ShowVias)  DrawVias(batch, cam, board, opt, cx, cy, z0, Spacing, slots);
            if (opt.ShowHoles) DrawHoles(batch, cam, board, cx, cy, z0, Spacing, slots, opt);
            if (opt.ShowCad)    DrawCadSolids(batch, cam, board, opt, cx, cy);
            if (opt.ShowMeshes) DrawMeshes(batch, cam, board, cx, cy, opt);
            if (opt.ShowComponents) DrawComponents(batch, hud, cam, board, opt, cx, cy, z0, slots);
            if (opt.ShowCursor) DrawCursor(batch, cam, board, opt, cx, cy, z0, Spacing, slots);
        }

        // ── One layer ─────────────────────────────────────────────────────────
        private void DrawLayer(VoxelBatch batch, SceneCamera cam, PcbLayer layer, int col, float z,
                               float cx, float cy, in PcbViewOptions opt)
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
                    batch.Line(
                        cam.Transform(Wx(s.X0, cx) + px * off, Wy(s.Y0, cy) + py * off, z),
                        cam.Transform(Wx(s.X1, cx) + px * off, Wy(s.Y1, cy) + py * off, z),
                        col);
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

        /// <summary>Scanline hatch of a polygon — a pour reads as filled for a fraction
        /// of the voxels a solid fill would cost.</summary>
        private void HatchRegion(VoxelBatch batch, SceneCamera cam, PcbRegion r,
                                 float cx, float cy, float z, int col,
                                 in PcbViewOptions opt)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < r.Count; i++)
            {
                if (r.Y[i] < minY) minY = r.Y[i];
                if (r.Y[i] > maxY) maxY = r.Y[i];
            }

            // Default is a hatch line every 6 voxels; density scales that gap, so 2.0
            // hatches every 3 voxels and 0.5 every 12. Clamped because a hatch step below
            // the voxel spacing is a solid fill in disguise — it would cost the budget of
            // a fill while still being described as a hatch.
            float density = Math.Clamp(opt.HatchDensity <= 0f ? 1f : opt.HatchDensity, 0.1f, 8f);
            float stepMm  = batch.Spacing * (6f / density) / MathF.Max(1e-6f, Scale);
            Span<float> xs = stackalloc float[64];

            for (float yy = minY; yy <= maxY; yy += stepMm)
            {
                int hits = 0;
                for (int i = 0; i < r.Count && hits < xs.Length; i++)
                {
                    int j = (i + 1) % r.Count;
                    float y0 = r.Y[i], y1 = r.Y[j];
                    if ((yy >= y0 && yy < y1) || (yy >= y1 && yy < y0))
                    {
                        float t = (yy - y0) / (y1 - y0);
                        xs[hits++] = r.X[i] + (r.X[j] - r.X[i]) * t;
                    }
                }
                // Sort the crossings and fill between pairs (even-odd rule).
                for (int a = 1; a < hits; a++)
                for (int b = a; b > 0 && xs[b - 1] > xs[b]; b--)
                    (xs[b - 1], xs[b]) = (xs[b], xs[b - 1]);

                for (int k = 0; k + 1 < hits; k += 2)
                {
                    batch.Line(cam.Transform(Wx(xs[k], cx),     Wy(yy, cy), z),
                               cam.Transform(Wx(xs[k + 1], cx), Wy(yy, cy), z), col);
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
            float zTop = z0 - spacing * 0.35f;
            float zBot = z0 + (slots - 1) * spacing + spacing * 0.35f;

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

                int col = h.IsBlind(copperCount) ? blind : through;

                float x = Wx(h.X, cx), y = Wy(h.Y, cy);
                float r = h.Dia * 0.5f * Scale;

                // The conductor itself: always drawn, always the full stack height.
                // Everything below is decoration and may be skipped or budget-cut
                // without ever breaking the connection this line represents.
                batch.Line(cam.Transform(x, y, zTop), cam.Transform(x, y, zBot), col);

                // Wall + annular ring only once the via is wider than a voxel or two.
                // Below that the wall lands on the same voxels as the centre line, so
                // it would cost four times the budget to draw the same thing.
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
                                   in PcbViewOptions opt, float cx, float cy)
        {
            float bright = opt.CadBrightness > 0f ? opt.CadBrightness : opt.Brightness;

            // Normalise the light once, not per edge. A zero vector would divide by zero
            // and blacken the model, so it falls back to lighting from above.
            float lx = opt.CadLightX, ly = opt.CadLightY, lz = opt.CadLightZ;
            float ll = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
            if (ll < 1e-6f) { lx = 0f; ly = 0f; lz = 1f; ll = 1f; }
            lx /= ll; ly /= ll; lz /= ll;

            float ambient = Math.Clamp(opt.CadAmbient, 0f, 1f);

            foreach (var solid in board.Solids)
            {
                if (!solid.Visible || solid.Edges.Count == 0) continue;

                int baseCol = Palette.Scale(solid.Colour, bright);

                foreach (var e in solid.Edges)
                {
                    int col = baseCol;
                    if (opt.CadLighting && e.HasNormal)
                    {
                        // Two-sided N·L: the sign of the dot is meaningless on a display
                        // with no viewpoint, since every face is seen from both sides at
                        // once. The magnitude is what carries the shape, so take |N·L|.
                        float ndl = MathF.Abs(e.NX * lx + e.NY * ly + e.NZ * lz);
                        col = Palette.Scale(baseCol, ambient + (1f - ambient) * ndl);
                    }

                    // A polyline, so consecutive points are joined — drawing the points
                    // alone would dot a long straight edge into two lonely voxels.
                    for (int i = 1; i < e.Count; i++)
                    {
                        var a = cam.Transform(Wx(e.X[i - 1], cx), Wy(e.Y[i - 1], cy),
                                              -e.Z[i - 1] * Scale);
                        var b = cam.Transform(Wx(e.X[i], cx), Wy(e.Y[i], cy),
                                              -e.Z[i] * Scale);
                        batch.Line(a, b, col);
                    }
                    if (batch.BudgetHit) return;
                }
            }
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
            float zTop    = z0 - Spacing * 0.55f;
            float zBottom = z0 + (slots - 1) * Spacing + Spacing * 0.55f;

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
            float zTop = z0 - spacing;
            float zBot = z0 + slots * spacing;
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
        private void BuildCopperZ(PcbBoard board, float z0, float spacing)
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
                if (!layer.Visible)
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
