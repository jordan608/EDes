# Testing EDes

Four suites, 526 assertions, none of which need the Voxon hardware. Run all four before
pushing.

```bash
dotnet build -t:Compile
dotnet run --project tests/PcbParserTests
dotnet run --project tests/ScpiTests
python fusion/tests/test_protocol.py
python fusion/tests/test_addin.py
```

Each prints `ALL ... CHECKS PASSED` and exits 0. Any `FAIL` line names the value it got, so
the failure is usually readable without opening the test.

| Suite | Assertions | Covers | Needs |
|---|---|---|---|
| `tests/PcbParserTests` | 402 | Gerber, Excellon, STEP, STL, nets, schematic PDF, circuits, layout, CAD light, Fusion client | nothing (some checks skip without a board fixture) |
| `tests/ScpiTests` | 38 | SCPI framing, IEEE-488.2 blocks, transport | nothing |
| `fusion/tests/test_protocol.py` | 51 | the add-in's wire format and unit maths | Python only |
| `fusion/tests/test_addin.py` | 35 | the add-in **running**, against a fake Fusion | Python only |

---

## Building

```bash
dotnet build              # full build, produces the exe
dotnet build -t:Compile   # type-check only
```

**Use `-t:Compile` while the app is running.** The exe is locked, so a full `dotnet build`
fails at the copy step — which looks like a compile error and is not one. The ComputeSharp
source generator still runs, so shader errors are still caught.

Never pass ad-hoc MSBuild output-path properties on the command line. A past attempt mangled
`obj/.../*.FileListAbsolute.txt` and broke incremental builds; if you see "given path's
format is not supported", delete that file and rebuild.

New code should add **no** warnings. The ~260 pre-existing ones live in the SDK wrappers
(`VoxonTypes.cs`, `LedHostCS.cs`, `LedWinCS.cs`). To check just yours:

```bash
dotnet build -t:Compile 2>&1 | grep -iE "warning" | grep -viE "VoxonTypes|LedHostCS|LedWinCS"
```

---

## What each suite is for

### `tests/PcbParserTests`

The big one. Compiles the real source files directly rather than referencing the app, so it
cannot drift from what ships. Sections:

| Section | What would break without it |
|---|---|
| design folder tree, real Altium outputs | coordinate-format handling — the part that silently corrupts boards |
| derived copper connectivity | net extraction from Gerbers, which carry no net names |
| via layer spans | blind vias spanning layers they do not connect |
| STL / STEP tessellation | normals recomputed from winding; grouping that keeps a cone affordable |
| Fusion bridge | the wire format, the socket, placement, and the renderer |
| schematic PDF | stroke/fill decisions, pen lifts, text placement |
| teaching circuits | every preset against the law it demonstrates |
| CAD point light | the point/directional distinction, falloff, sign handling |
| HUD text band | that no row of text escapes the volume |

**Run it after any change to a parser.** The coordinate-format code is the part that fails
quietly.

#### Board fixture (optional)

Checks against a real fabrication output set skip unless one is configured. Either:

- set `EDES_TEST_BOARD` to the folder, or
- put the path on the first non-comment line of `tests/local-testdata.txt` (gitignored).

Skipping is deliberate: a missing local fixture is not a defect, and a suite that goes red on
a colleague's machine for that reason stops being trusted.

### `tests/ScpiTests`

Bench-instrument protocol. Notably includes the regression for a real 1-in-6 bug where a
definite-length block split across TCP reads corrupted multi-channel captures — it forces the
packet split rather than hoping for it.

### `fusion/tests/test_protocol.py`

The add-in's arithmetic and bytes, with **no Autodesk import anywhere**. That is why it runs
on any machine, and it is how the add-in was verified before ever reaching Fusion.

Watch the units section in particular. `surfaceTolerance` is in **centimetres** like every
other length in the API, so the tolerance conversion runs the *opposite* way to the
coordinates — `cm_to_mm` multiplies, `tolerance_mm_to_cm` divides. A test asserts they are
inverses, because getting either backwards is silent.

### `fusion/tests/test_addin.py`

Runs the actual add-in against `fusion/tests/stub_adsk` — a fake Fusion, not a mock. It has a
document tree, a mesh calculator, and a custom-event pump that **dispatches on a separate
thread**, the way the real one dispatches on Fusion's main thread. A stub that ran handlers
synchronously would exercise none of the marshalling the add-in is shaped around and would
hide a deadlock.

It also has a `busy` flag — what an open modal dialog does to the real API — so the timeout
path is tested rather than assumed.

### Cross-language conformance

`fusion/tests/golden_frame.bin` is a frame built by the **real add-in code path** in Python
and parsed by the **C# suite**. Two implementations of one format is exactly where they drift,
and the drift would show up as subtly wrong geometry on hardware rather than as an error.

Regenerate it **only** when the format changes on purpose:

```bash
python fusion/tests/make_golden.py
```

Regenerating it to make a failing test pass defeats the entire point of having it.

---

## Testing the Fusion bridge on the Fusion machine

The add-in has never met real Fusion. This is the walkthrough.

### 1. Install

```bash
git pull
```

Copy the whole `fusion/FusionBridge` **folder** into Fusion's add-ins directory — the green
**+** in **Utilities → Add-Ins → Scripts and Add-Ins** shows where that is, typically:

```
%APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\
```

The folder, the `.py` and the `.manifest` must all be named `FusionBridge` — Fusion matches
them. Select it, tick **Run on Startup**, press **Run**.

The Text Commands palette should show:

```
[EDesFusionBridge] listening on 127.0.0.1:47800
```

### 2. Prove the round trip, before involving EDes

```bash
python fusion/tests/probe.py
```

Expected: `ok: True` and the name of your open document.

That proves more than an open socket. **The document name can only be read on Fusion's main
thread**, so getting it back proves the whole worker-thread → custom-event → main-thread
marshalling — the thing the add-in's entire structure exists for.

### 3. The units check

Open a **10 mm cube** and:

```bash
python fusion/tests/probe.py geometry
```

Look at the extent line:

```
extent mm : X 0.00..10.00   Y 0.00..10.00   Z 0.00..10.00
```

**If it reads `0.00..1.00`, the cm→mm conversion is missing.** Fusion's API is always
centimetres regardless of the document's display units, and a model ten times too small looks
entirely plausible — which is why this is checked in one command rather than discovered later.

### 4. The assertion everything rests on

Still the cube. In Fusion, **move it 50 mm**, then:

```bash
python fusion/tests/probe.py geometry
```

The extent must move 50 mm, with no change on our side. Then:

- **rotate it 45°** — the vertices arrive rotated;
- **nest it in a sub-assembly and move the *parent*** — it still tracks;
- **hide the sub-assembly** — the child reports `HIDDEN` even with its own bulb on;
- **switch off one body inside a shown component** — that body alone reports `HIDDEN`.

This is the *proxy claim*: `occurrence.bRepBodies` returns proxies in root-component context,
so tessellating one yields assembly-space coordinates and Fusion owns placement by
construction. It is read from Autodesk's documentation and has never been executed.

**If it fails**, the fallback is `transform2` applied per body — more code, same outcome.
Report what the coordinates actually did and that is a straightforward change.

### 5. In EDes

Open the **Fusion CAD** tab (Tab cycles to it in the volume):

1. Set **Add-in host** if Fusion is on another machine — and see *Serving another machine*
   below.
2. **Fetch now.** The status line should report bodies, triangles and milliseconds.
3. **Fit once** for a sensible scale.
4. **Follow changes automatically** to have the volume track your edits.

Expected: the assembly standing on the floor of the volume, growing upward, shaded by the
point light.

If it looks wrong, check the readout under **Where the assembly sits** — it says what
percentage of samples fall **outside** the volume, and the volume itself warns when that
exceeds 1%. A wrong origin or scale announces itself rather than looking like a broken
import.

### 6. Cost, before you rely on it

Tessellation runs on Fusion's UI thread, because the API gives no choice — so the tolerance is
your throttle on how long Fusion freezes. Try your real enclosure at 0.4 mm and at 0.1 mm and
watch the millisecond count in the status line before turning on **Follow changes**.

### What each failure means

| Symptom | Cause |
|---|---|
| Nothing in the log, no listener | Add-in not running, or it raised on load — check Text Commands |
| `CONNECTION REFUSED` | Not running, or bound to localhost while you ask from another machine |
| Accepts, sends nothing | A request landed while Fusion was busy. **Fusion services the API only when idle** |
| `TIMED OUT` | Same cause, or a genuinely huge tessellation. Try a coarser tolerance |
| `address in use` on restart | A previous load left the port bound. `stop()` handles this; a hard crash will not — restart Fusion |
| Model 10× too small | The cm→mm conversion. Step 3 finds it |
| Model barely visible | Origin or scale. The clipped-% readout says so |
| Geometry does not follow a move | The proxy claim (step 4). Fallback is `transform2` |

### Serving another machine

The socket has **no authentication** — anything that can reach the port can read the geometry
of whatever is open. It binds localhost by default for that reason.

To serve EDes on a different PC, edit the top of `FusionBridge.py`:

```python
HOST = "0.0.0.0"
```

and put that machine's address in EDes's **Add-in host** box. Do it on a bench network you
trust, not on anything shared.

---

## What no suite can cover

A clean build and green tests are necessary, not sufficient. These need a real run, and in
some cases real hardware:

- **GPU vs CPU lighting parity** — only testable on a DX12 machine; the math mirrors the CPU path.
- **Controller and SpaceNavigator axis signs** — hardware-dependent, which is exactly why the
  axis mapping is selectable and the triad is drawn in the volume.
- **Texture V orientation** — sampled with glTF's V-down convention; flip if models look mirrored.
- **Whether the display *looks* right** — text size, whether the triad crowds the scene,
  whether a schematic at full-sheet zoom is useful. The suites assert arithmetic, not legibility.
- **The six Fusion claims** above, and the motor spin-up, which needs the platter.

When a check asserts something that only hardware can settle, it is better to leave it out
than to assert a guess — a suite that is green for the wrong reason is worse than a gap you
know about.
