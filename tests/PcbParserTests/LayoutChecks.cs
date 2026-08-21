// ═══════════════════════════════════════════════════════════════════════════
//  LayoutChecks.cs — the HUD text band cannot escape the volume
//
//  This exists because it already went wrong: the app trusted the SDK wrapper's
//  GetAspectRatioZ, which returns xsiz/64 — the RADIUS, not the vertical
//  half-height — so on a VX2 it believed the volume was 3.92 units tall instead
//  of 2 and anchored the first row of text at z = -3.92, roughly twice as high
//  as the top of the display. The bounds check that should have caught it was
//  using the same wrong number.
//
//  The clamp now lives in FrameLayout, so it is checkable without the SDK.
//  Remember -Z is up: the top of the volume is -zHalf and rows advance by
//  ADDING to z.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using EDes.Sim;

namespace PcbParserTests
{
    internal static class LayoutChecks
    {
        private static int _failures;

        private static void Ok(string what, bool pass)
        {
            if (!pass) _failures++;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        public static int Run()
        {
            const float zHalf = 2f, step = 0.31f;

            // ── The anchor is honoured when it is inside the volume ───────────
            {
                var l = new FrameLayout(zHalf, step, 2, 4, -zHalf);
                Ok($"top anchor -zHalf is used as given ({l.TopZ:0.###})",
                   Math.Abs(l.TopZ + zHalf) < 1e-5f);

                var mid = new FrameLayout(zHalf, step, 2, 4, -1f);
                Ok($"an interior anchor is used as given ({mid.TopZ:0.###})",
                   Math.Abs(mid.TopZ + 1f) < 1e-5f);
            }

            // ── Above the top is clamped, not honoured ────────────────────────
            {
                // -3.92 is the exact value the GetAspectRatioZ bug produced on a VX2.
                var bad = new FrameLayout(zHalf, step, 2, 4, -3.92f);
                Ok($"an anchor above the volume is clamped to the top ({bad.TopZ:0.###})",
                   Math.Abs(bad.TopZ + zHalf) < 1e-5f);
                Ok("...and so is nothing above -zHalf", bad.TopZ >= -zHalf - 1e-5f);
            }

            // ── Every row of the band stays inside, not just the first ────────
            {
                var l = new FrameLayout(zHalf, step, 2, 4, -zHalf);
                var st = l.Readout();
                float first = st.Row();
                float last  = first;
                for (int i = 0; i < 5; i++) last = st.Row();
                Ok($"the first row is inside the volume ({first:0.###})",
                   first >= -zHalf - 1e-5f && first <= zHalf);
                Ok($"and the sixth row is still inside it ({last:0.###})",
                   last >= -zHalf - 1e-5f && last <= zHalf);
            }

            // ── Below the floor is clamped too, leaving room for one row ──────
            {
                var low = new FrameLayout(zHalf, step, 1, 1, zHalf * 5f);
                Ok($"an anchor below the floor is pulled back in ({low.TopZ:0.###})",
                   low.TopZ <= zHalf - step + 1e-5f && low.TopZ >= -zHalf);
            }

            // ── Geometry always keeps the lower half ──────────────────────────
            {
                // Twenty rows is more than the volume can hold. The text must overflow
                // over the geometry rather than pushing the content band off the floor:
                // ugly text beats no board.
                var greedy = new FrameLayout(zHalf, step, 2, 20, -zHalf);
                Ok($"a greedy text band cannot pass the half-way line "
                 + $"({greedy.ContentTopZ:0.###})",
                   greedy.ContentTopZ <= 0f + 1e-5f);
                Ok($"so geometry always keeps at least half the height "
                 + $"({greedy.ContentHeight:0.###} of {zHalf * 2f:0.###})",
                   greedy.ContentHeight >= zHalf - 1e-5f);
                Ok($"and the shortfall is reported rather than hidden "
                 + $"({greedy.ReadoutRowsThatFit} rows fit, 22 asked for)",
                   greedy.ReadoutRowsThatFit < 22);
            }

            // ── The anchor scales with the display, which is the point ────────
            {
                // A VX2-XL is bigger; the same -1 fraction has to land on ITS top edge,
                // not on a world coordinate measured off a VX2.
                const float xlZHalf = 3.2f;
                var vx2 = new FrameLayout(zHalf,   step, 1, 2, -1f * zHalf);
                var xl  = new FrameLayout(xlZHalf, step, 1, 2, -1f * xlZHalf);
                Ok($"fraction -1 reaches the top on both sizes "
                 + $"({vx2.TopZ:0.##} vs {xl.TopZ:0.##})",
                   Math.Abs(vx2.TopZ + zHalf) < 1e-5f &&
                   Math.Abs(xl.TopZ + xlZHalf) < 1e-5f);
                Ok("and they are genuinely different world positions",
                   Math.Abs(vx2.TopZ - xl.TopZ) > 1f);
            }
            return _failures;
        }
    }
}
