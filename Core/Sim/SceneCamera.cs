// ═══════════════════════════════════════════════════════════════════════════
//  SceneCamera.cs — one transform for the whole scene
//
//  Every coordinate is defined in plain "world space" first, then passed
//  through Transform() immediately before it is added to the voxel batch.
//  That is what lets pan / zoom / yaw / pitch / roll move the ENTIRE scene
//  for free: nothing except the single Transform() call site per draw needs
//  to know a camera exists.
//
//  Order is pan → scale → yaw → pitch → roll (the SDK template's reference
//  order, kept so this app feels like every other VLED app to fly around).
//
//  Axis convention used throughout this app:
//      -Z = up          (so "raise it" means SUBTRACT from z)
//       X = horizontal, the circuit's left→right layout direction
//       Y = depth, used to fan parallel branches into separate lanes
//
//  NOTE: the brief also said "+Y is to the right". That cannot hold at the
//  same time as "the HUD/scope lives on the y = 0.1 plane" — a constant-Y
//  plane is only a flat readable panel if Y is the plane normal (i.e. depth).
//  The layout therefore keeps X horizontal, and HORIZONTAL_IS_X below is the
//  single switch to flip that if the display is being viewed down the X axis.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes.Sim
{
    public sealed class SceneCamera
    {
        /// <summary>Set false to swap the layout's horizontal and depth axes
        /// (horizontal becomes +Y, depth becomes X). See the header note.</summary>
        public const bool HORIZONTAL_IS_X = true;

        public float PanX, PanY, PanZ;
        public float Zoom  = 1f;
        public float Yaw, Pitch, Roll;

        public void Reset()
        {
            PanX = PanY = PanZ = 0f;
            Zoom = 1f;
            Yaw  = Pitch = Roll = 0f;
        }

        /// <summary>World → display space. Call once per point, right before drawing.</summary>
        public point3d Transform(float x, float y, float z)
        {
            if (!HORIZONTAL_IS_X) { (x, y) = (y, x); }

            x += PanX; y += PanY; z += PanZ;
            x *= Zoom; y *= Zoom; z *= Zoom;

            // Yaw — around the vertical (Z) axis
            float cx = x * MathF.Cos(Yaw) - y * MathF.Sin(Yaw);
            float cy = x * MathF.Sin(Yaw) + y * MathF.Cos(Yaw);
            x = cx; y = cy;

            // Pitch — around the horizontal (X) axis
            float cz = z * MathF.Cos(Pitch) - y * MathF.Sin(Pitch);
            cy       = z * MathF.Sin(Pitch) + y * MathF.Cos(Pitch);
            z = cz; y = cy;

            // Roll — around the depth (Y) axis
            cx = x * MathF.Cos(Roll) + z * MathF.Sin(Roll);
            cz = -x * MathF.Sin(Roll) + z * MathF.Cos(Roll);
            x = cx; z = cz;

            return new point3d(x, y, z);
        }

        public point3d Transform(point3d p) => Transform(p.x, p.y, p.z);

        /// <summary>Apply one frame of 6-DOF SpaceMouse motion (SpaceNavigator).</summary>
        public void ApplyNav(in NavState nav, float dt, float panRate, float rotRate, float zoomRate)
        {
            if (!nav.Present) return;

            PanX += nav.Dx * panRate * dt;
            PanY += nav.Dz * panRate * dt;   // nav "forward/back" → scene depth
            PanZ += nav.Dy * panRate * dt;   // nav "lift" → vertical (-Z is up)

            Yaw   += nav.Ay * rotRate * dt;
            Pitch += nav.Ax * rotRate * dt;
            Roll  += nav.Az * rotRate * dt;

            // Twist the puck's own vertical axis to zoom when it is pushed down/up
            // hard while not rotating — cheap, and keeps zoom on the same device.
            if (MathF.Abs(nav.Dy) > 0.6f && MathF.Abs(nav.Ax) < 0.2f)
                Zoom = Math.Clamp(Zoom * (1f + nav.Dy * zoomRate * dt), 0.2f, 5f);

            ClampPitch();
        }

        public void Orbit(float dYaw, float dPitch)
        {
            Yaw   += dYaw;
            Pitch += dPitch;
            ClampPitch();
        }

        public void ZoomBy(float factor) => Zoom = Math.Clamp(Zoom * factor, 0.2f, 5f);

        private void ClampPitch() => Pitch = Math.Clamp(Pitch, -1.45f, 1.45f);
    }
}
