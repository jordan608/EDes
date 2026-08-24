# ═══════════════════════════════════════════════════════════════════════════
#  test_protocol.py — the add-in's non-Fusion half, checked without Fusion
#
#  Run:  python fusion/tests/test_protocol.py
#
#  This exists because the add-in has to be carried to a machine with Fusion on
#  it, and debugging inside Fusion is slow: a syntax error surfaces as a
#  message box, a wrong conversion surfaces as a plausible-looking model. So
#  everything that is arithmetic or bytes is proved here first, and what travels
#  is code whose plumbing is already known to work.
#
#  What this canNOT cover, by construction: that occurrence body proxies really
#  return assembly-space coordinates, and that custom events behave as documented
#  under a real UI. Those need the Fusion machine.
# ═══════════════════════════════════════════════════════════════════════════

import json
import os
import struct
import sys

sys.path.insert(
    0, os.path.join(os.path.dirname(os.path.realpath(__file__)), "..", "FusionBridge")
)

from bridge import protocol  # noqa: E402

_fails = 0


def ok(what, passed):
    global _fails
    if not passed:
        _fails += 1
    print("%s  %s" % ("PASS" if passed else "FAIL", what))


def read_frame(buf):
    """Decode a frame the way FusionWire.cs does, so the two agree by test.

    Payload is vertices (3 floats each) THEN indices (3 uint32 each) — the
    header's own vertex/triangle counts, summed across every body, say
    exactly where the split falls, so this needs no length prefix of its own.
    """
    assert buf[:4] == protocol.MAGIC, "bad magic"
    (hlen,) = struct.unpack("<I", buf[4:8])
    header = json.loads(buf[8 : 8 + hlen].decode("utf-8"))
    payload = buf[8 + hlen :]

    total_verts = sum(b["vertices"] for b in header["bodies"])
    total_tris = sum(b["triangles"] for b in header["bodies"])
    vbytes = total_verts * 3 * 4
    ibytes = total_tris * 3 * 4

    vertices = list(struct.unpack("<%df" % (total_verts * 3), payload[:vbytes]))
    indices = list(struct.unpack("<%dI" % (total_tris * 3), payload[vbytes : vbytes + ibytes]))
    return header, vertices, indices


def cube(s=10.0):
    """A cube in CENTIMETRES, as Fusion would report it. 8 nodes, 12 triangles."""
    coords = [
        0, 0, 0,  s, 0, 0,  s, s, 0,  0, s, 0,
        0, 0, s,  s, 0, s,  s, s, s,  0, s, s,
    ]
    idx = [
        0, 3, 2,  0, 2, 1,      # bottom
        4, 5, 6,  4, 6, 7,      # top
        0, 1, 5,  0, 5, 4,
        2, 3, 7,  2, 7, 6,
        1, 2, 6,  1, 6, 5,
        3, 0, 4,  3, 4, 7,
    ]
    return [float(c) for c in coords], idx


# ── Units. The two conversions run in OPPOSITE directions. ───────────────────
print("=== units ===")

ok("a coordinate goes cm -> mm, x10 (%.1f)" % protocol.cm_to_mm(1.0),
   abs(protocol.cm_to_mm(1.0) - 10.0) < 1e-9)

# The trap: surfaceTolerance is ALSO centimetres, so the tolerance divides while
# the coordinates multiply. Asserting both in one place is the point.
ok("a tolerance goes mm -> cm, /10 (%.3f)" % protocol.tolerance_mm_to_cm(0.4),
   abs(protocol.tolerance_mm_to_cm(0.4) - 0.04) < 1e-9)
ok("the two conversions are inverses, not the same direction",
   abs(protocol.cm_to_mm(protocol.tolerance_mm_to_cm(0.4)) - 0.4) < 1e-9)

# Zero would ask the mesher for infinite detail, on the UI thread.
ok("a zero tolerance is clamped, not passed through (%.5f)"
   % protocol.tolerance_mm_to_cm(0.0), protocol.tolerance_mm_to_cm(0.0) > 0)
ok("an absurd tolerance is clamped too (%.3f)" % protocol.tolerance_mm_to_cm(1e9),
   protocol.tolerance_mm_to_cm(1e9) <= protocol.MAX_TOLERANCE_MM / 10.0 + 1e-9)


# ── The unit conversion, and the indexed mesh it now travels as ─────────────
print()
print("=== indexed mesh, vertices kept separate from triangles ===")

coords_cm, idx = cube(1.0)          # a 1 cm cube == 10 mm
verts = protocol.convert_vertices_mm(coords_cm)
tris_idx = protocol.valid_triangle_indices(idx, len(verts) // 3)

ok("8 vertices came out, none duplicated (%d)" % (len(verts) // 3), len(verts) // 3 == 8)
ok("3 floats each, nothing ragged (%d)" % len(verts), len(verts) % 3 == 0)
ok("12 triangles' worth of indices (%d)" % (len(tris_idx) // 3), len(tris_idx) // 3 == 12)

# THE units check. A 1 cm cube must arrive as 10 mm. Getting this wrong gives a
# model ten times too small that looks entirely plausible.
ok("a 1 cm cube arrives as 10 mm (max %.1f)" % max(verts), abs(max(verts) - 10.0) < 1e-6)
ok("and starts at 0 (min %.1f)" % min(verts), abs(min(verts)) < 1e-6)

# Two triangles sharing an edge must share the same two vertex NUMBERS — the
# entire point of sending an indexed mesh instead of a soup, and the property
# a future cutting-plane contour walk depends on.
ok("adjacent triangles share vertex numbers, not just positions",
   set(idx[0:3]) & set(idx[3:6]) == {0, 2})

# Out-of-range indices lose a triangle rather than crashing the add-in on the
# main thread, where an exception would surface as a Fusion error dialog --
# and unlike the old soup format, this can never hand the reader an index
# that has nothing to look up.
bad = protocol.valid_triangle_indices([0, 1, 999, 0, 1, 2], len(verts) // 3)
ok("an out-of-range index drops that triangle only (%d left)" % (len(bad) // 3),
   len(bad) // 3 == 1)
ok("a ragged index list does not throw",
   protocol.valid_triangle_indices([0, 1], len(verts) // 3) == [])
ok("an empty mesh is empty, not an error", protocol.valid_triangle_indices([], 0) == [])


# ── Requests ────────────────────────────────────────────────────────────────
print()
print("=== request parsing ===")

r = protocol.parse_request('{"cmd":"geometry","tolerance_mm":0.25,"max_triangles":1000}')
ok("a geometry request parses (%s)" % r["cmd"], r["cmd"] == "geometry")
ok("its tolerance survives (%.2f)" % r["tolerance_mm"], abs(r["tolerance_mm"] - 0.25) < 1e-9)
ok("its cap survives (%d)" % r["max_triangles"], r["max_triangles"] == 1000)

ok("rev parses", protocol.parse_request('{"cmd":"rev"}')["cmd"] == "rev")
ok("ping parses", protocol.parse_request('{"cmd":"ping"}')["cmd"] == "ping")

# Every malformed input must become a reportable 'bad', never an exception: the
# worker thread has no way to show an error and would just drop the connection.
for junk in ["", "   ", "not json", "[]", "null", '{"cmd":"drop tables"}', "{}"]:
    p = protocol.parse_request(junk)
    ok("%-22r -> bad, with a reason" % junk,
       p["cmd"] == "bad" and len(p["error"]) > 0)

ok("a missing tolerance falls back to the default",
   abs(protocol.parse_request('{"cmd":"geometry"}')["tolerance_mm"]
       - protocol.DEFAULT_TOLERANCE_MM) < 1e-9)
ok("a non-numeric tolerance falls back rather than throwing",
   abs(protocol.parse_request('{"cmd":"geometry","tolerance_mm":"fine"}')["tolerance_mm"]
       - protocol.DEFAULT_TOLERANCE_MM) < 1e-9)
ok("an absurd cap is clamped",
   protocol.parse_request('{"cmd":"geometry","max_triangles":99999999999}')["max_triangles"]
   <= protocol.MAX_TRIANGLE_CAP)


# ── Framing. This has to agree with FusionWire.cs exactly. ──────────────────
print()
print("=== frame building ===")

coords_cm, idx = cube(1.0)
verts = protocol.convert_vertices_mm(coords_cm)
tris_idx = protocol.valid_triangle_indices(idx, len(verts) // 3)

frame = protocol.build_frame(
    [
        {"path": "A:1/Cube:1", "name": "Cube", "visible": True,
         "vertices": verts, "indices": tris_idx},
        {"path": "B:1/Cube:1", "name": "Cube", "visible": False,
         "vertices": verts, "indices": tris_idx},
    ],
    document="IRSensor",
    revision="abc123",
)
header, fverts, findices = read_frame(frame)

ok("the magic is right", frame[:4] == b"EDS1")
ok("unit is declared mm (%s)" % header["unit"], header["unit"] == "mm")
ok("the document name is carried (%s)" % header["document"], header["document"] == "IRSensor")
ok("the revision is carried (%s)" % header["revision"], header["revision"] == "abc123")
ok("two bodies (%d)" % len(header["bodies"]), len(header["bodies"]) == 2)

# Offsets are computed by build_frame, not supplied, because an offset that
# disagrees with the payload is the one error here that yields plausible-looking
# wrong geometry instead of a parse failure.
ok("vertex offsets are sequential in VERTICES, not bytes (%d, %d)"
   % (header["bodies"][0]["vertexOffset"], header["bodies"][1]["vertexOffset"]),
   header["bodies"][0]["vertexOffset"] == 0 and header["bodies"][1]["vertexOffset"] == 8)
ok("triangle offsets are sequential too (%d, %d)"
   % (header["bodies"][0]["triangleOffset"], header["bodies"][1]["triangleOffset"]),
   header["bodies"][0]["triangleOffset"] == 0 and header["bodies"][1]["triangleOffset"] == 12)
ok("the payload holds both bodies' vertices (%d)" % (len(fverts) // 3),
   len(fverts) // 3 == 16)
ok("the payload holds both bodies' triangles (%d)" % (len(findices) // 3),
   len(findices) // 3 == 24)

# The second body's indices must point into ITS OWN slice of the shared vertex
# array (vertex 8 onward), not silently reuse the first body's — this is
# exactly the "global indices" rebasing build_frame does on the writer's side.
second_body_first_index = findices[header["bodies"][1]["triangleOffset"] * 3]
ok("the second body's indices are rebased into its own vertex slice (%d)"
   % second_body_first_index, second_body_first_index >= 8)

ok("visibility is carried per body, hidden included",
   header["bodies"][0]["visible"] is True and header["bodies"][1]["visible"] is False)
ok("duplicate names stay distinct by path (%s vs %s)"
   % (header["bodies"][0]["path"], header["bodies"][1]["path"]),
   header["bodies"][0]["path"] != header["bodies"][1]["path"])

empty = protocol.build_frame([])
h2, ev2, ei2 = read_frame(empty)
ok("an empty frame is still a valid frame", h2["ok"] and ev2 == [] and ei2 == [])

err = protocol.error_frame("Fusion is busy")
h3, _, _ = read_frame(err)
ok("an error frame says ok:false and why (%s)" % h3["note"],
   h3["ok"] is False and "busy" in h3["note"])


# ── The cap: whole bodies, never half a shell ───────────────────────────────
print()
print("=== triangle cap ===")

bodies = [
    {"path": "small", "triangles": 10, "indices": [0] * 30},
    {"path": "big", "triangles": 1000, "indices": [0] * 3000},
    {"path": "mid", "triangles": 100, "indices": [0] * 300},
]

kept, dropped = protocol.cap_bodies(bodies, 200)
ok("the cap keeps what fits, in order (%s)" % [b["path"] for b in kept],
   [b["path"] for b in kept] == ["small", "mid"])
ok("and reports what it dropped (%d)" % dropped, dropped == 1000)

# Whole bodies, so a kept body is never partial: half a tessellated enclosure is
# a torn shell that reads as a modelling error rather than a budget limit.
ok("every kept body kept ALL its triangles",
   all(len(b["indices"]) // 3 == b["triangles"] for b in kept))

kept2, dropped2 = protocol.cap_bodies(bodies, 100_000)
ok("a generous cap keeps everything (%d)" % len(kept2),
   len(kept2) == 3 and dropped2 == 0)

# Nothing fits: send the smallest rather than an empty frame, so the operator
# sees something and the note explains it.
kept3, dropped3 = protocol.cap_bodies(bodies, 1)
ok("when nothing fits, the smallest body still goes (%s)"
   % [b["path"] for b in kept3],
   len(kept3) == 1 and kept3[0]["path"] == "small" and dropped3 == 1100)

ok("an empty list is handled", protocol.cap_bodies([], 100) == ([], 0))

# The cap must be honoured against the frame that is actually built, not just the
# bookkeeping -- so build it and count.
kept4, _ = protocol.cap_bodies(bodies, 200)
h4, _, i4 = read_frame(protocol.build_frame(kept4))
ok("the built frame really is under the cap (%d <= 200)" % (len(i4) // 3),
   len(i4) // 3 <= 200)


# ── The revision token ─────────────────────────────────────────────────────
print()
print("=== revision token ===")

a = protocol.revision_token(["p1|r1|1", "p2|r2|1"])
b = protocol.revision_token(["p1|r1|1", "p2|r2|1"])
ok("the same document gives the same token (%s)" % a, a == b)
ok("a geometry change moves it",
   protocol.revision_token(["p1|r9|1", "p2|r2|1"]) != a)

# Visibility is in the token on purpose: hiding a component in Fusion should make
# the display follow without a manual refresh.
ok("a VISIBILITY change moves it too",
   protocol.revision_token(["p1|r1|0", "p2|r2|1"]) != a)
ok("adding a body moves it",
   protocol.revision_token(["p1|r1|1", "p2|r2|1", "p3|r3|1"]) != a)
ok("an empty document has a stable token",
   protocol.revision_token([]) == protocol.revision_token([]))

# A separator matters: without one, ["ab","c"] and ["a","bc"] would collide, and
# two different assemblies would look unchanged to the poller.
ok("the parts are separated, so ab|c does not collide with a|bc",
   protocol.revision_token(["ab", "c"]) != protocol.revision_token(["a", "bc"]))


print()
print("ALL PROTOCOL CHECKS PASSED" if _fails == 0 else "%d CHECK(S) FAILED" % _fails)
sys.exit(0 if _fails == 0 else 1)
