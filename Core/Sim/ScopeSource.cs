// ═══════════════════════════════════════════════════════════════════════════
//  ScopeSource.cs — multi-channel sample input for the volumetric oscilloscope
//
//  Four input modes behind one set of per-channel ring buffers, so the renderer
//  and the measurement maths never know or care where samples came from:
//
//    Synthetic   Generated waveform. Always available, so the display is never
//                dead and the app demos without any instrument.
//
//    Serial      A device streaming ASCII over a COM port (an MCU front end, a
//                logger, a scope in VCP mode). Every numeric token on a line is
//                one channel: "1.23" / "0.10,0.42" / "t=1 v1=1.2 v2=0.4".
//                The channel count latches from the lines as they arrive.
//
//    ScpiTcp     A bench instrument over a raw LXI socket (port 5555). NO driver
//                and no vendor software needed — this is the route to prefer
//                when the instrument has an Ethernet port.
//
//    ScpiVisa    The same instrument over USBTMC, through an installed VISA
//                runtime. Needs NI-VISA / Keysight IO / R&S VISA / Ultra Sigma
//                present; that install is also what binds the USBTMC driver.
//
//  Threading: every mode does its blocking I/O on a background thread and pushes
//  into the rings; the game thread only ever calls Poll (open/close/synth) and
//  Snapshot (copy out). A wedged or unplugged instrument can never stall a frame.
//  _lock guards the rings only.
//
//  Sample rate: measured from the push rate for streaming inputs, but DECLARED by
//  the instrument for SCPI (1 / xincrement from the waveform preamble). A block
//  of 1400 points arriving 10x/second is not a 14 kHz stream, and the frequency
//  measurement has to know the difference.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;
using System.IO.Ports;
using System.Threading;
using EDes.Sim.Scpi;

namespace EDes.Sim
{
    public enum ScopeInput { Synthetic = 0, Serial = 1, ScpiTcp = 2, ScpiVisa = 3 }

    public sealed class ScopeSource : IDisposable
    {
        /// <summary>Ring depth per channel — the deepest window the renderer can ask for.</summary>
        public const int CAPACITY     = 8192;
        public const int MAX_CHANNELS = 4;

        private readonly object    _lock = new();
        private readonly float[][] _ring = new float[MAX_CHANNELS][];
        private int  _head;               // next write index (shared across channels)
        private long _written;            // total sample-sets ever written

        // ── Live status (game thread reads; I/O threads write) ────────────────
        public volatile bool   Connected;
        public volatile string Status       = "Synthetic";
        public volatile string Identity     = "";      // *IDN? of a SCPI instrument
        public volatile float  SampleRateHz;
        public volatile int    ChannelCount = 2;
        public volatile int    Overruns;                // unparseable lines / failed reads

        // ── Desired configuration (UI thread writes; Poll applies it) ─────────
        private volatile ScopeInput _wantMode = ScopeInput.Synthetic;
        private volatile string     _wantPort = "";
        private volatile int        _wantBaud = 115200;
        private volatile string     _wantHost = "";
        private volatile int        _wantTcpPort = 5555;
        private volatile string     _wantVisa = "";
        private volatile float      _wantPollHz = 10f;
        private volatile bool       _paused;

        // Serial
        private SerialPort?   _port;
        private Thread?       _serialReader;
        private volatile bool _serialStop;

        // SCPI
        private ScpiScope?    _scpi;
        private Thread?       _scpiThread;
        private volatile bool _scpiStop;
        private volatile float _declaredRate;          // 0 = measure instead

        private double _retryIn;          // seconds until the next open attempt
        private double _rateWindow;       // seconds accumulated for the rate estimate
        private long   _rateBase;
        private double _synthPhase;

        public ScopeSource()
        {
            for (int c = 0; c < MAX_CHANNELS; c++) _ring[c] = new float[CAPACITY];
        }

        /// <summary>Freeze acquisition (the renderer keeps showing the last window).</summary>
        public bool Paused
        {
            get => _paused;
            set => _paused = value;
        }

        public ScopeInput Mode => _wantMode;

        // ── Configuration (safe from any thread; just stores intent) ──────────

        public void Configure(ScopeInput mode, string serialPort, int baud,
                              string host, int tcpPort, string visaResource, float pollHz)
        {
            _wantMode    = mode;
            _wantPort    = serialPort  ?? "";
            _wantBaud    = baud;
            _wantHost    = host        ?? "";
            _wantTcpPort = tcpPort;
            _wantVisa    = visaResource ?? "";
            _wantPollHz  = Math.Clamp(pollHz, 0.5f, 60f);
        }

        public static string[] AvailablePorts()
        {
            try   { return SerialPort.GetPortNames(); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>VISA instrument resources, or empty if no VISA runtime is installed.</summary>
        public static string[] VisaResources() => VisaScpiTransport.ListResources();

        public static bool VisaRuntimeAvailable => VisaScpiTransport.RuntimeAvailable;

        // ── Per-frame pump (game thread) ──────────────────────────────────────

        public void Poll(float dt, float synthAmplitude, float synthFreqHz)
        {
            var mode = _wantMode;

            // Tear down whatever no longer matches the requested mode.
            if (mode != ScopeInput.Serial && _port != null) ClosePort();
            if (mode is not (ScopeInput.ScpiTcp or ScopeInput.ScpiVisa) && _scpi != null) CloseScpi();

            switch (mode)
            {
                case ScopeInput.Serial:
                    if (_port != null &&
                        !_port.PortName.Equals(_wantPort, StringComparison.OrdinalIgnoreCase))
                        ClosePort();
                    if (_port == null && _wantPort.Length > 0) Retry(dt, OpenPort);
                    else if (_wantPort.Length == 0) Status = "Serial: no port selected";
                    break;

                case ScopeInput.ScpiTcp:
                    if (_scpi == null && _wantHost.Length > 0) Retry(dt, OpenScpiTcp);
                    else if (_wantHost.Length == 0) Status = "SCPI TCP: no host set";
                    break;

                case ScopeInput.ScpiVisa:
                    if (_scpi == null) Retry(dt, OpenScpiVisa);
                    break;

                default:
                    Status = "Synthetic";
                    break;
            }

            if (!Connected && !_paused)
                GenerateSynthetic(dt, synthAmplitude, synthFreqHz);

            // Rate: declared by the instrument in SCPI mode, measured otherwise.
            if (_declaredRate > 0)
            {
                SampleRateHz = _declaredRate;
            }
            else
            {
                _rateWindow += dt;
                if (_rateWindow >= 0.5)
                {
                    long now = Interlocked.Read(ref _written);
                    SampleRateHz = (float)((now - _rateBase) / _rateWindow);
                    _rateBase    = now;
                    _rateWindow  = 0;
                }
            }
        }

        private void Retry(float dt, Action open)
        {
            _retryIn -= dt;
            if (_retryIn > 0) return;
            _retryIn = 2.0;
            try { open(); }
            catch (Exception ex)
            {
                Connected = false;
                Status    = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        private void GenerateSynthetic(float dt, float amplitude, float freqHz)
        {
            const double rate = 4000.0;
            int n = (int)(dt * rate);
            if (n <= 0) return;
            if (n > CAPACITY) n = CAPACITY;

            ChannelCount = 2;
            double step = 2.0 * Math.PI * freqHz / rate;
            for (int i = 0; i < n; i++)
            {
                _synthPhase += step;
                if (_synthPhase > 2 * Math.PI) _synthPhase -= 2 * Math.PI;

                float a = (float)(Math.Sin(_synthPhase) * amplitude);
                float b = (float)(Math.Sin(_synthPhase + 1.05) * amplitude * 0.8);
                b = Math.Clamp(b, -amplitude * 0.55f, amplitude * 0.55f);

                Push(a, b, 0f, 0f, 2);
            }
        }

        // ── Ring buffers ──────────────────────────────────────────────────────

        private void Push(float c0, float c1, float c2, float c3, int channels)
        {
            lock (_lock)
            {
                _ring[0][_head] = c0;
                _ring[1][_head] = c1;
                _ring[2][_head] = c2;
                _ring[3][_head] = c3;
                _head = (_head + 1) % CAPACITY;
            }
            if (channels > 0) ChannelCount = Math.Clamp(channels, 1, MAX_CHANNELS);
            Interlocked.Increment(ref _written);
        }

        /// <summary>Copy the newest samples of one channel into dest, oldest → newest.</summary>
        public int Snapshot(int channel, float[] dest, int count)
        {
            if (channel < 0 || channel >= MAX_CHANNELS) return 0;
            count = Math.Min(Math.Min(count, dest.Length), CAPACITY);
            lock (_lock)
            {
                var src   = _ring[channel];
                int start = ((_head - count) % CAPACITY + CAPACITY) % CAPACITY;
                for (int i = 0; i < count; i++)
                    dest[i] = src[(start + i) % CAPACITY];
            }
            return count;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SCPI instrument (Rigol DS/MSO and friends)
        // ─────────────────────────────────────────────────────────────────────

        private void OpenScpiTcp()
            => OpenScpi(new TcpScpiTransport(_wantHost, _wantTcpPort), $"SCPI {_wantHost}:{_wantTcpPort}");

        private void OpenScpiVisa()
        {
            if (!VisaScpiTransport.RuntimeAvailable)
            {
                Status = "No VISA runtime installed (see docs/SCOPE_USB.md)";
                return;
            }
            OpenScpi(new VisaScpiTransport(_wantVisa), "SCPI USBTMC");
        }

        private void OpenScpi(IScpiTransport transport, string label)
        {
            var scope = new ScpiScope(transport);
            try
            {
                scope.Open();
            }
            catch
            {
                scope.Dispose();
                throw;
            }

            _scpi     = scope;
            Identity  = scope.Identity;
            Connected = true;
            Status    = $"{label} — {ShortIdentity(scope.Identity)}";
            App.Log($"[ScopeSource] SCPI connected: {scope.Describe} / {scope.Identity}");

            _scpiStop   = false;
            _scpiThread = new Thread(ScpiLoop)
            {
                IsBackground = true,
                Name         = "ScopeScpiReader",
            };
            _scpiThread.Start();
        }

        private static string ShortIdentity(string idn)
        {
            // "RIGOL TECHNOLOGIES,MSO2302A,DS2F252400118,00.03.05" -> "MSO2302A"
            var parts = idn.Split(',');
            return parts.Length > 1 ? parts[1].Trim() : idn;
        }

        private void CloseScpi()
        {
            _scpiStop = true;
            try { _scpi?.Dispose(); } catch { }
            _scpi         = null;
            _declaredRate = 0;
            Connected     = false;
            Identity      = "";
            Status        = "SCPI closed";
        }

        /// <summary>Acquisition loop: pull one screen window per enabled channel at the
        /// requested rate and interleave it into the rings. Everything blocking happens
        /// here, never on the game thread.</summary>
        private void ScpiLoop()
        {
            var scope = _scpi;
            if (scope == null) return;

            var buffers = new float[MAX_CHANNELS][];
            for (int c = 0; c < MAX_CHANNELS; c++) buffers[c] = new float[ScpiScope.MAX_POINTS];

            uint mask       = 0;
            int  sinceQuery = 0;

            while (!_scpiStop)
            {
                try
                {
                    if (_paused) { Thread.Sleep(50); continue; }

                    // Re-ask which channels are on every ~2 s of updates, so switching
                    // a channel on at the front panel shows up without a reconnect.
                    if (sinceQuery <= 0)
                    {
                        mask       = scope.QueryEnabledChannels();
                        sinceQuery = Math.Max(1, (int)(_wantPollHz * 2f));
                        if (mask == 0) mask = 1;          // nothing on: still show CH1
                    }
                    sinceQuery--;

                    int  highest = 0, count = 0;
                    float rate   = 0;

                    for (int ch = 1; ch <= MAX_CHANNELS; ch++)
                    {
                        if ((mask & (1u << (ch - 1))) == 0) continue;

                        var wf = scope.ReadChannel(ch);
                        if (wf == null || wf.Count == 0) { Overruns++; continue; }

                        int n = Math.Min(wf.Count, buffers[ch - 1].Length);
                        Array.Copy(wf.Volts, buffers[ch - 1], n);   // copy: Volts is scratch
                        count   = count == 0 ? n : Math.Min(count, n);
                        highest = Math.Max(highest, ch);
                        if (wf.SampleRateHz > 0) rate = (float)wf.SampleRateHz;
                    }

                    if (count > 0)
                    {
                        _declaredRate = rate;
                        for (int i = 0; i < count; i++)
                            Push(buffers[0][i], buffers[1][i], buffers[2][i], buffers[3][i], highest);
                    }

                    int sleepMs = (int)(1000f / Math.Clamp(_wantPollHz, 0.5f, 60f));
                    Thread.Sleep(Math.Clamp(sleepMs, 5, 2000));
                }
                catch (Exception ex)
                {
                    Connected = false;
                    Status    = $"SCPI error: {ex.GetType().Name}";
                    App.Log($"[ScopeSource] SCPI loop stopped: {ex.Message}");
                    _declaredRate = 0;
                    return;      // Poll() reopens in 2 s
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Serial (ASCII stream)
        // ─────────────────────────────────────────────────────────────────────

        private void OpenPort()
        {
            var p = new SerialPort(_wantPort, _wantBaud)
            {
                ReadTimeout = 250,
                NewLine     = "\n",
                DtrEnable   = true,
                RtsEnable   = true,
            };
            p.Open();
            _port       = p;
            _serialStop = false;
            _serialReader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name         = "ScopeSerialReader",
            };
            _serialReader.Start();
            Connected     = true;
            _declaredRate = 0;
            Status        = $"Serial {_wantPort} @ {_wantBaud}";
            App.Log($"[ScopeSource] Opened {_wantPort} @ {_wantBaud}");
        }

        private void ClosePort()
        {
            _serialStop = true;
            try { _port?.Close();   } catch { }
            try { _port?.Dispose(); } catch { }
            _port     = null;
            Connected = false;
            Status    = "Serial closed";
        }

        private void ReadLoop()
        {
            var port = _port;
            while (!_serialStop && port != null && port.IsOpen)
            {
                try
                {
                    string line = port.ReadLine();
                    if (!_paused) ParseLine(line);
                }
                catch (TimeoutException) { /* idle device — keep waiting */ }
                catch (Exception ex)
                {
                    Connected = false;
                    Status    = $"Serial read error: {ex.GetType().Name}";
                    break;
                }
            }
        }

        // Scratch for the parser — reader thread only, so no allocation per line.
        private readonly float[] _parsed = new float[MAX_CHANNELS];

        /// <summary>Take the numeric tokens on the line as one sample per channel,
        /// in order. Tolerates CSV, whitespace and "key=value" framing.</summary>
        private void ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            int found = 0, i = 0, n = line.Length;
            while (i < n && found < MAX_CHANNELS)
            {
                while (i < n && !char.IsDigit(line[i]) &&
                       !((line[i] == '-' || line[i] == '+' || line[i] == '.') &&
                         i + 1 < n && (char.IsDigit(line[i + 1]) || line[i + 1] == '.')))
                    i++;
                if (i >= n) break;

                int start = i;
                if (line[i] == '-' || line[i] == '+') i++;
                while (i < n && (char.IsDigit(line[i]) || line[i] == '.' ||
                                 line[i] == 'e' || line[i] == 'E' ||
                                 ((line[i] == '-' || line[i] == '+') &&
                                  (line[i - 1] == 'e' || line[i - 1] == 'E')))) i++;

                if (float.TryParse(line.AsSpan(start, i - start), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out float v))
                    _parsed[found++] = v;
            }

            if (found == 0) { Overruns++; return; }
            Push(_parsed[0],
                 found > 1 ? _parsed[1] : 0f,
                 found > 2 ? _parsed[2] : 0f,
                 found > 3 ? _parsed[3] : 0f,
                 found);
        }

        public void Dispose()
        {
            CloseScpi();
            ClosePort();
        }
    }
}
