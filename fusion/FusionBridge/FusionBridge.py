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
import time
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
    """One request, handed from the worker to the main thread and back.

    `frames` exists so a "geometry" request can be answered as a SEQUENCE of
    single-body frames, put here one at a time as the main thread finishes
    tessellating each body, and drained by the worker thread concurrently —
    the worker starts sending body 1 while the main thread is still
    tessellating body 2, instead of the whole assembly sitting in memory as
    one reply before a single byte goes over the socket. `reply` is still used
    for the small, single-frame "rev"/"ping" replies, which have nothing to
    gain from streaming.
    """

    def __init__(self, req):
        self.req = req
        self.done = threading.Event()
        self.reply = None
        self.frames = queue.Queue()


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
    """One body to (vertices_mm, triangle_indices), or None.

    An indexed mesh, not a flattened triangle soup: two triangles sharing an
    edge keep sharing the same two vertex numbers all the way to the wire,
    which is what makes a cutting plane's contour a shared-edge walk instead
    of a search for near-duplicate floating point positions.
    """
    try:
        calc = body.meshManager.createMeshCalculator()
        if calc is None:
            return None
        calc.surfaceTolerance = tolerance_cm
        mesh = calc.calculate()
        if mesh is None:
            return None
        verts = protocol.convert_vertices_mm(mesh.nodeCoordinatesAsFloat)
        idx = protocol.valid_triangle_indices(mesh.nodeIndices, len(verts) // 3)
        return verts, idx
    except Exception:
        return None


def _build_geometry(req, job):
    """Walk, tessellate and STREAM one frame per body. MAIN THREAD ONLY.

    Tessellation is the slow part and it runs on the UI thread because the API
    gives no choice — so the tolerance in the request is the operator's
    throttle on how long Fusion freezes for. What this function does NOT do
    any more is hold the whole assembly's triangles in memory as one frame:
    each body is pushed onto `job.frames` the moment it is tessellated, so the
    worker thread can start sending it while this loop moves on to the next
    body. A big assembly's tessellation time is unchanged, but the transfer
    overlaps it instead of starting only after every body is done.

    The triangle cap is honoured the same way `protocol.cap_bodies` always
    did — bodies kept in order until the running total would exceed it — just
    decided incrementally instead of after tessellating everything: once the
    cap is reached, remaining bodies are neither tessellated nor sent. The one
    difference from the old batch behaviour is the "nothing fit" fallback: it
    used to keep the SMALLEST body (which requires knowing every size first);
    streaming can only guarantee the FIRST tessellated body is always sent,
    even if it alone is over the cap, so at least one body still shows.
    """
    design = _design()
    if design is None:
        job.frames.put(protocol.error_frame(
            "no active Design — switch to the Design workspace and open a document"
        ))
        return

    tol_cm = protocol.tolerance_mm_to_cm(req["tolerance_mm"])
    max_tris = req["max_triangles"]
    document, revision = _document_name(), _revision()

    body_list = list(_iter_bodies())     # cheap: no tessellation yet
    total_bodies = len(body_list)

    sent, running_total, failed, dropped_tris, dropped_bodies = 0, 0, 0, 0, 0

    for i, (path, name, visible, body) in enumerate(body_list):
        tessellated = _tessellate(body, tol_cm)
        if tessellated is None:
            failed += 1
            continue
        verts, idx = tessellated

        n = len(idx) // 3
        if running_total + n > max_tris and sent > 0:
            # This body's size IS known (it was just tessellated) so it counts
            # exactly; everything after it stays UNtessellated, so their sizes
            # never get measured at all — dropped_tris is a lower bound, not
            # the true total, and the note says so rather than implying
            # precision streaming cannot deliver.
            dropped_bodies = total_bodies - i
            dropped_tris = n
            break

        running_total += n
        sent += 1
        job.frames.put(protocol.build_frame(
            [{"path": path, "name": name, "visible": visible,
              "vertices": verts, "indices": idx}],
            document=document, revision=revision,
            body_index=sent - 1, body_count=total_bodies,
        ))

    notes = []
    if dropped_bodies:
        notes.append(
            "%d body(s) not sent (>= %d triangle(s)), over the %d-triangle cap"
            % (dropped_bodies, dropped_tris, max_tris)
        )
    if failed:
        notes.append("%d body(s) could not be tessellated" % failed)
    if not body_list:
        notes.append("the document contains no B-Rep bodies to send")

    # The terminator: zero bodies, so a client can tell "one more body" apart
    # from "that was the last one" without relying on EOF alone. Carries the
    # notes a single-frame reply used to carry directly.
    job.frames.put(protocol.build_frame(
        [], document=document, revision=revision,
        dropped=dropped_tris, note="; ".join(notes),
        body_index=sent, body_count=total_bodies,
    ))


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
                # Pushes its own frames onto job.frames as it goes; job.done
                # (set below, in `finally`) fires only once every frame this
                # request will ever produce has already been queued.
                _build_geometry(job.req, job)
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
            err = protocol.error_frame(
                "the add-in raised while building a reply: %s"
                % traceback.format_exc(limit=3).replace("\n", " ")
            )
            # The geometry path is drained from job.frames, not job.reply — an
            # exception that unwound partway through streaming bodies still
            # needs a terminator there, or the worker waits out the full idle
            # timeout for a reply that job.reply was never going to carry.
            if job.req.get("cmd") == "geometry":
                job.frames.put(err)
            else:
                job.reply = err
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


def _stream_frames(conn, job):
    """Send job.frames as they appear, instead of waiting for job.done.

    This is the entire point of the split: the main thread is still
    tessellating body 2 while this loop is already sending body 1's frame.
    Falls back to the same "Fusion never went idle" explanation the old
    single-reply path used, for the common case where a modal dialog blocks
    the main thread and NOTHING ever arrives.
    """
    got_any = False
    deadline = time.monotonic() + IDLE_TIMEOUT_S

    while True:
        try:
            frame = job.frames.get(timeout=0.2)
        except queue.Empty:
            if job.done.is_set():
                break
            if not got_any and time.monotonic() > deadline:
                conn.sendall(protocol.error_frame(
                    "Fusion did not become idle within %ds — a modal dialog or "
                    "an active command blocks the API. Close it and retry."
                    % int(IDLE_TIMEOUT_S)
                ))
                return
            continue
        got_any = True
        conn.sendall(frame)

    # job.done can be set the instant the last frame is queued, which can win
    # a race against this loop's own Empty check above — drain whatever is
    # left with no further waiting rather than risk dropping the last frame.
    while True:
        try:
            frame = job.frames.get_nowait()
        except queue.Empty:
            return
        conn.sendall(frame)


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

        if req["cmd"] == "geometry":
            # Streamed body-by-body; see _build_geometry and _stream_frames.
            _stream_frames(conn, job)
        else:
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
            err = protocol.error_frame("the add-in was stopped")
            job.reply = err
            job.frames.put(err)   # in case this was a "geometry" job
            job.done.set()
    except Exception:
        pass
