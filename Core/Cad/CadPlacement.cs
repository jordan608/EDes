// ═══════════════════════════════════════════════════════════════════════════
//  CadPlacement.cs — where a Fusion assembly lands in the volume
//
//  The whole mapping, in one place, because it is exactly the kind of arithmetic
//  that silently produces a plausibly-wrong model when it is spread across three
//  files.
//
//      display.x =  fusion.x * scale + OriginX
//      display.y =  fusion.y * scale + OriginY
//      display.z = -fusion.z * scale + OriginZ        <-- the flip
//
//  Two conventions meet here and disagree about which way is up:
//
//    • Fusion is Z-up. Its API is also always in CENTIMETRES regardless of the
//      document's display units, but the add-in converts, so what arrives here
//      is millimetres.
//    • This display is -Z-up, and its volume runs z = -zHalf (ceiling) to
//      z = +zHalf (floor).
//
//  So Fusion's +Z maps to display -Z, and the origin sits at (0, 0, +zHalf) --
//  the FLOOR, centre. The assembly then stands on the floor and grows upward
//  through the full height of the volume. Anchoring it at -zHalf instead would
//  put the origin on the ceiling and clip everything above it, which is the
//  first thing this got wrong on paper.
//
//  Nothing here re-centres or auto-fits. Fusion owns position; EDes contributes
//  a scale and a fixed offset and nothing else. An auto-fit would move the model
//  every time a component was added, which is the opposite of what "Fusion is
//  authoritative" means.
//
//  Map() folds the Origin in and is what most callers (tests included) want: a
// self-contained "Fusion mm -> intended display position" with no camera
//  involved. The render path (CadSceneRenderer) does NOT use it directly, though
//  -- it needs MapLinear() + Anchor() kept separate, applied on either side of
//  SceneCamera.Transform, for exactly the reason SceneCamera's own Pan was moved
//  after its rotation basis: an offset folded in BEFORE a rotation gets rotated
//  right along with everything else, so the assembly's own anchor point (the
//  floor, centred) would swing through an arc around the DISPLAY's origin every
//  time the scene rotated, instead of the assembly spinning in place on its own
//  floor spot. Anchor() is that offset, added back AFTER the camera transform,
//  so it is a fixed point in the volume no rotation or pan can move.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes.Cad
{
    public struct CadPlacement
    {
        /// <summary>Display world units per millimetre.</summary>
        public float Scale;

        /// <summary>Where the Fusion origin lands, in display units.</summary>
        public float OriginX, OriginY, OriginZ;

        /// <summary>The default: standing on the floor, centred, at a scale that suits a
        /// palm-sized assembly. zHalf is passed in because the volume's real height is read
        /// from the SDK every frame and must never be hardcoded.</summary>
        public static CadPlacement Default(float zHalf) => new CadPlacement
        {
            Scale   = 0.04f,          // 1 mm -> 0.04 units, so 100 mm spans 4 units
            OriginX = 0f,
            OriginY = 0f,
            OriginZ = zHalf,          // the FLOOR: +Z is down on this display
        };

        /// <summary>One Fusion point (mm, Z up) to display space (units, -Z up), Origin
        /// included. Self-contained on purpose — see the header for why the render path
        /// uses MapLinear()+Anchor() instead of this.</summary>
        public point3d Map(float xMm, float yMm, float zMm) => new point3d(
            xMm * Scale + OriginX,
            yMm * Scale + OriginY,
            -zMm * Scale + OriginZ);

        /// <summary>The scale-and-flip half of Map(), with NO Origin — this is what may
        /// safely pass through SceneCamera.Transform's rotation, because it carries nothing
        /// that a rotation would need to leave fixed.</summary>
        public point3d MapLinear(float xMm, float yMm, float zMm) => new point3d(
            xMm * Scale, yMm * Scale, -zMm * Scale);

        /// <summary>The Origin half of Map(), applied AFTER the camera transform so it is a
        /// fixed point in display space — the assembly's floor spot — that no rotation or
        /// pan can drag around the display's own origin instead.</summary>
        public point3d Anchor(point3d p) => new point3d(p.x + OriginX, p.y + OriginY, p.z + OriginZ);

        /// <summary>A scale that would fit the given extent across the usable width, for the
        /// one-shot "Fit once" button.
        ///
        /// One-shot on purpose: it computes a number and then stops being involved. A live
        /// fit would re-scale the model every time a component appeared or moved, which is
        /// precisely the authority over position that Fusion is supposed to hold.</summary>
        public static float FitScale(float widthMm, float depthMm, float heightMm,
                                     float radius, float zHalf)
        {
            float span = MathF.Max(widthMm, depthMm);
            float byWidth  = span     > 1e-3f ? radius * 1.9f / span     : 1f;
            float byHeight = heightMm > 1e-3f ? zHalf * 1.9f  / heightMm : 1f;
            float s = MathF.Min(byWidth, byHeight);
            return float.IsFinite(s) && s > 0f ? s : 0.04f;
        }

        /// <summary>Is this mapped point inside the cylinder? Used only to report the
        /// clipped fraction -- VoxelBatch does the real clipping. Reporting it matters
        /// because geometry placed outside the volume simply is not drawn, and without a
        /// number the operator is left guessing whether the model is wrong or the origin
        /// is.</summary>
        public static bool Inside(in point3d p, float radius, float zHalf)
            => p.x * p.x + p.y * p.y <= radius * radius
               && p.z >= -zHalf && p.z <= zHalf;
    }
}
