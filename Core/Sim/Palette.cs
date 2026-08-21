// ═══════════════════════════════════════════════════════════════════════════
//  Palette.cs — the app's colours in one place
//
//  Packed 0xRRGGBB ints, as the SDK expects.
//
//  THE DISPLAY HAS SEVEN COLOURS. It is effectively one bit per channel, so the
//  only things it can actually show are red, green, blue, cyan, magenta, yellow
//  and white. A mid grey or a dark teal is not a dimmer version of itself on this
//  hardware — it is unreliable, and a dark one is close to invisible while still
//  costing exactly as much voxel budget as a bright one.
//
//  Two consequences run through the whole app:
//
//    1. Every colour reaching the display is SNAPPED to one of the seven. That
//       happens in VoxelBatch.Add, the one choke point every drawn voxel passes
//       through, so no renderer can leak an illegal colour however it computes it.
//
//    2. BRIGHTNESS IS NOT A DIMENSION HERE. Scale() cannot dim anything: snap a
//       dimmed colour and it lands back on the same one. Shading, dimming and
//       "de-emphasis" have to be expressed as DENSITY instead — draw fewer voxels,
//       further apart. Scale() and Mix() therefore survive only for the on-screen
//       preview and the legend swatches, where the full 24-bit range is real.
// ═══════════════════════════════════════════════════════════════════════════

using System;

namespace EDes.Sim
{
    public static class Palette
    {
        // ── The seven ─────────────────────────────────────────────────────────
        public const int Red     = 0xFF0000;
        public const int Green   = 0x00FF00;
        public const int Blue    = 0x0000FF;
        public const int Cyan    = 0x00FFFF;
        public const int Magenta = 0xFF00FF;
        public const int Yellow  = 0xFFFF00;
        public const int White   = 0xFFFFFF;

        /// <summary>Every colour the display can show. Nothing else is legal.</summary>
        public static readonly int[] Seven =
            { Red, Green, Blue, Cyan, Magenta, Yellow, White };

        // ── Named roles, all drawn from the seven ─────────────────────────────
        // These used to be hand-mixed shades (0x2A6E8C, 0x0E2A3A, 0x808080 and so on).
        // Every one of them was either invisible on the display or indistinguishable
        // from its neighbour once snapped, so the distinctions they were drawing were
        // imaginary. Where two roles now share a colour they are separated by
        // DENSITY or by pattern instead — see the header.
        public const int WireDim    = Blue;
        public const int WireBright = Cyan;
        public const int Battery    = Yellow;
        public const int FlowDot    = White;
        public const int Text       = Cyan;
        public const int TextDim    = Blue;
        public const int TextHilite = Yellow;
        public const int Graticule  = Blue;
        public const int GridFloor  = Blue;
        public const int Globe      = Blue;
        public const int Trace      = Green;
        public const int TraceEdge  = Green;
        public const int Warning    = Red;

        /// <summary>Nearest displayable colour.
        ///
        /// Each channel is thresholded, which lands on one of eight corners of the RGB
        /// cube — and the eighth is BLACK, which is not a colour on this display but an
        /// invisible voxel that still costs budget. So a colour that thresholds to black
        /// is instead promoted to whichever channel was strongest, and a genuinely
        /// colourless one becomes white. Something visible is always better than
        /// something that silently is not.</summary>
        public static int Snap(int col)
        {
            int r = (col >> 16) & 0xFF, g = (col >> 8) & 0xFF, b = col & 0xFF;

            // 96, not 128: on a one-bit-per-channel display a channel at 40% was intended
            // to be present, and rounding it away loses hue distinctions that were real
            // (an orange becoming red rather than yellow, say).
            const int T = 96;
            int rr = r >= T ? 1 : 0, gg = g >= T ? 1 : 0, bb = b >= T ? 1 : 0;

            if ((rr | gg | bb) == 0)
            {
                int max = Math.Max(r, Math.Max(g, b));
                if (max <= 0) return White;
                if (r == max) rr = 1;
                if (g == max) gg = 1;
                if (b == max) bb = 1;
            }

            return (rr * 0xFF << 16) | (gg * 0xFF << 8) | (bb * 0xFF);
        }

        /// <summary>Multiply a packed colour by a brightness factor, per-channel clamped.
        ///
        /// PREVIEW AND LEGEND ONLY. The display cannot show the result: snap a scaled
        /// colour and it returns to where it started, so this cannot dim anything drawn
        /// into the volume. Use density for that.</summary>
        public static int Scale(int col, float f)
        {
            int r = (int)(((col >> 16) & 0xFF) * f);
            int g = (int)(((col >>  8) & 0xFF) * f);
            int b = (int)(( col        & 0xFF) * f);
            r = Math.Clamp(r, 0, 255);
            g = Math.Clamp(g, 0, 255);
            b = Math.Clamp(b, 0, 255);
            return (r << 16) | (g << 8) | b;
        }

        /// <summary>Linear blend between two packed colours (t = 0 → a, 1 → b).</summary>
        public static int Mix(int a, int b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            int r = (int)(((a >> 16) & 0xFF) + (((b >> 16) & 0xFF) - ((a >> 16) & 0xFF)) * t);
            int g = (int)(((a >>  8) & 0xFF) + (((b >>  8) & 0xFF) - ((a >>  8) & 0xFF)) * t);
            int bl= (int)(( a        & 0xFF) + (( b        & 0xFF) - ( a        & 0xFF)) * t);
            return (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(bl, 0, 255);
        }

        /// <summary>Power-dissipation heat ramp: blue → cyan → yellow → red.</summary>
        public static int Heat(float frac)
        {
            frac = Math.Clamp(frac, 0f, 1f);
            if (frac < 0.34f) return Mix(0x1030FF, 0x00E0FF, frac / 0.34f);
            if (frac < 0.67f) return Mix(0x00E0FF, 0xFFE000, (frac - 0.34f) / 0.33f);
            return Mix(0xFFE000, 0xFF2000, (frac - 0.67f) / 0.33f);
        }
    }
}
