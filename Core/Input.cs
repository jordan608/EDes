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

    // Immutable per-frame snapshot handed to the game.
    public readonly struct InputState
    {
        public readonly float MoveX, MoveY, MoveZ;   // primary movement (−1..1)
        public readonly float LookX, LookY;          // right stick (−1..1)
        private readonly uint _held;
        private readonly uint _pressed;

        public InputState(float mx, float my, float mz, float lx, float ly,
                          uint held, uint pressed)
        {
            MoveX = mx; MoveY = my; MoveZ = mz; LookX = lx; LookY = ly;
            _held = held; _pressed = pressed;
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
            return new InputState(mx, my, mz, lx, ly, held, pressed);
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
