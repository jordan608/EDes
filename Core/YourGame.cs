// ═══════════════════════════════════════════════════════════════════════════
//  YourGame.cs — STARTER STUB for your own game
//
//  This is a minimal IVoxonGame you fill in. To make it the active game, change
//  one line in App.axaml.cs:
//
//      Game = new YourGame(Settings);     // instead of new DemoGame(Settings)
//
//  Everything generic — window, game loop, lighting, audio, input, diagnostics,
//  profiles, preview, branding, splash — is provided by the engine. You only
//  implement the methods below. See DemoGame.cs for a complete worked example,
//  and CLAUDE.md → "Building a new game".
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Voxon;
using EDes.UI;

namespace EDes
{
    public class YourGame : IVoxonGame
    {
        private readonly YourGameSettings _state;   // your persisted settings (yourgame.json)
        private GameContext _ctx = null!;           // engine services (set in Init)
        private float _t;

        // Branding: shown in the title bar, the nav accent, and the splash screen.
        public GameManifest Manifest { get; } = new GameManifest
        {
            Title   = "EDes",
            Version = "0.1",
            Accent  = 0xFF00CCFF,
            // SplashPath = "splash.png",   // drop a PNG next to the exe to brand the splash
        };

        // The engine persists this for you whenever settings change.
        public object? Settings => _state;

        // Keep the GameSettings param if you need engine settings (gamma, density,
        // camera, audio volumes, etc.) — store it like DemoGame does.
        public YourGame(GameSettings settings)
        {
            _state = YourGameSettings.Load();
        }

        // Runs on the game thread during setup (before SDK init). Load assets here.
        public void Init(GameContext ctx)
        {
            _ctx = ctx;
            // TODO: load assets, e.g.:
            //   _renderer = new VoxelModelRenderer(VoxelModel.GridForDensity(ctx.Settings.VoxelDensity));
            // Available services: ctx.Lighting, ctx.Audio, ctx.Particles, ctx.Players, ctx.Settings
        }

        // Your per-frame logic. Movement and buttons come from the unified InputState
        // (keyboard + controller): input.MoveX/MoveY/MoveZ, input.IsDown/Pressed(GameButton.Fire), …
        public void Update(in InputState input, float dt)
        {
            _t += dt;
            // TODO: move things, spawn things, handle input.
        }

        // Draw your voxels. Lighting is already configured by the engine — shade
        // with _ctx.Lighting.QueryColor(...) or reuse a VoxelModelRenderer.
        public void Draw(LedHostCS ledHost, ref vxl_state_t vs)
        {
            // Placeholder: one lit voxel bobbing at the centre so you see life.
            // Replace with your scene.
            float z = 0.5f * MathF.Sin(_t * 2f);
            int col = _ctx.Lighting.QueryColor(0f, 0f, z, 0f, 0f, 1f, 0xFFFFFF);
            ledHost.DrawVox(ref vs, 0f, 0f, z, col);
        }

        // Build your settings tab with the shared PanelBuilder (matches engine tabs).
        public Control BuildSettingsPanel(PanelBuilder ui)
        {
            var stack = ui.Root();
            var group = new List<Expander>();

            var sec = ui.AddSection(stack, "Your Game", group, expanded: true);
            ui.AddInfo(sec, "Replace this with your game's settings.");
            ui.AddSlider(sec, "Example value", 0, 100, _state.ExampleValue,
                         v => _state.ExampleValue = (float)v, "F0");

            return ui.Wrap(stack);
        }

        public void Dispose()
        {
            // TODO: dispose anything you created in Init (e.g. a VoxelModelRenderer).
        }
    }
}
