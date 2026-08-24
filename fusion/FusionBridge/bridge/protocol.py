# ═══════════════════════════════════════════════════════════════════════════
#  protocol.py — the wire format and the unit maths, with NO Fusion in it
#
#  Deliberately importable without Autodesk anything. Everything here is
#  arithmetic and bytes, so it can be unit-tested on a machine that has never
#  seen Fusion — which is the whole reason this file is separate from the add-in
#  that uses it. What eventually gets carried to the Fusion machine is then code
#  whose plumbing is already known to work.
#
#  UNITS. Read this before touching anything:
#
#      Fusion's API is ALWAYS centimetres, regardless of the document's display
#      units. EDes works in millimetres. So there are two conversions and they
#      run in OPPOSITE directions:
#
#          coordinates out :  cm -> mm    (multiply by 10)
#          tolerance in    :  mm -> cm    (divide by 10)
#
#      Getting either backwards is silent: coordinates come out ten times too
#      small but perfectly plausible, and a tolerance ten times too fine turns a
#      rounded part into millions of triangles for no visible gain. Both live in
#      this file, once, with tests.
# ═══════════════════════════════════════════════════════════════════════════

import hashlib
import json
import struct

MAGIC = b"EDS1"

#: Fusion's database length unit is the centimetre. Everything crossing this
#: boundary is scaled by this, and nothing else in the add-in may do it again.
MM_PER_CM = 10.0

#: Defaults used when a request omits a field, so a hand-typed request still works.
DEFAULT_TOLERANCE_MM = 0.4
DEFAULT_MAX_TRIANGLES = 300_000

#: Sanity bounds on what a client may ask for. A tolerance of zero would ask the
#: mesher for infinite detail; a huge cap would let one request exhaust memory.
MIN_TOLERANCE_MM = 0.005
MAX_TOLERANCE_MM = 50.0
MAX_TRIANGLE_CAP = 20_000_000


def cm_to_mm(v):
    """One coordinate, Fusion's centimetres to our millimetres."""
    return v * MM_PER_CM


def tolerance_mm_to_cm(mm):
    """A requested tolerance in mm, as the centimetres surfaceTolerance wants.

    Clamped rather than trusted: surfaceTolerance is the single biggest lever on
    how long Fusion spends tessellating, and a zero would ask for infinite detail
    on the main thread, which is the UI thread.
    """
    mm = max(MIN_TOLERANCE_MM, min(MAX_TOLERANCE_MM, float(mm)))
    return mm / MM_PER_CM


def parse_request(line):
    """One request line to a dict of validated parameters.

    Never raises. An unreadable request becomes a 'bad' command that the caller
    answers with an error frame, because a client that sent nonsense still
    deserves to be told so rather than left waiting on a closed socket.
    """
    out = {
        "cmd": "bad",
        "tolerance_mm": DEFAULT_TOLERANCE_MM,
        "max_triangles": DEFAULT_MAX_TRIANGLES,
        "error": "",
    }

    if not line or not line.strip():
        out["error"] = "empty request"
        return out

    try:
        req = json.loads(line)
    except Exception as ex:
        out["error"] = "request is not JSON: %s" % ex
        return out

    if not isinstance(req, dict):
        out["error"] = "request is not a JSON object"
        return out

    cmd = str(req.get("cmd", "")).strip().lower()
    if cmd not in ("geometry", "rev", "ping"):
        out["error"] = "unknown command %r" % cmd
        return out
    out["cmd"] = cmd

    try:
        out["tolerance_mm"] = max(
            MIN_TOLERANCE_MM,
            min(MAX_TOLERANCE_MM, float(req.get("tolerance_mm", DEFAULT_TOLERANCE_MM))),
        )
    except (TypeError, ValueError):
        pass

    try:
        out["max_triangles"] = max(
            1, min(MAX_TRIANGLE_CAP, int(req.get("max_triangles", DEFAULT_MAX_TRIANGLES)))
        )
    except (TypeError, ValueError):
        pass

    return out


def cap_bodies(bodies, max_triangles):
    """Honour a triangle cap by dropping WHOLE bodies, largest last.

    Whole bodies rather than truncating one mid-surface: half a tessellated
    enclosure is a torn shell with a hole where the mesher happened to stop, and
    it reads as a modelling error rather than as a budget limit. A missing
    component is at least obviously missing, and the count is reported.

    Order is preserved for what is kept, so the same assembly always drops the
    same bodies and the display does not reshuffle between refreshes.

    bodies is a list of dicts with 'triangles' and 'tris'.
    Returns (kept, dropped_triangle_count).
    """
    kept, total, dropped = [], 0, 0

    for b in bodies:
        n = int(b.get("triangles", 0))
        if total + n <= max_triangles:
            kept.append(b)
            total += n
        else:
            dropped += n

    # Nothing fit at all: keep the single smallest body rather than sending an
    # empty frame, so the operator sees SOMETHING and the note explains why.
    if not kept and bodies:
        smallest = min(bodies, key=lambda b: int(b.get("triangles", 0)))
        kept = [smallest]
        dropped = sum(int(b.get("triangles", 0)) for b in bodies) - int(
            smallest.get("triangles", 0)
        )

    return kept, dropped


def revision_token(entries):
    """A cheap change token for the whole document.

    Cheap is the point: EDes polls this several times a second, so it must be a
    read of things Fusion already knows and never a tessellation. Anything that
    changes geometry, placement or visibility has to move it, or the display goes
    stale without saying so.

    entries: an iterable of stringable parts (paths, revision ids, visibility).
    """
    h = hashlib.sha1()
    for e in entries:
        h.update(str(e).encode("utf-8", "replace"))
        h.update(b"\x1f")
    return h.hexdigest()[:16]


def build_frame(bodies, document="", revision="", dropped=0, note="", ok=True,
                body_index=None, body_count=None):
    """Assemble a response frame.

        magic         4 bytes   b"EDS1"
        headerLen     uint32    little-endian
        header        JSON
        payload       vertices, THEN indices — see below

    bodies: list of dicts with keys path, name, visible, vertices (a flat
    sequence of floats, 3 per vertex, already in MILLIMETRES) and indices (a
    flat sequence of ints, 3 per triangle — a REAL indexed mesh, not a
    triangle soup: two triangles sharing an edge share the same two vertex
    numbers, which is what lets a cutting plane walk that shared edge to
    build a closed cut contour instead of hoping two independently-computed
    intersection points land on the same float. See CadPlacement.cs's header
    for why this replaced the flat soup format.

    Multiple bodies concatenate into ONE shared vertex payload and ONE shared
    index payload, each body's indices already offset (GLOBAL, not
    body-local) to point into the shared vertex array directly — a reader
    never needs to re-base them. In practice the streaming send (see
    FusionBridge.py) only ever puts 0 or 1 body in a frame; the general case
    is kept here because it costs nothing and a hand-built test frame with
    several bodies is a convenient fixture.

    vertexOffset/triangleOffset are computed here rather than supplied, for
    the same reason the old soup format's `offset` was: a value that
    disagrees with the payload is the one error in this format that produces
    plausible-looking wrong geometry instead of a parse failure.

    body_index / body_count: present only when ONE geometry response is being
    sent as a SEQUENCE of single-body frames rather than one frame holding
    every body (see FusionBridge.py's streaming send). Purely a progress hint
    for the receiving end — every frame remains independently parseable on its
    own, so an old client that has never heard of these two fields still reads
    everything else correctly; they are just extra keys it does not look at.
    """
    header_bodies = []
    vertex_payload = []
    index_payload = []
    voffset = 0
    toffset = 0

    for b in bodies:
        verts = b.get("vertices") or []
        vcount = len(verts) // 3
        raw_idx = b.get("indices") or []
        tcount = len(raw_idx) // 3

        header_bodies.append(
            {
                "path": str(b.get("path", "")),
                "name": str(b.get("name", "")),
                "visible": bool(b.get("visible", True)),
                "vertices": vcount,
                "triangles": tcount,
                "vertexOffset": voffset,
                "triangleOffset": toffset,
            }
        )
        vertex_payload.extend(verts[: vcount * 3])
        # Shift to GLOBAL indices so the reader never has to re-base them —
        # this body's own vertex 0 is voffset in the shared vertex array.
        index_payload.extend(idx + voffset for idx in raw_idx[: tcount * 3])
        voffset += vcount
        toffset += tcount

    header = {
        "ok": bool(ok),
        "unit": "mm",
        "revision": str(revision),
        "document": str(document),
        "bodies": header_bodies,
        "dropped": int(dropped),
        "note": str(note),
    }
    if body_index is not None:
        header["bodyIndex"] = int(body_index)
    if body_count is not None:
        header["bodyCount"] = int(body_count)

    hb = json.dumps(header, separators=(",", ":")).encode("utf-8")
    out = bytearray()
    out += MAGIC
    out += struct.pack("<I", len(hb))
    out += hb
    if vertex_payload:
        out += struct.pack("<%df" % len(vertex_payload), *vertex_payload)
    if index_payload:
        out += struct.pack("<%dI" % len(index_payload), *index_payload)
    return bytes(out)


def error_frame(note):
    """A frame that says why there is no geometry.

    Sent instead of closing silently. A client that gets nothing cannot tell a
    crashed add-in from a busy one from a wrong port, and the busy case is the
    common one here — Fusion only services the request queue when it is idle.
    """
    return build_frame([], note=note, ok=False)


def convert_vertices_mm(node_coords_cm):
    """Fusion's node coordinate array, centimetres to millimetres. Every vertex
    is sent exactly once — the whole reason this replaced the old triangle-soup
    format, which repeated a shared vertex once per triangle that touched it."""
    return [cm_to_mm(v) for v in node_coords_cm]


def valid_triangle_indices(node_indices, vertex_count):
    """Fusion's flat triangle-index list, with any triangle referencing an
    out-of-range vertex dropped — a malformed mesh should lose a triangle, not
    crash the add-in on the main thread, or (worse, for an INDEXED format)
    hand the reader an index it cannot safely look up.

    Unlike the old expand_triangles, this never touches vertex data: indices
    stay indices, so two triangles sharing an edge still share the same two
    numbers on the wire, which is the property a cutting plane needs to walk
    the cut as a closed loop instead of matching duplicated float positions.
    """
    out = []
    for i in range(0, len(node_indices) - 2, 3):
        a, b, c = node_indices[i], node_indices[i + 1], node_indices[i + 2]
        if 0 <= a < vertex_count and 0 <= b < vertex_count and 0 <= c < vertex_count:
            out.append(a); out.append(b); out.append(c)
    return out
