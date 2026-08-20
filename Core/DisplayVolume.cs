// ═══════════════════════════════════════════════════════════════════════════
//  DisplayVolume.cs — Runtime display bounds
//
//  Always use these values instead of hardcoded floats.
//  The Voxon VX2 and VX2XL have different physical sizes:
//
//    VX2   : HalfXY ≈ 2.0,  HalfZ ≈ 1.0
//    VX2XL : HalfXY ≈ 4.0,  HalfZ ≈ 2.0
//    Sim   : 4.0 / 2.0  (default until hardware is detected)
//
//  Call Init(ref vs) once per session on the first FrameStart.
//  The static values are updated from the live hardware — all code
//  that runs after Init() sees the correct values automatically.
//
//  Coordinate axes:
//    X = left/right    (−HalfXY to +HalfXY)
//    Y = depth         (−HalfXY to +HalfXY)
//    Z = vertical      (≈ −0.5 to +HalfZ)
//
//  NOTE: Z is NOT symmetric. The usable upper bound is ~HalfZ * 0.875f.
// ═══════════════════════════════════════════════════════════════════════════

using Voxon;

namespace EDes
{
    public static class DisplayVolume
    {
        // ── Raw hardware values ───────────────────────────────────────────────
        public static float HalfXY { get; private set; } = 4.0f;  // VX2XL default
        public static float HalfZ  { get; private set; } = 2.0f;
        public static float Scale  { get; private set; } = 1.0f;  // HalfXY / 4.0
        public static float ScaleZ { get; private set; } = 1.0f;  // HalfZ  / 2.0

        private static bool _initialised = false;

        // Call once per session — reads the live display size from the SDK.
        public static void Init(ref vxl_state_t vs)
        {
            if (_initialised) return;
            // boundr == 0 means the DLL hasn't filled it yet — keep the default.
            if (vs.boundr > 0.1f)
            {
                HalfXY = vs.boundr;
                HalfZ  = vs.boundz > 0.1f ? vs.boundz : 2.0f;
                Scale  = HalfXY / 4.0f;
                ScaleZ = HalfZ  / 2.0f;
                _initialised = true;
                App.Log($"[DisplayVolume] HalfXY={HalfXY:F2}  HalfZ={HalfZ:F2}");
            }
        }

        // Override from the settings panel for testing different hardware sizes.
        public static void ForceOverride(float halfXY, float halfZ)
        {
            HalfXY = halfXY;
            HalfZ  = halfZ > 0.1f ? halfZ : HalfZ;
            Scale  = HalfXY / 4.0f;
            ScaleZ = HalfZ  / 2.0f;
            _initialised = true;
        }

        // ── Pre-computed gameplay constants ────────────────────────────────────
        // Use these instead of multiplying by magic numbers everywhere.

        /// Movement arena: 87.5% of display radius
        public static float GameHalfXY  => HalfXY * 0.875f;

        /// Display edge (spawn position)
        public static float EdgeXY      => HalfXY;

        /// Just off-screen (off-screen spawn, wrapping)
        public static float FarXY       => HalfXY * 1.125f;

        /// Safe upper Z limit inside the display
        public static float TopZ        => HalfZ  * 0.875f;

        /// Despawn bullets here — well outside the display
        public static float KillXY      => HalfXY * 1.625f;
    }
}
