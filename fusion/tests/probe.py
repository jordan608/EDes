# ═══════════════════════════════════════════════════════════════════════════
#  probe.py — talk to the add-in without running EDes
#
#  The FIRST thing to run on the Fusion machine. It isolates "is the add-in
#  working" from "is EDes working", which otherwise get debugged together and
#  take twice as long.
#
#      python fusion/tests/probe.py                 ping
#      python fusion/tests/probe.py rev             the change token
#      python fusion/tests/probe.py geometry        the full fetch, summarised
#      python fusion/tests/probe.py geometry 0.2    at a finer tolerance
#      python fusion/tests/probe.py geometry 0.4 HOST PORT
#
#  A successful `ping` proves more than a live socket: the document name it
#  returns can only be read on Fusion's main thread, so getting it back proves
#  the whole worker-thread → custom-event → main-thread round trip.
# ═══════════════════════════════════════════════════════════════════════════

import json
import socket
import struct
import sys

HOST = "127.0.0.1"
PORT = 47800


def request(cmd, tolerance=0.4, host=HOST, port=PORT, timeout=40.0):
    if cmd == "geometry":
        line = json.dumps(
            {"cmd": "geometry", "tolerance_mm": tolerance, "max_triangles": 300000}
        )
    else:
        line = json.dumps({"cmd": cmd})

    s = socket.create_connection((host, port), timeout=8.0)
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
    if len(buf) < 8:
        raise ValueError("response too short (%d bytes)" % len(buf))
    if buf[:4] != b"EDS1":
        raise ValueError("not a bridge frame: %r" % buf[:16])
    (hlen,) = struct.unpack("<I", buf[4:8])
    header = json.loads(buf[8 : 8 + hlen].decode("utf-8"))
    payload = buf[8 + hlen :]
    return header, payload


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "ping"
    tol = float(sys.argv[2]) if len(sys.argv) > 2 else 0.4
    host = sys.argv[3] if len(sys.argv) > 3 else HOST
    port = int(sys.argv[4]) if len(sys.argv) > 4 else PORT

    print("-> %s  (%s:%d)" % (cmd, host, port))

    try:
        buf = request(cmd, tol, host, port)
    except socket.timeout:
        print("TIMED OUT. Fusion answers only when it is IDLE — close any open")
        print("dialog or finish the active command, then try again.")
        return 1
    except ConnectionRefusedError:
        print("CONNECTION REFUSED. The add-in is not running, or it is bound to")
        print("localhost while you are asking from another machine.")
        return 1
    except OSError as ex:
        print("SOCKET ERROR: %s" % ex)
        return 1

    if not buf:
        print("The add-in accepted the connection and sent NOTHING.")
        print("That is what a request arriving while Fusion is busy looks like.")
        return 1

    try:
        header, payload = decode(buf)
    except ValueError as ex:
        print("BAD RESPONSE: %s" % ex)
        return 1

    print("%d bytes" % len(buf))
    print("ok        : %s" % header.get("ok"))
    print("document  : %s" % header.get("document"))
    print("revision  : %s" % header.get("revision"))
    print("unit      : %s" % header.get("unit"))
    if header.get("note"):
        print("note      : %s" % header["note"])
    if header.get("dropped"):
        print("dropped   : %s triangles" % header["dropped"])

    bodies = header.get("bodies") or []
    total = sum(b.get("triangles", 0) for b in bodies)
    print("bodies    : %d  (%d triangles)" % (len(bodies), total))

    for b in bodies[:25]:
        print(
            "   %-46s %7d tri  %s"
            % (b.get("path", "")[:46], b.get("triangles", 0),
               "shown" if b.get("visible") else "HIDDEN")
        )
    if len(bodies) > 25:
        print("   ... and %d more" % (len(bodies) - 25))

    # The units check, in the place it is cheapest to notice. Fusion's API is
    # centimetres and the add-in must convert; if these numbers are ten times
    # smaller than the real part, that conversion is missing.
    if payload:
        n = len(payload) // 4
        vals = struct.unpack("<%df" % n, payload[: n * 4])
        xs, ys, zs = vals[0::3], vals[1::3], vals[2::3]
        print(
            "extent mm : X %.2f..%.2f   Y %.2f..%.2f   Z %.2f..%.2f"
            % (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs))
        )
        print("            (a 10 mm cube should read 0.00..10.00 — if it reads")
        print("             0.00..1.00 the cm->mm conversion is missing)")

    return 0 if header.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
