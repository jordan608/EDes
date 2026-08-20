# EDes — Electronics Design Explorer

An electronics workbench for the **Voxon VX2 / VX2-XL volumetric display**: circuits you can
walk around, live oscilloscope traces, and PCB layer stacks in real 3D. Built on .NET 9 +
Avalonia, with the Voxon SDK driven from a background thread and a live 2D preview of the
volume so it can be developed and demoed with no hardware attached.

Three modes, switched with **Tab** (or the Mode section of the settings tab):

| Mode | What it is |
|---|---|
| **Education** | Four built-in circuits solved live from Ohm's law. Resistors heat up (colour + height) with real power dissipation, current flows as animated dots at branch-current speed, parallel branches fan into separate depth lanes, and every component carries its own R / V / I / P readout in the volume. |
| **Oscilloscope** | Up to four channels of live data from a USB serial device (or a synthetic signal), with a software trigger, graticule and a bench-style measurement row — drawn on the `y = 0.1` plane. |
| **PCB** | An imported board: Gerber layers spread along Z, drills bored through the stack, mechanical meshes, a measurement cursor, and a fabrication/DRC-lite readout. |

---

## Requirements

- **Windows**, **.NET 9 SDK**
- Voxon SDK (`LedHost.dll`, `LedWin.dll`) — the built-in simulator runs without hardware;
  a physical VX2/VX2-XL is detected automatically at start-up
- A DX12 GPU is optional (only the inherited GPU-lighting path uses it)

```bash
dotnet build
dotnet run
```

Close the app before rebuilding, or the `.exe` copy step fails on the file lock. To
type-check while it is running: `dotnet build -t:Compile`.

Parser checks for the PCB importers:

```bash
dotnet run --project tests/PcbParserTests
```

---

## Controls

| Input | Action |
|---|---|
| **Left-drag the preview** | orbit the simulator camera — walk around the volume |
| **Right-drag the preview** | rotate the scene content |
| **Ctrl + wheel** | simulator camera distance; plain wheel scales the model |
| **SpaceNavigator** | 6-DOF pan / rotate / zoom of the scene |
| `W` `A` `S` `D` | orbit · `Q` `E` roll · `,` `.` zoom · `R` reset camera |
| `Tab` | cycle mode · `L` labels · `G` backdrop · `Esc` quit |
| **Education** | `1`–`4` circuit · `←` `→` select resistor · `↑` `↓` ±10 % · `-` `=` source volts · `P` pause flow |
| **Oscilloscope** | `1`–`4` channels · `↑` `↓` V/div · `←` `→` trigger level · `T` trigger channel · `E` edge · `P` freeze |
| **PCB** | arrows move the cursor (Shift = 0.1 mm) · `C` cursor · `H` drills · `P` pads · `F` hatch pours · `N` `M` isolate layer |

An Xbox/Voxon controller works too: right stick orbits, triggers zoom. Click the preview to
give it keyboard focus; focusing any settings box suspends game input so typing can never
drive the display.

---

## The display, and how this app respects it

Everything is drawn through **one budgeted voxel batch** per frame. That single choke point
is what makes the app behave on real hardware:

- **One native call.** Points accumulate into pre-allocated arrays and ship as a single
  `DrawVox_Batch`.
- **A hard max-voxel limit** (`Render budget → Max voxels / frame`, default 150 000) covering
  *everything* — geometry, traces and HUD text. Draw order is priority order: the backdrop is
  dropped first, then labels, then geometry. The on-glass readout shows `N VOX` and
  `+N DROPPED` so you can see the budget bite instead of guessing.
- **Nothing leaves the volume.** The volume is a cylinder; every point is tested against
  `x² + y² ≤ radius²` and `|z| ≤ zHalf`, read live from the SDK each frame
  (`GetAspectRatioX`, `vs.boundr`, `vs.boundz`) — never hardcoded, so one build fits a VX2 and
  a VX2-XL.
- **Voxel density means what it says.** Point spacing is the display's real voxel pitch
  (`2·radius / vs.xsiz`) divided by the density setting, so density 1.0 = exactly one point
  per voxel.

Axes: **-Z is up**, X is the layout's left/right, Y is depth. Readout panels live on a single
constant-Y plane (default `y = 0.1`) and are deliberately *not* camera-transformed, so they
stay legible while the scene is flown around.

---

## Getting data in

- **Oscilloscope over USB** — any device streaming ASCII samples, one line per sample set,
  CSV for multiple channels. Full wire format, an Arduino front end, and the trigger and
  measurement details: **[docs/SCOPE_USB.md](docs/SCOPE_USB.md)**.
- **PCB files** — point the PCB tab at a fabrication output *folder* (Gerbers + drill) or a
  single file. Gerber RS-274X, Excellon drill/route, and STL/OBJ/PLY/GLB meshes are read
  natively. **STEP needs converting to STL first** (it is a B-rep format requiring a CAD
  kernel) — the app says so instead of failing silently, and the conversion recipe is in
  **[docs/PCB_IMPORT.md](docs/PCB_IMPORT.md)**, along with layer naming, unit/zero-suppression
  handling, and what parts of the Gerber spec are approximated.

---

## Settings tabs

- **Simulator / Lighting / Profiles** — inherited engine tabs (gamma, dither, voxel density,
  30 VPS cap, simulator camera, lighting rig, player profiles).
- **Game** — this app: Mode, Circuit, Oscilloscope, PCB import, Render budget & text,
  Camera & SpaceNavigator, and a Controls reference. Several rows are live readouts that
  refresh once a second (detected serial ports, circuit totals, per-channel measurements,
  board statistics, drawn-voxel count).

Numeric fields accept only valid numbers and apply on **Enter** or focus loss. Settings
auto-save ~2 s after the last change.

### Where data is stored

- `%AppData%/EDes/settings.json` — engine + lighting settings
- `%AppData%/EDes/edes.json` — this app's settings (mode, circuit, scope, PCB path…)
- `%AppData%/EDes/players.json` — profiles
- `Desktop/edes_crash.log` — diagnostics / crash log

---

## Architecture

```
Program.cs (STAThread)         Avalonia on the main thread; software rendering
  App.axaml.cs                 Settings, PlayerStore, Game = new EDesApp(...)
    MainWindow (UI thread)     tabs + preview + status bar; left-drag = camera orbit
    Game thread (background STA)
      VoxonPreflight           USB hardware scan before any SDK call
      EDesApp.Init             load settings, reload last board
      EDesApp.Update           keys, camera, scope pump, PCB import requests
      EDesApp.Draw             bounds -> budget -> mode -> HUD -> backdrop -> flush
```

| Area | Files |
|---|---|
| App shell, modes, HUD, settings tab | `Core/EDesApp.cs`, `Core/EDesSettings.cs` |
| Voxel budget, bounds, camera, text, colour | `Core/Sim/VoxelBatch.cs`, `SceneCamera.cs`, `Hud.cs`, `Palette.cs` |
| Circuits | `Core/Sim/Ohm.cs` (solver), `CircuitScene.cs` (solve+layout), `CircuitRenderer.cs` |
| Oscilloscope | `Core/Sim/ScopeSource.cs` (USB/synthetic), `ScopeMath.cs`, `ScopeRenderer.cs` |
| PCB | `Core/Pcb/PcbBoard.cs`, `GerberParser.cs`, `ExcellonParser.cs`, `MeshLoader.cs`, `PcbImporter.cs`, `PcbRenderer.cs` |
| Engine (inherited) | `Core/GameLoop.cs`, `LightingSystem.cs`, `AudioManager.cs`, `Input.cs`, `VoxelFont.cs`, `UI/*` |

`CLAUDE.md` has the threading rules, invariants and rendering practices — read it before
changing the game loop or adding a draw path.

---

## Known limitations

- **STEP is not read directly** — convert to STL/GLB (see the import doc).
- Gerber **aperture macros**, **step-and-repeat** and **clear polarity** are approximated,
  each reported as an import note rather than silently dropped.
- Copper pours draw as outlines unless hatching is enabled; a true solid fill is not worth the
  voxels on this display.
- Per-layer visibility is driven by **isolate layer** rather than per-layer checkboxes (the
  settings tab is built once, before a board is loaded).
- Layer stack spacing is uniform — it is a legibility aid, not a to-scale dielectric stackup.
- Only verifiable on hardware: SpaceNavigator axis directions and the controller axis signs.
