# VLED Ohm's Law Simulator — Project Guide

A design and architecture write-up of `VLED_OhmSimulator`, written so it can be
handed to a colleague who wants to build their own volumetric simulation app
on the same SDK — whether that's a different Ohm's-law variant or an entirely
different kind of simulator.

It covers what the app is, how it's built, why each design decision was made,
and a step-by-step playbook for replicating the pattern.

---

## 1. What it is

A Falstad-style circuit simulator that renders on a **Voxon VX2 / VX2-XL**
volumetric display. You pick a preset circuit (single resistor, series,
parallel, or series-parallel), tune the source voltage and each resistor's
value live, and watch:

- **Current flow** as animated dots moving along the wires, speed scaled to
  the actual current in that branch.
- **Resistors physically "heat up"** — rising in height and shifting along a
  blue → cyan → yellow → red gradient — in proportion to the power they're
  dissipating (`P = I²R`).
- **Parallel branches rendered as separate lanes** you can walk around the
  display to compare, instead of overlapping lines on a flat schematic —
  the actual reason to do this volumetrically rather than on paper.
- **Live readouts rendered as real 3D text** floating in the volume (circuit
  name, source voltage, per-resistor values, circuit totals), not just on a
  flat 2D overlay window.

It is not a full SPICE engine. It solves exactly Ohm's law plus the
series/parallel resistance combination rules — intentionally scoped down
from a full free-wiring circuit editor (see [§8](#8-scope-decision-preset-templates-vs-free-editor)).

## 2. Hardware & SDK context

| Item | Detail |
|---|---|
| Target hardware | Voxon VX2 (~7M voxels) / VX2-XL (~24M voxels), cylindrical display volume |
| SDK | Voxel/Voxon VLED SDK, installed at `C:\VLED\SDK` |
| Language template | `C:\VLED\SDK\VisualStudioTemplates\New_CS_VLED_Application` (the official C# starter) |
| Runtime libraries | `LedHost.dll` (volume rendering + motor control), `LedWin.dll` (windowing, input, PC simulator) |
| Target framework | .NET 9, console `Exe` project |
| Guides | [C# Developer Guide](https://www.voxel3d.co/user%20guides/CSharpDeveloperGuide.html), [Workflow / SDK guide](https://www.voxel3d.co/workflow-sdk.html) |

The C# template ships four files that every app in this family reuses
**verbatim** — copy them, don't rewrite them:

- `LedHostCS.cs` — C# wrapper over `LedHost.dll` (voxel drawing, motor RPM, frame lifecycle)
- `LedWinCS.cs` — C# wrapper over `LedWin.dll` (window, keyboard/mouse/SpaceMouse/gamepad input, PC simulator)
- `VoxonTypes.cs` — shared structs/enums (`point3d`, `poltex`, `tiletype`, `vxl_state_t`, `VX_KEYS`, ...)
- `VLED_CS_Template.csproj` — the plain net9.0 console project file

Only `VLED_Program.cs` (and, for this repo's convention, a `.slnx` solution
file) is project-specific.

## 3. Project structure

```
VLED_OhmSimulator/
├── LedHostCS.cs          ← copied verbatim from the SDK template
├── LedWinCS.cs            ← copied verbatim from the SDK template
├── VoxonTypes.cs           ← copied verbatim from the SDK template
├── VLED_OhmSimulator.csproj  ← template's .csproj, renamed
├── VLED_OhmSimulator.slnx
├── VLED_Program.cs         ← everything project-specific lives here
├── README.md               ← usage/controls reference
└── PROJECT_GUIDE.md         ← this document
```

No CSV files, sample data, or command-line arguments are needed — every
circuit is a built-in preset, so the app runs the instant you double-click
the exe.

## 4. Architecture at a glance

The program is organized into five layers, each with one job:

```
┌─────────────────┐   ApplyVoltage()   ┌──────────────────┐
│  Circuit model   │ ─────────────────▶│  Solved values    │
│ (Resistor /      │                    │ (V, I, P per      │
│  Series/Parallel)│                    │  element)         │
└─────────────────┘                    └────────┬─────────┘
                                                  │ dirty flag
                                                  ▼
┌─────────────────┐   BuildSegments()   ┌──────────────────┐
│  Layout          │ ◀──────────────────│  Wire segment      │
│ (3D geometry)    │ ─────────────────▶ │  list (structs)    │
└─────────────────┘                    └────────┬─────────┘
                                                  │ every frame (cheap)
                                                  ▼
┌─────────────────┐                     ┌──────────────────┐
│  Camera          │ ──── Transform() ──▶│  Rendering        │
│ (pan/zoom/       │                     │ (wires, zigzags,  │
│  yaw/pitch/roll) │                     │  flow dots, text, │
└─────────────────┘                     │  3D backdrop)      │
                                          └──────────────────┘
                                                  ▲
                                          ┌──────────────────┐
                                          │  Input handling    │
                                          │ (keyboard/mouse/    │
                                          │  SpaceMouse)         │
                                          └──────────────────┘
```

The key discipline: **solve and layout only run when something changes**
(a "dirty flag"), never inside the render loop. The render loop only ever
*reads* the precomputed wire-segment list. This matters a lot on this SDK
because every `DrawLine`/`DrawSphere`/`DrawTxt` call is a real native call
into the volumetric renderer — recomputing a circuit solve or a layout 60
times a second for no reason is wasted native-call budget.

## 5. The circuit model (Ohm's law solver)

A small recursive class hierarchy, not a matrix solver — it's the direct
translation of the physics rules a textbook states:

```csharp
abstract class CircuitElement
{
    public double Voltage, Current;
    public abstract double Resistance();
    public abstract void ApplyVoltage(double v);
    public abstract void ApplyCurrent(double i);
}

sealed class Resistor : CircuitElement
{
    public double R;
    public override double Resistance() => R;
    public override void ApplyVoltage(double v) { Voltage = v; Current = v / R; }
    public override void ApplyCurrent(double i) { Current = i; Voltage = i * R; }
    public double Power => Voltage * Current;
}

sealed class SeriesGroup : CircuitElement       // same current through every child
{
    public CircuitElement[] Children;
    public override double Resistance() => Children.Sum(c => c.Resistance());
    public override void ApplyCurrent(double i)
    {
        Current = i; Voltage = i * Resistance();
        foreach (var c in Children) c.ApplyCurrent(i);
    }
    public override void ApplyVoltage(double v) { Voltage = v; ApplyCurrent(v / Resistance()); }
}

sealed class ParallelGroup : CircuitElement     // same voltage across every child
{
    public CircuitElement[] Children;
    public override double Resistance() => 1.0 / Children.Sum(c => 1.0 / c.Resistance());
    public override void ApplyVoltage(double v)
    {
        Voltage = v; Current = v / Resistance();
        foreach (var c in Children) c.ApplyVoltage(v);
    }
    public override void ApplyCurrent(double i) { Current = i; ApplyVoltage(i * Resistance()); }
}
```

Solving the whole circuit is one call: `root.ApplyVoltage(sourceVoltage)`.
Each node figures out its own values and recurses into its children. Adding
a new topology is just composing these three types differently — the
series-parallel preset is literally `new SeriesGroup(r1, new ParallelGroup(r2, r3))`.

**Why this shape, not a nodal-analysis matrix solver:** the scope decision
(see [§8](#8-scope-decision-preset-templates-vs-free-editor)) was fixed
preset topologies, not free-form wiring. For a fixed, known-shape circuit,
a recursive series/parallel solver is exact, trivial to reason about, and
maps directly onto the physics rules — a matrix solver would be solving a
harder problem than the one that exists.

## 6. Coordinate system & camera

The SDK uses a **Z-down** coordinate system: increasing Z moves *down* in
the physical volume. This app leans on that directly instead of picking
axes arbitrarily:

- **X** — horizontal position along the circuit path (left → right, matching
  how current is drawn flowing through a schematic).
- **Y** — depth, used *only* by parallel branches to fan out into separate
  lanes. Zero everywhere else. This is what makes walking around the
  display show you separate parallel branches instead of one flat loop.
- **Z** — vertical schematic position (top wire vs. bottom wire of the
  loop), with resistors additionally offset *upward* (more negative Z)
  proportional to their power dissipation — the "heating up" effect.

The camera pattern — pan → scale → yaw → pitch → roll — is copied directly
from the SDK template's reference implementation and reused unchanged
across every app in this repo, so all of them feel the same to fly around:

```csharp
static point3d Transform(float x, float y, float z)
{
    x += panX; y += panY; z += panZ;
    x *= zoom; y *= zoom; z *= zoom;

    float cx = x * MathF.Cos(rotYaw) - y * MathF.Sin(rotYaw);
    float cy = x * MathF.Sin(rotYaw) + y * MathF.Cos(rotYaw);
    x = cx; y = cy;

    float cz = z * MathF.Cos(rotPitch) - y * MathF.Sin(rotPitch);
    cy = z * MathF.Sin(rotPitch) + y * MathF.Cos(rotPitch);
    z = cz; y = cy;

    cx = x * MathF.Cos(rotRoll) + z * MathF.Sin(rotRoll);
    cz = -x * MathF.Sin(rotRoll) + z * MathF.Cos(rotRoll);
    x = cx; z = cz;

    return new point3d(x, y, z);
}
```

Every coordinate you draw — wires, resistor bodies, flow dots, text anchor
points, backdrop rings — is defined in this simple "world space" first, then
passed through `Transform()` right before the actual `DrawLine`/`DrawSphere`/
`DrawTxt` call. This is what lets the camera rotate/pan/zoom the *entire*
scene for free: nothing needs to know about the camera except the one
`Transform()` call site per draw.

## 7. Rendering pipeline

### 7.1 Wire segments — the precomputed render list

```csharp
struct WireSegment
{
    public point3d Start, End;
    public double Current;
    public Resistor? Body;   // non-null => this segment is a resistor
    public bool IsBattery;
}
```

`BuildSegments()` walks the `CircuitElement` tree once (after a solve) and
appends one `WireSegment` per leaf/connector, doing the layout math:

- A `Resistor` leaf becomes one segment between the two points it was asked
  to span.
- A `SeriesGroup` subdivides its span evenly among its children and recurses.
- A `ParallelGroup` fans its children sideways in Y into lanes, adding a
  short "splice" connector segment on each side of the fan so it visually
  reads as one wire splitting and rejoining.

This is a flat `List<WireSegment>` of small structs — not a list of
per-component render objects — so the render loop is just "iterate a list
and draw," no allocation, no virtual dispatch.

### 7.2 Resistor visual: schematic zigzag

Instead of a plain box, each resistor draws the classic 6-peak schematic
zigzag between two lead stubs, computed from the segment's direction vector
and its perpendicular:

```csharp
float ux = dx / len, uz = dz / len;   // unit vector along the wire
float px = -uz, pz = ux;              // perpendicular, for the zigzag peaks
```

Six line segments alternate `+px`/`-px` offset from the centerline between
the leads. Cheap, and reads unmistakably as "resistor" rather than "wire."

### 7.3 Heat color + power bulge

A resistor's fraction of the *current template's* max power drives both its
color and how far it visually rises:

```csharp
float powerFrac = (float)(seg.Body.Power / maxPower);
float bulge = powerFrac * 0.22f;
s3.z -= bulge; e3.z -= bulge;          // Z-down: subtracting raises it up
int col = HeatColor(powerFrac);        // blue → cyan → yellow → red
```

`maxPower` is recomputed per-template (not a fixed constant) so the color
range always uses the full gradient regardless of which preset or voltage
you're on.

### 7.4 Current flow animation

Dots are placed along a segment at fixed spacing and slide along it based
on a global animation clock, wrapping with `%`:

```csharp
float frac = (baseFrac + animClock * speedMag) % 1.0f;
point3d p = LerpPoint(start, end, frac);
```

`speedMag` scales with that segment's actual current, so a heavily-loaded
branch visibly flows faster/denser than a lightly-loaded one — the
animation *is* the data, not decoration on top of it.

### 7.5 Volumetric text vs. the 2D HUD

Two different text paths exist, and they carry different content on
purpose:

| Text path | API | What it shows |
|---|---|---|
| **Volumetric** (in the display volume) | `LedHost.DrawTxt(ref vs, ref pos, ref right, ref down, radius, col, str)` | Circuit name + voltage (floating title), circuit totals, each resistor's live R/I label next to its zigzag |
| **2D HUD** (flat window overlay) | `LedWin.DrawTxt(x, y, col, bcol, str)` | Controls/instructions, selected-resistor highlight, full per-resistor breakdown |

The rule of thumb: **content that's true about the circuit goes in the
volume; chrome that's about how to operate the app stays on the flat
window.** A colleague building their own simulator should draw that same
line early — it's an easy trap to put everything on the convenient 2D
overlay and end up with a physical display showing nothing but wires.

### 7.6 3D backdrop

Two decorative-but-orienting elements, both built from a single reusable
ring-drawing helper:

```csharp
static void DrawRing(LedHostCS lh, ref vxl_state_t vs, point3d center,
    float radius, point3d axisU, point3d axisV, int col, int segs)
```

- **Grid floor** — three concentric rings + 8 radial spokes in the X-Y
  plane, positioned below the circuit (`Z ≈ 0.62`, i.e. "down" in Z-down).
  Reads as graph paper underfoot.
- **Wireframe globe** — the *same* `DrawRing` call three times, once per
  plane (`XY`, `XZ`, `YZ`), all centered at the origin at the same radius.
  Three orthogonal great circles is the cheapest way to draw something that
  unmistakably reads as a sphere in wireframe, and it gives you an
  orientation reference no matter how the camera is rotated.

Both are drawn in dim, desaturated colors (`0x0E2A3A`, `0x152233`) so they
frame the circuit without competing with the heat-gradient resistors or the
bright flow dots for attention.

## 8. Scope decision: preset templates vs. free editor

Before building, there were three options on the table for how much
circuit-building freedom to give the user:

1. **Preset templates** *(what was built)* — a handful of built-in
   topologies, live-adjustable values. Fast to build, matches how every
   other app in this repo works (fixed demo + keyboard controls).
2. **Buildable component chain** — pick components from a small palette and
   assemble custom series/parallel branches at runtime.
3. **Full freeform circuit editor** — place and wire arbitrary components in
   3D like the real Falstad simulator, solved via nodal analysis every
   frame. Effectively a small SPICE engine plus a 3D circuit editor.

Option 1 was chosen because it directly matches the actual ask ("Ohm's law
simulation," not "build me a SPICE engine") and because it's genuinely a
different scale of project past that point — option 3 would mean a matrix
solver, a component palette/placement UI, and wire-routing logic, which is
multiple times the work for a feature ("free wiring") the ask didn't call
for. **If your own project needs more topology freedom, start with option 2**
— it reuses this exact `CircuitElement` tree and solver unchanged; you'd
only be adding a runtime UI for composing which children go in which group.

## 9. Controls

| Input | Action |
|---|---|
| `1`–`4` | Switch circuit preset |
| `Left` / `Right` | Select previous/next resistor |
| `Up` / `Down` | Increase/decrease selected resistor's value (×1.1 per press) |
| `[` / `]` | Decrease/increase source voltage (1–24 V) |
| `Space` | Pause/resume current-flow animation |
| Right mouse drag | Rotate (yaw/pitch) |
| Middle mouse drag | Pan |
| Mouse wheel | Zoom |
| `W`/`S`/`A`/`D` | Rotate (yaw/pitch) |
| `+` / `-` | Zoom in/out |
| SpaceMouse (SpaceNav) | Full 6DOF pan/rotate/zoom |
| `R` | Reset view |
| `Esc` | Quit |

## 10. Guide principles applied

Two Voxel Photonics guides informed specific decisions here (both are
high-level architecture guides, not API references — the API itself comes
from reading the template's `LedHostCS.cs`/`LedWinCS.cs` directly):

- [C# Developer Guide](https://www.voxel3d.co/user%20guides/CSharpDeveloperGuide.html) —
  "favor parallel arrays / flat structs with a live count for frame-critical
  data, not rich objects" → the `WireSegment` struct list; "use dirty flags
  to avoid recomputing unchanged data" → `RecomputeIfDirty()`.
- [Workflow / SDK guide](https://www.voxel3d.co/workflow-sdk.html) —
  confirms the Z-down coordinate convention and the `FrameStart`/`FrameEnd`-
  bracketed draw model this whole app is built around.

## 11. Building your own simulator: a playbook

If you're starting a new volumetric simulator on this SDK, this is the
order that worked well here:

1. **Copy the four template files** from
   `C:\VLED\SDK\VisualStudioTemplates\New_CS_VLED_Application`
   (`LedHostCS.cs`, `LedWinCS.cs`, `VoxonTypes.cs`, the `.csproj`) into a new
   project folder. Don't modify them — everything project-specific belongs
   in your own `VLED_Program.cs`.
2. **Model your domain first, with no rendering in mind.** For this app that
   was `Resistor`/`SeriesGroup`/`ParallelGroup` with `ApplyVoltage`/
   `ApplyCurrent`. Get the physics/logic correct and testable in isolation
   before touching a single `DrawLine` call.
3. **Decide your coordinate mapping deliberately**, using the Z-down
   convention as given: which axis is "the interesting dimension" (here,
   power → height), which axis is free for depth-separating parallel/
   repeated structures (here, Y for parallel branches), and which axis is
   your baseline layout direction (here, X for time/position).
4. **Write one `Transform()` camera function** (pan → scale → yaw → pitch →
   roll, copied from the template) and route every draw call through it.
   Never hand-roll camera math per draw site.
5. **Separate "solve" from "layout" from "render."** Solve populates your
   domain model's values. Layout walks that model once and produces a flat
   list of small structs (positions + whatever the renderer needs). Render
   just iterates that list every frame. Gate the first two behind a dirty
   flag.
6. **Put real data in the volume, chrome on the window.** Use
   `LedHost.DrawTxt` for anything that's a fact about the simulation;
   reserve the `LedWin.DrawTxt` 2D overlay for controls and instructions.
7. **Add a cheap 3D backdrop last.** A grid floor and a 3-ring wireframe
   globe (both from one reusable ring-drawing helper) go a long way toward
   making a handful of lines feel like they're sitting in a real space
   instead of floating in a void — and it costs almost nothing once you have
   `Transform()`.
8. **Build and test in the PC simulator before hardware.** The SDK ships a
   simulator inside `LedWin` specifically so this loop doesn't require the
   physical display.

## 12. Possible extensions

Ideas raised but not built, in case they're useful starting points:

- `Q`/`E` keys for manual roll control (currently roll is SpaceNav-only).
- A 4-resistor Wheatstone-bridge preset, or a "build your own branch" mode
  (see [§8](#8-scope-decision-preset-templates-vs-free-editor), option 2).
- A visible "short circuit" / over-current warning state.
- Live data ingestion for a *scope* companion app in this repo
  (`VLED_ScopeViewer`) rather than the simulator: either polling a CSV a
  scope continuously re-exports, or talking SCPI directly over USB/LAN.

## 13. References

- Source: [`VLED_Program.cs`](VLED_Program.cs)
- Usage/controls reference: [`README.md`](README.md)
- SDK template: `C:\VLED\SDK\VisualStudioTemplates\New_CS_VLED_Application`
- [C# Developer Guide](https://www.voxel3d.co/user%20guides/CSharpDeveloperGuide.html)
- [Voxel SDK / Workflow guide](https://www.voxel3d.co/workflow-sdk.html)
- Sibling apps in this repo for more reference patterns: `SkyRadar`,
  `VLED_WeatherStation`, `Volumetric_Calender`, `VLED_ScopeViewer`
