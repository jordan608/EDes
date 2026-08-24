# ═══════════════════════════════════════════════════════════════════════════
#  A stub 'adsk' package — just enough of Fusion's API to RUN the add-in
#
#  Not a mock in the assert-it-was-called sense. It is a fake Fusion: a document
#  tree with occurrences and bodies, a mesh calculator that tessellates a box,
#  and a custom-event mechanism that dispatches on a SEPARATE thread the way the
#  real one dispatches on Fusion's main thread.
#
#  That last detail is the point. The add-in's whole structure exists because the
#  API is main-thread only and a worker must marshal across via fireCustomEvent.
#  A stub that ran the handler synchronously on the caller's thread would exercise
#  none of that and would hide a deadlock — which is precisely the bug that would
#  otherwise be found on the Fusion machine, slowly.
#
#  What this cannot prove, and nothing here claims to: that real occurrence body
#  proxies return assembly-space coordinates, or how a real MeshCalculator behaves
#  at a given tolerance. Those are the assertions the Fusion machine is for.
# ═══════════════════════════════════════════════════════════════════════════

import queue
import threading

_terminate = True


def autoTerminate(value):
    global _terminate
    _terminate = bool(value)


def terminate():
    pass


class _EventDispatcher:
    """Stands in for Fusion's idle-time event pump.

    Runs on its own thread and calls handlers there, so 'the main thread' is a
    real, distinct thread in the tests. Also models the thing that surprises
    people: while `busy` is set, nothing is dispatched — which is what an open
    modal dialog does to the real API.
    """

    def __init__(self):
        self.q = queue.Queue()
        self.busy = threading.Event()
        self._stop = threading.Event()
        self.t = threading.Thread(target=self._pump, daemon=True)
        self.t.start()

    def _pump(self):
        while not self._stop.is_set():
            try:
                handler = self.q.get(timeout=0.02)
            except queue.Empty:
                continue
            while self.busy.is_set() and not self._stop.is_set():
                threading.Event().wait(0.01)
            try:
                handler.notify(None)
            except Exception:
                pass

    def stop(self):
        self._stop.set()


_dispatcher = _EventDispatcher()
