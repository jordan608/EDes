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
        /// <summary>Generous, because the add-in cannot answer until Fusion goes idle and a
        /// big assembly's tessellation is genuinely slow. Short enough that a wrong host
        /// does not look like a hang.</summary>
        public int TimeoutMs = 20_000;

        /// <summary>Cap on a single response. A corrupt length field or a runaway assembly
        /// should stop the read, not exhaust memory — 400 MB is far past any real model and
        /// far short of hurting.</summary>
        public const int MaxResponseBytes = 400 * 1024 * 1024;

        /// <summary>Fetch geometry. Never throws.</summary>
        public FusionResult Fetch(string host, int port, float toleranceMm, int maxTriangles)
        {
            var r = new FusionResult();
            var sw = Stopwatch.StartNew();

            byte[]? raw = Exchange(host, port,
                                   FusionWire.GeometryRequest(toleranceMm, maxTriangles),
                                   out int len, out string err);
            sw.Stop();
            r.Millis = sw.ElapsedMilliseconds;

            if (raw == null) { r.Message = err; return r; }

            var frame = FusionWire.Parse(raw, len);
            if (frame.Failed) { r.Message = frame.Error; return r; }

            r.Solids.AddRange(FusionWire.ToSolids(frame, r.Notes));
            r.Document  = frame.Document;
            r.Revision  = frame.Revision;
            r.Triangles = frame.TriangleCount;
            r.Ok        = true;
            r.Message   = $"{r.Solids.Count} body(s), {r.Triangles:N0} triangle(s), "
                        + $"{r.Millis} ms";
            return r;
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
        /// at this level — the frame's own header covers the rest.</summary>
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
