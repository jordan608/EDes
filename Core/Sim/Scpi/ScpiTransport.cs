// ═══════════════════════════════════════════════════════════════════════════
//  ScpiTransport.cs — how we reach a bench instrument
//
//  Two transports, one interface, because the same SCPI command set is reached
//  two very different ways and each has a real trade-off:
//
//    TCP    A raw socket on port 5555 (Rigol / most LXI instruments). NO driver,
//           no vendor software, no dependency — just the network stack. If the
//           instrument has an Ethernet port, this is the route to use: it is the
//           most robust and the easiest to debug (you can telnet to it).
//
//    VISA   The USBTMC path, via the standard visa32.dll C API. Requires a VISA
//           runtime to be installed (NI-VISA, Keysight IO Libraries, R&S VISA,
//           or Rigol Ultra Sigma) — that install is also what binds the USBTMC
//           kernel driver to the instrument in the first place. visa32.dll is
//           loaded DYNAMICALLY, exactly like the Voxon SDK DLLs, so the app runs
//           normally on a machine with no VISA at all and reports why instead of
//           failing to start.
//
//  Both speak IEEE-488.2: commands are newline-terminated ASCII, and bulk
//  waveform replies are definite-length blocks (#<n><length><bytes>).
//
//  Threading: one transport instance is owned by one acquisition thread. None of
//  this is called from the UI thread or the game thread.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace EDes.Sim.Scpi
{
    public interface IScpiTransport : IDisposable
    {
        bool   IsOpen   { get; }
        string Describe { get; }

        void Open();
        void Write(string command);
        /// <summary>Read one newline-terminated ASCII reply.</summary>
        string ReadLine();
        /// <summary>Read an IEEE-488.2 definite-length block into dest.
        /// Returns the payload length, or 0 if the reply was not a block.</summary>
        int ReadBlock(byte[] dest);
    }

    // ── TCP (LXI raw socket) ──────────────────────────────────────────────────

    public sealed class TcpScpiTransport : IScpiTransport
    {
        private readonly string _host;
        private readonly int    _port;
        private readonly int    _timeoutMs;

        private TcpClient?     _client;
        private NetworkStream? _stream;

        public TcpScpiTransport(string host, int port = 5555, int timeoutMs = 3000)
        {
            _host      = host;
            _port      = port;
            _timeoutMs = timeoutMs;
        }

        public bool   IsOpen   => _client?.Connected == true;
        public string Describe => $"TCP {_host}:{_port}";

        public void Open()
        {
            var c = new TcpClient { NoDelay = true };
            if (!c.ConnectAsync(_host, _port).Wait(_timeoutMs))
            {
                c.Dispose();
                throw new TimeoutException($"No answer from {_host}:{_port}");
            }
            c.ReceiveTimeout = _timeoutMs;
            c.SendTimeout    = _timeoutMs;
            _client = c;
            _stream = c.GetStream();
        }

        public void Write(string command)
        {
            if (_stream == null) throw new InvalidOperationException("not open");
            byte[] bytes = Encoding.ASCII.GetBytes(command.EndsWith("\n") ? command : command + "\n");
            _stream.Write(bytes, 0, bytes.Length);
        }

        public string ReadLine()
        {
            if (_stream == null) throw new InvalidOperationException("not open");
            var sb = new StringBuilder(64);

            // Leading terminators are SKIPPED rather than treated as an empty reply.
            // No SCPI query answers with nothing, so a leading newline can only be a
            // leftover from the previous reply — and returning "" for it turns one
            // mis-framed read into a failure of the NEXT command, which is where the
            // symptom used to appear and the cause did not.
            for (int i = 0; i < 64 * 1024; i++)
            {
                int b = _stream.ReadByte();
                if (b < 0) break;                 // closed
                if (b == '\n' || b == '\r')
                {
                    if (sb.Length == 0) continue; // still waiting for the reply to start
                    if (b == '\n') break;
                    continue;                     // bare CR inside a reply
                }
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        public int ReadBlock(byte[] dest)
        {
            if (_stream == null) throw new InvalidOperationException("not open");

            // Header: '#' then one digit giving the digit-count of the length.
            int first = _stream.ReadByte();
            while (first == '\n' || first == '\r') first = _stream.ReadByte();
            if (first != '#') return 0;

            int nDigits = _stream.ReadByte() - '0';
            if (nDigits < 1 || nDigits > 9) return 0;

            int length = 0;
            for (int i = 0; i < nDigits; i++)
            {
                int d = _stream.ReadByte();
                if (d < '0' || d > '9') return 0;
                length = length * 10 + (d - '0');
            }

            int want = Math.Min(length, dest.Length);
            int got  = 0;
            while (got < want)
            {
                int n = _stream.Read(dest, got, want - got);
                if (n <= 0) break;
                got += n;
            }

            // Drain any payload that did not fit.
            for (int i = want; i < length; i++) _stream.ReadByte();

            // Then the response terminator, with a BLOCKING read.
            //
            // This used to be `if (_stream.DataAvailable) _stream.ReadByte()`, which only
            // consumed the terminator when it had ALREADY arrived. DataAvailable is a
            // snapshot of the receive buffer, and an instrument routinely sends the block
            // and its terminator in separate packets — so most of the time the terminator
            // had not landed yet, was left in the stream, and became the first byte of the
            // NEXT reply. ReadLine then returned an empty string, the following query
            // failed to parse, and the read came back null. Intermittently: it depended
            // entirely on packet timing, which is why it looked like flakiness rather than
            // a defect.
            //
            // IEEE-488.2 always terminates a definite-length block response, so waiting
            // for it is correct rather than optimistic. Wrapped because a device that
            // omits it would otherwise hit the socket timeout and throw away a payload
            // that is already complete and correct.
            try
            {
                int b = _stream.ReadByte();
                if (b == '\r') _stream.ReadByte();      // CRLF
            }
            catch (System.IO.IOException) { /* no terminator: the payload still stands */ }

            return got;
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
            _stream = null;
            _client = null;
        }
    }

    // ── VISA (USBTMC and everything else a VISA runtime exposes) ──────────────

    public sealed class VisaScpiTransport : IScpiTransport
    {
        // visa32.dll is the standard VISA C API — every vendor's runtime installs
        // it into System32 under that name, so this works against NI, Keysight,
        // R&S or Rigol without caring which is present.
        private const string DLL = "visa32.dll";

        [DllImport(DLL)] private static extern int viOpenDefaultRM(out IntPtr sesn);
        [DllImport(DLL)] private static extern int viOpen(IntPtr sesn, string name, int mode,
                                                         int timeout, out IntPtr vi);
        [DllImport(DLL)] private static extern int viClose(IntPtr vi);
        [DllImport(DLL)] private static extern int viSetAttribute(IntPtr vi, int attr, IntPtr value);
        [DllImport(DLL)] private static extern int viWrite(IntPtr vi, byte[] buf, int count, out int retCount);
        [DllImport(DLL)] private static extern int viRead(IntPtr vi, byte[] buf, int count, out int retCount);
        [DllImport(DLL)] private static extern int viFindRsrc(IntPtr sesn, string expr, out IntPtr findList,
                                                             out int retCount, StringBuilder desc);
        [DllImport(DLL)] private static extern int viFindNext(IntPtr findList, StringBuilder desc);

        private const int VI_ATTR_TMO_VALUE = 0x3FFF001A;

        private readonly string _resource;
        private readonly int    _timeoutMs;
        private IntPtr _rm = IntPtr.Zero;
        private IntPtr _vi = IntPtr.Zero;

        /// <summary>resource may be a full VISA resource string, or empty/"auto" to
        /// take the first USB instrument the runtime can see.</summary>
        public VisaScpiTransport(string resource, int timeoutMs = 3000)
        {
            _resource  = resource ?? "";
            _timeoutMs = timeoutMs;
        }

        public bool   IsOpen   => _vi != IntPtr.Zero;
        public string Describe => $"VISA {ResolvedResource}";
        public string ResolvedResource { get; private set; } = "";

        /// <summary>True if a VISA runtime is present at all. Everything else in this
        /// class throws a DllNotFoundException without one, so callers check first
        /// and can give the operator an actionable message.</summary>
        public static bool RuntimeAvailable
        {
            get
            {
                try
                {
                    int rc = viOpenDefaultRM(out IntPtr rm);
                    if (rm != IntPtr.Zero) viClose(rm);
                    return rc >= 0;
                }
                catch (DllNotFoundException) { return false; }
                catch (BadImageFormatException) { return false; }   // 32-bit visa32 in a 64-bit process
                catch (EntryPointNotFoundException) { return false; }
            }
        }

        /// <summary>Every instrument resource the runtime can see (USB, TCPIP, ASRL).
        /// Empty if no VISA runtime is installed.</summary>
        public static string[] ListResources()
        {
            var found = new System.Collections.Generic.List<string>();
            IntPtr rm = IntPtr.Zero;
            try
            {
                if (viOpenDefaultRM(out rm) < 0) return Array.Empty<string>();

                var sb = new StringBuilder(512);
                if (viFindRsrc(rm, "?*INSTR", out IntPtr list, out int count, sb) < 0)
                    return Array.Empty<string>();

                if (count > 0) found.Add(sb.ToString());
                for (int i = 1; i < count; i++)
                {
                    sb.Clear();
                    if (viFindNext(list, sb) < 0) break;
                    found.Add(sb.ToString());
                }
            }
            catch (DllNotFoundException) { }
            catch (BadImageFormatException) { }
            catch (EntryPointNotFoundException) { }
            finally { if (rm != IntPtr.Zero) viClose(rm); }
            return found.ToArray();
        }

        public void Open()
        {
            if (viOpenDefaultRM(out _rm) < 0)
                throw new InvalidOperationException("VISA runtime present but viOpenDefaultRM failed");

            string resource = _resource;
            if (resource.Length == 0 || resource.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string r in ListResources())
                    if (r.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) { resource = r; break; }
                if (resource.Length == 0)
                    throw new InvalidOperationException("No USB instrument visible to VISA");
            }

            if (viOpen(_rm, resource, 0, _timeoutMs, out _vi) < 0)
            {
                viClose(_rm);
                _rm = IntPtr.Zero;
                throw new InvalidOperationException($"viOpen failed for {resource}");
            }

            viSetAttribute(_vi, VI_ATTR_TMO_VALUE, (IntPtr)_timeoutMs);
            ResolvedResource = resource;
        }

        public void Write(string command)
        {
            if (_vi == IntPtr.Zero) throw new InvalidOperationException("not open");
            byte[] bytes = Encoding.ASCII.GetBytes(command.EndsWith("\n") ? command : command + "\n");
            if (viWrite(_vi, bytes, bytes.Length, out _) < 0)
                throw new InvalidOperationException("viWrite failed");
        }

        private readonly byte[] _rx = new byte[4096];

        public string ReadLine()
        {
            if (_vi == IntPtr.Zero) throw new InvalidOperationException("not open");
            var sb = new StringBuilder(64);
            for (int guard = 0; guard < 64; guard++)
            {
                if (viRead(_vi, _rx, _rx.Length, out int n) < 0 || n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    char c = (char)_rx[i];
                    if (c == '\n') return sb.ToString();
                    if (c != '\r') sb.Append(c);
                }
                if (n < _rx.Length) break;      // short read = end of reply
            }
            return sb.ToString();
        }

        public int ReadBlock(byte[] dest)
        {
            if (_vi == IntPtr.Zero) throw new InvalidOperationException("not open");

            // VISA hands back the whole reply including the block header, so the
            // header is parsed out of the first chunk rather than byte-at-a-time.
            if (viRead(_vi, dest, dest.Length, out int got) < 0 || got <= 0) return 0;

            int header = ScpiBlock.HeaderLength(dest, got, out int payload);
            if (header <= 0) return 0;

            // Shift the payload down over the header, then top up if VISA split it.
            int have = Math.Min(payload, got - header);
            Buffer.BlockCopy(dest, header, dest, 0, have);

            int want = Math.Min(payload, dest.Length);
            while (have < want)
            {
                byte[] more = new byte[want - have];
                if (viRead(_vi, more, more.Length, out int n) < 0 || n <= 0) break;
                Buffer.BlockCopy(more, 0, dest, have, n);
                have += n;
            }
            return have;
        }

        public void Dispose()
        {
            try { if (_vi != IntPtr.Zero) viClose(_vi); } catch { }
            try { if (_rm != IntPtr.Zero) viClose(_rm); } catch { }
            _vi = IntPtr.Zero;
            _rm = IntPtr.Zero;
        }
    }

    /// <summary>IEEE-488.2 definite-length block header parsing, split out so it can
    /// be tested without an instrument: "#9000001400" means 1400 payload bytes.</summary>
    public static class ScpiBlock
    {
        /// <summary>Length of the block header in buf, or 0 if buf does not start with
        /// one. payloadLength receives the declared payload size.</summary>
        public static int HeaderLength(byte[] buf, int count, out int payloadLength)
        {
            payloadLength = 0;
            int i = 0;
            while (i < count && (buf[i] == (byte)'\n' || buf[i] == (byte)'\r')) i++;
            if (i >= count || buf[i] != (byte)'#') return 0;

            int digitsAt = i + 1;
            if (digitsAt >= count) return 0;
            int nDigits = buf[digitsAt] - '0';
            if (nDigits < 1 || nDigits > 9) return 0;
            if (digitsAt + 1 + nDigits > count) return 0;

            int len = 0;
            for (int d = 0; d < nDigits; d++)
            {
                byte c = buf[digitsAt + 1 + d];
                if (c < '0' || c > '9') return 0;
                len = len * 10 + (c - '0');
            }
            payloadLength = len;
            return digitsAt + 1 + nDigits;
        }
    }
}
