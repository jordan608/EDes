# CLAUDE.md — EDes

Guidance for AI agents working in this repo. Read this before editing.

## What this is

**EDes - Electronics Design Explorer**: an electronics workbench for the **Voxon VX2 / VX2-XL
volumetric display**, built on the VoxonStarter engine. An **Avalonia** window provides a settings
UI + live 2D preview of the 3D volume. A background thread drives the **Voxon SDK**
(`LedHost.dll` / `LedWin.dll`) which renders voxels into the display (or a simulator when no
hardware is present).

The app (`Core/EDesApp.cs`, an `IVoxonGame`) has three modes, cycled with `Tab`:

| Mode | Contents |
|---|---|
| **Education** | 4 built-in circuits solved from Ohm law: heat-coloured/raised resistors, animated current flow, parallel branches in separate Y lanes, per-component R/V/I/P readouts, plus the scope strip. |
| **Oscilloscope** | Up to 4 channels from USB serial (or synthetic), software trigger, graticule, measurement row - on the `y = PlaneY` plane (default 0.1). |
| **PCB** | Imported Gerber + Excellon + meshes: layers spread along Z, drills bored through the stack, measurement cursor, fab/DRC-lite readout. |

`DemoGame`/`YourGame` remain as engine reference examples; they are not wired up.
See `README.md` (user-facing), `docs/PCB_IMPORT.md` and `docs/SCOPE_USB.md` (formats),
`docs/FUSION_BRIDGE.md` (the Fusion 360 bridge design) and **`docs/TESTING.md`** (how to
run all four suites, and the walkthrough for testing the Fusion bridge on real hardware).

## Build & verify

```sh
dotnet build                 # full build (produces the exe)
dotnet build -t:Compile      # compile ONLY — use this to type-check while the app is running
```

- **The exe is often locked** because the app is running → a full `dotnet build` fails at the
  copy step. Use `dotnet build -t:Compile` to validate code without touching `bin/`.
- The **ComputeSharp source generator** runs during compilation, so `-t:Compile` will catch
  shader errors too.
- Do **not** pass ad-hoc MSBuild output-path props on the command line — a past mishap mangled
  `obj/.../*.FileListAbsolute.txt` and broke incremental builds. If you see
  "given path's format is not supported", delete that file and rebuild.
- **Parser tests**: `dotnet run --project tests/PcbParserTests` (exit 0 = all checks pass). It
  compiles `Core/Pcb/{PcbBoard,GerberParser,ExcellonParser}.cs` directly, so it runs without the
  Avalonia/Voxon dependency chain and can never drift from the shipping files. `tests/**` is
  excluded from `EDes.csproj`. Run it after ANY change to a parser - the coordinate-format code is
  the part that silently corrupts boards.
- **Fusion bridge tests** (plain Python, no Fusion needed, no dependencies):
  `python fusion/tests/test_protocol.py` (the wire format + the cm/mm maths) and
  `python fusion/tests/test_addin.py` (the add-in RUN against a stub `adsk` that dispatches
  custom events on a separate thread, so the worker-to-main-thread marshalling is really
  exercised). Run both after ANY change under `fusion/` - and note
  `fusion/tests/golden_frame.bin` is a cross-language fixture: Python writes it, the C#
  suite parses it, so the two implementations of one format cannot drift. Regenerate it
  ONLY when the format changes on purpose (`python fusion/tests/make_golden.py`).
- Pre-existing warnings (~260) live in the SDK wrapper files (`VoxonTypes.cs`, `LedHostCS.cs`,
  `LedWinCS.cs`). New code should add none. Filter with `grep -iE ": error"`.

## Architecture / data flow

The engine is constant; the **game** is a drop-in `IVoxonGame` (the demo is `DemoGame`).

```
Program.cs (STAThread)
  └─ App.axaml.cs  → Settings (GameSettings), Players (PlayerStore), Game (IVoxonGame)
       ├─ MainWindow (UI thread): engine tabs + game's tab + preview Image + status bar + splash
       └─ Game thread (background STA, AboveNormal): GameLoop.RunLoopCore
            ├─ VoxonPreflight.Run(...)           (BEFORE any SDK call — see below)
            ├─ create engine services (LightingSystem, AudioManager, ParticleManager)
            ├─ game.Init(GameContext)            (BEFORE SDK init — loads assets)
            ├─ InputManager.Poll(ledWin) → InputState
            ├─ lighting.Update(dt)
            ├─ game.Update(in InputState, dt)
            ├─ lighting.ApplyConfig(settings.Lighting) + lighting.BeginFrame()
            ├─ game.Draw(ledHost, vs)
            │     └─ DemoGame: VoxelModelRenderer.Draw()
            │            shell-cull → CPU QueryColor or GpuLighting → DrawVox_Batch
            └─ Rend2D → preview buffer → settings.OnPreviewFrame → UI WriteableBitmap
```

### EDesApp own frame (inside game.Update / game.Draw)

```
Update: HandleKeys (per-mode)  → DriveCamera (keys + controller + SpaceNav)
        → Sync (settings → CircuitScene / ScopeSource / VoxelFont)
        → PCB import request?  → ScopeSource.Poll (open/close port, synth fill)
Draw:   ReadBounds (from the SDK, every frame)
        → VoxelBatch.BeginFrame(MaxVoxels, radius, zHalf, spacing)
        → mode content → HUD text → backdrop        (draw order = priority order)
        → VoxelBatch.Flush  = ONE DrawVox_Batch     → settings.LiveVoxelCount
```

Solve/layout sits behind a dirty flag in `CircuitScene`; the renderers only ever read the flat
`WireSegment` list. PCB import runs on the game thread (never the UI thread) when the UI sets
`EDesSettings.PcbImportRequested`.

## File map (`Core/` unless noted)

| File | Role |
|------|------|
| `Program.cs` (root) | Entry point, `[STAThread]`, crash log → `Desktop/edes_crash.log`, **software rendering** (avoids D3D conflict with the SDK). |
| `App.axaml.cs` (root) | Bootstrap. `App.Settings` (GameSettings), `App.Players` (PlayerStore), `App.Game` (IVoxonGame). Swap the `new DemoGame(...)` line to ship a different game. Wires `OnPreviewFrame`, starts the game thread. |
| `GameLoop.cs` | SDK lifecycle + main loop. Runs `VoxonPreflight` first, owns engine services, builds `GameContext`, calls `game.Init/Update/Draw`, drives lighting + `FrameProfiler`. 30-VPS cap, latency timing. **Read its header — stack-corruption defenses are load-bearing.** |
| `VoxonHardwareCheck.cs` | Self-contained (WMI only, no SDK dependency) pre-init USB device scan — confirms the expected Voxon boards are present BEFORE touching LedHost/LedWin. `Check()` once, or `WaitForHardware()` to poll with a timeout. |
| `VoxonPreflight.cs` | The boot-screen orchestration around `VoxonHardwareCheck`: polls with a live countdown, offers Retry/Continue-in-Simulator/Quit, auto-falls-through to Simulator on timeout. UI-agnostic (`IPreflightUi`) — bridged to the splash overlay via `GameSettings.Splash*` fields; see `MainWindow.axaml.cs`'s button handlers and `OnStatusTick`. |
| `FrameProfiler.cs` | Opt-in (press 'O' in-game) per-frame phase timer + voxel counter. Streams a CSV to `profiles/` and logs a 5s rolling average. `FrameProfiler.Phase` has `Custom0..Custom3` slots — rename/reuse for your own subsystems. Zero overhead when disabled. |
| `SpriteFrameSet.cs` | Loads a folder of PNG frames into voxel points (one per opaque/bright pixel, brightened, downsampled to a budget grid) + computes two reference hues (bright/dark halves) for retinting. |
| `SpriteLibrary.cs` | Discovers every PNG-frame folder under `Assets/Sprites` (lazy decode + cache per set). Pair with `SpriteBurstRenderer`. |
| `SpriteBurstRenderer.cs` | Engine service (`ctx.Sprites`): plays a sprite animation on 1–5 revolved billboard planes (`SpriteBillboardMode`) with a two-tone hue-shift retint toward any primary/secondary colour. `Spawn(x,y,z,name,size,life,primary,secondary,billboard)`. See its header for the billboard-plane math. |
| `ColorHsv.cs` | RGB↔HSV + `ShiftHue`/`ShiftHueBlend` (two-tone hue rotation by pixel brightness) — used by the sprite retint. |
| `VoxelFont.cs` | HUD text renderer, 3 fonts behind one `Draw()` call: `Classic` (SDK's built-in vector alphabet), `Blocky`/`Bold` (5×7 voxel bitmap, own glyph table, `Thickness` controls stroke density). Draws in world space at any position/size/colour — draw everything at a fixed Y (e.g. `y=0.1`) for one shared HUD plane. |
| `IVoxonGame.cs` | The engine↔game contract: `IVoxonGame`, `GameManifest` (title/logo/splash/accent), `GameContext` (engine services). |
| `DemoGame.cs` | The example game (`IVoxonGame`): model viewer with movement/fire/particles. Copy this to build a game. |
| `DemoGameSettings.cs` | The demo's own settings (`PlayerSpeed`, `ParticleBudget`) → `game.json`. `IGameSettings` (engine persists it on save). |
| `VoxelModelRenderer.cs` | Engine service: model load/voxelize/density/transform + two-pass shell-cull + CPU/GPU lit `DrawVox_Batch`. |
| `GameSettings.cs` | Shared engine UI↔game state + persistence (`settings.json`). Game-specific fields live in the game's own settings. |
| `IVoxonGame.Modes` / `ActiveMode` | Default interface members (empty / 0), so a game with no modes needs no changes. EDesApp implements them over `EDesSettings.Mode`; the shell draws one header per entry and rebuilds the game's panel when one is clicked. |
| `LightingConfig.cs` | Serializable lighting snapshot (global, sun, 4 spots, UseGpu, boost, cull). |
| `LightingSystem.cs` | CPU N·L shading, ambient/brightness, scene lights + sun + transient pool, shell culling, `QueryColor`, `ApplyConfig`. |
| `GpuLighting.cs` | ComputeSharp (DX12) port of the lighting. `IsAvailable` guard + CPU fallback. |
| `VoxelModel.cs` | GLB import (AssimpNet) + surface voxelization + base-color texture sampling (GDI+) + fallback sphere. Density → grid resolution. |
| `Input.cs` | `InputState` (per-frame snapshot) + `InputManager` (keyboard + controller). |
| `UI/PanelBuilder.cs` | Reusable settings widgets (sections, validated sliders, toggles, RGB). Engine + game tabs share it. |
| `NativeInput.cs` | Keyboard via `GetAsyncKeyState`. Gates: `InputEnabled`, `WindowHasFocus`, `SuspendForTextEntry`, `GameInputActive`. |
| `AudioManager.cs` | NAudio looping music + one-shot SFX + looping SFX (`PlaySfxLoop`/`StopSfxLoop`, fade in/out). `PlaySfx`/`PlaySfxLoop` fall back to a procedural synth preset (see `SynthAudio.cs`) when the named file doesn't exist — never silently no-ops just because an asset isn't sourced yet. Defensive when files absent. |
| `SynthAudio.cs` | Procedural sound engine: `SynthVoice` (one-shot sweep+noise+envelope), `SynthArpVoice` (short note-chime), `SustainedSynthProvider` (continuous loop tone) + `SynthPresets.Map`, a name→recipe dictionary. The 6 example presets are placeholders — replace/extend for your game; drop a real file with the same name next to the exe at any time and it takes over automatically. |
| `PlayerData.cs` | `PlayerStore` profiles + high scores → `players.json`. Thread-safe (locked). |
| `ParticleManager.cs`, `DisplayVolume.cs` | Emissive particles; runtime display bounds. |
| `UI/MainWindow.axaml(.cs)` | Settings window: mode headers across the top (from `IVoxonGame.Modes`), the active mode's settings on the LEFT, the Voxon display settings on the RIGHT, preview in the middle, status bar. There is no tab strip and no nav list — the Lighting and Profiles tabs were deleted. |
| `LedHostCS.cs`, `LedWinCS.cs`, `VoxonTypes.cs` (root) | SDK P/Invoke wrappers. **Don't edit casually**; they mirror native signatures. |

### The EDes app (`Core/`, `Core/Sim/`, `Core/Pcb/`)

| File | Role |
|------|------|
| `EDesApp.cs` | The `IVoxonGame`: modes, per-mode input, camera driving, bounds reading, budget setup, HUD, backdrop, and the whole settings tab. Start here. |
| `EDesSettings.cs` | Persisted app state to `%AppData%/EDes/edes.json` (`IGameSettings`). All scalars `volatile`; `PcbImportRequested` is the UI-to-game-thread request flag. |
| `Sim/VoxelBatch.cs` | **The choke point.** Budget-limited, bounds-clipping voxel accumulator + line/blob/ring/rect primitives; one `DrawVox_Batch` per frame. Everything drawn goes through it. |
| `Sim/SceneCamera.cs` | pan, scale + an orthonormal **basis** `Transform()` + SpaceNav application. Every scene point passes through it exactly once. Orientation is a basis, NOT Euler angles — see invariant 12. `HORIZONTAL_IS_X` is the one switch that swaps the layout horizontal/depth axes. |
| `Sim/Hud.cs` | Text in the volume, routed through `VoxelBatch` (so text obeys the budget + bounds), scene-space or panel-space, plus engineering-notation formatting. |
| `Sim/Palette.cs` | Packed 0xRRGGBB colours, clamped `Scale`/`Mix`, and the power `Heat` ramp. |
| `Sim/Ohm.cs` | `Resistor`/`SeriesGroup`/`ParallelGroup` recursive solver + the 4 `CircuitPresets`. |
| `Sim/CircuitScene.cs` | Solve + layout behind a dirty flag into a flat `List<WireSegment>`; extents derived from live display bounds. |
| `Sim/CircuitRenderer.cs` | Draws the segment list: zigzags, heat colour + power bulge, battery, flow dots, labels. |
| `Sim/ScopeSource.cs` | Serial (USB) reader thread + synthetic generator into per-channel ring buffers; auto channel count, 2 s reconnect, `Snapshot()` for the renderer. |
| `Sim/ScopeMath.cs` | `ScopeStats.Compute`: Vpp/Vrms/mean/min/max, frequency + period from zero-crossings, duty. |
| `Sim/ScopeRenderer.cs` | The scope face on a constant-Y plane: graticule, software trigger window, traces (one sample per voxel column), clip warning, readouts. |
| `Pcb/PcbBoard.cs` | Board model in **mm** (layers, segs, pads, regions, holes, mesh clouds) + bounds + analysis (drill table, min track/drill, copper layer count). |
| `Pcb/GerberParser.cs` | RS-274X subset: `%FS/%MO/%AD/%LP`, `D01/02/03`, `G01/02/03` (+`G74/75` arcs), `G36/37` regions. Unsupported constructs add a note, never silently drop. |
| `Pcb/ExcellonParser.cs` | Drill/route: units, **zero suppression**, tool table, modal hits, `G85` slots. |
| `Pcb/MeshLoader.cs` | Assimp to a deterministic area-weighted surface point cloud (STL/OBJ/PLY/GLB/...). Surface-only sampling — never a solid fill. STEP goes to `StepParser` instead. |
| `Pcb/StepParser.cs` | STEP (ISO 10303-21) to an EDGE wireframe with no CAD kernel: tokenizer (incl. complex instances), units, assembly placement, `LINE`/`CIRCLE` edges, colours, designators. Surfaces deliberately not read — read its header before touching it. |
| `Pcb/CadModel.cs` | `CadSolid`/`CadEdge`/`CadModel` — what a STEP import produces, in board mm with Z up. |
| `Pcb/PcbImporter.cs` | Folder/file dispatch + layer-kind classification (KiCad AND Altium/Eagle naming) + stack ordering. |
| `Pcb/PcbRenderer.cs` | Fits the board to the cylinder (bounding **circle**), spreads layers along Z, draws tracks/pads/pours(hatched)/drills/meshes/cursor. |
| `tests/PcbParserTests/` | Console check harness for the two parsers (30 assertions incl. inch/metric, legacy 2.4, arc sweep direction, slots). |

## Critical invariants (don't break these)

1. **Threading**: ALL Voxon SDK calls happen on the game thread (STA). Never call the SDK from
   the Avalonia UI thread. The UI communicates via `GameSettings`.
2. **Settings concurrency**: scalar settings are `volatile` (UI writes / game reads; a 1-frame torn
   read is acceptable). Whole objects use **atomic reference swap** — see `GameSettings.Lighting`
   and `ApplyLighting` (clone → mutate → assign). Never mutate `Lighting` in place from the UI.
3. **Preview frames**: `OnPreviewFrame` drops frames when one is in flight (`_previewPending`),
   reuses one buffer (no per-frame LOH alloc), and posts at `DispatcherPriority.Background`. Don't
   revert to per-frame `Clone()` or `Render` priority — it reintroduces multi-second latency.
4. **Lighting target**: lighting applies to **solid model geometry only**. Particles and any
   emissive geometry are drawn unlit by design.
5. **Input isolation**: game input (keyboard + controller) is gated by `NativeInput.GameInputActive`,
   which is false while a `TextBox` has focus. This keeps typed characters and the controller out of
   the settings UI. The controller is read only inside the game loop via the SDK.
6. **Stack-corruption defenses** in `GameLoop` (`s_settings` static, `RescueSettings`,
   `[MethodImpl(NoOptimization|NoInlining)]`): the SDK init zeroes managed stack frames. Keep them.
7. **Drawing**: prefer `ledHost.DrawVox_Batch` for many voxels (one native call). Colors are packed
   `0xRRGGBB` ints. World coords: X=left/right, Y=depth, Z=vertical (asymmetric; see `DisplayVolume`).
8. **EDes: everything draws through `VoxelBatch`.** Do not call `DrawVox`/`DrawLine`/`DrawSphere`
   or `VoxelFont.Draw` directly from app code - that bypasses the max-voxel limit AND the
   display-bounds clipping, the two guarantees the app is built on. Use
   `batch.Line/Blob/Ring/RectXZ/Add` and `Hud.Text` (which routes `VoxelFont.Emit` into the batch).
   `HudFont.Classic` is the SDK own vector font and cannot be routed - it is the one exception,
   and it is unbudgeted.
9. **EDes: bounds are read from the SDK every frame** in `EDesApp.ReadBounds`
   (`GetAspectRatioX`, `vs.boundr`, `vs.boundz`) and handed to `VoxelBatch.BeginFrame`. Never
   hardcode 4.0/2.0 in app code, and do not widen the 6% safety margin without checking on
   hardware.
10. **EDes: -Z is up.** "Raise it" means SUBTRACT from z (resistor power bulge, layer stacking,
    scope volts). Readout panels sit on the constant-Y `PlaneY` plane and are deliberately NOT
    camera-transformed, so they stay legible while the scene rotates.
11. **EDes: scene orientation is a BASIS, not Euler angles.** `SceneCamera` stores the
    scene's own X/Y/Z axes (`_ux.._wz`) and `RotateLocal` POST-multiplies each increment,
    which is what makes every rotation happen about the scene's own axis instead of the
    display's. Do not reintroduce accumulated yaw/pitch/roll scalars — three scalars
    replayed in a fixed order can only rotate about the frame that order is defined in.
    `Yaw`/`Pitch`/`Roll` still exist but are READ-ONLY projections for the status line.
    Every rotation routes through `RotateLocal`, and it re-orthonormalises each call
    because the basis is fed back into itself every frame.

12. **EDes: SpaceNav axes are RAW driver counts.** The SDK hands back roughly ±350 at
    full deflection with a few counts of noise at rest — not a -1..1 signal, whatever the
    old docstring claimed. Always run a reading through `NavState.Condition(fullScale,
    deadzone)` before using it as a rate; feeding raw counts to a rate of 0.1 moved the
    scene 1.17 units per FRAME, and with no dead-zone the resting noise integrated into
    permanent drift. `NavFullScale` is a setting because the true full-scale is per-device
    — the diagnostics block reports the observed peak so it can be calibrated, not guessed.

13. **EDes: CAD is drawn as EDGES, never as filled surfaces.** The display is
    transparent and has no occlusion, so a filled or densely-sampled surface shows its
    own back faces through its front and the part reads as fog. `StepParser` therefore
    reads only B-rep edges, and `MeshLoader` samples only surfaces (never interiors).
    Do not "improve" either by adding a solid fill — it costs budget and reduces
    legibility at the same time. A whole 2-layer board's CAD is ~10k voxels as edges.

14. **EDes: draw order is priority order** - mode content, then HUD text, then backdrop. When the
    budget runs out, the tail of that order is what disappears. Keep new decoration last.

## Rendering best practices

Lessons learned the hard way building games on this engine — read before writing a new draw path.

1. **Batch, don't loop `DrawVox`.** Every native call has fixed overhead; drawing 500 voxels as
   500 `DrawVox` calls is dramatically slower than filling `float[]`/`int[]` scratch arrays and
   making ONE `DrawVox_Batch` call. `VoxelModelRenderer.cs` and `SpriteBurstRenderer.cs` both do
   this: accumulate into pre-allocated arrays sized for the worst case, flush once per frame.
   Never allocate those arrays per-frame — allocate once as instance fields and reuse.
2. **Budget your voxels, not your object count.** The display has a finite voxels/frame budget
   (see README's Voxel Budgeting table) shared across EVERYTHING drawn that frame — model,
   particles, sprites, HUD text, background. A single dense effect can starve everything else.
   Expose a density/budget knob (`ParticleBudget`, `VoxelDensity`, a sprite's `size`) so users can
   trade visual density for frame time on weaker hardware, and check `FrameProfiler`'s per-phase
   breakdown + voxel count before assuming "it feels slow" is one specific system's fault.
3. **Cull interior voxels on solid models.** A naive filled sphere/cube is ~70% voxels nobody can
   ever see (the display has no camera and no occlusion, but interior voxels are still wasted
   draw calls). `LightingSystem`'s 6-face shell map (`SubmitToShells`/`IsExterior`) exists
   specifically to skip them — always run lit models through it rather than drawing every voxel.
4. **Prefer hollow/shell shapes to solid fills.** Same principle without lighting: a hollow sphere
   (`DrawSphere` with the hollow flag, or a sparse-shell point sampling) reads exactly as bright
   from outside as a solid one for a fraction of the voxel cost — the display is translucent, so
   interior fill mostly just burns budget without changing what's visible.
5. **LOD by distance is cheap and effective.** For anything whose distance from the viewer varies
   (background fields, far-away enemies), skip every Nth voxel/particle past a distance threshold
   rather than rendering full density everywhere — the display can't resolve fine detail at range
   anyway, so the quality loss is invisible while the voxel savings are real.
6. **One retinted image beats N hand-authored ones.** `SpriteBurstRenderer`/`ColorHsv.ShiftHueBlend`
   retint a single grey/white source PNG toward any primary/secondary colour at spawn time — reuse
   one sprite across every "themed" variant of an effect (per-enemy-type deaths, per-stage
   palettes) instead of shipping a separate image per colour.
7. **Billboard plane count is a direct cost multiplier for 2D sprites.** `SpriteBillboardMode` costs
   pixels × planes: Single (×1) reads fine from most angles for something small/fast; Pair45 (×2)
   is the general-purpose default; Lathe (a true revolve, N ∝ radius) looks best from every angle
   but costs the most — reserve it for slow/large/important effects, not every muzzle flash.
8. **Brightness/colour scaling clamps at both ends.** Always `Math.Clamp(..., 0, 255)` per channel
   after multiplying a colour by a brightness factor — an unclamped scale-up wraps or overflows the
   packed int and produces garbage colours, not just "too bright."
9. **GPU lighting needs a CPU fallback, always check `IsAvailable`.** Not every machine has a
   ComputeSharp-compatible GPU; `GpuLighting.IsAvailable` gates it, and the CPU path must produce
   the same visual result (see `LightingSystem`'s dual code paths) so switching the toggle doesn't
   change how anything looks, only how fast it draws.
10. **Two-pass shell-cull, not per-voxel light queries in a naive nested loop.** Pass 1 submits
    every voxel's position to build the shell map; pass 2 culls interior + shades exterior. Doing
    the shading query before the shell map is complete gives wrong results (some "interior"
    voxels haven't been marked yet) — always finish pass 1 before starting pass 2.

## Conventions

- New UI rows use the helpers in `MainWindow.axaml.cs`: `AddSection` (accordion), `AddSlider`
  (editable+validated numeric box, commits on Enter/blur), `AddToggle`, `AddRgb`, `AddButton`,
  `AddInfo`. Numeric entry is filtered by `RestrictNumeric` and committed deferred — preserve that.
- Persisted settings: add the field to `GameSettings` (or `LightingConfig`) AND to its
  `Save`/`Load`/`Reset`. `LightingConfig` is JSON-serialized with `IncludeFields = true`.
- Logging: `App.Log(...)` (→ `Program.SafeLog`, the desktop crash log).
- Assets load from `AppContext.BaseDirectory` (next to the exe): `*.glb`, `music.*`, `fire.wav`.

## Runtime-only caveats (cannot be verified by building)

- **GPU vs CPU lighting parity** — only testable on a DX12 machine; the math mirrors the CPU path.
- **Controller axis signs / detection** — left-stick Y sign and whether `GetJoyCount()` needs
  `SetJoyAPIType` first depend on hardware.
- **Texture V orientation** — sampled with glTF's V-down convention; flip if models look mirrored.
- After code changes, prefer a real run to confirm behavior; a clean build is necessary but not
  sufficient.

## Building a new game

1. Implement `IVoxonGame` (copy `DemoGame.cs`): `Manifest`, `Init(GameContext)`,
   `Update(in InputState, dt)`, `Draw(ledHost, vs)`, `BuildSettingsPanel(PanelBuilder)`, `Settings`.
2. Heavy/asset work goes in `Init` (runs on the game thread BEFORE SDK init — see GameLoop note 3).
3. Game-specific settings: a class like `DemoGameSettings` implementing `IGameSettings` (the engine
   saves it on the debounced settings save); return it from `Settings`.
4. Build the settings tab with the supplied `PanelBuilder` so it matches the engine tabs.
5. In `App.OnFrameworkInitializationCompleted`, change `new DemoGame(Settings)` to your game.

## Planned / not yet done

- Game-state machine + in-volume HUD/menus (via `ledHost.DrawTxt`).
- Asset folder conventions (`models/`, `sounds/`, `fonts/`) + GLB hot-reload.
- Keybinding / controller-mapping UI; in-app log panel.
- Splash currently shows title + optional `Manifest.SplashPath`/`LogoPath` image; no fade/animation.
