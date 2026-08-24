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


def build_frame(bodies, document="", revision="", dropped=0, note="", ok=True):
    """Assemble a response frame.

        magic       4 bytes   b"EDS1"
        headerLen   uint32    little-endian
        header      JSON
        payload     float32[] little-endian, 9 per triangle, MILLIMETRES

    bodies: list of dicts with keys path, name, visible, tris (a flat sequence of
    floats, 9 per triangle, already in mm).

    Offsets are computed here rather than supplied, because an offset that
    disagrees with the payload is the one error in this format that produces
    plausible-looking wrong geometry instead of a parse failure.
    """
    header_bodies = []
    payload = []
    offset = 0

    for b in bodies:
        tris = b.get("tris") or []
        n = len(tris) // 9
        header_bodies.append(
            {
                "path": str(b.get("path", "")),
                "name": str(b.get("name", "")),
                "visible": bool(b.get("visible", True)),
                "triangles": n,
                "offset": offset,
            }
        )
        payload.extend(tris[: n * 9])
        offset += n

    header = {
        "ok": bool(ok),
        "unit": "mm",
        "revision": str(revision),
        "document": str(document),
        "bodies": header_bodies,
        "dropped": int(dropped),
        "note": str(note),
    }

    hb = json.dumps(header, separators=(",", ":")).encode("utf-8")
    out = bytearray()
    out += MAGIC
    out += struct.pack("<I", len(hb))
    out += hb
    if payload:
        out += struct.pack("<%df" % len(payload), *payload)
    return bytes(out)


def error_frame(note):
    """A frame that says why there is no geometry.

    Sent instead of closing silently. A client that gets nothing cannot tell a
    crashed add-in from a busy one from a wrong port, and the busy case is the
    common one here — Fusion only services the request queue when it is idle.
    """
    return build_frame([], note=note, ok=False)


def expand_triangles(node_coords_cm, node_indices):
    """Indexed mesh in centimetres to a flat triangle soup in millimetres.

    Nine floats per triangle rather than indices-plus-vertices. That is three
    times the bytes for a closed mesh, and worth it: the receiving end already
    reads triangle soup from STL files, so sharing that path means one
    implementation of normal recomputation and face grouping rather than two.
    Bandwidth is not the constraint here — Fusion's tessellation is.

    Indices that fall outside the coordinate array are skipped rather than
    trusted; a malformed mesh should lose a triangle, not crash the add-in on the
    main thread.
    """
    out = []
    n_nodes = len(node_coords_cm) // 3

    for i in range(0, len(node_indices) - 2, 3):
        a, b, c = node_indices[i], node_indices[i + 1], node_indices[i + 2]
        if not (0 <= a < n_nodes and 0 <= b < n_nodes and 0 <= c < n_nodes):
            continue
        for idx in (a, b, c):
            j = idx * 3
            out.append(cm_to_mm(node_coords_cm[j]))
            out.append(cm_to_mm(node_coords_cm[j + 1]))
            out.append(cm_to_mm(node_coords_cm[j + 2]))

    return out
