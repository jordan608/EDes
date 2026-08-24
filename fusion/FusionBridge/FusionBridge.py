# ═══════════════════════════════════════════════════════════════════════════
#  FusionBridge.py — serves the active Fusion assembly to EDes over a socket
#
#  Install: copy or symlink the FusionBridge FOLDER into Fusion's add-ins
#  directory, then Utilities > Add-Ins > the green plus, and tick "Run on
#  Startup". See fusion/README.md.
#
#  ── The one thing to understand before editing ──────────────────────────
#
#  The Fusion API may ONLY be touched from the main thread. Fusion runs its UI
#  and every add-in on that one thread. So this file is split in two and the
#  split is load-bearing:
#
#      worker thread   owns the socket. Never calls adsk. Ever.
#      main thread     owns the geometry. Reached only via fireCustomEvent.
#
#  A fired custom event is queued and runs when Fusion is IDLE. That means a
#  request arriving while a modal dialog is open, or mid-command, is not served
#  until that finishes — so the worker waits with a timeout and answers with an
#  error frame explaining exactly that, rather than hanging or closing silently.
#
#  Three traps, all of which cost an afternoon if hit:
#
#    1. Handlers must be kept alive at MODULE level. Python garbage-collects a
#       handler whose only reference was local, and the event then silently stops
#       firing — no error, it just never runs again.
#    2. adsk.autoTerminate(False), or the add-in is torn down the moment run()
#       returns and the socket dies with it.
#    3. stop() must close the listener AND join the thread. Without it every
#       reload during development leaves the port bound and the next start fails
#       with "address in use" for no visible reason.
#
#  ── Units ───────────────────────────────────────────────────────────────
#
#  Handled entirely in bridge/protocol.py. Coordinates go out cm -> mm; the
#  tolerance comes in mm -> cm. Opposite directions, both silent when wrong.
#  Nothing in this file converts anything.
# ═══════════════════════════════════════════════════════════════════════════

import os
import queue
import socket
import sys
import threading
import traceback

import adsk.core
import adsk.fusion

# The add-in's own folder has to be importable before 'bridge' can be found;
# Fusion does not add it to sys.path.
_HERE = os.path.dirname(os.path.realpath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from bridge import protocol  # noqa: E402

# ── Configuration ─────────────────────────────────────────────────────────
#
# LOCALHOST by default even though EDes may be on another machine, because this
# socket has NO AUTHENTICATION: anything that can reach the port can read the
# geometry of whatever is open. Listening wider is a deliberate choice for a
# network you trust, so it is an explicit edit here rather than a default.
#
# To serve another machine, set HOST = "0.0.0.0" and put this machine's address
# into EDes's "Add-in host" box.
HOST = "127.0.0.1"
PORT = 47800

#: How long the worker waits for Fusion to go idle before giving up on one
#: request. Long enough for a big tessellation, short enough that a wrong host
#: does not look like a hang.
IDLE_TIMEOUT_S = 30.0

_EVENT_ID = "EDesFusionBridgeRequest"

# ── Module-level state. See trap 1: these references keep things alive. ───
_handlers = []
_app = None
_event = None
_listener = None
_thread = None
_stop = threading.Event()
_jobs = queue.Queue()


class _Job:
    """One request, handed from the worker to the main thread and back."""

    def __init__(self, req):
        self.req = req
        self.done = threading.Event()
        self.reply = None


# ══ Main thread: everything that touches adsk ═════════════════════════════


def _design():
    """The active Design, or None if the user is in a workspace without one."""
    if _app is None:
        return None
    product = _app.activeProduct
    if product is None or not isinstance(product, adsk.fusion.Design):
        return None
    return product


def _iter_bodies():
    """Every B-Rep body in the document, with its identity and visibility.

    Yields (path, name, visible, body).

    Two things worth knowing:

      • occurrence.bRepBodies returns PROXIES in root-component context, so a
        mesh taken from one is already in assembly space. That is what makes
        Fusion authoritative for position and orientation without a single
        transform crossing the wire.

      • Bodies modelled directly in the ROOT component belong to no occurrence
        at all, so a walk of allOccurrences alone silently misses them. A part
        designed without ever creating a component is the common case for a
        quick test model — exactly what someone would try first.

    Visibility is the occurrence's AND the body's. occurrence.isVisible accounts
    for parent state, so a component in a hidden sub-assembly reads hidden; the
    body's own flag then covers a single body switched off inside a shown
    component.
    """
    design = _design()
    if design is None:
        return

    root = design.rootComponent

    for body in root.bRepBodies:
        yield (body.name, body.name, bool(body.isVisible), body)

    for occ in root.allOccurrences:
        try:
            occ_visible = bool(occ.isVisible)
            path = occ.fullPathName or occ.name
            for body in occ.bRepBodies:
                yield (
                    "%s/%s" % (path, body.name),
                    body.name,
                    occ_visible and bool(body.isVisible),
                    body,
                )
        except Exception:
            # One unreadable occurrence must not lose the rest of the assembly.
            continue


def _tessellate(body, tolerance_cm):
    """One body to a flat triangle soup in millimetres, or None."""
    try:
        calc = body.meshManager.createMeshCalculator()
        if calc is None:
            return None
        calc.surfaceTolerance = tolerance_cm
        mesh = calc.calculate()
        if mesh is None:
            return None
        return protocol.expand_triangles(mesh.nodeCoordinatesAsFloat, mesh.nodeIndices)
    except Exception:
        return None


def _build_geometry(req):
    """Walk, tessellate, cap, frame. MAIN THREAD ONLY.

    This is the slow part and it runs on the UI thread, because the API gives no
    choice — so the tolerance in the request is the operator's throttle on how
    long Fusion freezes for.
    """
    design = _design()
    if design is None:
        return protocol.error_frame(
            "no active Design — switch to the Design workspace and open a document"
        )

    tol_cm = protocol.tolerance_mm_to_cm(req["tolerance_mm"])

    bodies, failed = [], 0
    for path, name, visible, body in _iter_bodies():
        tris = _tessellate(body, tol_cm)
        if tris is None:
            failed += 1
            continue
        bodies.append(
            {
                "path": path,
                "name": name,
                "visible": visible,
                "triangles": len(tris) // 9,
                "tris": tris,
            }
        )

    kept, dropped = protocol.cap_bodies(bodies, req["max_triangles"])

    notes = []
    if dropped:
        notes.append(
            "%d triangle(s) in %d body(s) dropped to stay under the cap"
            % (dropped, len(bodies) - len(kept))
        )
    if failed:
        notes.append("%d body(s) could not be tessellated" % failed)
    if not bodies:
        notes.append("the document contains no B-Rep bodies to send")

    return protocol.build_frame(
        kept,
        document=_document_name(),
        revision=_revision(),
        dropped=dropped,
        note="; ".join(notes),
    )


def _document_name():
    try:
        doc = _app.activeDocument
        return doc.name if doc else ""
    except Exception:
        return ""


def _revision():
    """The change token. MAIN THREAD ONLY, but cheap: it reads ids, never geometry.

    Includes each body's revisionId (which moves when its geometry changes), its
    path, and its visibility — so hiding a component moves the token too, and the
    display follows a visibility change without needing a manual refresh.
    """
    parts = []
    try:
        for path, _name, visible, body in _iter_bodies():
            rev = ""
            try:
                rev = body.revisionId
            except Exception:
                pass
            parts.append("%s|%s|%d" % (path, rev, 1 if visible else 0))
    except Exception:
        pass
    return protocol.revision_token(parts)


def _serve_on_main_thread():
    """Drain the queue. Called from the custom event handler, so: main thread."""
    while True:
        try:
            job = _jobs.get_nowait()
        except queue.Empty:
            return

        try:
            cmd = job.req.get("cmd")
            if cmd == "geometry":
                job.reply = _build_geometry(job.req)
            elif cmd == "rev":
                job.reply = protocol.build_frame(
                    [], document=_document_name(), revision=_revision()
                )
            elif cmd == "ping":
                # Deliberately returns the document NAME, which can only be read
                # on the main thread — so a successful ping proves the whole
                # marshalling round trip, not merely that the socket is open.
                job.reply = protocol.build_frame(
                    [], document=_document_name(), revision=_revision(),
                    note="FusionBridge alive",
                )
            else:
                job.reply = protocol.error_frame(
                    job.req.get("error") or "unknown command"
                )
        except Exception:
            job.reply = protocol.error_frame(
                "the add-in raised while building a reply: %s"
                % traceback.format_exc(limit=3).replace("\n", " ")
            )
        finally:
            job.done.set()


class _RequestHandler(adsk.core.CustomEventHandler):
    def __init__(self):
        super().__init__()

    def notify(self, args):
        try:
            _serve_on_main_thread()
        except Exception:
            # Never let an exception escape into Fusion's event loop.
            pass


# ══ Worker thread: the socket, and nothing else ═══════════════════════════


def _handle_client(conn):
    conn.settimeout(10.0)
    try:
        # One line, newline-terminated. Capped so a client that never sends a
        # newline cannot grow this without bound.
        chunks, total = [], 0
        while total < 64 * 1024:
            b = conn.recv(1)
            if not b or b == b"\n":
                break
            chunks.append(b)
            total += 1
        line = b"".join(chunks).decode("utf-8", "replace")

        req = protocol.parse_request(line)
        job = _Job(req)
        _jobs.put(job)

        # Wake the main thread. The payload is unused — the job went through the
        # queue — but fireCustomEvent is what gets us onto the right thread.
        if _app is not None:
            _app.fireCustomEvent(_EVENT_ID, "")

        if not job.done.wait(IDLE_TIMEOUT_S):
            # Fusion never went idle. Say so: this is by far the most common
            # confusing state, and "the add-in sent nothing" would not explain it.
            reply = protocol.error_frame(
                "Fusion did not become idle within %ds — a modal dialog or an "
                "active command blocks the API. Close it and retry."
                % int(IDLE_TIMEOUT_S)
            )
        else:
            reply = job.reply or protocol.error_frame("the add-in produced no reply")

        conn.sendall(reply)
    except Exception:
        pass
    finally:
        try:
            conn.shutdown(socket.SHUT_RDWR)
        except Exception:
            pass
        try:
            conn.close()
        except Exception:
            pass


def _worker():
    global _listener
    try:
        _listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        # Without SO_REUSEADDR a reload during development leaves the port in
        # TIME_WAIT and the next start fails with "address in use".
        _listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        _listener.bind((HOST, PORT))
        _listener.listen(4)
        _listener.settimeout(0.5)          # so _stop is checked regularly
    except Exception:
        _listener = None
        return

    while not _stop.is_set():
        try:
            conn, _addr = _listener.accept()
        except socket.timeout:
            continue
        except OSError:
            break
        _handle_client(conn)


# ══ Add-in lifecycle ═════════════════════════════════════════════════════


def run(context):
    global _app, _event, _thread
    try:
        _app = adsk.core.Application.get()

        # A stale registration from a previous load would make this fail; clearing
        # first makes a reload during development reliable.
        try:
            _app.unregisterCustomEvent(_EVENT_ID)
        except Exception:
            pass

        _event = _app.registerCustomEvent(_EVENT_ID)
        handler = _RequestHandler()
        _event.add(handler)
        _handlers.append(handler)          # trap 1: keep it alive

        _stop.clear()
        _thread = threading.Thread(target=_worker, name="EDesFusionBridge", daemon=True)
        _thread.start()

        # trap 2: without this the add-in is torn down as run() returns.
        adsk.autoTerminate(False)

        _app.log("[EDesFusionBridge] listening on %s:%d" % (HOST, PORT))
    except Exception:
        if _app:
            try:
                _app.userInterface.messageBox(
                    "FusionBridge failed to start:\n%s" % traceback.format_exc()
                )
            except Exception:
                pass


def stop(context):
    global _listener, _thread, _event
    try:
        _stop.set()

        # trap 3: close the listener so accept() returns, then wait for the thread.
        if _listener is not None:
            try:
                _listener.close()
            except Exception:
                pass
            _listener = None

        if _thread is not None:
            _thread.join(timeout=2.0)
            _thread = None

        if _app is not None:
            try:
                _app.unregisterCustomEvent(_EVENT_ID)
            except Exception:
                pass
        _event = None
        _handlers.clear()

        # Drain any request still waiting, so a blocked worker is released rather
        # than sitting on its Event until the timeout.
        while True:
            try:
                job = _jobs.get_nowait()
            except queue.Empty:
                break
            job.reply = protocol.error_frame("the add-in was stopped")
            job.done.set()
    except Exception:
        pass
