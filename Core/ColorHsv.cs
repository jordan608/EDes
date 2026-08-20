using System;

namespace EDes
{
    // Small shared RGB<->HSV helper used to hue-shift baked sprite-explosion pixels
    // (see SpriteFrameSet.BaseHueDeg + ExplosionManager.DrawSpriteBurst) while leaving
    // each pixel's saturation/value untouched — same effect as GIMP's Hue-Shift.
    public static class ColorHsv
    {
        public static void RgbToHsv(int col, out float h, out float s, out float v)
        {
            int r = (col >> 16) & 0xFF, g = (col >> 8) & 0xFF, b = col & 0xFF;
            float rf = r / 255f, gf = g / 255f, bf = b / 255f;
            float max = MathF.Max(rf, MathF.Max(gf, bf));
            float min = MathF.Min(rf, MathF.Min(gf, bf));
            float delta = max - min;
            v = max;
            s = max <= 0.0001f ? 0f : delta / max;
            if (delta < 0.0001f) { h = 0f; return; }
            float hh;
            if (max == rf)      hh = 60f * (((gf - bf) / delta) % 6f);
            else if (max == gf) hh = 60f * (((bf - rf) / delta) + 2f);
            else                hh = 60f * (((rf - gf) / delta) + 4f);
            if (hh < 0f) hh += 360f;
            h = hh;
        }

        public static int HsvToRgb(float h, float s, float v)
        {
            h = ((h % 360f) + 360f) % 360f;
            float c = v * s;
            float x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
            float m = v - c;
            float rf, gf, bf;
            if      (h < 60f)  { rf = c; gf = x; bf = 0f; }
            else if (h < 120f) { rf = x; gf = c; bf = 0f; }
            else if (h < 180f) { rf = 0f; gf = c; bf = x; }
            else if (h < 240f) { rf = 0f; gf = x; bf = c; }
            else if (h < 300f) { rf = x; gf = 0f; bf = c; }
            else               { rf = c; gf = 0f; bf = x; }
            int r = Math.Clamp((int)MathF.Round((rf + m) * 255f), 0, 255);
            int g = Math.Clamp((int)MathF.Round((gf + m) * 255f), 0, 255);
            int b = Math.Clamp((int)MathF.Round((bf + m) * 255f), 0, 255);
            return (r << 16) | (g << 8) | b;
        }

        /// <summary>Rotate a baked pixel's hue by <paramref name="shiftDeg"/>, keeping its
        /// saturation and value. Near-grey/white pixels (no real hue) pass through
        /// unchanged so a sprite's white-hot core still reads as white after retinting.</summary>
        public static int ShiftHue(int col, float shiftDeg)
        {
            if (MathF.Abs(shiftDeg) < 0.5f) return col;
            RgbToHsv(col, out float h, out float s, out float v);
            if (s < 0.03f) return col;
            return HsvToRgb(h + shiftDeg, s, v);
        }

        /// <summary>Two-tone variant of <see cref="ShiftHue"/>: blends between
        /// <paramref name="shiftDark"/> and <paramref name="shiftBright"/> using the
        /// pixel's OWN value (brightness) as the weight, so a sprite's darker regions
        /// retint toward one target hue and its brighter regions toward another instead
        /// of the whole sprite rotating by one fixed amount. Near-grey/white pixels pass
        /// through unchanged, same as ShiftHue.</summary>
        public static int ShiftHueBlend(int col, float shiftDark, float shiftBright)
        {
            RgbToHsv(col, out float h, out float s, out float v);
            if (s < 0.03f) return col;
            float shift = shiftDark + (shiftBright - shiftDark) * Math.Clamp(v, 0f, 1f);
            if (MathF.Abs(shift) < 0.5f) return col;
            return HsvToRgb(h + shift, s, v);
        }
    }
}
