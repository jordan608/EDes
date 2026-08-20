// ═══════════════════════════════════════════════════════════════════════════
//  Hud.cs — text in the volume, routed through the app's voxel batch
//
//  VoxelFont owns the glyph table; this owns the policy:
//    • every glyph voxel goes through VoxelBatch, so HUD text obeys the same
//      max-voxel limit and display-bounds clipping as the geometry, and ships
//      in the same single DrawVox_Batch call;
//    • text can be drawn in scene space (transformed by the camera, so labels
//      stay attached to what they label) or in panel space (fixed to the
//      display, for readouts that must stay put while you fly the scene).
//
//  Glyphs advance in +x and grow in +z. Since -Z is up in this app, that means
//  a line of text reads left-to-right and successive lines step DOWNWARD by
//  LineStep. Panels live on a constant-Y plane (the scope panel is y = 0.1).
//
//  The sink delegate is cached and its per-call state lives in fields — one
//  cached delegate instead of a closure allocation per string per frame.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes.Sim
{
    public sealed class Hud
    {
        private readonly VoxelBatch _batch;
        private readonly Func<float, float, float, bool> _sink;

        // Per-call state for the cached sink.
        private int          _col;
        private SceneCamera? _cam;

        public HudFont Font { get; set; } = HudFont.Bold;

        public Hud(VoxelBatch batch)
        {
            _batch = batch;
            _sink  = (x, y, z) =>
            {
                if (_cam != null)
                {
                    point3d p = _cam.Transform(x, y, z);
                    _batch.Add(p.x, p.y, p.z, _col);
                }
                else _batch.Add(x, y, z, _col);
                return !_batch.BudgetHit;      // stop the glyph walk once full
            };
        }

        /// <summary>Vertical step between consecutive lines at this glyph size.</summary>
        public static float LineStep(float size) => size * 1.55f;

        /// <summary>Rendered width of a string at this glyph size.</summary>
        public static float Width(string text, float size) => VoxelFont.MeasureWidth(text, size);

        /// <summary>Draw text with its top-left at pos. Pass cam to attach it to the
        /// scene, or null to pin it to the display.</summary>
        public void Text(point3d pos, float size, int col, string text, SceneCamera? cam = null)
        {
            if (string.IsNullOrEmpty(text) || col == 0 || _batch.BudgetHit) return;
            _col = col;
            _cam = cam;
            VoxelFont.Emit(Font, pos, size, text, _sink);
            _cam = null;
        }

        /// <summary>Draw text centred horizontally on cx.</summary>
        public void TextCentred(float cx, float y, float z, float size, int col, string text,
                               SceneCamera? cam = null)
            => Text(new point3d(cx - Width(text, size) * 0.5f, y, z), size, col, text, cam);

        /// <summary>Draw text ending at rx (right-aligned).</summary>
        public void TextRight(float rx, float y, float z, float size, int col, string text,
                              SceneCamera? cam = null)
            => Text(new point3d(rx - Width(text, size), y, z), size, col, text, cam);

        /// <summary>Write a block of lines downward from (x, z), returning the z it ended at.</summary>
        public float Lines(float x, float y, float z, float size, int col, params string[] lines)
        {
            foreach (var l in lines)
            {
                Text(new point3d(x, y, z), size, col, l);
                z += LineStep(size);
            }
            return z;
        }

        // ── Value formatting ──────────────────────────────────────────────────
        // Engineering notation keeps readouts short enough to fit in the volume:
        // 4700 ohms reads "4.70K", 0.0123 A reads "12.3M" (milli).

        public static string Eng(double v, string unit)
        {
            double a = Math.Abs(v);
            if (a >= 1e6) return (v / 1e6).ToString("0.##") + "M" + unit;
            if (a >= 1e3) return (v / 1e3).ToString("0.##") + "K" + unit;
            if (a >= 1)   return v.ToString("0.##") + unit;
            if (a >= 1e-3) return (v * 1e3).ToString("0.##") + "M" + unit;   // milli
            if (a >= 1e-6) return (v * 1e6).ToString("0.##") + "U" + unit;   // micro
            return v.ToString("0.###") + unit;
        }
    }
}
