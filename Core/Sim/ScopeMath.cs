// ═══════════════════════════════════════════════════════════════════════════
//  ScopeMath.cs — measurements on a captured window
//
//  Everything a bench scope shows in its measurement row, computed on the
//  snapshot copy (never on the live ring buffer):
//    Vmin / Vmax / Vpp / Vmean / Vrms, frequency + period from zero-crossings
//    of the mean-removed signal, and duty cycle above the mean.
//
//  Frequency from crossings (rather than an FFT) is the right trade here: the
//  window is a few hundred samples, the signals are periodic, and it costs one
//  pass instead of a transform per channel per frame.
// ═══════════════════════════════════════════════════════════════════════════

using System;

namespace EDes.Sim
{
    public struct ScopeStats
    {
        public float Vmin, Vmax, Vpp, Vmean, Vrms;
        public float FreqHz, PeriodMs, DutyPct;
        public int   Samples;

        public static ScopeStats Compute(float[] s, int n, float sampleRateHz)
        {
            var st = new ScopeStats { Samples = n };
            if (n <= 0) return st;

            float min = float.MaxValue, max = float.MinValue, sum = 0, sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                float v = s[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum   += v;
                sumSq += v * v;
            }

            st.Vmin  = min;
            st.Vmax  = max;
            st.Vpp   = max - min;
            st.Vmean = sum / n;
            st.Vrms  = MathF.Sqrt(sumSq / n);

            // Zero-crossings of the mean-removed signal, rising edges only.
            int   crossings = 0, above = 0;
            float mean      = st.Vmean;
            bool  wasAbove  = s[0] > mean;
            int   firstRise = -1, lastRise = -1;

            for (int i = 1; i < n; i++)
            {
                bool isAbove = s[i] > mean;
                if (isAbove) above++;
                if (isAbove && !wasAbove)
                {
                    crossings++;
                    if (firstRise < 0) firstRise = i;
                    lastRise = i;
                }
                wasAbove = isAbove;
            }

            st.DutyPct = 100f * above / n;

            if (crossings >= 2 && lastRise > firstRise && sampleRateHz > 1f)
            {
                float cycles      = crossings - 1;
                float spanSamples = lastRise - firstRise;
                float periodS     = spanSamples / cycles / sampleRateHz;
                if (periodS > 1e-9f)
                {
                    st.FreqHz   = 1f / periodS;
                    st.PeriodMs = periodS * 1000f;
                }
            }
            return st;
        }
    }
}
