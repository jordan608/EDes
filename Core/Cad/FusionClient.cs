// ═══════════════════════════════════════════════════════════════════════════
//  FusionClient.cs — the socket half of the Fusion bridge
//
//  Thin on purpose. Everything that can be got wrong about the format lives in
//  FusionWire, which takes a byte array and can be tested against hand-built
//  frames; this file only moves bytes. That split is why Milestone 2 could be
//  finished and verified with no Fusion installed anywhere.
//
//  Runs on the GAME thread, inside the same request-flag pattern the PCB import
//  uses — never on the UI thread. It therefore must not throw and must not block
//  for long: every path returns a result carrying an error string, and every
//  socket operation has a timeout.
//
//  The add-in answers only when Fusion is IDLE, because the API is main-thread
//  only and a worker there has to marshal through a custom event. So a request
//  landing while a modal dialog is open will simply not be served until it
//  closes. That is not a failure to retry blindly — it is a state to report.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using EDes.Pcb;

namespace EDes.Cad
{
    /// <summary>One fetch attempt: what came back, or why nothing did.</summary>
    public sealed class FusionResult
    {
        public bool   Ok;
        /// <summary>True when the stream broke AFTER at least one body arrived intact. The
        /// bodies already received are kept (and already drawn, if the caller streams them
        /// via onBody) rather than discarded — showing a known-incomplete assembly beats
        /// reverting to whatever was on screen a moment ago, which would just replace one
        /// silent gap with a confusing flash.</summary>
        public bool   Partial;
        public string Message  = "";
        public string Document = "";
        public string Revision = "";
        public int    Triangles;
        public long   Millis;
        public readonly List<CadSolid> Solids = new();
        public readonly List<string>   Notes  = new();
    }

    public sealed class FusionClient
    {
        /// <summary>Applies to EVERY socket read across the WHOLE streamed transfer, not
        /// just the first byte — each body gets its own fresh window, since nothing is sent
        /// while Fusion tessellates the next one. This used to be 20s, which is generous for
        /// a small part but not for one genuinely complex body in a large assembly; a single
        /// slow body used to abort the entire fetch, discarding every body already received.
        /// 120s is still short enough that a wrong host or a wedged add-in doesn't look like
        /// an indefinite hang, and it is a setting (FusionTimeoutSeconds) for the rare body
        /// that needs longer still.</summary>
        public int TimeoutMs = 120_000;

        /// <summary>Cap on how much UNCONSUMED data may sit in the receive buffer at once —
        /// not on the response's total size. The buffer is compacted as frames are parsed
        /// (see StreamGeometry), so a legitimately huge multi-body assembly can exceed this
        /// in TOTAL bytes transferred without ever tripping it; only a corrupt length field
        /// or a stream that never produces a parseable frame, and so never compacts, would.</summary>
        public const int MaxResponseBytes = 400 * 1024 * 1024;

        /// <summary>How much already-parsed prefix to let accumulate before compacting it
        /// away. Small enough that peak memory for a huge assembly tracks the unconsumed
        /// tail rather than everything ever received; large enough that compacting (an
        /// array copy) does not happen on every single small frame.</summary>
        private const int CompactThreshold = 4 * 1024 * 1024;

        /// <summary>Fetch geometry, streamed body-by-body exactly as the add-in sends it —
        /// see FusionBridge.py's _build_geometry. Never throws.
        ///
        /// <paramref name="onProgress"/>, if given, is called with a 0..1 fraction as bodies
        /// complete (using the bodyIndex/bodyCount the streaming add-in's frames carry) — a
        /// big assembly's transfer is genuinely slow, and without this the UI has no way to
        /// tell "still receiving" from "hung". <paramref name="onBody"/>, if given, is called
        /// once per body AS SOON as its own frame is fully parsed, which is what lets a
        /// caller draw the assembly filling in rather than waiting for the last body.
        ///
        /// An older, non-streaming add-in's reply — one frame holding every body — still
        /// works here unchanged: it is simply the one-frame case of the same loop, so onBody
        /// fires once per body all at once instead of as they arrive, and onProgress never
        /// gets a mid-value. Nothing about the format had to change for that to be true.</summary>
        public FusionResult Fetch(string host, int port, float toleranceMm, int maxTriangles,
                                  Action<float>? onProgress = null,
                                  Action<CadSolid>? onBody = null)
        {
            var r = new FusionResult();
            var sw = Stopwatch.StartNew();
            onProgress?.Invoke(0f);

            string request = FusionWire.GeometryRequest(toleranceMm, maxTriangles);
            bool ok = StreamGeometry(host, port, request, r, onProgress, onBody, out string err);

            sw.Stop();
            r.Millis = sw.ElapsedMilliseconds;

            if (!ok)
            {
                r.Partial = r.Solids.Count > 0;
                r.Message = r.Partial
                    ? $"FAILED after {r.Solids.Count} body(s): {err}"
                    : err;
                return r;
            }

            r.Ok      = true;
            r.Message = $"{r.Solids.Count} body(s), {r.Triangles:N0} triangle(s), "
                      + $"{r.Millis} ms";
            return r;
        }

        /// <summary>The streaming reader: drains every COMPLETE frame already sitting in the
        /// receive buffer before asking the socket for more, so a body renders the instant
        /// its own bytes are in rather than after the whole assembly has arrived. Each frame
        /// is independently parsed by the existing FusionWire.Parse — this method only finds
        /// where one frame ends and the next begins; it invents no new format.</summary>
        private bool StreamGeometry(string host, int port, string request, FusionResult r,
                                    Action<float>? onProgress, Action<CadSolid>? onBody,
                                    out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(host)) { error = "no host set"; return false; }
            if (port <= 0 || port > 65535) { error = $"port {port} is not valid"; return false; }

            int consumed = 0;       // bytes already resolved into complete frames
            int bodyCount = -1;     // known once the first streamed frame arrives
            bool sawEnd = false;    // saw the zero-body terminator frame

            try
            {
                using var client = new TcpClient();
                client.NoDelay = true;

                var connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(Math.Min(4000, TimeoutMs)))
                {
                    error = $"no add-in listening on {host}:{port} — is the Fusion add-in "
                          + "running, and is that the right host?";
                    return false;
                }
                client.EndConnect(connect);

                client.SendTimeout    = TimeoutMs;
                client.ReceiveTimeout = TimeoutMs;

                using var stream = client.GetStream();
                byte[] req = Encoding.UTF8.GetBytes(request);
                stream.Write(req, 0, req.Length);
                stream.Flush();

                var buf = new MemoryStream(1 << 20);
                var chunk = new byte[64 * 1024];

                while (true)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    if (buf.Length + n > MaxResponseBytes)
                    {
                        error = $"response exceeded {MaxResponseBytes / (1024 * 1024)} MB — "
                              + "coarsen the tolerance or lower the triangle cap";
                        return false;
                    }
                    buf.Write(chunk, 0, n);

                    byte[] data = buf.GetBuffer();
                    int available = (int)buf.Length;

                    while (FusionWire.TryPeekExpectedBytes(data, available, out long frameLenL,
                                                           consumed)
                           && consumed + frameLenL <= available)
                    {
                        int frameLen = (int)frameLenL;
                        var slice = new byte[frameLen];
                        Buffer.BlockCopy(data, consumed, slice, 0, frameLen);
                        consumed += frameLen;

                        var frame = FusionWire.Parse(slice, frameLen);
                        if (frame.Failed)      { error = frame.Error; return r.Solids.Count > 0; }
                        if (!frame.Ok)
                        {
                            error = frame.Note.Length > 0 ? frame.Note
                                                          : "the add-in reported a failure";
                            return r.Solids.Count > 0;
                        }

                        if (frame.BodyCount >= 0) bodyCount = frame.BodyCount;
                        if (frame.Document.Length > 0) r.Document = frame.Document;
                        if (frame.Revision.Length > 0) r.Revision = frame.Revision;

                        if (frame.Bodies.Count == 0)
                        {
                            // The terminator. Its own arrival — not just EOF — is what marks
                            // "no more bodies", and it carries what a single reply used to
                            // carry directly: the overall drop count and any note.
                            sawEnd = true;
                            if (frame.Dropped > 0)
                                r.Notes.Add($"{frame.Dropped:N0} triangle(s) dropped by the "
                                          + "add-in to stay under the requested cap — coarsen "
                                          + "the tolerance for the full model");
                            if (frame.Note.Length > 0) r.Notes.Add(frame.Note);
                        }
                        else
                        {
                            foreach (var solid in FusionWire.ToSolids(frame, r.Notes))
                            {
                                r.Solids.Add(solid);
                                r.Triangles += frame.TriangleCount;
                                onBody?.Invoke(solid);
                            }

                            if (onProgress != null && bodyCount > 0)
                                onProgress(Math.Clamp(r.Solids.Count / (float)bodyCount,
                                                      0f, 1f));
                        }
                    }

                    // Compact away whatever has already been parsed into frames. Without
                    // this, `buf` only ever grows for the life of the connection — every
                    // body already turned into a CadSolid and handed to onBody would still
                    // sit in memory a second time as raw bytes, so a large assembly's peak
                    // memory would track the WHOLE response instead of whatever is still
                    // in flight.
                    if (consumed > CompactThreshold)
                    {
                        int leftover = (int)buf.Length - consumed;
                        var trimmed = new MemoryStream(Math.Max(1 << 16, leftover + chunk.Length));
                        trimmed.Write(buf.GetBuffer(), consumed, leftover);
                        buf = trimmed;
                        consumed = 0;
                    }
                }

                if (consumed < buf.Length)
                {
                    if (consumed == 0)
                    {
                        // Nothing was ever recognised as a complete frame — let
                        // FusionWire's own parser explain exactly what is wrong (bad
                        // magic, header too short, ...) rather than a generic
                        // "truncated" message that would misdescribe plain garbage.
                        var whole = new byte[buf.Length];
                        Buffer.BlockCopy(buf.GetBuffer(), 0, whole, 0, (int)buf.Length);
                        var badFrame = FusionWire.Parse(whole, (int)buf.Length);
                        error = badFrame.Error.Length > 0 ? badFrame.Error : "unrecognised reply";
                    }
                    else
                    {
                        error = "frame ends inside its own header or payload — "
                              + "truncated transfer";
                    }
                    return r.Solids.Count > 0;
                }
                if (consumed == 0)
                {
                    error = "the add-in accepted the connection and sent nothing. That is "
                          + "what a request arriving while Fusion is busy looks like — "
                          + "close any open dialog and try again.";
                    return false;
                }
                if (!sawEnd)
                    r.Notes.Add("the connection closed before the add-in's final summary — "
                              + "the model may be incomplete");

                onProgress?.Invoke(1f);
                return true;
            }
            catch (SocketException ex)
            {
                error = $"socket error talking to {host}:{port} — {ex.SocketErrorCode}";
                return r.Solids.Count > 0;
            }
            catch (IOException ex)
            {
                // A read timeout lands here, and it is the signature of Fusion never going
                // idle (or, mid-stream, of one body's tessellation taking longer than
                // TimeoutMs). Saying so beats "an I/O error occurred".
                error = "timed out waiting for the add-in. Fusion answers only when it is "
                      + "idle, so an open dialog or a command mid-edit will hold this up. "
                      + $"({ex.GetType().Name})";
                return r.Solids.Count > 0;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return r.Solids.Count > 0;
            }
        }

        /// <summary>Ask only for the revision token. Cheap enough to poll: on the add-in
        /// side it is a dictionary read, not a tessellation.</summary>
        public string FetchRevision(string host, int port, out string error)
        {
            byte[]? raw = Exchange(host, port, FusionWire.RevisionRequest(),
                                   out int len, out error);
            if (raw == null) return "";

            var frame = FusionWire.Parse(raw, len);
            if (frame.Failed) { error = frame.Error; return ""; }
            return frame.Revision;
        }

        /// <summary>Connect, send one line, read to end of stream. The add-in closes after
        /// responding, so end-of-stream IS the frame delimiter and no length prefix is needed
        /// at this level — the frame's own header covers the rest. Used only by
        /// FetchRevision: that reply is always one small frame, with nothing to stream.</summary>
        private byte[]? Exchange(string host, int port, string request,
                                 out int length, out string error)
        {
            length = 0;
            error  = "";

            if (string.IsNullOrWhiteSpace(host)) { error = "no host set"; return null; }
            if (port <= 0 || port > 65535) { error = $"port {port} is not valid"; return null; }

            try
            {
                using var client = new TcpClient();
                client.NoDelay = true;

                // Connect with its own timeout: the default would wait tens of seconds on a
                // host that is simply not there, which reads as a frozen app.
                var connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(Math.Min(4000, TimeoutMs)))
                {
                    error = $"no add-in listening on {host}:{port} — is the Fusion add-in "
                          + "running, and is that the right host?";
                    return null;
                }
                client.EndConnect(connect);

                client.SendTimeout    = TimeoutMs;
                client.ReceiveTimeout = TimeoutMs;

                using var stream = client.GetStream();
                byte[] req = Encoding.UTF8.GetBytes(request);
                stream.Write(req, 0, req.Length);
                stream.Flush();

                var buf = new MemoryStream(1 << 20);
                var chunk = new byte[64 * 1024];
                while (true)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    if (buf.Length + n > MaxResponseBytes)
                    {
                        error = $"response exceeded {MaxResponseBytes / (1024 * 1024)} MB — "
                              + "coarsen the tolerance or lower the triangle cap";
                        return null;
                    }
                    buf.Write(chunk, 0, n);
                }

                length = (int)buf.Length;
                if (length == 0)
                {
                    error = "the add-in accepted the connection and sent nothing. That is "
                          + "what a request arriving while Fusion is busy looks like — "
                          + "close any open dialog and try again.";
                    return null;
                }
                return buf.GetBuffer();
            }
            catch (SocketException ex)
            {
                error = $"socket error talking to {host}:{port} — {ex.SocketErrorCode}";
                return null;
            }
            catch (IOException ex)
            {
                // A read timeout lands here, and it is the signature of Fusion never going
                // idle. Saying so beats "an I/O error occurred".
                error = "timed out waiting for the add-in. Fusion answers only when it is "
                      + "idle, so an open dialog or a command mid-edit will hold this up. "
                      + $"({ex.GetType().Name})";
                return null;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }
    }
}
