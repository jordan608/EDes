// ═══════════════════════════════════════════════════════════════════════════
//  ScopeSource.cs — multi-channel sample input for the volumetric oscilloscope
//
//  Two sources behind one set of per-channel ring buffers:
//
//    USB (serial)  A scope / MCU / DAQ streaming ASCII over a COM port. Every
//                  numeric token on a line is one channel's sample, so all of
//                  these work with no configuration:
//                      "1.234"                 1 channel
//                      "0.10,0.42"             2 channels (ch1, ch2)
//                      "t=12 v1=1.2 v2=0.4"    numbers are taken in order
//                  The channel count latches from the first complete line and
//                  is reported as ChannelCount. A background reader thread does
//                  the blocking reads; the game thread only snapshots, so an
//                  unplugged or wedged device can never stall a frame. The port
//                  is reopened every 2 s while it is unavailable.
//
//    Synthetic     When USB is off or not yet connected, two generated channels
//                  so the display is never dead: ch1 a sine at the requested
//                  frequency, ch2 the same signal phase-shifted and clipped
//                  (something with visible harmonics to look at).
//
//  Threading: _lock guards the ring buffers only. Snapshot() copies out under
//  the lock (a few hundred floats — trivial) and all analysis works on the copy.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;
using System.IO.Ports;
using System.Threading;

namespace EDes.Sim
{
    public sealed class ScopeSource : IDisposable
    {
        /// <summary>Ring depth per channel — the deepest window the renderer can ask for.</summary>
        public const int CAPACITY     = 8192;
        public const int MAX_CHANNELS = 4;

        private readonly object    _lock  = new();
        private readonly float[][] _ring  = new float[MAX_CHANNELS][];
        private int  _head;               // next write index (shared across channels)
        private long _written;            // total sample-sets ever written

        // ── Live status (game thread reads; reader thread writes) ─────────────
        public volatile bool   Connected;
        public volatile string Status       = "Synthetic (USB off)";
        public volatile float  SampleRateHz;
        public volatile int    ChannelCount = 2;
        public volatile int    Overruns;    // lines that failed to parse

        // ── Desired configuration (UI thread writes; Poll applies it) ─────────
        private volatile bool   _wantSerial;
        private volatile string _wantPort = "";
        private volatile int    _wantBaud = 115200;
        private volatile bool   _paused;

        private SerialPort?   _port;
        private Thread?       _reader;
        private volatile bool _readerStop;
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

        // ── Configuration (safe from any thread; just stores intent) ──────────

        public void Configure(bool useSerial, string port, int baud)
        {
            _wantSerial = useSerial;
            _wantPort   = port ?? "";
            _wantBaud   = baud;
        }

        public string WantedPort => _wantPort;
        public bool   WantsSerial => _wantSerial;

        public static string[] AvailablePorts()
        {
            try   { return SerialPort.GetPortNames(); }
            catch { return Array.Empty<string>(); }
        }

        // ── Per-frame pump (game thread) ──────────────────────────────────────

        /// <summary>Open/close the port to match the requested config, generate
        /// synthetic samples while not connected, and update the rate estimate.</summary>
        public void Poll(float dt, float synthAmplitude, float synthFreqHz)
        {
            bool wantOpen = _wantSerial && _wantPort.Length > 0;

            if (!wantOpen && _port != null) ClosePort();
            if (wantOpen && _port != null &&
                !_port.PortName.Equals(_wantPort, StringComparison.OrdinalIgnoreCase))
                ClosePort();

            if (wantOpen && _port == null)
            {
                _retryIn -= dt;
                if (_retryIn <= 0) { OpenPort(); _retryIn = 2.0; }
            }

            if (!Connected)
            {
                if (!_wantSerial)                Status = "Synthetic (USB off)";
                else if (_wantPort.Length == 0)  Status = "USB: no port selected";
                if (!_paused) GenerateSynthetic(dt, synthAmplitude, synthFreqHz);
            }

            _rateWindow += dt;
            if (_rateWindow >= 0.5)
            {
                long now = Interlocked.Read(ref _written);
                SampleRateHz = (float)((now - _rateBase) / _rateWindow);
                _rateBase    = now;
                _rateWindow  = 0;
            }
        }

        private void GenerateSynthetic(float dt, float amplitude, float freqHz)
        {
            // Fixed 4 kHz synthetic rate — enough that several cycles fill the
            // window no matter what the frame rate is doing.
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
                // ch2: phase-shifted and soft-clipped, so the two traces differ
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

        /// <summary>Copy the newest samples of one channel into dest, oldest → newest.
        /// Returns how many were written.</summary>
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

        // ── Serial plumbing ───────────────────────────────────────────────────

        private void OpenPort()
        {
            try
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
                _readerStop = false;
                _reader     = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name         = "ScopeSerialReader",
                };
                _reader.Start();
                Connected = true;
                Status    = $"USB {_wantPort} @ {_wantBaud}";
                App.Log($"[ScopeSource] Opened {_wantPort} @ {_wantBaud}");
            }
            catch (Exception ex)
            {
                Connected = false;
                Status    = $"USB {_wantPort}: {ex.GetType().Name} — retrying";
            }
        }

        private void ClosePort()
        {
            _readerStop = true;
            try { _port?.Close();   } catch { }
            try { _port?.Dispose(); } catch { }
            _port     = null;
            Connected = false;
            Status    = _wantSerial ? "USB closed" : "Synthetic (USB off)";
        }

        private void ReadLoop()
        {
            var port = _port;
            while (!_readerStop && port != null && port.IsOpen)
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
                    Status    = $"USB read error: {ex.GetType().Name}";
                    break;      // Poll() retries the open in 2 s
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

        public void Dispose() => ClosePort();
    }
}
