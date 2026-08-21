// ═══════════════════════════════════════════════════════════════════════════
//  IVoxonGame.cs — The engine ↔ game contract
//
//  The engine (window, game loop, audio, input, diagnostics, preview, and the
//  Display settings panel) is constant. A game is a class implementing
//  IVoxonGame: it brings its own logic, settings panel, and branding, and reaches
//  engine services through GameContext.
//
//  Lifecycle / threading:
//    • ctor              — cheap, on the bootstrap thread. NO SDK calls here.
//    • Init(ctx)         — game thread, during engine setup BEFORE the SDK is
//                          initialised (the SDK init can break later object
//                          construction — see GameLoop note 3). Load assets here.
//    • Update / Draw     — game thread, every frame.
//    • BuildSettingsPanel— UI thread (builds the game's Avalonia settings tab).
//    • Settings          — read by both UI and game threads → use the same
//                          volatile / atomic-reference-swap discipline as
//                          GameSettings (see GameSettings.cs).
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using EDes.UI;
using Voxon;

namespace EDes
{
    /// <summary>Per-game branding shown by the shell (title bar, accent, splash).</summary>
    public sealed class GameManifest
    {
        public string  Title      { get; init; } = "EDes";
        public string  Version    { get; init; } = "1.0";
        public string? LogoPath   { get; init; }            // PNG next to the exe
        public string? SplashPath { get; init; }            // splash art next to the exe
        public uint    Accent     { get; init; } = 0xFF00CCFF;   // 0xAARRGGBB UI accent
    }

    /// <summary>Engine services handed to the game in Init().</summary>
    public sealed class GameContext
    {
        public required GameSettings        Settings  { get; init; }
        public required LightingSystem      Lighting  { get; init; }
        public required AudioManager        Audio     { get; init; }
        public required ParticleManager     Particles { get; init; }
        public required SpriteBurstRenderer Sprites   { get; init; }
        public required PlayerStore         Players   { get; init; }
    }

    /// <summary>A drop-in game/app module driven by the engine.</summary>
    public interface IVoxonGame : IDisposable
    {
        GameManifest Manifest { get; }

        void Init(GameContext ctx);
        void Update(in InputState input, float dt);
        void Draw(LedHostCS ledHost, ref vxl_state_t vs);

        /// <summary>Build the game's settings tab. Use the supplied PanelBuilder
        /// so it matches the engine tabs. Return the panel content control.</summary>
        Control BuildSettingsPanel(PanelBuilder ui);

        /// <summary>Serializable settings object (or null). Persisted by the engine.</summary>
        object? Settings { get; }

        /// <summary>Top-level modes the shell offers as header buttons across the top
        /// of the window. Empty (the default) means the game has no modes and the shell
        /// draws no headers, so games that predate this need no changes.</summary>
        IReadOnlyList<string> Modes => Array.Empty<string>();

        /// <summary>Index into <see cref="Modes"/>. The shell sets this when a header is
        /// clicked and then rebuilds the game's settings panel, which is what lets a
        /// game show only the settings belonging to the mode on screen.</summary>
        int ActiveMode { get => 0; set { } }
    }
}
