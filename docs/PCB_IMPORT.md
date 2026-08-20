# PCB import — what EDes reads, and how to get your board in

EDes imports a board from **fabrication output**, not from CAD project files. That is
deliberate: every EDA tool writes its own project format and changes it between versions,
but they all export the same two industry formats for manufacturing, and those are stable.

Point the **PCB tab → Path** at a **folder** (a whole fab output set) or a single file, then
press **Import / reload**. Import runs on the game thread, takes tens of milliseconds for a
typical board, and reports what it found in the panel underneath the button.

---

## Supported formats

| Format | Extensions | Status |
|---|---|---|
| **Gerber RS-274X** | `.gbr .ger .gtl .gbl .gto .gbo .gts .gbs .gtp .gbp .gm1 .gko .g1–.g4 .cmp .sol .plc .pls .art .pho` | Full common subset — see below |
| **Excellon drill/route** | `.drl .xln .nc .tap .exc .drd`, or `.txt` with an `M48` header | Holes + `G85` slots |
| **Mesh (mechanical)** | `.stl .obj .ply .glb .gltf .fbx .dae .3ds .off` | Sampled to a point cloud |
| **STEP** | `.step .stp` | **Not supported directly — convert first (below)** |

Extension-less or oddly-named files are sniffed: a file starting with `%FS`, `%MO` or `G04`
is treated as Gerber, and one starting with `M48`, `METRIC` or `INCH` as a drill file.

### Gerber: what is and is not handled

Handled: coordinate format (`%FS`, both leading- and decimal-point styles), units
(`%MOMM` / `%MOIN`), aperture definitions `C` circle / `R` rect / `O` obround / `P` polygon,
aperture selection, `D01/D02/D03` draw/move/flash, linear and circular interpolation
(`G01/G02/G03` with `G74/G75` quadrant modes, flattened to segments), regions (`G36/G37`),
absolute and incremental modes, and modal coordinates.

Not handled, each reported as an import note rather than silently dropped:

- **Aperture macros** (`%AM`) and **block apertures** (`%AB`) — a macro-defined flash is
  approximated by a circle of its first parameter, so the pad still appears.
- **Step-and-repeat** (`%SR`) — the block is drawn once, not repeated.
- **Clear polarity** (`%LPC`) — drawn as normal copper. A volumetric display has no
  painter's-algorithm layering to subtract from, so a knockout cannot be rendered as one;
  the geometry is shown instead of being thrown away.

### Excellon: the two things that usually go wrong

1. **Units.** Read from `METRIC` / `INCH`. If the header says nothing, inch is assumed
   (the historical default) and a note is added.
2. **Zero suppression.** Coordinates without a decimal point are integers in an implied
   format. `,LZ` / `,TZ` are honoured; with neither, the conventional **2.4 inch** and
   **3.3 metric** formats are used. Getting this wrong scales the whole drill map by 10× or
   100×, so if holes land in the wrong place this is the first thing to check — the note in
   the panel tells you which assumption was applied.

Files with `NPTH` in the name are marked non-plated and drawn in a darker grey.

### Layer identification

Both major naming conventions are recognised:

| Layer | KiCad | Altium / generic |
|---|---|---|
| Top copper | `*-F_Cu.gbr` | `.GTL`, `.cmp`, `*toplayer*` |
| Bottom copper | `*-B_Cu.gbr` | `.GBL`, `.sol`, `*bottomlayer*` |
| Inner copper | `*-In1_Cu.gbr` … | `.G1`–`.G4`, `*inner*` |
| Silkscreen | `*-F_SilkS.gbr`, `*-B_SilkS.gbr` | `.GTO`, `.GBO`, `.plc`, `.pls` |
| Solder mask | `*-F_Mask.gbr`, `*-B_Mask.gbr` | `.GTS`, `.GBS` |
| Paste | `*-F_Paste.gbr` | `.GTP`, `.GBP` |
| Board outline | `*-Edge_Cuts.gbr` | `.GM1`, `.GKO`, `*outline*`, `*profile*` |

An unrecognised Gerber still imports, as an `Unknown` layer in the stack.

---

## STEP files

STEP (ISO 10303) is a boundary-representation CAD format: surfaces are defined
analytically, not as triangles. Reading it requires a geometry kernel (OpenCascade and
friends) — Assimp, which EDes uses for meshes, does not read it, and there is no practical
pure-.NET STEP loader to bundle. EDes therefore **detects STEP and tells you**, rather than
half-loading it.

Convert once, on the command line, with FreeCAD (free, scriptable):

```bash
freecadcmd -c "import Mesh; Mesh.Mesh(__import__('Import').open('board.step') or __import__('FreeCAD').ActiveDocument.Objects[0].Shape.tessellate(0.05)).write('board.stl')"
```

More reliably, as a small script (`step2stl.py`, run with `freecadcmd step2stl.py`):

```python
import FreeCAD, Import, Mesh, sys
doc = FreeCAD.newDocument()
Import.insert("board.step", doc.Name)
shapes = [o.Shape for o in doc.Objects if hasattr(o, "Shape")]
mesh = Mesh.Mesh()
for s in shapes:
    verts, faces = s.tessellate(0.05)      # 0.05 mm deflection
    mesh.addFacets([[verts[i] for i in f] for f in faces])
mesh.write("board.stl")
```

Other routes that work: **KiCad** (`File → Export → STEP` has a mesh counterpart via the
3D viewer's `Export current view as ...`), **FreeCAD GUI** (`File → Export → STL`),
**Fusion 360** / **SolidWorks** (`Save As → STL`), or `assimp export in.stp out.stl` if your
local Assimp build happens to include the STEP importer.

Tessellation deflection controls point density: 0.05 mm is plenty for a display whose
voxel pitch is around 30 µm of board at typical fit scales.

### Mesh assumptions

- **Units are millimetres** and **Z is up in the model** — the convention every MCAD export
  from a PCB tool follows. The board frame keeps that convention; the renderer is what maps
  height onto the display's `-Z is up` axis.
- Meshes are **surface-sampled**, area-weighted, to at most **Mesh point budget** points
  (PCB tab). Sampling is deterministic, so two revisions of the same board can be compared
  point-for-point.
- A mesh-only import still fits and displays; it just has no layers, drills or DRC numbers.

---

## Reading the display

- Layers are spread along **Z**, top layer highest (`-Z` is up), spacing set by
  **Layer spacing**. This is the whole point of the volumetric view: you can see *which*
  layer a track is on.
- **Drills are bored through the whole stack** — a via reads as a physical column, so it is
  obvious which pads it connects.
- **Copper pours** draw as outlines by default; **Hatch pours** (`F` key) fills them with a
  scanline hatch, which reads as solid for a fraction of the voxels.
- **Isolate layer** (`N` / `M` keys, or the slider) shows one plane at a time.
- The **measurement cursor** (`C`, arrows to move, Shift for 0.1 mm steps) reads out board
  coordinates in millimetres.

The readout under the board quotes board size, copper layer count, hole count, **minimum
track width** and **minimum drill** — the numbers a fab house checks first. The full
per-layer object counts and the grouped drill table are in the PCB tab.

---

## Voxel budget

A dense 4-layer board with pours can ask for far more voxels than the display can draw.
Everything is drawn through one budgeted batch (**Render budget → Max voxels / frame**), and
draw order is priority order, so what you lose first is the backdrop, then labels, then
geometry. If a board looks thin, either raise the budget or reduce what is drawn (turn off
pours, isolate a layer, lower the mesh point budget). The on-glass readout shows
`N VOX` and `+N DROPPED` so you can see it happening rather than guessing.
