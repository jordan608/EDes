// ═══════════════════════════════════════════════════════════════════════════
//  ScpiScope.cs — waveform acquisition from a SCPI bench oscilloscope
//
//  Written against the Rigol DS2000/MSO2000 command set (verified against the
//  MSO2302A programming guide), which is also what DS1000Z/DS4000/MSO5000 and
//  most Siglent scopes accept — the :WAVeform subsystem is near-identical.
//
//  One acquisition = per channel:
//      :WAV:SOUR CHAN<n>      pick the source
//      :WAV:MODE NORMal       screen memory (see the note on RAW below)
//      :WAV:FORM BYTE         1 byte per point, 0..255
//      :WAV:PREamble?         10 CSV fields; we need xincrement + the 3 y terms
//      :WAV:DATA?             #<n><len><bytes>
//
//  and the conversion every one of these scopes uses:
//
//      volts = (raw - yorigin - yreference) * yincrement
//      seconds/sample = xincrement          (so sample rate = 1 / xincrement)
//
//  Why NORMal and not RAW: RAW reads deep acquisition memory (up to 14 Mpts on
//  an MSO2302A) but requires the scope to be STOPped and the read to be chunked
//  with :WAV:STARt/:WAV:STOP. NORMal returns the ~1400 points behind the screen
//  while the scope keeps running, which is exactly what a live volumetric trace
//  wants: it is already decimated to something a display can show, and it costs
//  one transfer per channel per update.
//
//  The scope does its own triggering, so what arrives is already a stable,
//  trigger-aligned window.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;

namespace EDes.Sim.Scpi
{
    /// <summary>One channel's worth of a single acquisition, already in volts.</summary>
    public sealed class ScpiWaveform
    {
        public int     Channel;
        public float[] Volts   = Array.Empty<float>();
        public int     Count;
        public double  SampleRateHz;    // 1 / xincrement
        public double  XOrigin;         // time of the first sample, relative to trigger
    }

    public sealed class ScpiScope : IDisposable
    {
        /// <summary>Screen memory on these scopes is ~1400 points; allow generous
        /// headroom for models with wider screens without ever reallocating.</summary>
        public const int MAX_POINTS = 64 * 1024;

        private readonly IScpiTransport _t;
        private readonly byte[]  _raw   = new byte[MAX_POINTS + 64];
        private readonly float[] _volts = new float[MAX_POINTS];

        public string Identity { get; private set; } = "";
        public string Describe => _t.Describe;

        public ScpiScope(IScpiTransport transport) { _t = transport; }

        /// <summary>Open the link and read *IDN?. Throws if either fails.</summary>
        public void Open()
        {
            if (!_t.IsOpen) _t.Open();
            _t.Write("*IDN?");
            Identity = _t.ReadLine().Trim();
            if (Identity.Length == 0) throw new InvalidOperationException("No response to *IDN?");
        }

        /// <summary>Which analog channels are switched on, as a bit mask (bit 0 = CH1).
        /// Channels the instrument does not have simply answer nothing and are skipped.</summary>
        public uint QueryEnabledChannels(int maxChannels = 4)
        {
            uint mask = 0;
            for (int ch = 1; ch <= maxChannels; ch++)
            {
                try
                {
                    _t.Write($":CHAN{ch}:DISP?");
                    string reply = _t.ReadLine().Trim();
                    if (reply == "1" || reply.Equals("ON", StringComparison.OrdinalIgnoreCase))
                        mask |= 1u << (ch - 1);
                }
                catch { break; }        // no such channel / link trouble
            }
            return mask;
        }

        /// <summary>Fetch one channel's screen waveform, converted to volts.
        /// Returns null if the instrument returned nothing usable.</summary>
        public ScpiWaveform? ReadChannel(int channel)
        {
            _t.Write($":WAV:SOUR CHAN{channel}");
            _t.Write(":WAV:MODE NORM");
            _t.Write(":WAV:FORM BYTE");

            _t.Write(":WAV:PRE?");
            string preamble = _t.ReadLine();
            if (!TryParsePreamble(preamble, out var pre)) return null;

            _t.Write(":WAV:DATA?");
            int n = _t.ReadBlock(_raw);
            if (n <= 0) return null;

            int count = Math.Min(n, _volts.Length);
            for (int i = 0; i < count; i++)
                _volts[i] = (float)((_raw[i] - pre.YOrigin - pre.YReference) * pre.YIncrement);

            return new ScpiWaveform
            {
                Channel      = channel,
                Volts        = _volts,          // caller copies immediately (see ScopeSource)
                Count        = count,
                SampleRateHz = pre.XIncrement > 0 ? 1.0 / pre.XIncrement : 0,
                XOrigin      = pre.XOrigin,
            };
        }

        // ── Preamble ──────────────────────────────────────────────────────────

        public struct Preamble
        {
            public int    Format, Type, Points, Count;
            public double XIncrement, XOrigin, XReference;
            public double YIncrement, YOrigin, YReference;
        }

        /// <summary>Parse the 10-field :WAVeform:PREamble? reply. Public and static so
        /// the scaling maths can be tested without an instrument attached.</summary>
        public static bool TryParsePreamble(string reply, out Preamble p)
        {
            p = default;
            if (string.IsNullOrWhiteSpace(reply)) return false;

            string[] f = reply.Split(',');
            if (f.Length < 10) return false;

            double D(int i) => double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture,
                                               out double v) ? v : 0;
            int I(int i) => int.TryParse(f[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                         out int v) ? v : 0;

            p.Format     = I(0);
            p.Type       = I(1);
            p.Points     = I(2);
            p.Count      = I(3);
            p.XIncrement = D(4);
            p.XOrigin    = D(5);
            p.XReference = D(6);
            p.YIncrement = D(7);
            p.YOrigin    = D(8);
            p.YReference = D(9);
            return true;
        }

        /// <summary>The conversion every DS/MSO-series scope documents. Separate so it
        /// is covered by the tests.</summary>
        public static float RawToVolts(byte raw, in Preamble p)
            => (float)((raw - p.YOrigin - p.YReference) * p.YIncrement);

        public void Dispose() => _t.Dispose();
    }
}
