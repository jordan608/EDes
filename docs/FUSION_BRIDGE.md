# Fusion 360 bridge

Streams a whole Fusion assembly into the volume while you model it. **Fusion is
authoritative** for position, orientation and visibility; EDes applies only a scale and a
fixed origin offset.

Status: **built, untested on real Fusion.** Both halves exist and are verified without
Autodesk installed anywhere — the EDes client and renderer under `Core/Cad/`, the add-in
under `fusion/`. What remains is running it against real geometry; see *What the Fusion
machine still has to confirm* at the end.

---

## Why it is cheap

The receiving half already exists. `StlMesh` turns triangle soup into `CadFace` groups with
normals recomputed from winding, `PcbRenderer` draws `CadSolid` lists with the point light,
and the budget, legend and inspector already understand solids. The bridge does not render
anything — it delivers **triangles with names**.

Every component in the file is STEP-derived, the PCB included, so there is no Gerber stack,
no drill table and no designator matching on this path. One CAD tree.

## Two constraints that decide the architecture

**The Fusion API is main-thread only.** A socket server lives on a worker thread that never
calls `adsk`; it hands work to the main thread with `app.fireCustomEvent` and waits. A fired
event runs *when Fusion is idle*, so an open modal dialog stalls it — the worker must time out
and name that cause rather than hang.

**The Fusion API is always in centimetres**, not the document's display units. Our model is
millimetres, so every coordinate is `× 10`. A missing conversion gives a plausibly-wrong
model, which is the worst kind — hence the 10 mm cube fixture.

## Fusion owns placement, by construction

`occurrence.bRepBodies` returns body **proxies in root-component context**, so tessellating a
proxy yields assembly-space coordinates. There is no transform to transmit and no matrix maths
on either side.

Visibility is `occurrence.isVisible`, **not** `isLightBulbOn`: the bulb is only the
occurrence's own switch, while `isVisible` accounts for assembly context, so a component in a
hidden sub-assembly correctly reads as hidden.

All components are sent every time, hidden ones flagged rather than omitted — the legend stays
stable and toggling visibility in Fusion costs no re-tessellation. EDes's own legend can hide a
component locally, but a refresh re-applies Fusion's state. Fusion wins.

`Occurrence.transform` is retired; if a case ever needs an explicit matrix it must be
`transform2`.

## Where the assembly lands

    display.x =  fusion.x * 10 * scale  +  originX
    display.y =  fusion.y * 10 * scale  +  originY
    display.z = -fusion.z * 10 * scale  +  originZ     // Fusion Z up -> display -Z up

Origin is **`(0, 0, +zHalf)` — the FLOOR, centre.** `z = +zHalf` is the bottom of the volume
because -Z is up, so with Fusion's +Z mapping to display -Z the assembly stands on the floor
and grows upward through the full height.

It is stored as a **fraction** of zHalf (`FusionOriginZFrac`, default `+1`), so it means the
same thing on a VX2 and a VX2-XL — the same reasoning as the HUD anchor.

The first draft of this plan specified `(0, 0, -2)`, which is the CEILING. That clips
everything above the Fusion origin, so a normal upward-growing assembly is almost entirely
invisible. Both cases are now asserted in the suite: a floor-anchored 20 mm cube clips
nothing, and the same cube anchored to the ceiling reports 76% of its samples outside the
volume. A live clipped-fraction readout appears in the panel AND in the volume, so a wrong
origin announces itself instead of looking like a broken import.

No auto-centring and no per-component fitting: either would fight Fusion for control of
position. Scale is one setting with a one-shot "Fit once" button that computes a number and
then stops being involved.

## Wire protocol

Request — one line of JSON, newline-terminated:

    {"cmd":"geometry","tolerance_mm":0.4,"max_triangles":300000}

**Streamed, one frame per body**, not one frame holding the whole assembly: the add-in pushes
each body's frame the moment it finishes tessellating, so the worker thread can start sending
body 1 while the main thread is still tessellating body 2. A big assembly's overlap comes for
free; nothing else about the format needed to change for it to work, because every frame below
is already fully self-describing on its own.

Each frame:

    magic       4 bytes   "EDS1"
    headerLen   uint32    little-endian
    header      JSON, headerLen bytes
    payload     vertices, THEN indices — see below

**A real indexed mesh, not a flattened triangle soup.** The payload is every vertex ONCE
(float32 x,y,z, mm, already in ASSEMBLY space) followed by every triangle as three uint32
vertex indices. Two triangles sharing an edge share the same two index values on the wire —
this is what lets a cutting plane's cross-section walk the intersection as a graph of shared
edges instead of matching independent triangles' floating-point positions, and it also runs
roughly a third smaller than the soup format it replaced (Fusion's own tessellation already
comes back indexed; the old format threw that away by expanding it before sending).

    header = {
      "ok": true,
      "unit": "mm",
      "revision": "a91f...",
      "document": "IRSensor v4",
      "bodyIndex": 0, "bodyCount": 2,
      "bodies": [
        {"path":"Enclosure:1", "name":"Enclosure", "visible":true,
         "vertices":9522, "triangles":19044, "vertexOffset":0, "triangleOffset":0}
      ],
      "dropped": 0,
      "note": ""
    }

In practice a frame carries exactly one body (or, for the terminator frame that closes the
sequence, none — see below). Several bodies sharing one frame is still supported, for a
hand-built test fixture's convenience: their vertex/index data shares one payload, and every
index is GLOBAL (already offset into the shared vertex array), so a reader never has to re-base
anything itself. `bodyIndex`/`bodyCount` are a progress hint only, for a receiver that wants to
show "body 3 of 12" — every frame is fully parseable without them.

The sequence ends with a **terminator frame**: `"bodies": []`, carrying the overall `dropped`
count and `note` that a single-frame reply used to carry directly.

`path` is `occurrence.fullPathName` and is the **stable identity**: Fusion permits duplicate
component names, so `name` cannot key the legend or survive a refresh. `name` is for display.

**Normals are not transmitted.** Fusion offers `TriangleMesh.normalVectors`, but `StlMesh`
already recomputes from winding and treats stored normals as untrustworthy — a decision that
earned itself when STL files turned out to lie. Recomputing keeps one code path.

### Networking

Fusion may be on a different machine from the Voxon, so the host is a setting and the add-in
can bind a LAN address. It **defaults to localhost and requires an explicit opt-in** to listen
wider, because a LAN socket here has no authentication. Acceptable on a bench network; the
panel says so plainly. Never bind `0.0.0.0` silently.

## Live refresh

Pull, not push. A push would have to wait for Fusion to be idle anyway, so it buys no latency,
and polling keeps EDes in control of when it spends voxel budget.

The add-in maintains a cheap **revision token** — body count plus each body's `revisionId`,
hashed — updated on document and command-terminated events. EDes polls `{"cmd":"rev"}` a few
times a second, which is a dictionary read on the main thread, and fetches geometry only when
the token changes.

## Milestones

| # | What | Needs Fusion |
|---|---|---|
| 1 | Add-in skeleton answering `ping` with the document name | **done** |
| 2 | EDes client + fake server, framing and grouping | **done** |
| 3 | Real tessellation via occurrence body proxies | **written**, needs real geometry |
| 4 | Wire into the app: its own Fusion CAD mode, placement, readouts | **done** |
| 5 | Revision-token polling for live refresh | **done** |
| 6 | Budget and tolerance negotiation | **done** |

The add-in's own protocol framing, unit conversion, header assembly and triangle capping are
ordinary Python with no Fusion in them, so a stub `adsk` module makes them testable without
the tool. What travels to the Fusion machine is then one file whose plumbing already works,
leaving only the genuinely-Fusion claims to check — chiefly: **move a body 50 mm in Fusion and
its coordinates must move 50 mm** with no change on our side. That is the proxy claim, and it
is the single most important assertion in the plan.

## Stretch goals

All three answer one problem: a transparent display has no occlusion, so a closed assembly
shows its internals through its own shell and reads as fog. All three *reduce* voxel cost.

- **Outer-surface only.** Exposure, not lighting — though the instinct to call it lighting is
  sound, because a surface no exterior ray reaches is both invisible and unlit, so one pass
  answers both. Coarse occupancy grid, march inward from six directions, mark first hits as
  exposed. Per-triangle, so a half-buried part shows its exposed half. The engine already has
  the per-body version in `LightingSystem`'s six-face shell map. **Not started** for Fusion CAD.
- **Ghost or crease-edge wireframe**, per component, from the legend. Ghost = low sample
  density, which is already how dimming works here. Crease edges = keep mesh edges whose
  adjacent face normals differ beyond a threshold, because the Fusion path has no exact edges
  and drawing every tessellation edge would be noise. **Shipped**, as the per-body render-mode
  picker (`CadDrawMode`: Lit/Flat/Wireframe/Hidden, plus a global Ghost toggle) — Wireframe
  draws every tessellation edge rather than only crease edges, since that refinement was never
  built; it is noisier on a dense mesh than the crease-only version described here.
- **Section plane**, nearly free: faces are filled by point-sampling a barycentric lattice, so
  rejecting samples on one side of a plane gives an exact cut with no triangle clipping. Plus,
  eventually, aligning the active sketch's plane to `y = 0.1` and sectioning the near side, so
  you draw on the glass with the model cut back behind it. **Shipped** as `CutPlane` in
  `CadSceneRenderer` (fixed to the assembly's own axes, not the display's, so rotating the
  scene never changes what is cut away), with a highlighted colour band for the cut face. Still
  missing a real flat CAP where the plane cuts through a solid body — the mesh is surface-only,
  so today's cut exposes the shell's own far interior wall rather than a true cross-section; a
  real cap needs a mesh-slicing pass (walk the plane/mesh intersection, close the contour, fill
  it) that the indexed wire format above exists to make tractable, but that pass itself is not
  built yet. The active-sketch-plane alignment was never built either.

**The interaction to get right:** exposure culling and the section plane fight each other if
combined naively — cut an assembly open and the revealed faces were classified interior a
moment ago, so the cull hides exactly what you cut open to see. The rule is *the cut re-exposes
what it reveals*: suspend exposure culling at and behind the cut plane. Written down here
because it is invisible until both features exist, at which point the section tool looks broken.

## What the Fusion machine still has to confirm

Everything below is written and passing against a stub. None of it has met real Fusion, and
these are the claims a stub cannot settle. Run `python fusion/tests/probe.py` first — it
isolates "is the add-in working" from "is EDes working".

1. **Body proxies bake the assembly transform.** THE assertion. Move a body 50 mm in Fusion
   and its coordinates must move 50 mm with no change on our side. Then rotate it; then nest
   it in a sub-assembly and move the PARENT. If this fails, the fallback is `transform2`
   applied per body — more code, same outcome.
2. **cm → mm on real geometry.** `probe.py geometry` prints the extent. A 10 mm cube must
   read `0.00..10.00`. If it reads `0.00..1.00`, the conversion is missing.
3. **Custom events dispatch under a real UI.** The stub models "Fusion is busy" with a flag;
   only the real thing proves an open modal dialog behaves the same way and that the
   timeout message is the one you actually see.
4. **`createMeshCalculator().calculate()` and `surfaceTolerance`** exist with those exact
   names and semantics. Read from the docs, never executed. Note `surfaceTolerance` is in
   CENTIMETRES like everything else, so the tolerance conversion runs the OPPOSITE way to
   the coordinates — `tolerance_mm_to_cm` divides while `cm_to_mm` multiplies.
5. **`occurrence.fullPathName` format.** Used only as an opaque key, so the format does not
   matter — but it must be stable across refreshes or the legend loses its rows.
6. **Tessellation cost on a real assembly.** The tolerance is the throttle, and it runs on
   Fusion's UI thread because the API gives no choice. Worth knowing what 0.4 mm costs on
   your actual enclosure before turning on Follow changes.

## Verified against Autodesk's docs

- [Working in a separate thread](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/Threading_UM.htm)
- [`fireCustomEvent`](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/Application_fireCustomEvent.htm)
- [`Occurrence`](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/Occurrence.htm) — body proxies, `isVisible`, `transform2`
- [`TriangleMesh`](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/TriangleMesh.htm)
- [Units](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/Units_UM.htm)

Proxy coordinate space is read from that documentation and **not yet confirmed against real
geometry**. Milestone 3 exists to confirm it; the fallback is `transform2` applied per body.
