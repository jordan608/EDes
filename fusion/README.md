# Fusion 360 add-in

Serves the active assembly's geometry to EDes over a socket. Design notes and the
reasoning behind the protocol are in [`docs/FUSION_BRIDGE.md`](../docs/FUSION_BRIDGE.md).

## Install

1. In Fusion: **Utilities → Add-Ins → Scripts and Add-Ins**, and note the add-ins folder
   (the green **+** shows it). Typically:

       %APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\

2. Copy the whole `fusion/FusionBridge` **folder** in there. The folder, the `.py` and the
   `.manifest` must all share the name `FusionBridge` — Fusion matches them.

3. Back in the Add-Ins tab, select **FusionBridge**, tick **Run on Startup**, press **Run**.

4. The Text Commands palette should show:

       [EDesFusionBridge] listening on 127.0.0.1:47800

## Check it before involving EDes

Isolate "is the add-in working" from "is EDes working" — otherwise both get debugged at once
and it takes twice as long.

```
python fusion/tests/probe.py            # ping
python fusion/tests/probe.py geometry   # the real fetch, summarised
```

A successful `ping` proves more than an open socket: the document name it returns can only
be read on Fusion's main thread, so getting it back proves the whole worker-thread →
custom-event → main-thread round trip.

`geometry` prints the extent in millimetres. **A 10 mm cube must read `0.00..10.00`.** If it
reads `0.00..1.00`, the cm→mm conversion is missing — Fusion's API is always centimetres
regardless of the document's display units.

## Then in EDes

The **Fusion CAD** tab: set the host if it is not this machine, press **Fetch now**, then
**Fit once** to get a sensible scale. Turn on **Follow changes automatically** to have the
volume track your edits.

## If it does not work

| Symptom | Cause |
|---|---|
| Nothing in the log, no listener | Add-in not running, or it raised on load — check the Text Commands palette |
| `CONNECTION REFUSED` | Not running, or bound to localhost while you ask from another machine |
| Accepts, sends nothing | A request arrived while Fusion was busy. **Fusion only services the API when idle** — close any dialog |
| `TIMED OUT` | Same cause, or a genuinely huge tessellation. Try a coarser tolerance |
| `address in use` on restart | A previous load left the port bound. `stop()` handles this, but a hard crash will not — restart Fusion |
| Model is 10× too small | The cm→mm conversion. `probe.py geometry` shows the extent |
| Model barely visible | Origin or scale in the Fusion CAD tab. The volume readout says what % is outside |

## Serving another machine

The socket has **no authentication** — anything that can reach the port can read the
geometry of whatever is open. So it binds localhost by default.

To serve EDes on a different PC, edit the top of `FusionBridge.py`:

```python
HOST = "0.0.0.0"
```

and put this machine's address in EDes's **Add-in host** box. Do that on a bench network you
trust, not on anything shared.

## Layout, and why

    FusionBridge/
      FusionBridge.py         the add-in: adsk, threading, socket, geometry walk
      FusionBridge.manifest
      bridge/protocol.py      the wire format and the unit maths — NO adsk import
    tests/
      test_protocol.py        48 checks, runnable anywhere
      make_golden.py          regenerates the cross-language fixture
      golden_frame.bin        a frame built by the real add-in code path
      probe.py                talk to the add-in without EDes

`protocol.py` imports nothing from Autodesk on purpose. Everything that is arithmetic or
bytes is therefore testable on a machine that has never seen Fusion, which is how this got
written and verified before reaching one. `golden_frame.bin` closes the loop from the other
side: the C# suite parses a frame the Python built, so the two implementations of one format
cannot drift apart unnoticed.

## Development notes

Three traps in the Fusion API, each of which costs an afternoon:

- **Handlers must be referenced at module level.** Python collects a handler whose only
  reference was local and the event then silently stops firing — no error, it just never
  runs again.
- **`adsk.autoTerminate(False)`**, or the add-in is torn down the moment `run()` returns.
- **`stop()` must close the listener *and* join the thread**, or every reload leaves the port
  bound.

And the constraint that shapes the whole file: **the API is main-thread only.** The worker
thread owns the socket and never calls `adsk`; the main thread owns the geometry and is
reached only through `fireCustomEvent`. A fired event runs when Fusion is *idle*, which is
why "busy" is a first-class answer rather than a hang.
