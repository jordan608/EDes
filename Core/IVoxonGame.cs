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

    /// <summary>One row of the legend the shell overlays on the preview: a swatch and
    /// what it means. Colour is packed 0xRRGGBB, matching everything else drawn.</summary>
    public readonly struct LegendRow
    {
        public readonly string Label;
        public readonly int    Colour;
        /// <summary>Loaded but not currently drawn — shown greyed so it is clear the
        /// layer EXISTS and is hidden, rather than simply missing.</summary>
        public readonly bool   Hidden;

        /// <summary>Stable identifier the shell hands back when the row is toggled or
        /// recoloured. A KEY rather than the row's index on purpose: an import can
        /// replace the whole layer list between the snapshot the shell is showing and the
        /// click it sends back, and an index would then land on a different layer.</summary>
        public readonly string Key;

        /// <summary>Whether this row offers a checkbox / a colour swatch. A row that
        /// stands for a derived count rather than a drawable thing offers neither.</summary>
        public readonly bool CanToggle;
        public readonly bool CanRecolour;

        public LegendRow(string label, int colour, bool hidden = false, string key = "",
                         bool canToggle = false, bool canRecolour = false)
        {
            Label = label; Colour = colour; Hidden = hidden;
            Key = key; CanToggle = canToggle; CanRecolour = canRecolour;
        }
    }

    /// <summary>One entry in the shell's pick list: something in the scene that can be
    /// selected by name instead of hunted for with a pointer.</summary>
    public readonly struct PickRow
    {
        public readonly string Group;    // "Nets", "Components" — the shell groups by this
        public readonly string Label;
        public readonly string Detail;   // shown dimmed beside the label
        public readonly string Key;      // handed back on click
        public readonly int    Colour;

        public PickRow(string group, string label, string detail, string key, int colour)
        {
            Group = group; Label = label; Detail = detail; Key = key; Colour = colour;
        }
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

        /// <summary>What is currently on screen and in what colour, for the overlay in
        /// the preview's corner. Empty (the default) means no legend is drawn.
        ///
        /// MUST return an immutable snapshot: the shell reads this from the UI thread
        /// while the game thread is free to rebuild it, so handing back a live list
        /// would tear or throw mid-enumeration. Swap a fresh array in instead.</summary>
        IReadOnlyList<LegendRow> Legend => Array.Empty<LegendRow>();

        /// <summary>Key/control reference, shown permanently by the shell rather than
        /// hidden behind a panel section — a control list you have to go looking for is one
        /// you do not read. Empty for none.</summary>
        string ControlsHelp => "";

        /// <summary>Free text the shell overlays on the top-left of the preview, or empty
        /// for none. Preformatted by the game: the first line is used as a heading.
        ///
        /// Goes through the contract rather than the shell reading the game's own settings,
        /// so the shell stays independent of any particular game's state object — the same
        /// reason Legend does.</summary>
        string StatusOverlay => "";

        /// <summary>Everything selectable by name, for the shell's pick list. Empty means
        /// no list is drawn. MUST be an immutable snapshot for the same reason Legend is:
        /// the shell reads it on the UI thread while the game thread rebuilds.</summary>
        IReadOnlyList<PickRow> PickList => Array.Empty<PickRow>();

        /// <summary>Select the thing a pick row stands for, or clear with an empty key.
        /// Called on the UI thread.</summary>
        void Pick(string key) { }

        /// <summary>Key of the current pick, so the shell can show which row is active.</summary>
        string PickedKey => "";

        /// <summary>Show or hide what a legend row stands for. Called on the UI thread;
        /// implementations must be safe against a concurrent import.</summary>
        void SetLegendVisible(string key, bool visible) { }

        /// <summary>Recolour what a legend row stands for (packed 0xRRGGBB).</summary>
        void SetLegendColour(string key, int colour) { }
    }
}
