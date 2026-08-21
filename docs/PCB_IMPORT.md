# PCB import — what EDes reads, and how to get your board in

EDes imports a board from **fabrication output**, not from CAD project files. That is
deliberate: every EDA tool writes its own project format and changes it between versions,
but they all export the same two industry formats for manufacturing, and those are stable.

Point the **PCB tab → Path** at a **folder** (a whole design tree, or just a fab output set)
or a single file, then press **Import / reload**. Folders are walked **recursively** — see
[Design folder trees](#design-folder-trees). Import runs on the game thread, takes tens of milliseconds for a
typical board, and reports what it found in the panel underneath the button.

---

## Supported formats

| Format | Extensions | Status |
|---|---|---|
| **Gerber RS-274X** | `.gbr .ger .gtl .gbl .gto .gbo .gts .gbs .gtp .gbp .gm1 .gko .g1–.g4 .cmp .sol .plc .pls .art .pho` | Full common subset — see below |
| **Excellon drill/route** | `.drl .xln .nc .tap .exc .drd`, or `.txt` with an `M48` header | Holes + `G85` slots |
| **Mesh (mechanical)** | `.stl .obj .ply .glb .gltf .fbx .dae .3ds .off` | Sampled to a point cloud |
| **STEP** | `.step .stp` | Edge wireframe, with assembly placement, colours and designators |

A STEP file, or a folder containing nothing but one, imports on its own — no Gerbers
required. With no board outline present the model's own extents become the bounds, so it
fits the volume like any other geometry.

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

STEP (ISO 10303) is a boundary-representation format: surfaces are defined
analytically, not as triangles. The usual conclusion is that reading it needs a geometry
kernel — and for **surfaces** that is true. EDes does not read surfaces.

It reads **edges**, and that turns out to be both the cheap half and the half you
actually want. The display is transparent, so it has no occlusion: a filled or
densely-sampled surface shows its own back faces through its front and the part reads as
fog. The B-rep feature curves are what read as CAD. Measured on a real Altium AP214
export of a 2-layer sensor board:

| Edge curve type | Count | Share |
|---|---:|---:|
| `LINE` | 1083 | 84.8% |
| `CIRCLE` | 138 | 10.8% |
| `B_SPLINE_CURVE_WITH_KNOTS` | 76 | 5.9% |

95.6% of edges are a straight line or a circular arc, which need arithmetic rather than a
kernel. So `Core/Pcb/StepParser.cs` reads the subset directly, in the same spirit as
`GerberParser` reading a subset of RS-274X.

### What you get

- **Geometry** — every `MANIFOLD_SOLID_BREP` / `SHELL_BASED_SURFACE_MODEL` as edge
  polylines. Edges are de-duplicated: each is referenced by two `ORIENTED_EDGE`s, and
  drawing both doubles the voxel cost for identical pixels.
- **Assembly placement** — walked through `CONTEXT_DEPENDENT_SHAPE_REPRESENTATION` →
  `REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION` → `ITEM_DEFINED_TRANSFORMATION`.
  Without this every component collapses onto the origin in one heap, which is the
  classic wrong-looking STEP import.
- **Units** — `SI_UNIT` prefixes and `CONVERSION_BASED_UNIT`, so an inch or metre file
  lands at the right size instead of 25.4x or 1000x out.
- **Colour** — `STYLED_ITEM` → `COLOUR_RGB` per solid.
- **Designators** — the name is taken from the assembly chain, not just the leaf. Altium
  nests the vendor body inside a per-placement node, so the leaf product is called
  something like `CRCW060310R5FKEC` while `R2` sits one level up. Solids are then matched
  against the designators from the placement file, by exact match only — a fuzzy match
  would put the wrong part number beside the wrong body, which is worse than no link.

On that board: **31 solids, 30 matched to a designator, 1670 mm of edge ≈ 10,100 voxels**
of the default 150,000 budget.

### What is approximated

`B_SPLINE_CURVE_WITH_KNOTS` and the other analytic curves (`ELLIPSE`, `HYPERBOLA`,
`PARABOLA`) are drawn as the straight chord between their end vertices. On mechanical
parts these are fillet blends and the chord is visually close. The import note reports how
many were approximated, so it is never a silent lie.

**Surfaces are not read at all.** That is deliberate, not a gap to be filled by default —
see the reasoning above.

### If you do want surfaces

Tessellate to a mesh and import that instead; the mesh path samples surfaces only (never a
solid fill), so it stays legible. `gmsh` is the lightest route — one pip install, headless,
OpenCASCADE inside, reads STEP directly:

```bash
pip install gmsh
```

```bash
gmsh board.step -2 -format stl -o board.stl
```

FreeCAD's `freecadcmd` also works if it is already installed. Either way, drop the
resulting `.stl` in the same folder and it imports alongside the wireframe.
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
draw order is priority order, so what you lose first is labels, then
geometry. If a board looks thin, either raise the budget or reduce what is drawn (turn off
pours, isolate a layer, lower the mesh point budget). The on-glass readout shows
`N VOX` and `+N DROPPED` so you can see it happening rather than guessing.


---

## Design folder trees

A real design folder is a tree: gerbers in one sub-folder, the 3D model in another,
schematics and drawings somewhere else, and a BOM and placement file next to the assembly
outputs. Point EDes at the **top** of that tree and it walks the whole thing. Nothing has to
be named or arranged a particular way — every file is classified by its extension, its
header, and the folder it sits in.

```
MyBoard/                    <- point EDes here
├─ 01_Schematic/  board_schematic.pdf        -> inventory (sheet count)
├─ 02_PCB/
│  ├─ Gerbers/   board-F_Cu.gbr, …, board.drl -> layer stack + drills   (drawn)
│  └─ backup/    (ignored)
├─ 03_Assembly/  board-pos.csv                -> component placement    (drawn)
│                board_BOM.csv                -> values + footprints    (labels)
├─ 04_3D/        board.step / board.stl       -> mechanical model       (drawn if STL)
└─ README.md                                  -> inventory
```

**What the walk does**

- Recurses to 12 levels, up to 20 000 files, and reports if it stops early.
- Skips folders that never hold anything useful: `.git`, `.svn`, `backup`, `backups`,
  `autosave`, `node_modules`, `obj`, `bin`, `cache`, `temp`, `tmp`, `__MACOSX`.
- **De-duplicates** by file name + size, shallowest copy first. Fab output copied into a
  release folder is the normal case; it imports once and the note says how many duplicates
  were skipped.
- Reports what every folder contributed in the PCB tab (folders walked, per-layer object
  counts, drill table, part counts, document list).

### Component placement (the big one)

A pick-and-place / centroid file is what turns a stack of copper into a board with parts on
it. EDes draws a marker per component, on the side it is mounted, with a stub showing its
rotation, and its designator (plus value, from the BOM) beside it.

Recognised: KiCad `.pos` and `-pos.csv`, Altium "Pick Place" CSV, Eagle/generic CSV — and
any CSV whose header names a designator column and an X column. Parsing is driven by the
**header row**, not by column position, so these all work:

```
Ref,Val,Package,PosX,PosY,Rot,Side              (KiCad)
Designator,…,Mid X,Mid Y,Rotation,Layer         (Altium, units suffixed per value)
Designator,X,Y,Rotation,Side                    (generic)
```

Units: a per-value suffix (`12.7mm`, `500mil`, `0.5in`) wins; otherwise the file's stated
unit; otherwise millimetres.

Toggles: **Components**, **Component designators**, **Label limit** (labels are skipped
above that part count, since text is the most expensive thing on this display).

### BOM

Any `*bom*` / `*parts*` CSV. Rows are matched to placed designators (`"R1,R2,R3"` in one
row is expanded), filling in **value** and **footprint** for the labels, and the row count
appears in the readout.

### Documents

Schematics (`.kicad_sch`, `.sch`, `.SchDoc`, `*sch*.pdf`), drawings (`.pdf`, `.dxf`, `.dwg`,
`.svg`), netlists (`.net`, `.ipc`, `.d356`), 3D CAD (`.step`, `.stp`, `.wrl`, `.iges`),
archives and readmes are **catalogued, not rendered** — EDes cannot draw a vector schematic
in a volumetric display, and pretending otherwise would be worse than saying so. What you get
is a design-package inventory: how many schematic sheets, drawings, netlists and 3D models
the folder contains, in the volume (`SCH 1/2SH  DWG 2  3D 1  NET 1  BOM 34`) and in full in
the PCB tab. PDF page counts are approximate — counted from page objects, no PDF library.

If you want a schematic *rendered*, export it to **DXF** or **SVG** and tell me — those are
vector line art that this renderer could draw directly, unlike PDF.
