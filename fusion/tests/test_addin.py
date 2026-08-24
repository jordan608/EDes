# ═══════════════════════════════════════════════════════════════════════════
#  test_addin.py — the add-in RUN, against a fake Fusion
#
#  Run:  python fusion/tests/test_addin.py
#
#  test_protocol.py covers the arithmetic. This covers the part that would
#  otherwise never execute until it was on the Fusion machine: the add-in
#  lifecycle, the socket, and the worker-thread → custom-event → main-thread
#  marshalling that the whole file is shaped around.
#
#  The stub dispatches events on a SEPARATE thread, so "the main thread" is real
#  here. It also has a `busy` flag, which is what an open modal dialog does to the
#  real API — so the timeout path is exercised rather than assumed.
#
#  Still out of reach, and honestly so: whether real occurrence body proxies
#  return assembly-space coordinates, and what a real MeshCalculator emits.
# ═══════════════════════════════════════════════════════════════════════════

import json
import os
import socket
import struct
import sys
import threading
import time

HERE = os.path.dirname(os.path.realpath(__file__))
sys.path.insert(0, os.path.join(HERE, "stub_adsk"))      # the fake adsk
sys.path.insert(0, os.path.join(HERE, "..", "FusionBridge"))

import adsk                     # noqa: E402  (the stub)
import adsk.core                # noqa: E402
import adsk.fusion              # noqa: E402

import FusionBridge             # noqa: E402
from bridge import protocol     # noqa: E402

_fails = 0


def ok(what, passed):
    global _fails
    if not passed:
        _fails += 1
    print("%s  %s" % ("PASS" if passed else "FAIL", what))


def ask(cmd, port, tolerance=0.4, timeout=10.0):
    line = json.dumps({"cmd": cmd, "tolerance_mm": tolerance,
                       "max_triangles": 300000})
    s = socket.create_connection(("127.0.0.1", port), timeout=5.0)
    s.settimeout(timeout)
    try:
        s.sendall((line + "\n").encode("utf-8"))
        chunks = []
        while True:
            b = s.recv(1 << 16)
            if not b:
                break
            chunks.append(b)
        return b"".join(chunks)
    finally:
        s.close()


def decode(buf):
    if len(buf) < 8 or buf[:4] != b"EDS1":
        raise ValueError("not a frame: %r" % buf[:16])
    (hlen,) = struct.unpack("<I", buf[4:8])
    header = json.loads(buf[8 : 8 + hlen].decode("utf-8"))
    payload = buf[8 + hlen :]
    n = len(payload) // 4
    floats = list(struct.unpack("<%df" % n, payload[: n * 4])) if n else []
    return header, floats


def build_document():
    """A root body, a shown occurrence, a HIDDEN occurrence, and a nested one.

    The root body matters: bodies modelled directly in the root component belong
    to no occurrence, so a walk of allOccurrences alone silently misses them —
    and a part designed without ever making a component is exactly what someone
    tries first.
    """
    F = adsk.fusion
    root_body = F.BRepBody("RootBox", size_cm=1.0, revision="rb1")

    shown = F.Occurrence("Shown:1", [F.BRepBody("Cube", size_cm=2.0, revision="c1")])
    hidden = F.Occurrence("Hidden:1", [F.BRepBody("Cube", size_cm=3.0, revision="c2")],
                          visible=False)
    nested = F.Occurrence("Bolt:1", [F.BRepBody("Bolt", size_cm=0.5, revision="b1")],
                          parent_path="Shown:1")
    # A body switched off inside a SHOWN occurrence: visibility is the AND of the
    # two, so this must read hidden even though its parent is shown.
    partly = F.Occurrence("Partly:1",
                          [F.BRepBody("Off", size_cm=1.0, visible=False, revision="o1")])

    root = F.Component(bodies=[root_body],
                       occurrences=[shown, hidden, nested, partly])
    return F.Design(root)


def main():
    port = 47812
    FusionBridge.PORT = port
    FusionBridge.HOST = "127.0.0.1"
    FusionBridge.IDLE_TIMEOUT_S = 3.0

    app = adsk.core.Application.get()
    app.activeProduct = build_document()

    print("=== lifecycle ===")
    FusionBridge.run(None)
    time.sleep(0.3)
    ok("run() started a listener and logged it (%s)"
       % (app.logs[-1] if app.logs else "no log"),
       any("listening" in m for m in app.logs))
    ok("autoTerminate(False) was called, or the add-in would be torn down",
       adsk._terminate is False)
    ok("the handler is referenced at MODULE level, so it cannot be collected",
       len(FusionBridge._handlers) == 1)

    print()
    print("=== ping proves the round trip ===")
    header, _ = decode(ask("ping", port))
    # The document name can only be read on the main thread, so getting it back
    # proves the marshalling, not merely that the socket is open.
    ok("ping returns the document name from the main thread (%s)"
       % header.get("document"), header.get("document") == "StubDoc")
    ok("and a revision token (%s)" % header.get("revision"),
       len(header.get("revision", "")) > 0)

    print()
    print("=== geometry ===")
    header, floats = decode(ask("geometry", port))
    ok("ok is true (%s)" % header.get("ok"), header.get("ok") is True)
    ok("the unit is declared mm (%s)" % header.get("unit"), header.get("unit") == "mm")

    bodies = header["bodies"]
    paths = [b["path"] for b in bodies]
    ok("five bodies were found (%d)" % len(bodies), len(bodies) == 5)

    # The root body is the one a naive allOccurrences walk loses.
    ok("a body in the ROOT component was not missed (%s)" % paths[0],
       any(p == "RootBox" for p in paths))
    ok("a nested occurrence keeps its full path (%s)"
       % next((p for p in paths if "Bolt" in p), "none"),
       any(p.startswith("Shown:1/Bolt:1/") for p in paths))

    def find(frag):
        return next(b for b in bodies if frag in b["path"])

    ok("a shown body reads visible", find("Shown:1/Cube") ["visible"] is True)
    ok("a body in a HIDDEN occurrence reads hidden",
       find("Hidden:1/Cube")["visible"] is False)
    # Visibility is the AND of occurrence and body: a body switched off inside a
    # shown component must read hidden.
    ok("a body switched off inside a SHOWN occurrence reads hidden",
       find("Partly:1/Off")["visible"] is False)

    # Hidden bodies are still SENT, so the legend stays stable and toggling
    # visibility in Fusion costs no re-tessellation.
    ok("hidden bodies are still sent, with their triangles",
       find("Hidden:1/Cube")["triangles"] == 12)

    print()
    print("=== units, the conversion that matters ===")
    # Each stub body is a box of `size_cm` centimetres. The 2 cm cube must arrive
    # as 20 mm. This is the assertion that catches a missing x10.
    cube = find("Shown:1/Cube")
    off = cube["offset"] * 9
    xs = floats[off : off + cube["triangles"] * 9 : 3]
    ok("a 2 cm body arrives spanning 20 mm (max %.2f)" % max(xs),
       abs(max(xs) - 20.0) < 1e-4)

    root = find("RootBox")
    roff = root["offset"] * 9
    rxs = floats[roff : roff + root["triangles"] * 9 : 3]
    ok("a 1 cm body arrives spanning 10 mm (max %.2f)" % max(rxs),
       abs(max(rxs) - 10.0) < 1e-4)

    # Offsets must address each body's own slice; sharing one would draw the same
    # geometry twice and nothing would look obviously wrong.
    ok("each body's offset addresses its own triangles",
       abs(max(xs) - max(rxs)) > 1.0)

    print()
    print("=== the revision token tracks changes ===")
    r1 = decode(ask("rev", port))[0]["revision"]
    r2 = decode(ask("rev", port))[0]["revision"]
    ok("an unchanged document gives a stable token (%s)" % r1, r1 == r2)

    app.activeProduct.rootComponent.bRepBodies[0].revisionId = "CHANGED"
    r3 = decode(ask("rev", port))[0]["revision"]
    ok("editing geometry moves it (%s -> %s)" % (r1, r3), r3 != r1)

    app.activeProduct.rootComponent.allOccurrences[0].isVisible = False
    r4 = decode(ask("rev", port))[0]["revision"]
    ok("hiding a component moves it too (%s)" % r4, r4 != r3)
    app.activeProduct.rootComponent.allOccurrences[0].isVisible = True

    print()
    print("=== failure paths are explained, not silent ===")
    header, _ = decode(ask("nonsense", port))
    ok("an unknown command returns ok:false with a reason (%s)"
       % header.get("note", "")[:40], header.get("ok") is False
       and len(header.get("note", "")) > 0)

    # A body whose tessellation fails must lose that body, not the whole fetch.
    app.activeProduct.rootComponent.bRepBodies[0].fail = True
    header, _ = decode(ask("geometry", port))
    ok("one un-tessellatable body does not lose the rest (%d bodies)"
       % len(header["bodies"]), header.get("ok") and len(header["bodies"]) == 4)
    ok("and it is reported (%s)" % header.get("note", "")[:44],
       "tessellated" in header.get("note", ""))
    app.activeProduct.rootComponent.bRepBodies[0].fail = False

    # No Design at all — the user is in a different workspace.
    saved, app.activeProduct = app.activeProduct, None
    header, _ = decode(ask("geometry", port))
    ok("no active Design is explained (%s)" % header.get("note", "")[:40],
       header.get("ok") is False and "Design" in header.get("note", ""))
    app.activeProduct = saved

    print()
    print("=== Fusion busy: the state that looks like a hang ===")
    # This is what an open modal dialog does. The add-in must answer with an
    # explanation rather than hanging or closing silently.
    adsk._dispatcher.busy.set()
    t0 = time.time()
    header, _ = decode(ask("geometry", port, timeout=20.0))
    took = time.time() - t0
    adsk._dispatcher.busy.clear()

    ok("a busy Fusion still gets an answer (%.1fs)" % took,
       header.get("ok") is False)
    ok("and the answer names the cause (%s)" % header.get("note", "")[:52],
       "idle" in header.get("note", "").lower())
    ok("it returned near the timeout, not instantly and not never (%.1fs)" % took,
       FusionBridge.IDLE_TIMEOUT_S - 0.5 <= took <= FusionBridge.IDLE_TIMEOUT_S + 4.0)

    # And it recovers: the very next request must work.
    header, _ = decode(ask("geometry", port))
    ok("the next request after a busy period succeeds", header.get("ok") is True)

    print()
    print("=== the triangle cap ===")
    line = json.dumps({"cmd": "geometry", "tolerance_mm": 0.4, "max_triangles": 20})
    s = socket.create_connection(("127.0.0.1", port), timeout=5.0)
    s.settimeout(10.0)
    s.sendall((line + "\n").encode("utf-8"))
    chunks = []
    while True:
        b = s.recv(1 << 16)
        if not b:
            break
        chunks.append(b)
    s.close()
    header, floats = decode(b"".join(chunks))

    total = sum(b["triangles"] for b in header["bodies"])
    ok("the cap is honoured (%d <= 20)" % total, total <= 20)
    ok("the payload matches the header (%d)" % (len(floats) // 9),
       len(floats) // 9 == total)
    ok("and the drop is reported (%d)" % header.get("dropped", 0),
       header.get("dropped", 0) > 0)

    print()
    print("=== shutdown ===")
    FusionBridge.stop(None)
    time.sleep(0.3)
    ok("stop() cleared the handlers", len(FusionBridge._handlers) == 0)
    ok("stop() released the listener", FusionBridge._listener is None)

    # The port must be free, or every reload during development fails with
    # "address in use" for no visible reason.
    freed = False
    try:
        probe = socket.socket()
        probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        probe.bind(("127.0.0.1", port))
        probe.close()
        freed = True
    except OSError:
        pass
    ok("the port is genuinely free again", freed)

    ok("no thread was left running",
       FusionBridge._thread is None
       and not any(t.name == "EDesFusionBridge" and t.is_alive()
                   for t in threading.enumerate()))

    print()
    print("ALL ADD-IN CHECKS PASSED" if _fails == 0 else "%d CHECK(S) FAILED" % _fails)
    return 0 if _fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
