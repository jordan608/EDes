# EDes

A starter template for building games and apps on the **Voxon volumetric display** (VX2 / VX2XL),
with a live **Avalonia** simulator/settings window so you can develop without hardware attached.

It loads a 3D model, voxelizes it, lights it, and draws it into the display volume — all
configurable from a tabbed settings UI with a real-time preview.

---

## Requirements

- **Windows** (uses the Voxon SDK, `System.Drawing`, and DirectX 12 for GPU lighting)
- **.NET 9 SDK**
- Voxon SDK DLLs (`LedHost.dll`, `LedWin.dll`) discoverable by the app — the simulator runs
  without hardware; a physical VX2/VX2XL is detected automatically.
- A DirectX 12 GPU is optional (GPU lighting falls back to CPU if absent).

## Build & run

```sh
dotnet build
dotnet run
```

> If you have the app open while rebuilding, the build may fail to overwrite the `.exe`
> (file lock). Just close the running app first.

---

## Drop-in assets

Place these **next to the executable** (`bin/Debug/net9.0-windows/`) — all are optional:

| File          | Purpose                                                        |
|---------------|---------------------------------------------------------------|
| `*.glb`       | The 3D model to display (first one alphabetically is used).   |
| `music.mp3`   | Looping background music (`.wav`/`.aiff`/`.ogg` also work).   |
| `fire.wav`    | Sound effect played on the fire action.                       |

If no `.glb` is present, a colorful fallback sphere is shown so you can still test lighting.

None of these are required to hear sound, either — see **Audio without assets** below.

---

## Audio without assets

`AudioManager.PlaySfx`/`PlaySfxLoop` never just go silent because a sound file hasn't been
sourced yet. If the named file (e.g. `fire.wav`) isn't next to the exe, they render a procedural
sound instead, from the recipe catalog in `Core/SynthAudio.cs` (`SynthPresets.Map`). Six example
presets ship (`fire.wav`, `hit.wav`, `boom.wav`, `pickup.wav`, `blip.wav`, `engine.wav`) — edit or
add to that dictionary for your own game. Drop a real file with the same name next to the exe at
any point and it takes over automatically; nothing else needs to change.

```csharp
_audio.PlaySfx(path, volume);              // one-shot — synth fallback if path doesn't exist
_audio.PlaySfxLoop("engine.wav", volume);  // held loop — synth-only (Sustained presets)
_audio.StopSfxLoop("engine.wav");          // fades out and stops
_audio.UpdateLoops(dt);                    // call once/frame to advance loop fades
```

---

## 2D sprite animations as voxel billboards

Drop a folder of numbered PNG frames under `Assets/Sprites/<name>/` (e.g.
`Assets/Sprites/Explosion/frame01.png`, `frame02.png`, …) and play it back on 1–5 billboard
planes revolved through the volume — see `Core/SpriteBurstRenderer.cs` for the full billboard-mode
and hue-shift-retint explanation.

```csharp
// ctx.Sprites in Init(); call Update(dt) and Draw(ledHost, ref vs) once per frame (the
// engine already does both for you — see GameLoop.cs).
_sprites.Spawn(x, y, z, "Explosion", size: 0.5f, life: 0.4f,
                primaryColor: 0x00FFCC, secondaryColor: 0xFFFFFF,
                billboard: SpriteBillboardMode.Pair45);
```

---

## Controls

| Input                         | Action                                  |
|-------------------------------|-----------------------------------------|
| Arrow keys / WASD             | Move the model in X/Y                   |
| Left Shift / NumPad 0         | Move up / down (Z)                      |
| Spacebar / controller **A**   | Fire (particle burst + flash light)     |
| Right-mouse drag (on preview) | Rotate the model (yaw / pitch)          |
| Mouse wheel (on preview)      | Scale the model                         |
| `[` / `]`                     | Rotate the simulator camera             |
| Ctrl + `[` / `]`              | Zoom the simulator camera               |
| Escape / controller **Back**  | Quit                                    |

An Xbox/Voxon controller works too (left stick = move, triggers = up/down, A = fire).
**Click the preview** to give it keyboard focus; clicking a settings box pauses game input
so typing never moves the model.

---

## Settings tabs

- **Simulator** — gamma, dithering, debug border, **voxel density** (re-meshes the model),
  30-VPS frame cap, and the simulator camera.
- **Lighting** — global ambient/brightness, **CPU/GPU toggle**, dark-color boost, black-voxel cull,
  a directional "sun", and 4 configurable spotlights (position, radius, intensity, RGB).
- **Game** — provided by the active game (the demo: player speed, particle budget, audio
  volumes, control reference).
- **Profiles** — player profiles and a top-10 high-score table.

Numeric fields accept only valid numbers and apply when you press **Enter** or click away.
Settings auto-save ~2 s after the last change.

### Where data is stored

- `%AppData%/EDes/settings.json` — app + lighting settings
- `%AppData%/EDes/players.json` — profiles + high scores
- `Desktop/edes_crash.log` — diagnostics / crash log

---

## Making it your own game

The engine (window, game loop, lighting, audio, input, diagnostics, preview, and the
Simulator/Lighting/Profiles tabs) is reusable. A **game** is just a class implementing
`IVoxonGame` — the demo is `Core/DemoGame.cs` (a model viewer with movement, particles, lighting).

To build your own game:

1. Copy `Core/DemoGame.cs` and implement `IVoxonGame`:
   - `Manifest` — title, version, accent colour, optional logo/splash art (shown on the splash screen).
   - `Init(ctx)` — receive engine services (lighting, audio, particles, players, settings); load assets.
   - `Update(in InputState, dt)` and `Draw(ledHost, vs)` — your game each frame.
   - `BuildSettingsPanel(ui)` — your settings tab, built with the shared `PanelBuilder`.
   - `Settings` — your own settings object (see `DemoGameSettings`), persisted to `game.json`.
2. In `App.axaml.cs`, change `new DemoGame(Settings)` to your game.

Everything generic — window, loop, lighting, audio, input, diagnostics, profiles, preview,
branding, splash — is inherited. Record scores with `App.Players.SubmitScore(...)`.

See `CLAUDE.md` for the full architecture, threading rules, and a step-by-step "Building a new game".

---

## Known limitations / TODO

- No in-volume HUD / menus / game-state machine yet.
- No asset-folder conventions / GLB hot-reload yet (assets load from beside the exe).
- GLB textures are decoded via GDI+ (PNG/JPG); exotic formats (KTX2/basis) fall back to a gradient.
- A few things are only verifiable on hardware: GPU-vs-CPU lighting parity, controller axis
  directions, and texture vertical (V) orientation.
