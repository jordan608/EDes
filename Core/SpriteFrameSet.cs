using System;
using System.Collections.Generic;
using System.Drawing;          // System.Drawing.Common (Windows)
using System.IO;
using System.Linq;

namespace EDes
{
    // ==========================================================================
    //  SPRITE FRAME SET — voxelised 2D explosion animation frames
    //
    //  Loads a sequence of frame PNGs (sorted by filename) and pre-bakes each into
    //  a list of points: every sufficiently opaque & bright pixel becomes one point
    //  with plane-local coords in [-0.5, +0.5] (origin centred) plus a brightened
    //  colour so it survives the near-binary RGB volume (dim/transparent pixels are
    //  dropped — fading by REMOVING voxels, not darkening). At draw time the chosen
    //  frame is splatted onto billboard plane(s); no per-frame decode.
    // ==========================================================================
    public sealed class SpriteFrameSet
    {
        public int FrameCount { get; }
        /// <summary>Source pixel aspect ratio (width / height) of the frames, so callers
        /// can scale the billboard without squashing wide/tall art.</summary>
        public float Aspect { get; }
        /// <summary>Saturation-weighted circular-mean hue (degrees, 0-360) of frame 0 — the
        /// sprite's "as-authored" dominant colour. Callers that want to retint the whole
        /// animation (e.g. per-enemy-type) rotate every pixel's hue by (targetHue - this),
        /// which preserves each pixel's original saturation/value (its brightness and
        /// "how colourful" it is) exactly like a GIMP Hue-Shift — only the hue moves.</summary>
        public float BaseHueDeg { get; }
        /// <summary>Same idea as <see cref="BaseHueDeg"/> but averaged only over frame 0's
        /// DARKER half (below-median value) instead of every pixel. Callers that want a
        /// two-tone retint (Primary → bright pixels, Secondary → dark pixels) rotate each
        /// pixel by a blend of (targetPrimaryHue - BaseHueDeg) and
        /// (targetSecondaryHue - BaseHueDeg2), weighted by that pixel's own brightness —
        /// see ExplosionManager.DrawSpriteBurst / ColorHsv.ShiftHueBlend.</summary>
        public float BaseHueDeg2 { get; }
        private readonly float[][] _u;    // horizontal, [-0.5, +0.5]
        private readonly float[][] _v;    // vertical,   [-0.5, +0.5]  (image-up = +v)
        private readonly int[][]   _col;  // brightened RGB

        public int   PixelCount(int f) => _u[f].Length;
        public float U(int f, int i)   => _u[f][i];
        public float V(int f, int i)   => _v[f][i];
        public int   Col(int f, int i) => _col[f][i];

        private SpriteFrameSet(float[][] u, float[][] v, int[][] col, float aspect)
        {
            _u = u; _v = v; _col = col; FrameCount = u.Length; Aspect = aspect > 0f ? aspect : 1f;
            var frame0 = col.Length > 0 ? col[0] : Array.Empty<int>();
            BaseHueDeg  = ComputeBaseHue(frame0, bright: true);
            BaseHueDeg2 = ComputeBaseHue(frame0, bright: false);
        }

        // Circular mean of each opaque pixel's hue, weighted by its saturation so
        // near-white/grey pixels (no real hue) don't skew the average. Restricted to
        // the bright (value >= median-ish 0.5) or dark half of the frame when
        // `bright` narrows the sample — see BaseHueDeg / BaseHueDeg2.
        private static float ComputeBaseHue(int[] frame0, bool bright)
        {
            double sx = 0, sy = 0;
            foreach (int col in frame0)
            {
                ColorHsv.RgbToHsv(col, out float h, out float s, out float v);
                if (s < 0.03f) continue;
                if (bright ? v < 0.5f : v >= 0.5f) continue;
                double rad = h * Math.PI / 180.0;
                sx += Math.Cos(rad) * s;
                sy += Math.Sin(rad) * s;
            }
            if (Math.Abs(sx) < 1e-6 && Math.Abs(sy) < 1e-6)
                return bright ? 0f : ComputeBaseHueAny(frame0);
            float deg = (float)(Math.Atan2(sy, sx) * 180.0 / Math.PI);
            return deg < 0f ? deg + 360f : deg;
        }

        // Fallback for BaseHueDeg2 when the dark half has no saturated pixels at all
        // (flat-brightness sprites) — reuse the whole-frame mean so it still tracks
        // Secondary sanely instead of collapsing to 0°.
        private static float ComputeBaseHueAny(int[] frame0)
        {
            double sx = 0, sy = 0;
            foreach (int col in frame0)
            {
                ColorHsv.RgbToHsv(col, out float h, out float s, out _);
                if (s < 0.03f) continue;
                double rad = h * Math.PI / 180.0;
                sx += Math.Cos(rad) * s;
                sy += Math.Sin(rad) * s;
            }
            if (Math.Abs(sx) < 1e-6 && Math.Abs(sy) < 1e-6) return 0f;
            float deg = (float)(Math.Atan2(sy, sx) * 180.0 / Math.PI);
            return deg < 0f ? deg + 360f : deg;
        }

        /// <summary>Load every *.png in <paramref name="folder"/> (sorted by name) as
        /// animation frames. Returns null if the folder is missing/empty or decode
        /// fails. Pixels with alpha &lt; alphaCutoff or max-channel &lt; lumaCutoff are
        /// skipped (transparent / too dark to show on the on/off display).</summary>
        public static SpriteFrameSet Load(string folder, int alphaCutoff = 40, int lumaCutoff = 40, int maxDim = 160)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                string[] files = Directory.GetFiles(folder, "*.png");
                if (files.Length == 0) return null;
                // Natural sort so "frame2" precedes "frame10" (plain string sort wouldn't).
                Array.Sort(files, (a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));

                var us = new List<float[]>(); var vs = new List<float[]>(); var cs = new List<int[]>();
                float aspect = 1f;
                foreach (var file in files)
                {
                    DecodeImage(file, alphaCutoff, lumaCutoff, maxDim, out var u, out var v, out var c, out int w, out int h);
                    if (us.Count == 0 && h > 0) aspect = w / (float)h;   // from first frame
                    us.Add(u); vs.Add(v); cs.Add(c);
                }
                return new SpriteFrameSet(us.ToArray(), vs.ToArray(), cs.ToArray(), aspect);
            }
            catch (Exception ex)
            {
                App.Log($"[SpriteFrameSet] load failed for '{folder}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Load a SINGLE image file as a one-frame set (e.g. a "K.O." graphic).
        /// Returns null on failure.</summary>
        public static SpriteFrameSet LoadFile(string file, int alphaCutoff = 40, int lumaCutoff = 40, int maxDim = 200)
        {
            try
            {
                if (!File.Exists(file)) return null;
                DecodeImage(file, alphaCutoff, lumaCutoff, maxDim, out var u, out var v, out var c, out int w, out int h);
                if (u.Length == 0) return null;
                return new SpriteFrameSet(new[] { u }, new[] { v }, new[] { c }, h > 0 ? w / (float)h : 1f);
            }
            catch (Exception ex)
            {
                App.Log($"[SpriteFrameSet] load failed for '{file}': {ex.Message}");
                return null;
            }
        }

        // Decode one PNG into voxel points: every opaque, bright-enough pixel → a point
        // at plane-local coords [-0.5,+0.5] (image-up = +v) with a brightened colour.
        //
        // The source is DOWNSAMPLED to a grid no larger than maxDim on its longest side.
        // This matters a great deal: the UI art is multi-megapixel (e.g. Warning.png is
        // 3776×1120 ≈ 4.2M px), and one point per opaque pixel would emit millions of
        // voxels — blowing past the draw buffer (so only the top rows survive, reading as
        // a solid line) and making decode crawl. A grid of ~200 cells/side gives a few
        // thousand legible points and decodes instantly. Small frames (explosions) are
        // already under maxDim, so they sample 1:1 and are unaffected.
        private static void DecodeImage(string file, int alphaCutoff, int lumaCutoff, int maxDim,
                                        out float[] u, out float[] v, out int[] col,
                                        out int width, out int height)
        {
            using var bmp = new Bitmap(file);
            int sw = bmp.Width, sh = bmp.Height;
            width = sw; height = sh;                         // aspect comes from the source

            int step = Math.Max(1, (int)Math.Ceiling(Math.Max(sw, sh) / (float)Math.Max(1, maxDim)));
            int ow = Math.Max(1, sw / step);                 // output grid dimensions
            int oh = Math.Max(1, sh / step);

            var lu = new List<float>(); var lv = new List<float>(); var lc = new List<int>();
            float invW = ow > 1 ? 1f / (ow - 1) : 1f;
            float invH = oh > 1 ? 1f / (oh - 1) : 1f;
            for (int oy = 0; oy < oh; oy++)
                for (int ox = 0; ox < ow; ox++)
                {
                    // Point-sample the centre of each grid cell (cheap; one GetPixel per cell).
                    int sx = Math.Min(sw - 1, ox * step + step / 2);
                    int sy = Math.Min(sh - 1, oy * step + step / 2);
                    Color px = bmp.GetPixel(sx, sy);
                    if (px.A < alphaCutoff) continue;
                    int mx = Math.Max(px.R, Math.Max(px.G, px.B));
                    if (mx < lumaCutoff) continue;          // rounds to off — skip
                    // Brighten: normalise so the brightest channel hits 255 while
                    // preserving hue — keeps dim pixels visible on the volume.
                    float k = 255f / mx;
                    int r = Math.Min(255, (int)(px.R * k));
                    int g = Math.Min(255, (int)(px.G * k));
                    int b = Math.Min(255, (int)(px.B * k));
                    lu.Add(ox * invW - 0.5f);
                    lv.Add(0.5f - oy * invH);               // image y is down → flip to up
                    lc.Add((r << 16) | (g << 8) | b);
                }
            u = lu.ToArray(); v = lv.ToArray(); col = lc.ToArray();
        }

        // Compare two filenames so embedded numbers order numerically:
        // "Explosion2" < "Explosion10" (a plain string sort gets this wrong).
        private static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0');
                    string nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }
    }
}
