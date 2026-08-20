// ═══════════════════════════════════════════════════════════════════════════
//  SynthAudio.cs — procedural sound-effect engine (no audio files required)
//
//  Ported from a shipped Voxon game's SoundManager. The idea: every sound
//  AudioManager plays is looked up by a logical NAME (e.g. "fire.wav",
//  "pickup.wav") which normally resolves to a file next to the exe. This
//  engine gives you a code-only FALLBACK for any name that doesn't have a
//  file yet — so a brand-new game never has silent gaps in its SFX while
//  you're sourcing/recording the real thing. Drop a real .wav next to the
//  exe with the same name at any point and it takes over automatically —
//  nothing here needs to change (see AudioManager.PlaySfx).
//
//  Three generators, all NAudio ISampleProvider (44.1kHz stereo float):
//    SynthVoice             — one-shot: frequency sweep + noise blend +
//                              attack/release envelope + optional sweeping
//                              low-pass filter. Covers zaps/hits/whooshes/
//                              booms/UI blips — most one-shot SFX.
//    SynthArpVoice           — a short sequence of (freq, duration) notes,
//                              each with its own tiny envelope. For pickup/
//                              reward/UI chimes that want a little ascending
//                              run instead of one sweep.
//    SustainedSynthProvider  — continuous, never-ending tone/noise with a
//                              slow LFO wobble so it doesn't sound like a
//                              dead flat buzz. For held-fire / engine-hum /
//                              ambient LOOPS (paired with AudioManager's
//                              PlaySfxLoop/StopSfxLoop fade in/out).
//
//  SynthPresets.Map is the "recipe book": one entry per logical sound name,
//  hand-tuned by ear. The 6 entries below are EXAMPLES — replace them with
//  whatever sounds your game actually needs, or add more. Nothing is
//  hardcoded to a genre; sweep/arp/sustained cover most game-SFX shapes.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Dsp;

namespace EDes
{
    /// <summary>Oscillator timbre. Sine reads as "sci-fi/energy"; Square/Triangle read as
    /// "retro chiptune" — pick per preset for the character you want.</summary>
    public enum SynthWave { Sine, Square, Triangle }

    /// <summary>Which of the three generators a preset renders through.</summary>
    public enum SynthKind { Sweep, Arp, Sustained }

    /// <summary>One playable sound recipe. Use the static factory methods to build one —
    /// don't set fields directly, they're grouped by kind and unused ones are ignored.</summary>
    public sealed class SynthPreset
    {
        public SynthKind Kind = SynthKind.Sweep;

        // ── Sweep (one-shot) ──────────────────────────────────────────────
        public float F0, F1, Dur = 0.25f, NoiseMix, Vol = 0.7f, Atk = 0.005f, Rel = 0.12f, LpStart, LpPeak;
        public bool SweepLp;
        public SynthWave Wave = SynthWave.Sine;

        // ── Arp (short note run) ──────────────────────────────────────────
        public (float freq, float dur)[] Notes = Array.Empty<(float, float)>();
        public float ArpNoiseMix;

        // ── Sustained (continuous loop) ───────────────────────────────────
        public float SusVol = 0.5f;

        public static SynthPreset Sweep(float f0, float f1, float dur, float noiseMix, float vol,
            float atk, float rel, float lpStart = 0f, float lpPeak = 0f, bool sweepLp = false, SynthWave wave = SynthWave.Sine)
            => new SynthPreset
            {
                Kind = SynthKind.Sweep, F0 = f0, F1 = f1, Dur = dur, NoiseMix = noiseMix, Vol = vol,
                Atk = atk, Rel = rel, LpStart = lpStart, LpPeak = lpPeak, SweepLp = sweepLp, Wave = wave,
            };

        public static SynthPreset Arp((float freq, float dur)[] notes, float vol, SynthWave wave = SynthWave.Square, float noiseMix = 0f)
            => new SynthPreset { Kind = SynthKind.Arp, Notes = notes, Vol = vol, Wave = wave, ArpNoiseMix = noiseMix };

        public static SynthPreset Sustained(float f0, float f1, float noiseMix, float vol,
            float lpStart, float lpPeak, SynthWave wave = SynthWave.Sine)
            => new SynthPreset
            {
                Kind = SynthKind.Sustained, F0 = f0, F1 = f1, NoiseMix = noiseMix, SusVol = vol,
                LpStart = lpStart, LpPeak = lpPeak, Wave = wave,
            };

        /// <summary>Build a fresh, playable ISampleProvider for this preset. Call once per
        /// play — providers are stateful (they track playback position).</summary>
        public ISampleProvider Build(Random rng, float volumeMul = 1f) => Kind switch
        {
            SynthKind.Arp       => new SynthArpVoice(Notes, Vol * volumeMul, Wave, ArpNoiseMix),
            SynthKind.Sustained => new SustainedSynthProvider(F0, F1, NoiseMix, Wave, LpStart, LpPeak, rng),
            _                   => new SynthVoice(F0, F1, Dur, NoiseMix, Vol * volumeMul, Atk, Rel, LpStart, LpPeak, SweepLp, rng, Wave),
        };
    }

    /// <summary>EXAMPLE synth fallback catalog, keyed case-insensitively by the same logical
    /// filename you'd pass to AudioManager.PlaySfx/PlaySfxLoop. Replace/extend freely — this
    /// is your game's data, not engine code.</summary>
    public static class SynthPresets
    {
        public static readonly Dictionary<string, SynthPreset> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            // One-shot fire/hit — bright upward chirp with a touch of noise bite.
            ["fire.wav"]   = SynthPreset.Sweep(520, 700, 0.09f, 0.10f, 0.55f, 0.004f, 0.08f, 3200, 1600, true),
            // Impact/hit — short high-pass noise crack.
            ["hit.wav"]    = SynthPreset.Sweep(0, 0, 0.06f, 1.0f, 0.5f, 0.001f, 0.05f, 6000, 2500, true),
            // Explosion — low sub-boom with a long noisy tail.
            ["boom.wav"]   = SynthPreset.Sweep(90, 24, 1.2f, 0.5f, 0.9f, 0.004f, 1.0f, 900, 200, true),
            // Pickup — short upward two-note chime.
            ["pickup.wav"] = SynthPreset.Arp(new (float, float)[] { (660, 0.09f), (990, 0.14f) }, 0.6f),
            // UI blip — chiptune square-wave tick.
            ["blip.wav"]   = SynthPreset.Sweep(880, 880, 0.045f, 0f, 0.35f, 0.001f, 0.035f, 0, 0, false, SynthWave.Square),
            // Engine hum — sustained loop for a held action (thrust, beam, etc).
            ["engine.wav"] = SynthPreset.Sustained(140, 180, 0.5f, 0.4f, 800, 1600),
        };
    }

    // ── One-shot sweep + noise + envelope + optional sweeping low-pass ──────────
    public sealed class SynthVoice : ISampleProvider
    {
        private static readonly WaveFormat Fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        /// <summary>One cycle of the given timbre at phase (radians) — shared by all three
        /// generators so they sound like one consistent "engine".</summary>
        internal static float Osc(SynthWave shape, double phase)
        {
            switch (shape)
            {
                case SynthWave.Square:
                {
                    double p = phase % (2.0 * Math.PI); if (p < 0) p += 2.0 * Math.PI;
                    return p < Math.PI ? 1f : -1f;
                }
                case SynthWave.Triangle:
                {
                    double p = phase % (2.0 * Math.PI); if (p < 0) p += 2.0 * Math.PI;
                    double u = p / (2.0 * Math.PI);
                    return (float)(4.0 * Math.Abs(u - 0.5) - 1.0);
                }
                default:
                    return (float)Math.Sin(phase);
            }
        }

        private readonly float f0, f1, dur, noiseMix, vol, atk, rel, lpStart, lpPeak;
        private readonly bool  sweepLp, pureNoise;
        private readonly Random rng;
        private readonly int   total;
        private readonly BiQuadFilter? lp;
        private readonly SynthWave shape;

        private double phase;
        private int    pos;

        public SynthVoice(float f0, float f1, float dur, float noiseMix, float vol,
                          float atk, float rel, float lpStart, float lpPeak,
                          bool sweepLp, Random rng, SynthWave shape = SynthWave.Sine)
        {
            this.f0 = f0; this.f1 = f1; this.dur = dur; this.noiseMix = noiseMix;
            this.vol = vol; this.atk = atk; this.rel = rel;
            this.lpStart = lpStart; this.lpPeak = lpPeak; this.sweepLp = sweepLp;
            this.rng = rng; this.shape = shape;
            pureNoise = f0 <= 0f && f1 <= 0f;
            total = Math.Max(1, (int)(dur * 44100f));
            if (lpStart > 0f) lp = BiQuadFilter.LowPassFilter(44100, lpStart, 0.8f);
        }

        public WaveFormat WaveFormat => Fmt;

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int written = 0;
            for (int i = 0; i < frames; i++)
            {
                if (pos >= total) break;
                float t = pos / 44100f;
                float u = t / dur;

                float sig;
                if (pureNoise)
                {
                    sig = (float)(rng.NextDouble() * 2.0 - 1.0);
                }
                else
                {
                    float freq = f0 + (f1 - f0) * u;
                    phase += 2.0 * Math.PI * freq / 44100.0;
                    float s = Osc(shape, phase);
                    float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                    sig = s * (1f - noiseMix) + n * noiseMix;
                }

                if (lp != null)
                {
                    if (sweepLp && (pos & 63) == 0)
                    {
                        float tri = 1f - Math.Abs(u * 2f - 1f);
                        float cut = lpStart + (lpPeak - lpStart) * tri;
                        lp.SetLowPassFilter(44100, Math.Clamp(cut, 40f, 18000f), 0.8f);
                    }
                    sig = lp.Transform(sig);
                }

                float env = t < atk        ? t / atk
                          : t > (dur - rel) ? Math.Max(0f, (dur - t) / rel)
                          :                    1f;
                sig *= env * vol;

                buffer[offset + written++] = sig;
                buffer[offset + written++] = sig;
                pos++;
            }
            return written;
        }
    }

    // ── Short ascending/descending note run — pickups, rewards, UI chimes ───────
    public sealed class SynthArpVoice : ISampleProvider
    {
        private static readonly WaveFormat Fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        private readonly (float freq, float dur)[] notes;
        private readonly float vol;
        private readonly SynthWave shape;
        private readonly float noiseMix;

        private int noteIdx;
        private int posInNote;
        private double phase;

        public SynthArpVoice((float freq, float dur)[] notes, float vol, SynthWave shape = SynthWave.Square, float noiseMix = 0f)
        {
            this.notes = notes; this.vol = vol; this.shape = shape; this.noiseMix = noiseMix;
        }

        public WaveFormat WaveFormat => Fmt;

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int written = 0;
            var rng = Random.Shared;
            for (int i = 0; i < frames; i++)
            {
                if (noteIdx >= notes.Length) break;
                var (freq, durSec) = notes[noteIdx];
                int noteTotal = Math.Max(1, (int)(durSec * 44100f));
                float t = posInNote / 44100f;
                float atk = Math.Min(0.01f, durSec * 0.15f);
                float rel = Math.Min(0.05f, durSec * 0.5f);
                float env = t < atk           ? t / atk
                          : t > (durSec - rel) ? Math.Max(0f, (durSec - t) / rel)
                          :                       1f;

                phase += 2.0 * Math.PI * freq / 44100.0;
                float s = SynthVoice.Osc(shape, phase);
                if (noiseMix > 0f)
                {
                    float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                    s = s * (1f - noiseMix) + n * noiseMix;
                }
                float sig = s * env * vol;

                buffer[offset + written++] = sig;
                buffer[offset + written++] = sig;
                posInNote++;
                if (posInNote >= noteTotal) { posInNote = 0; noteIdx++; phase = 0; }
            }
            return written;
        }
    }

    // ── Continuous, never-ending tone/noise — held-fire/engine/ambient loops ───
    // No envelope, no end — the caller (AudioManager.PlaySfxLoop) fades it in/out
    // via a wrapping VolumeSampleProvider, same as it would a looping file.
    public sealed class SustainedSynthProvider : ISampleProvider
    {
        private static readonly WaveFormat Fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        private readonly float f0, f1, noiseMix;
        private readonly SynthWave shape;
        private readonly BiQuadFilter? lp;
        private readonly float lpStart, lpPeak;
        private readonly Random rng;

        private double phase;
        private long   pos;

        public SustainedSynthProvider(float f0, float f1, float noiseMix, SynthWave shape,
                                      float lpStart, float lpPeak, Random rng)
        {
            this.f0 = f0; this.f1 = f1; this.noiseMix = noiseMix; this.shape = shape;
            this.lpStart = lpStart; this.lpPeak = lpPeak; this.rng = rng;
            if (lpStart > 0f) lp = BiQuadFilter.LowPassFilter(44100, lpStart, 0.8f);
        }

        public WaveFormat WaveFormat => Fmt;

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            for (int i = 0; i < frames; i++)
            {
                double t = pos / 44100.0;
                float wobble = (float)((Math.Sin(t * 2.0 * Math.PI * 0.35) + 1.0) * 0.5);   // 0..1, ~2.9s period
                float freq = f0 + (f1 - f0) * wobble;
                phase += 2.0 * Math.PI * freq / 44100.0;
                float s = SynthVoice.Osc(shape, phase);

                float sig;
                if (noiseMix >= 1f)
                    sig = (float)(rng.NextDouble() * 2.0 - 1.0);
                else
                {
                    float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                    sig = s * (1f - noiseMix) + n * noiseMix;
                }

                if (lp != null)
                {
                    if ((pos & 511) == 0)
                    {
                        float cut = lpStart + (lpPeak - lpStart) * wobble;
                        lp.SetLowPassFilter(44100, Math.Clamp(cut, 40f, 18000f), 0.8f);
                    }
                    sig = lp.Transform(sig);
                }

                buffer[offset + i * 2]     = sig;
                buffer[offset + i * 2 + 1] = sig;
                pos++;
            }
            return frames * 2;
        }
    }
}
