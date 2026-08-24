# Stub of adsk.core: the Application, and the custom-event mechanism.

import adsk


class CustomEventHandler:
    """Base the add-in subclasses. Real one is a SWIG shim; this just needs to
    exist and be constructible."""

    def __init__(self):
        pass

    def notify(self, args):
        pass


class _CustomEvent:
    def __init__(self, event_id):
        self.eventId = event_id
        self.handlers = []

    def add(self, handler):
        self.handlers.append(handler)
        return True

    def remove(self, handler):
        if handler in self.handlers:
            self.handlers.remove(handler)
        return True


class _Document:
    def __init__(self, name):
        self.name = name


class _UserInterface:
    def __init__(self):
        self.messages = []

    def messageBox(self, text, *a):
        self.messages.append(text)


class Application:
    """The single fake app. Tests build one and hand it a product."""

    _instance = None

    def __init__(self):
        self.activeProduct = None
        self.activeDocument = _Document("StubDoc")
        self.userInterface = _UserInterface()
        self.logs = []
        self._events = {}

    @classmethod
    def get(cls):
        if cls._instance is None:
            cls._instance = Application()
        return cls._instance

    @classmethod
    def _reset(cls):
        cls._instance = None

    def log(self, text, *a):
        self.logs.append(text)

    def registerCustomEvent(self, event_id):
        ev = _CustomEvent(event_id)
        self._events[event_id] = ev
        return ev

    def unregisterCustomEvent(self, event_id):
        # Raises when absent, as the real one does — the add-in relies on that
        # being survivable, since it clears a possibly-absent registration on load.
        if event_id not in self._events:
            raise RuntimeError("no such event: %s" % event_id)
        del self._events[event_id]
        return True

    def fireCustomEvent(self, event_id, payload=""):
        """Queue the handlers for the dispatcher thread — NOT called inline.

        Calling inline would run the handler on the worker's thread, which is the
        one arrangement the add-in is built to avoid, and would make a deadlock
        invisible.
        """
        ev = self._events.get(event_id)
        if ev is None:
            return False
        for h in ev.handlers:
            adsk._dispatcher.q.put(h)
        return True
