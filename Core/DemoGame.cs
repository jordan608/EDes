// ═══════════════════════════════════════════════════════════════════════════
//  DemoGame.cs — The example game (a lit voxel-model viewer)
//
//  A drop-in IVoxonGame: the engine constructs it, hands it services via
//  Init(ctx), drives Update/Draw, renders its settings tab, and persists its
//  Settings. Copy this class as the starting point for your own game.
//
//  What this demo does:
//    • Loads a *.glb (or fallback sphere) and draws it lit + batched.
//    • Move the model in X/Y/Z (arrow keys / WASD / Shift / NumPad0 / controller).
//    • Fire (Space / controller A) spawns a particle burst + a transient light.
//    • Right-drag rotates the model; mouse wheel scales it (handled in the UI).
//
//  Coordinate system:
//    X = left / right   (−HalfXY to +HalfXY)
//    Y = near / far      (−HalfXY to +HalfXY)
//    Z = down / up       (≈ −0.5 to +HalfZ)
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Voxon;
using EDes.UI;

namespace EDes
{
    public class DemoGame : IVoxonGame
    {
        // ── Dependencies ──────────────────────────────────────────────────────
        // _settings (engine) is given at construction; engine services arrive in
        // Init(ctx). _demo holds this game's own persisted settings.
        private readonly GameSettings     _settings;
        private readonly DemoGameSettings _demo;
        private LightingSystem      _lighting  = null!;
        private ParticleManager     _particles = null!;
        private AudioManager        _audio     = null!;
        private SpriteBurstRenderer _sprites   = null!;
        private readonly Random  _rand = new Random();

        // ── Player state ──────────────────────────────────────────────────────
        private float _px, _py, _pz;

        // Shoot cooldown — prevents holding Fire from firing every frame
        private float _shootCooldown = 0f;
        private const float SHOOT_RATE = 0.25f;   // seconds between shots

        // ── Voxel model rendering (created in Init) ───────────────────────────
        private VoxelModelRenderer _renderer = null!;

        private float _totalTime = 0f;

        // ── IVoxonGame ────────────────────────────────────────────────────────

        public GameManifest Manifest { get; } = new GameManifest
        {
            Title   = "Voxon Model Viewer",
            Version = "1.0",
            Accent  = 0xFF00CCFF,
            // SplashPath = "splash.png",   // drop one next to the exe to brand the splash
        };

        public object? Settings => _demo;   // engine persists this (game.json)

        public DemoGame(GameSettings settings)
        {
            _settings = settings;
            _demo     = DemoGameSettings.Load();
        }

        // Receive engine services and load the model. Runs on the game thread
        // during setup (before SDK init) — heavy asset work belongs here.
        public void Init(GameContext ctx)
        {
            _lighting  = ctx.Lighting;
            _particles = ctx.Particles;
            _audio     = ctx.Audio;
            _sprites   = ctx.Sprites;
            _renderer  = new VoxelModelRenderer(VoxelModel.GridForDensity(_settings.VoxelDensity));
            _settings.ModelSource = _renderer.Source;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Update — input + physics, called each frame before Draw
        // ═════════════════════════════════════════════════════════════════════

        public void Update(in InputState input, float dt)
        {
            _totalTime += dt;
            _shootCooldown = Math.Max(0f, _shootCooldown - dt);

            // Keep music volume in sync with the settings UI.
            _audio.Update(_settings.MusicVolume);
            _audio.UpdateLoops(dt);

            // Procedural "engine hum" loop while moving — demonstrates PlaySfxLoop/
            // StopSfxLoop (see SynthAudio.cs). No real engine.wav needed; a synth
            // preset renders it. Drop a real engine.wav next to the exe to replace it.
            bool moving = input.MoveX != 0f || input.MoveY != 0f || input.MoveZ != 0f;
            if (moving) _audio.PlaySfxLoop("engine.wav", _settings.SfxVolume * 0.5f);
            else        _audio.StopSfxLoop("engine.wav");

            // ── Movement (keyboard or controller, via unified InputState) ─────
            float speed = _demo.PlayerSpeed;
            float hxy   = DisplayVolume.GameHalfXY;
            float topZ  = DisplayVolume.TopZ;
            float botZ  = -0.45f;

            _px += input.MoveX * speed * dt;
            _py += input.MoveY * speed * dt;
            _pz += input.MoveZ * speed * 0.5f * dt;

            _px = Math.Clamp(_px, -hxy, hxy);
            _py = Math.Clamp(_py, -hxy, hxy);
            _pz = Math.Clamp(_pz, botZ, topZ);

            // ── Fire (Fire button — Space / controller A) ─────────────────────
            if (input.IsDown(GameButton.Fire) && _shootCooldown <= 0f)
            {
                _shootCooldown = SHOOT_RATE;
                OnFire();
            }

            _particles.Update(dt);
        }

        // ── OnFire — spawn particles and a transient light ────────────────────
        private void OnFire()
        {
            _particles.SpawnBurst(
                _px, _py, _pz,
                minSpeed: 1.5f, maxSpeed: 4.0f,
                minLife:  0.3f, maxLife:  0.8f,
                colorA: 0x00FFCC, colorB: 0xFFFFFF,
                count: 30, rand: _rand);

            // 2D sprite-animation burst, if any PNG-frame folder exists under
            // Assets/Sprites (see SpriteBurstRenderer.cs) — no-ops otherwise
            // (SpawnByIndex(-1, ...) is a deliberate no-op). Drop a folder of
            // numbered frames there (e.g. Assets/Sprites/Explosion/frame01.png...)
            // to see it play on fire.
            if (SpriteLibrary.Count > 0)
                _sprites.SpawnByIndex(_px, _py, _pz, spriteIndex: 0,
                    size: 0.5f, life: 0.4f, primaryColor: 0x00FFCC, secondaryColor: 0xFFFFFF);

            _lighting.AddTransientLight(
                _px, _py, _pz,
                color:     0xFFFFCC,
                intensity: 2.5f,
                duration:  0.15f,
                radius:    3.0f);

            // Fire sound effect (no-op if fire.wav isn't present next to the exe)
            _audio.PlaySfx(_audio.FireSfxPath, _settings.SfxVolume);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Draw — all voxel rendering, called each frame after FrameStart
        // (Lighting is configured + begun by the engine before this runs.)
        // ═════════════════════════════════════════════════════════════════════

        public void Draw(LedHostCS ledHost, ref vxl_state_t vs)
        {
            DrawModel(ledHost, ref vs);
            // Particles drawn after lighting, unlit (they glow).
            _particles.Draw(ledHost, ref vs, _demo.ParticleBudget);

            // HUD text on a fixed-depth plane (y=0.1) — see VoxelFont.cs. Position/size
            // are in world units; text extends +x (right) and +z (down) from `pos`.
            VoxelFont.Draw(ledHost, ref vs, HudFont.Bold,
                new point3d(-DisplayVolume.HalfXY * 0.9f, 0.1f, DisplayVolume.TopZ - 0.15f),
                size: 0.12f, col: 0x00CCFF, "VOXON STARTER");
        }

        // Delegates the lit, transformed, batched model draw to the renderer.
        private void DrawModel(LedHostCS ledHost, ref vxl_state_t vs)
        {
            var lcfg = _settings.Lighting;
            int drawn = _renderer.Draw(ledHost, ref vs, _lighting,
                VoxelModel.GridForDensity(_settings.VoxelDensity),
                _px, _py, _pz,
                _settings.ModelYaw, _settings.ModelPitch, _settings.ModelScale,
                lcfg.UseGpu, lcfg.CullBlack, lcfg.BoostDark, lcfg.BoostStrength);

            _settings.LiveVoxelCount = drawn;
            _settings.ModelSource    = _renderer.Source;
        }

        // ── BuildSettingsPanel — the game's settings tab ──────────────────────
        // Built on the UI thread with the shared PanelBuilder. Player/particle
        // settings are the game's own (_demo); audio volumes are engine settings.
        public Control BuildSettingsPanel(PanelBuilder ui)
        {
            var stack = ui.Root();
            var group = new List<Expander>();

            var player = ui.AddSection(stack, "Player", group, expanded: true);
            ui.AddSlider(player, "Player speed", 0.5, 10.0, _demo.PlayerSpeed,
                         v => _demo.PlayerSpeed = (float)v, "F1");

            var parts = ui.AddSection(stack, "Particles", group);
            ui.AddSlider(parts, "Particle budget", 50, 2000, _demo.ParticleBudget,
                         v => _demo.ParticleBudget = (int)v, "F0");

            var audio = ui.AddSection(stack, "Audio", group);
            ui.AddInfo(audio, "Drop music.mp3 / fire.wav next to the exe to enable sound.");
            ui.AddSlider(audio, "SFX volume",   0, 1.0, _settings.SfxVolume,
                         v => _settings.SfxVolume = (float)v, "F2");
            ui.AddSlider(audio, "Music volume", 0, 1.0, _settings.MusicVolume,
                         v => _settings.MusicVolume = (float)v, "F2");

            var ctrls = ui.AddSection(stack, "Controls", group);
            ui.AddInfo(ctrls, "Arrow keys / WASD   — move");
            ui.AddInfo(ctrls, "Shift               — move up");
            ui.AddInfo(ctrls, "Numpad 0            — move down");
            ui.AddInfo(ctrls, "Spacebar / (A)      — fire burst");
            ui.AddInfo(ctrls, "Escape / (Back)     — quit");
            ui.AddInfo(ctrls, "[ / ]               — rotate camera");
            ui.AddInfo(ctrls, "Right-drag rotate · wheel scale (model)");

            return ui.Wrap(stack);
        }

        // Audio is engine-owned (disposed by GameLoop); the renderer is ours.
        public void Dispose() => _renderer.Dispose();
    }
}
