// ═══════════════════════════════════════════════════════════════════════════
//  NativeInput.cs — Keyboard input via Win32 GetAsyncKeyState
//
//  WHY: ledWin.GetKeyIsDown() only works when the LedWin HWND has OS focus.
//  We hide the LedWin window offscreen (−32000, −32000, 1×1) so it never
//  gets focus and never receives WM_KEY messages. GetAsyncKeyState bypasses
//  the message queue and reads hardware state directly — works regardless
//  of which window has focus.
//
//  USAGE (each frame in the game loop):
//    1. NativeInput.Poll();                     ← call ONCE at top of frame
//    2. NativeInput.IsDown(VX_KEYS.KB_A)        ← held this frame (0 or 1)
//       NativeInput.OnDown(VX_KEYS.KB_Space_Bar) ← just pressed (0 or 1)
//
//  Both methods return int (0 or 1) to match the ledWin.GetKey* signatures
//  so they are drop-in replacements.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Voxon;

namespace EDes
{
    public static class NativeInput
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // ── VX_KEYS (PS/2 scan codes) → Win32 Virtual Key codes ──────────────
        // Add any keys you need. The full table is in the Voxon SDK docs.
        private static readonly Dictionary<int, int> _scanToVk = new()
        {
            // Arrow keys
            [(int)VX_KEYS.KB_Arrow_Up]    = 0x26,
            [(int)VX_KEYS.KB_Arrow_Down]  = 0x28,
            [(int)VX_KEYS.KB_Arrow_Left]  = 0x25,
            [(int)VX_KEYS.KB_Arrow_Right] = 0x27,
            // WASD
            [(int)VX_KEYS.KB_W] = 0x57,
            [(int)VX_KEYS.KB_A] = 0x41,
            [(int)VX_KEYS.KB_S] = 0x53,
            [(int)VX_KEYS.KB_D] = 0x44,
            // Action keys
            [(int)VX_KEYS.KB_Space_Bar]  = 0x20,
            [(int)VX_KEYS.KB_Enter]      = 0x0D,
            [(int)VX_KEYS.KB_Escape]     = 0x1B,
            [(int)VX_KEYS.KB_Shift_Left] = 0xA0,
            // Digits
            [(int)VX_KEYS.KB_1] = 0x31, [(int)VX_KEYS.KB_2] = 0x32,
            [(int)VX_KEYS.KB_3] = 0x33, [(int)VX_KEYS.KB_4] = 0x34,
            // Simulator camera rotation
            [(int)VX_KEYS.KB_Square_Bracket_Open]  = 0xDB,  // [  rotates left
            [(int)VX_KEYS.KB_Square_Bracket_Close] = 0xDD,  // ]  rotates right
            // Numpad (useful for debug / dev tools)
            [(int)VX_KEYS.KB_NUMPAD_0]     = 0x60,
            [(int)VX_KEYS.KB_NUMPAD_4]     = 0x64,
            [(int)VX_KEYS.KB_NUMPAD_5]     = 0x65,
            [(int)VX_KEYS.KB_NUMPAD_6]     = 0x66,
            [(int)VX_KEYS.KB_NUMPAD_8]     = 0x68,
            [(int)VX_KEYS.KB_NUMPAD_2]     = 0x62,
            [(int)VX_KEYS.KB_NUMPAD_7]     = 0x67,
            [(int)VX_KEYS.KB_NUMPAD_9]     = 0x69,
            [(int)VX_KEYS.KB_NUMPAD_Plus]  = 0x6B,
            [(int)VX_KEYS.KB_NUMPAD_Minus] = 0x6D,
            [(int)VX_KEYS.KB_NUMPAD_Enter] = 0x0D,
            [(int)VX_KEYS.KB_NUMPAD_Decimal] = 0x6E,
        };

        private static readonly int[] _vkList;
        private static readonly bool[] _currDown = new bool[256];
        private static readonly bool[] _prevDown = new bool[256];

        /// <summary>
        /// Set to true when the user starts the game (e.g. clicks Play or presses Fire).
        /// When false all keys read as released — prevents game input during the
        /// settings / boot phase.
        /// </summary>
        public static bool InputEnabled    { get; set; } = false;

        /// <summary>Set false when the Avalonia window loses OS focus.</summary>
        public static bool WindowHasFocus  { get; set; } = true;

        /// <summary>
        /// Set true while a text-entry box has keyboard focus. Suspends ALL game
        /// input (keyboard + controller) so typed characters don't also drive the
        /// game (e.g. typing "wasd" into a box must not move the model).
        /// </summary>
        public static bool SuspendForTextEntry { get; set; } = false;

        /// <summary>True only when the game/simulator should consume input.</summary>
        public static bool GameInputActive =>
            InputEnabled && WindowHasFocus && !SuspendForTextEntry;

        static NativeInput()
        {
            var vkSet = new HashSet<int>(_scanToVk.Values);
            _vkList = new int[vkSet.Count];
            vkSet.CopyTo(_vkList, 0);
        }

        /// <summary>
        /// Snapshot the hardware key state. Call EXACTLY ONCE per game-loop frame,
        /// before any IsDown / OnDown queries.
        /// </summary>
        public static void Poll()
        {
            Array.Copy(_currDown, _prevDown, 256);
            if (!GameInputActive)
            {
                Array.Clear(_currDown, 0, 256);
                return;
            }
            foreach (int vk in _vkList)
                _currDown[vk] = (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>Returns 1 while key is held. Drop-in for ledWin.GetKeyIsDown().</summary>
        public static int IsDown(VX_KEYS key)
        {
            if (!_scanToVk.TryGetValue((int)key, out int vk)) return 0;
            return _currDown[vk] ? 1 : 0;
        }

        /// <summary>Returns 1 on the first frame a key is pressed. Drop-in for ledWin.GetKeyOnDown().</summary>
        public static int OnDown(VX_KEYS key)
        {
            if (!_scanToVk.TryGetValue((int)key, out int vk)) return 0;
            return (_currDown[vk] && !_prevDown[vk]) ? 1 : 0;
        }
    }
}
