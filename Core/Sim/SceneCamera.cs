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
        public float Zoom = 1f;

        // ── Orientation ───────────────────────────────────────────────────────
        // The scene's orientation is an orthonormal BASIS, not three Euler angles.
        //
        // Euler angles cannot express "rotate about the object's own axis": three
        // accumulated scalars replayed in a fixed order always rotate about the
        // frame the order is defined in, i.e. the display's global axes. That is why
        // twisting the puck used to tilt the board about global Y no matter how the
        // board was already sitting. Storing the basis instead and POST-multiplying
        // each increment makes every rotation local by construction, and drops
        // gimbal lock (and the pitch clamp that existed to hide it) on the way out.
        //
        // U, V, W are the scene's own X, Y, Z axes expressed in display space, so
        // Transform is just  world = U*x + V*y + W*z.
        private float _ux = 1, _uy = 0, _uz = 0;   // scene X in display space
        private float _vx = 0, _vy = 1, _vz = 0;   // scene Y
        private float _wx = 0, _wy = 0, _wz = 1;   // scene Z

        public void Reset()
        {
            PanX = PanY = PanZ = 0f;
            Zoom = 1f;
            _ux = 1; _uy = 0; _uz = 0;
            _vx = 0; _vy = 1; _vz = 0;
            _wx = 0; _wy = 0; _wz = 1;
        }

        /// <summary>World → display space. Call once per point, right before drawing.</summary>
        public point3d Transform(float x, float y, float z)
        {
            if (!HORIZONTAL_IS_X) { (x, y) = (y, x); }

            x += PanX; y += PanY; z += PanZ;
            x *= Zoom; y *= Zoom; z *= Zoom;

            return new point3d(_ux * x + _vx * y + _wx * z,
                               _uy * x + _vy * y + _wy * z,
                               _uz * x + _vz * y + _wz * z);
        }

        public point3d Transform(point3d p) => Transform(p.x, p.y, p.z);

        /// <summary>False to rotate about the DISPLAY's fixed axes instead of the scene's
        /// own. Local is the default because it is what "turn the board over" means when
        /// the board is already tilted; global is easier to reason about when lining an
        /// object up with the volume itself.</summary>
        public bool LocalAxes = true;

        /// <summary>Blocks ALL rotation, whatever the per-axis locks say. Set while
        /// inspecting: the probe is positioned in display space, so a scene that kept
        /// turning would slide the board out from under a stationary pointer.</summary>
        public bool RotationLocked;

        /// <summary>Per-axis rotation locks, in whichever frame LocalAxes selects — so a
        /// lock means the same thing as the axis it is named after, rather than silently
        /// referring to the other frame.</summary>
        public bool LockRotX, LockRotY, LockRotZ;

        /// <summary>Rotate by three small angles, about whichever frame LocalAxes selects.
        ///
        /// This is the ONLY funnel every input path uses — ApplyNav, Orbit and RollBy all
        /// come through here — which is why the locks live here rather than at each call
        /// site. Put them anywhere else and the next input path added would quietly
        /// bypass them. RotateLocal/RotateGlobal remain the unlocked primitives.</summary>
        public void Rotate(float aboutX, float aboutY, float aboutZ)
        {
            if (RotationLocked) return;

            if (LockRotX) aboutX = 0f;
            if (LockRotY) aboutY = 0f;
            if (LockRotZ) aboutZ = 0f;

            // Everything locked out, or nothing asked for: skip the work AND the
            // re-orthonormalisation, so a locked camera cannot drift.
            if (aboutX == 0f && aboutY == 0f && aboutZ == 0f) return;

            if (LocalAxes) RotateLocal(aboutX, aboutY, aboutZ);
            else           RotateGlobal(aboutX, aboutY, aboutZ);
        }

        /// <summary>Rotate about the DISPLAY's fixed axes.
        ///
        /// The mirror image of RotateLocal: that one post-multiplies, which leaves the
        /// scene's own axes as the axis of rotation, while this PRE-multiplies, i.e. it
        /// turns each basis vector about a world axis. Same basis, opposite side of the
        /// product — that single distinction is the whole difference between the two
        /// modes.</summary>
        public void RotateGlobal(float aboutX, float aboutY, float aboutZ)
        {
            if (aboutX != 0f)
            {
                float c = MathF.Cos(aboutX), sn = MathF.Sin(aboutX);
                RotAxis(ref _uy, ref _uz, c, sn);
                RotAxis(ref _vy, ref _vz, c, sn);
                RotAxis(ref _wy, ref _wz, c, sn);
            }
            if (aboutY != 0f)
            {
                float c = MathF.Cos(aboutY), sn = MathF.Sin(aboutY);
                RotAxis(ref _uz, ref _ux, c, sn);
                RotAxis(ref _vz, ref _vx, c, sn);
                RotAxis(ref _wz, ref _wx, c, sn);
            }
            if (aboutZ != 0f)
            {
                float c = MathF.Cos(aboutZ), sn = MathF.Sin(aboutZ);
                RotAxis(ref _ux, ref _uy, c, sn);
                RotAxis(ref _vx, ref _vy, c, sn);
                RotAxis(ref _wx, ref _wy, c, sn);
            }
            Orthonormalise();
        }

        /// <summary>Rotate one 2D component pair — the plane perpendicular to the axis.</summary>
        private static void RotAxis(ref float a, ref float b, float c, float sn)
        {
            float na = a * c - b * sn;
            float nb = a * sn + b * c;
            a = na; b = nb;
        }

        /// <summary>Display space → world space: the exact inverse of Transform.
        ///
        /// Needed so a cursor the user positions in the VOLUME can be asked what it is
        /// pointing at, which is a question about the scene's own coordinates. Exact
        /// rather than approximate because the basis is orthonormal, so its inverse is
        /// its transpose — no matrix inversion and no drift.</summary>
        public point3d InverseTransform(float dx, float dy, float dz)
        {
            // Undo the rotation with the transpose (rows become the dot products).
            float x = _ux * dx + _uy * dy + _uz * dz;
            float y = _vx * dx + _vy * dy + _vz * dz;
            float z = _wx * dx + _wy * dy + _wz * dz;

            float iz = Zoom > 1e-6f ? 1f / Zoom : 1f;
            x = x * iz - PanX;
            y = y * iz - PanY;
            z = z * iz - PanZ;

            // Mirrors Transform's swap so this stays a true inverse if HORIZONTAL_IS_X is
            // ever flipped. Unreachable while the const is true, hence the suppression —
            // deleting it would silently break the inverse the day the switch moves.
#pragma warning disable 162
            if (!HORIZONTAL_IS_X) { (x, y) = (y, x); }
#pragma warning restore 162
            return new point3d(x, y, z);
        }

        public point3d InverseTransform(point3d p) => InverseTransform(p.x, p.y, p.z);

        /// <summary>Rotate about the scene's OWN axes by three small angles (radians).</summary>
        public void RotateLocal(float aboutX, float aboutY, float aboutZ)
        {
            // Post-multiplying by a rotation about a basis axis just mixes the OTHER
            // two basis vectors — no matrix product needed, and it is obvious by
            // inspection that the axis of rotation is the scene's own.
            if (aboutX != 0f)
            {
                float c = MathF.Cos(aboutX), sn = MathF.Sin(aboutX);
                (_vx, _wx) = (_vx * c + _wx * sn, _wx * c - _vx * sn);
                (_vy, _wy) = (_vy * c + _wy * sn, _wy * c - _vy * sn);
                (_vz, _wz) = (_vz * c + _wz * sn, _wz * c - _vz * sn);
            }
            if (aboutY != 0f)
            {
                float c = MathF.Cos(aboutY), sn = MathF.Sin(aboutY);
                (_wx, _ux) = (_wx * c + _ux * sn, _ux * c - _wx * sn);
                (_wy, _uy) = (_wy * c + _uy * sn, _uy * c - _wy * sn);
                (_wz, _uz) = (_wz * c + _uz * sn, _uz * c - _wz * sn);
            }
            if (aboutZ != 0f)
            {
                float c = MathF.Cos(aboutZ), sn = MathF.Sin(aboutZ);
                (_ux, _vx) = (_ux * c + _vx * sn, _vx * c - _ux * sn);
                (_uy, _vy) = (_uy * c + _vy * sn, _vy * c - _uy * sn);
                (_uz, _vz) = (_uz * c + _vz * sn, _vz * c - _uz * sn);
            }
            Orthonormalise();
        }

        /// <summary>Gram-Schmidt the basis back to square and unit-length.
        ///
        /// Rotations here are INCREMENTAL — the basis is fed back into itself every
        /// frame, so float error compounds instead of cancelling. Left alone it shows
        /// up as the scene slowly shearing and scaling after a few minutes of driving
        /// the puck. Renormalising each time costs nothing at this rate.</summary>
        private void Orthonormalise()
        {
            float ul = MathF.Sqrt(_ux * _ux + _uy * _uy + _uz * _uz);
            if (ul < 1e-6f) { Reset(); return; }
            _ux /= ul; _uy /= ul; _uz /= ul;

            float d = _ux * _vx + _uy * _vy + _uz * _vz;      // V -= (U·V) U
            _vx -= d * _ux; _vy -= d * _uy; _vz -= d * _uz;
            float vl = MathF.Sqrt(_vx * _vx + _vy * _vy + _vz * _vz);
            if (vl < 1e-6f) { Reset(); return; }
            _vx /= vl; _vy /= vl; _vz /= vl;

            _wx = _uy * _vz - _uz * _vy;                      // W = U x V
            _wy = _uz * _vx - _ux * _vz;
            _wz = _ux * _vy - _uy * _vx;
        }

        /// <summary>Apply one frame of 6-DOF SpaceMouse motion (SpaceNavigator).
        ///
        /// Axis mapping is empirical, not from the SDK's enum names: the aliases in
        /// VX_NAV_AXIS_CODES (PITCH=X, YAW=Y, ROLL=Z) do not describe what this puck
        /// actually reports, so each rotation is commented with the physical gesture
        /// it comes from. Change the three lines below to re-tune; nothing else needs
        /// to know.</summary>
        public void ApplyNav(in NavState nav, float dt, float panRate, float rotRate,
                             float zoomRate, bool allowPan = true)
        {
            if (!nav.Present) return;

            // One rate for all three, and NavState.Condition has already put the three
            // translation axes on a common scale, so pushing the puck the same distance
            // in X, Y or Z moves the scene the same distance.
            //
            // Which raw axis is which is EMPIRICAL, like the rotations below. The SDK's
            // NAV_Y_AXIS_DIRECTION / NAV_Z_AXIS_DIRECTION names suggest Y is depth and Z
            // is lift; on this puck they are the other way round, so binding them by name
            // made lifting the cap move the model in depth and pushing it forward move it
            // vertically. Bound by measured behaviour instead.
            // Suppressed in inspection mode: the same three axes drive the probe there,
            // and having them do both at once would move the probe and the scene under it
            // together, so the probe could never actually reach anything.
            if (allowPan)
            {
                PanX += nav.Dx * panRate * dt;   // slide left / right  → scene left / right
                PanY += nav.Dy * panRate * dt;   // push fore / aft     → scene depth
                // Negated: -Z is up in this app, so lifting the cap has to SUBTRACT
                // from z for the model to rise with your hand.
                PanZ -= nav.Dz * panRate * dt;   // lift up / down      → scene vertical
            }

            // All three rotations share rotRate, so the puck feels isotropic in rotation
            // exactly as translation does above. The Y term is negated: this axis reports
            // the opposite sense to the other two, so without it tilting left rolled the
            // scene right.
            Rotate( nav.Ay * rotRate * dt,   // tilt forward / back → X
                   -nav.Ax * rotRate * dt,   // tilt left / right   → Y
                    nav.Az * rotRate * dt);  // twist left / right  → Z

            // Zoom is on the two puck buttons. It used to be a gesture on the lift
            // axis, which fought the pan already bound to that same axis — pushing
            // down to raise the board zoomed it as a side effect.
            bool zoomIn  = nav.ButtonDown(VX_NAV_BUTTON_CODES.NAV_RIGHT_BUTTON);
            bool zoomOut = nav.ButtonDown(VX_NAV_BUTTON_CODES.NAV_LEFT_BUTTON);
            if (zoomIn ^ zoomOut)
                ZoomBy(1f + (zoomIn ? zoomRate : -zoomRate) * dt);
        }

        /// <summary>Keyboard orbit — also local, so WASD and the puck agree.</summary>
        public void Orbit(float dYaw, float dPitch) => Rotate(dPitch, 0f, dYaw);

        /// <summary>Keyboard roll (Q/E), about the scene's own depth axis.</summary>
        public void RollBy(float d) => Rotate(0f, d, 0f);

        /// <summary>Zoom by a factor, with no practical limit.
        ///
        /// The 0.2..5 range this used to clamp to was a UX guess, and it stopped anyone
        /// getting close enough to inspect a 0.2 mm track. The bounds that remain are
        /// NUMERICAL, not editorial: at zero the transform collapses every point onto the
        /// origin and InverseTransform divides by it, and past ~1e6 single-precision
        /// coordinates stop resolving neighbouring voxels at all. Both would look like a
        /// crash rather than a limit. A non-finite factor is rejected outright for the same
        /// reason — one NaN here propagates into every transformed point for the rest of
        /// the session.</summary>
        public void ZoomBy(float factor)
        {
            if (!float.IsFinite(factor) || factor <= 0f) return;
            float z = Zoom * factor;
            if (!float.IsFinite(z)) return;
            Zoom = Math.Clamp(z, 1e-4f, 1e6f);
        }

        // ── Derived angles, for the settings readout only ──────────────────────
        // The basis is the truth; these are a human-readable projection of it and are
        // ambiguous near straight up, which is fine for a status line.
        public float Yaw   => MathF.Atan2(_uy, _ux);
        public float Pitch => MathF.Asin(Math.Clamp(-_uz, -1f, 1f));
        public float Roll  => MathF.Atan2(_vz, _wz);
    }
}
