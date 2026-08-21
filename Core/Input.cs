// ═══════════════════════════════════════════════════════════════════════════
//  Input.cs — Unified input (keyboard + Voxon/Xbox controller)
//
//  Games read a single InputState each frame instead of touching NativeInput or
//  the SDK joystick API directly:
//
//      InputState in = inputManager.Poll(ledWin);   // once per frame (engine)
//      in.MoveX / MoveY / MoveZ                      // movement, −1..1
//      in.LookX / LookY                              // right stick, −1..1
//      in.IsDown(GameButton.Fire)                    // held this frame
//      in.Pressed(GameButton.Start)                  // edge: pressed this frame
//
//  Keyboard (NativeInput) and controller 0 are merged: either source can drive
//  any action. Controller reads are wrapped in try/catch so a build without the
//  joystick API still works on keyboard alone.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes
{
    // Logical game actions (bit index in the InputState masks).
    public enum GameButton
    {
        Fire = 0, Secondary = 1, Up = 2, Down = 3,
        Start = 4, Back = 5,
        MenuUp = 6, MenuDown = 7, MenuLeft = 8, MenuRight = 9,
    }

    /// <summary>One frame of 6-DOF SpaceNavigator (SpaceMouse) motion, read from
    /// LedWin's nav API or LedHost's vxl_nav_read.
    ///
    /// The axes here are RAW driver counts, NOT normalised and NOT dead-zoned — a
    /// 3Dconnexion puck reports roughly +/-350 at full deflection and a few counts
    /// of noise at rest. Feeding those straight into a camera rate is what made the
    /// puck unusable (~1 world unit per frame at a rate of 0.1) and what made the
    /// scene drift when nobody was touching it. Call Condition() to turn a raw
    /// reading into the -1..1, dead-zoned form every consumer actually wants; the
    /// raw values are kept as-is so the diagnostics readout can still show them.
    ///
    /// Present is false when no puck is plugged in, so consumers can skip nav
    /// handling entirely.</summary>
    public readonly struct NavState
    {
        public readonly bool  Present;
        public readonly int   Devices;      // what LedWin's GetNavCount() reported
        public readonly float Dx, Dy, Dz;   // translation: right, lift, forward
        public readonly float Ax, Ay, Az;   // rotation: pitch, yaw, roll
        public readonly float Sx, Sy, Sz;   // the SDK's summed (accumulated) axes
        public readonly int   Buttons;      // bit 0 = left, bit 1 = right

        public NavState(bool present, int devices,
                        float dx, float dy, float dz,
                        float ax, float ay, float az,
                        float sx, float sy, float sz, int buttons)
        {
            Present = present;
            Devices = devices;
            Dx = dx; Dy = dy; Dz = dz;
            Ax = ax; Ay = ay; Az = az;
            Sx = sx; Sy = sy; Sz = sz;
            Buttons = buttons;
        }

        public bool ButtonDown(VX_NAV_BUTTON_CODES b) => (Buttons & (1 << (int)b)) != 0;

        /// <summary>True if any axis is off zero — i.e. the puck is actually moving.</summary>
        public bool HasMotion =>
            MathF.Abs(Dx) + MathF.Abs(Dy) + MathF.Abs(Dz) +
            MathF.Abs(Ax) + MathF.Abs(Ay) + MathF.Abs(Az) > 1e-4f;

        /// <summary>Largest absolute value on the three TRANSLATION axes, and on the
        /// three ROTATION axes, tracked separately.
        ///
        /// Separately because the puck does not report the same range on both groups —
        /// which is precisely why one shared full-scale left translation and rotation
        /// feeling unequal no matter how the rates were trimmed. Calibrate each group
        /// against its own peak and the six axes finally agree.</summary>
        public float PeakTranslation =>
            MathF.Max(MathF.Max(MathF.Abs(Dx), MathF.Abs(Dy)), MathF.Abs(Dz));

        public float PeakRotation =>
            MathF.Max(MathF.Max(MathF.Abs(Ax), MathF.Abs(Ay)), MathF.Abs(Az));

        /// <summary>Raw driver counts → a usable -1..1 control signal.
        ///
        /// Two steps, in this order:
        ///   1. divide by the group full-scale (the count the puck reports at full
        ///      deflection) and clamp, so the axes mean the same thing on any device and
        ///      the camera rates are expressed in world-units-per-second. Translation
        ///      and rotation get their OWN full-scale because the puck does not report
        ///      the same range for both; sharing one is what made them feel unequal;
        ///   2. subtract a dead-zone and RE-SCALE what is left back over the full
        ///      0..1 travel. Re-scaling matters: a bare "under the threshold is zero"
        ///      test leaves a step discontinuity at the edge, so the scene would jump
        ///      the instant the puck crossed it instead of easing in from a stop.
        ///
        /// The summed axes and the buttons are passed through untouched — they are not
        /// rates and nothing integrates them.</summary>
        public NavState Condition(float transFullScale, float rotFullScale, float deadzone)
        {
            if (!Present) return default;

            float ts   = MathF.Max(1e-3f, transFullScale);
            float rs   = MathF.Max(1e-3f, rotFullScale);
            float dead = Math.Clamp(deadzone, 0f, 0.9f);

            // One scale for all three translation axes and one for all three rotation
            // axes — never per-axis. Per-axis trimming would let X drift away from Y
            // and make the puck feel skewed rather than merely fast or slow.
            return new NavState(true, Devices,
                                Cond(Dx, ts, dead), Cond(Dy, ts, dead), Cond(Dz, ts, dead),
                                Cond(Ax, rs, dead), Cond(Ay, rs, dead), Cond(Az, rs, dead),
                                Sx, Sy, Sz, Buttons);
        }

        private static float Cond(float raw, float scale, float dead)
        {
            float v = Math.Clamp(raw / scale, -1f, 1f);
            float a = MathF.Abs(v);
            if (a <= dead) return 0f;
            return MathF.Sign(v) * (a - dead) / (1f - dead);
        }
    }

    // Immutable per-frame snapshot handed to the game.
    public readonly struct InputState
    {
        public readonly float MoveX, MoveY, MoveZ;   // primary movement (−1..1)
        public readonly float LookX, LookY;          // right stick (−1..1)
        public readonly NavState Nav;                // SpaceNavigator (Present=false if absent)
        private readonly uint _held;
        private readonly uint _pressed;

        public InputState(float mx, float my, float mz, float lx, float ly,
                          uint held, uint pressed, NavState nav = default)
        {
            MoveX = mx; MoveY = my; MoveZ = mz; LookX = lx; LookY = ly;
            _held = held; _pressed = pressed; Nav = nav;
        }

        /// <summary>True while the action is held (keyboard or controller).</summary>
        public bool IsDown(GameButton b)  => (_held    & (1u << (int)b)) != 0;
        /// <summary>True only on the first frame the action goes down.</summary>
        public bool Pressed(GameButton b) => (_pressed & (1u << (int)b)) != 0;
    }

    public sealed class InputManager
    {
        private const float DEAD = 0.15f;   // analog stick dead-zone
        private uint _prevHeld;

        /// <summary>Snapshot input for this frame. Call once per game-loop frame.</summary>
        public InputState Poll(LedWinCS ledWin)
        {
            NativeInput.Poll();   // refreshes keyboard + advances edge state

            float mx = 0, my = 0, mz = 0, lx = 0, ly = 0;
            uint held = 0;

            // ── Keyboard ──────────────────────────────────────────────────────
            if (Key(VX_KEYS.KB_Arrow_Left)  || Key(VX_KEYS.KB_A)) mx -= 1f;
            if (Key(VX_KEYS.KB_Arrow_Right) || Key(VX_KEYS.KB_D)) mx += 1f;
            if (Key(VX_KEYS.KB_Arrow_Up)    || Key(VX_KEYS.KB_W)) my -= 1f;
            if (Key(VX_KEYS.KB_Arrow_Down)  || Key(VX_KEYS.KB_S)) my += 1f;
            if (Key(VX_KEYS.KB_Shift_Left)) mz += 1f;
            if (Key(VX_KEYS.KB_NUMPAD_0))   mz -= 1f;

            if (Key(VX_KEYS.KB_Space_Bar))     held |= Bit(GameButton.Fire);
            if (Key(VX_KEYS.KB_Enter))         held |= Bit(GameButton.Start);
            if (Key(VX_KEYS.KB_Escape))        held |= Bit(GameButton.Back);
            if (Key(VX_KEYS.KB_Arrow_Up))      held |= Bit(GameButton.MenuUp);
            if (Key(VX_KEYS.KB_Arrow_Down))    held |= Bit(GameButton.MenuDown);
            if (Key(VX_KEYS.KB_Arrow_Left))    held |= Bit(GameButton.MenuLeft);
            if (Key(VX_KEYS.KB_Arrow_Right))   held |= Bit(GameButton.MenuRight);

            // ── Controller 0 (only when game input is active) ──────────────────
            // GameInputActive is false while a text box is focused, so controller
            // input is confined to the simulator/game and never reaches the UI.
            if (NativeInput.GameInputActive)
            {
                try
                {
                    if (ledWin != null && ledWin.GetJoyCount() > 0)
                    {
                        mx += Dz(ledWin.GetJoyAxisValue(0, VX_JOY_AXIS_CODES.JOY_AXIS_LEFT_STICK_X));
                        // Stick up reads positive; "up" is −Y in world, so subtract.
                        my -= Dz(ledWin.GetJoyAxisValue(0, VX_JOY_AXIS_CODES.JOY_AXIS_LEFT_STICK_Y));
                        lx  = Dz(ledWin.GetJoyAxisValue(0, VX_JOY_AXIS_CODES.JOY_AXIS_RIGHT_STICK_X));
                        ly  = Dz(ledWin.GetJoyAxisValue(0, VX_JOY_AXIS_CODES.JOY_AXIS_RIGHT_STICK_Y));
                        mz += ledWin.GetJoyTriggerValue(0, VX_JOY_TRIGGER_CODES.JOY_RIGHT_TRIGGER)
                            - ledWin.GetJoyTriggerValue(0, VX_JOY_TRIGGER_CODES.JOY_LEFT_TRIGGER);

                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_A,              GameButton.Fire);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_B,              GameButton.Secondary);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_RIGHT_SHOULDER, GameButton.Up);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_LEFT_SHOULDER,  GameButton.Down);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_START,          GameButton.Start);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_BACK,           GameButton.Back);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_DPAD_UP,        GameButton.MenuUp);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_DPAD_DOWN,      GameButton.MenuDown);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_DPAD_LEFT,      GameButton.MenuLeft);
                        held |= Joy(ledWin, VX_JOY_BUTTON_CODES.JOY_DPAD_RIGHT,     GameButton.MenuRight);
                    }
                }
                catch { /* joystick API unavailable — keyboard only */ }
            }

            mx = Math.Clamp(mx, -1f, 1f);
            my = Math.Clamp(my, -1f, 1f);
            mz = Math.Clamp(mz, -1f, 1f);

            uint pressed = held & ~_prevHeld;
            _prevHeld = held;
            return new InputState(mx, my, mz, lx, ly, held, pressed, PollNav(ledWin));
        }

        // ── SpaceNavigator (SpaceMouse) ────────────────────────────────────────
        // LedWin only fills the nav struct once it has been told to track at least
        // one device, so SetNavCount(1) is issued once on the first poll. Everything
        // is wrapped in try/catch: a build or machine without the nav API must still
        // run on keyboard alone.
        private bool _navInit;

        private NavState PollNav(LedWinCS ledWin)
        {
            if (ledWin == null) return default;
            try
            {
                // LedWin only fills its nav slots once it has been told to track at
                // least one device. Ask once; GetNavCount then reports what it found.
                if (!_navInit) { ledWin.SetNavCount(1); _navInit = true; }

                int devices = ledWin.GetNavCount();

                float dx = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_X_AXIS_DIRECTION);
                float dy = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_Y_AXIS_DIRECTION);
                float dz = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_Z_AXIS_DIRECTION);
                float ax = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_PITCH_AXIS_ANGLE);
                float ay = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_YAW_AXIS_ANGLE);
                float az = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_ROLL_AXIS_ANGLE);
                float sx = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_X_AXIS_SUMMED);
                float sy = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_Y_AXIS_SUMMED);
                float sz = ledWin.GetNavAxisValue(0, VX_NAV_AXIS_CODES.NAV_Z_AXIS_SUMMED);
                int   bt = ledWin.GetNavButtonState(0);

                // Present when the SDK claims a device OR when any axis is alive: on
                // some builds GetNavCount stays 0 while the axes still report motion,
                // and refusing input in that case is worse than trusting the data.
                bool live = devices > 0 ||
                            MathF.Abs(dx) + MathF.Abs(dy) + MathF.Abs(dz) +
                            MathF.Abs(ax) + MathF.Abs(ay) + MathF.Abs(az) > 1e-4f || bt != 0;

                return new NavState(live, devices, dx, dy, dz, ax, ay, az, sx, sy, sz, bt);
            }
            catch { return default; }   // nav API unavailable — keyboard/mouse only
        }

        /// <summary>Rumble controller 0 (0..1 per motor). Safe if no controller.</summary>
        public void SetVibration(LedWinCS ledWin, float left, float right)
        {
            try { if (ledWin != null && ledWin.GetJoyCount() > 0) ledWin.SetJoyVibration(0, left, right); }
            catch { }
        }

        private static bool Key(VX_KEYS k) => NativeInput.IsDown(k) == 1;
        private static uint Bit(GameButton b) => 1u << (int)b;
        private static uint Joy(LedWinCS w, VX_JOY_BUTTON_CODES code, GameButton b)
            => w.GetJoyButtonIsDown(0, code) == 1 ? Bit(b) : 0u;
        private static float Dz(float v) => MathF.Abs(v) < DEAD ? 0f : v;
    }
}
