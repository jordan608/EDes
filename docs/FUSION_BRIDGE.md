# Fusion 360 bridge

Streams a whole Fusion assembly into the volume while you model it. **Fusion is
authoritative** for position, orientation and visibility; EDes applies only a scale and a
fixed origin offset.

Status: **planned, not built.** This is the agreed design. The full plan with milestones,
verification steps and risks is an artifact; this file is the part that has to stay next to
the code.

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

Origin defaults to `(0, 0, -2)` as specified. **Note that this is the top centre of the
volume** — `z = -zHalf` is the ceiling, since -Z is up — so with Fusion's +Z mapping to
display -Z, anything above the Fusion origin falls outside the volume and is clipped. That is
correct for an assembly that hangs downward from its origin; an assembly that grows upward
wants `(0, 0, +2)`. A live clipped-fraction readout sits beside the setting so it is never a
mystery.

No auto-centring and no per-component fitting: either would fight Fusion for control of
position. Scale is one setting with a one-shot "Fit once" button.

## Wire protocol

Request — one line of JSON, newline-terminated:

    {"cmd":"geometry","tolerance_mm":0.4,"max_triangles":300000}

Response:

    magic       4 bytes   "EDS1"
    headerLen   uint32    little-endian
    header      JSON, headerLen bytes
    payload     float32[] little-endian, 9 per triangle (3 verts x xyz), mm,
                          already in ASSEMBLY space

    header = {
      "ok": true,
      "unit": "mm",
      "revision": "a91f...",
      "document": "IRSensor v4",
      "bodies": [
        {"path":"Enclosure:1",       "name":"Enclosure", "visible":true,
         "triangles":19044, "offset":0},
        {"path":"Enclosure:1/PCB:1", "name":"PCB",       "visible":true,
         "triangles":812,   "offset":19044}
      ],
      "dropped": 0,
      "note": ""
    }

`path` is `occurrence.fullPathName` and is the **stable identity**: Fusion permits duplicate
component names, so `name` cannot key the legend or survive a refresh. `name` is for display.

**Normals are not transmitted.** Fusion offers `TriangleMesh.normalVectors`, but `StlMesh`
already recomputes from winding and treats stored normals as untrustworthy — a decision that
earned itself when STL files turned out to lie. Recomputing keeps one code path and a third
less on the wire.

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
| 1 | Add-in skeleton answering `ping` with the document name | yes |
| 2 | EDes client + fake server, framing and grouping | no |
| 3 | Real tessellation via occurrence body proxies | yes |
| 4 | Wire into the app: source, placement, readouts | no |
| 5 | Revision-token polling for live refresh | yes |
| 6 | Budget and tolerance negotiation | no |

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
  the per-body version in `LightingSystem`'s six-face shell map.
- **Ghost or crease-edge wireframe**, per component, from the legend. Ghost = low sample
  density, which is already how dimming works here. Crease edges = keep mesh edges whose
  adjacent face normals differ beyond a threshold, because the Fusion path has no exact edges
  and drawing every tessellation edge would be noise.
- **Section plane**, nearly free: faces are filled by point-sampling a barycentric lattice, so
  rejecting samples on one side of a plane gives an exact cut with no triangle clipping. Plus,
  eventually, aligning the active sketch's plane to `y = 0.1` and sectioning the near side, so
  you draw on the glass with the model cut back behind it.

**The interaction to get right:** exposure culling and the section plane fight each other if
combined naively — cut an assembly open and the revealed faces were classified interior a
moment ago, so the cull hides exactly what you cut open to see. The rule is *the cut re-exposes
what it reveals*: suspend exposure culling at and behind the cut plane. Written down here
because it is invisible until both features exist, at which point the section tool looks broken.

## Verified against Autodesk's docs

- [Working in a separate thread](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/Threading_UM.htm)
- [`fireCustomEvent`](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/Application_fireCustomEvent.htm)
- [`Occurrence`](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/Occurrence.htm) — body proxies, `isVisible`, `transform2`
- [`TriangleMesh`](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/TriangleMesh.htm)
- [Units](https://help.autodesk.com/cloudhelp/ENU/Fusion-360-API/files/Units_UM.htm)

Proxy coordinate space is read from that documentation and **not yet confirmed against real
geometry**. Milestone 3 exists to confirm it; the fallback is `transform2` applied per body.
