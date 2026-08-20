# Getting Started with EDes

A 15-minute path from clone to your own game running on the Voxon volumetric display.
For the full architecture and rules, see `README.md` (humans) and `CLAUDE.md` (AI agents).

---

## 1. Prerequisites (Windows)

- **.NET 9 SDK**
- **Voxon SDK installed** — provides `LedHost.dll` / `LedWin.dll`. These are *not* in the
  repo (proprietary), so a fresh clone won't run until the SDK is present.
- A DirectX 12 GPU is optional (GPU lighting falls back to CPU automatically).

## 2. Run the sample first

```sh
dotnet build
dotnet run
```

Confirm the demo works before changing anything — it separates "is my environment set up?"
from "is my code right?". You should see:

- a splash screen, then the settings window with a live preview;
- a model (drop a `*.glb` next to the exe) or a colourful fallback sphere, lit;
- working **Simulator / Lighting / Game / Profiles** tabs.

> Building fails to overwrite the exe while the app is open (file lock) — close it first.
> To type-check without touching `bin/`: `dotnet build -t:Compile`.

## 3. Make it your game

The engine is fixed; a game is one class. A ready-to-fill stub is already included:

1. Open **`Core/YourGame.cs`** (a minimal `IVoxonGame`) and **`Core/YourGameSettings.cs`**
   (its persisted settings). Fill in the `TODO`s:
   - **`Manifest`** — title, accent colour, optional `SplashPath`/`LogoPath` (images go next to the exe).
   - **`Init(ctx)`** — grab services (`ctx.Lighting`, `ctx.Audio`, `ctx.Particles`, `ctx.Players`,
     `ctx.Settings`) and load assets. Runs on the game thread *before* SDK init.
   - **`Update(in InputState, dt)`** — your logic. Read `input.MoveX/MoveY/MoveZ` and
     `input.IsDown/Pressed(GameButton.Fire)` etc. (keyboard + controller, unified).
   - **`Draw(ledHost, vs)`** — your voxels via `ledHost.DrawVox` / `DrawVox_Batch`. Lighting is
     already applied by the engine; shade with `ctx.Lighting.QueryColor(...)` or reuse
     `VoxelModelRenderer`.
   - **`BuildSettingsPanel(ui)`** — your settings tab, built with the shared `PanelBuilder`.
   - **`Settings`** — return your `YourGameSettings`; the engine persists it to `yourgame.json`.
2. **Switch the active game** — in `App.axaml.cs`, change one line:
   ```csharp
   Game = new YourGame(Settings);     // was: new DemoGame(Settings)
   ```
3. Run. Your title/accent/splash, your tab, and your `Draw` are now live — everything else is inherited.

`Core/DemoGame.cs` is the complete worked example (model viewer with movement, particles,
fire SFX, lighting). Keep it as a reference until your game runs, then delete it (and its
`DemoGameSettings`).

## 4. Don't edit the engine

Leave these alone unless you know why: `GameLoop.cs`, `Program.cs`, the MainWindow shell,
`LedHostCS`/`LedWinCS`/`VoxonTypes` (SDK wrappers), `LightingSystem`, `GpuLighting`.
`CLAUDE.md` lists the load-bearing invariants (SDK-calls-on-game-thread, the stack-corruption
defences, the settings threading model) — breaking them causes subtle, hard-to-debug failures.

## 5. Handy APIs

| Need | Use |
|------|-----|
| Move/aim/buttons | `InputState` (`MoveX/Y/Z`, `IsDown/Pressed(GameButton.*)`) |
| Draw many voxels fast | `ledHost.DrawVox_Batch` (one native call; colours are packed `0xRRGGBB`) |
| Shade a voxel | `ctx.Lighting.QueryColor(x,y,z, nx,ny,nz, baseColor)` |
| Display bounds | `DisplayVolume.HalfXY / HalfZ / GameHalfXY / TopZ` |
| Play a sound | `ctx.Audio.PlaySfx(path, volume)` (drop `fire.wav` / `music.mp3` next to the exe) |
| Record a score | `App.Players.SubmitScore(score)` |
| Logging | `App.Log("...")` → desktop crash log |

## 6. Where data lives

- `%AppData%/EDes/settings.json` — engine settings
- `%AppData%/EDes/game.json` / `yourgame.json` — your game's settings
- `%AppData%/EDes/players.json` — profiles + high scores
- `Desktop/edes_crash.log` — diagnostics / crash log
