// ═══════════════════════════════════════════════════════════════════════════
//  SchematicRenderer.cs — the schematic sheet, drawn flat in the volume
//
//  A schematic is a 2D drawing, so it goes on ONE plane -- the same constant-Y
//  plane the HUD and the scope use. Spreading it through the depth of the volume
//  would add a dimension the drawing does not have, and on a display with no
//  occlusion that reads as blur rather than as depth.
//
//  The hard limit here is TEXT, and it is worth being plain about: a schematic's
//  labels are around 5 pt on a 690 pt sheet, i.e. 0.8% of the sheet width. Fit
//  that sheet across an 8-unit volume at a 0.03-unit voxel pitch and a glyph cell
//  lands at a fifth of a voxel -- unreadable, and no amount of care in this file
//  changes that. So text is drawn only when it is actually big enough to read,
//  and the count that was skipped is reported rather than hidden. Zooming in
//  makes more of it appear, which is the honest behaviour: the geometry is a map,
//  and you zoom to read the labels.
//
//  PDF has Y UP; the display has -Z up. The flip happens in Map(), once.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using EDes.Sim;
using Voxon;

namespace EDes.Pcb
{
    public sealed class SchematicRenderer
    {
        /// <summary>Strings too small to read at the current fit, from the last Draw. The
        /// caller surfaces it -- silently dropping most of a drawing's labels would look
        /// like a parser fault rather than a resolution limit.</summary>
        public int TextSkipped { get; private set; }
        public int TextDrawn   { get; private set; }
        public int LinesDrawn  { get; private set; }

        /// <summary>Fit scale in use: world units per PDF point.</summary>
        public float Scale { get; private set; } = 1f;

        // Per-frame fit, set at the top of Draw and read by Map. Instance fields rather
        // than parameters because Map is called twice per line and once per string, and
        // six extra arguments on that path would be noise.
        private float _cxPt, _cyPt, _planeY;

        public void Draw(VoxelBatch batch, Hud hud, SceneCamera cam, SchematicSheet sheet,
                         float planeY, float radius, float zHalf,
                         float textSize, float minTextSize, bool showText)
        {
            TextSkipped = TextDrawn = LinesDrawn = 0;
            if (!sheet.HasGeometry) return;

            // Fit into the volume's INSCRIBED rectangle, not its bounding box. The volume is
            // a cylinder: at the plane's y the usable half-width is sqrt(r^2 - y^2), and a
            // sheet fitted to the full radius would have its left and right edges clipped
            // away by the batch without anything saying so.
            float inside = radius * radius - planeY * planeY;
            float halfW  = (inside > 0f ? MathF.Sqrt(inside) : radius) * 0.97f;
            float halfH  = zHalf * 0.97f;

            float sx = 2f * halfW / MathF.Max(1e-3f, sheet.WidthPt);
            float sy = 2f * halfH / MathF.Max(1e-3f, sheet.HeightPt);
            Scale = MathF.Min(sx, sy);

            _cxPt   = (sheet.MinX + sheet.MaxX) * 0.5f;
            _cyPt   = (sheet.MinY + sheet.MaxY) * 0.5f;
            _planeY = planeY;

            // ── Wires and symbols ────────────────────────────────────────────
            foreach (var l in sheet.Lines)
            {
                if (batch.BudgetHit) break;
                batch.Line(Map(cam, l.X1, l.Y1), Map(cam, l.X2, l.Y2), Palette.Text);
                LinesDrawn++;
            }

            if (!showText) return;

            // ── Labels ───────────────────────────────────────────────────────
            foreach (var t in sheet.Texts)
            {
                if (batch.BudgetHit) break;
                if (string.IsNullOrWhiteSpace(t.Text)) continue;

                // The sheet's own type size, through the same fit AND the camera's zoom --
                // so zooming in genuinely brings labels into range rather than only
                // magnifying the ones already drawn.
                float mapped = t.Size * Scale * cam.Zoom;
                if (mapped < minTextSize) { TextSkipped++; continue; }

                // Legible where it fits, but capped: a label blown up to fill the volume
                // stops being a label. And never below the floor -- past that a glyph is a
                // blob that costs budget to be one.
                //
                // NOT Math.Clamp(mapped, minTextSize, textSize * 1.5f). Math.Clamp throws
                // ArgumentException when min > max, and the cap CAN fall below the floor:
                // the floor here is a fixed 0.174 from the voxel pitch, while textSize
                // follows the HUD's own text setting, whose legibility floor is now allowed
                // to go to zero. Set the HUD floor to 0 and Text size to 0.05 and the cap
                // becomes 0.075 -- below 0.174 -- and this line threw on the game thread,
                // taking out the render loop. Min-of-then-max never throws and keeps the
                // floor winning, which is the intent either way.
                float size = MathF.Max(minTextSize, MathF.Min(mapped, textSize * 1.5f));

                // PDF puts the text origin on the BASELINE; Hud.Text takes a TOP-LEFT
                // anchor and grows downward. Passing the baseline straight through drew
                // every label one glyph-height too low -- labels sat below the wires they
                // name, and the bottom row of the sheet ran out of the volume and was
                // clipped. The 5x7 grid is 7 cells of 0.18*size, so the top is 1.26*size
                // above the baseline; -Z is up, so that is a SUBTRACTION.
                point3d at = Map(cam, t.X, t.Y);
                at.z -= size * 7f * 0.18f;

                hud.Text(at, size, Palette.TextHilite, t.Text);
                TextDrawn++;
            }
        }

        /// <summary>Sheet point (Y up, PDF points) to display point (-Z up, world units).
        ///
        /// The camera IS applied, unlike the readout panels: a schematic is content you
        /// navigate -- pan to a corner, zoom to read a value -- not a fixed overlay. Only
        /// the plane's depth is held constant, which is what keeps it a flat drawing.</summary>
        private point3d Map(SceneCamera cam, float px, float py)
            => cam.Transform((px - _cxPt) * Scale, _planeY, -(py - _cyPt) * Scale);
    }
}
