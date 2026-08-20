// ═══════════════════════════════════════════════════════════════════════════
//  Palette.cs — the app's colours in one place
//
//  Packed 0xRRGGBB ints, as the SDK expects. Scale() clamps every channel
//  after multiplying: an unclamped brightness scale wraps the packed int and
//  produces garbage colours rather than just "too bright".
// ═══════════════════════════════════════════════════════════════════════════

using System;

namespace EDes.Sim
{
    public static class Palette
    {
        public const int WireDim    = 0x2A6E8C;
        public const int WireBright = 0x9FE8FF;
        public const int Battery    = 0xFFCC33;
        public const int FlowDot    = 0xFFFFFF;
        public const int Text       = 0x9FE8FF;
        public const int TextDim    = 0x4E7A8C;
        public const int TextHilite = 0xFFDD44;
        public const int Graticule  = 0x14384A;
        public const int GridFloor  = 0x0E2A3A;
        public const int Globe      = 0x152233;
        public const int Trace      = 0x33FF99;
        public const int TraceEdge  = 0x0E7A4A;
        public const int Warning    = 0xFF5533;

        /// <summary>Multiply a packed colour by a brightness factor, per-channel clamped.</summary>
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
