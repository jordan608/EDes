// ═══════════════════════════════════════════════════════════════════════════
//  AudioManager.cs — NAudio sound effects + background music
//
//  • Music: loops one track from a file next to the exe (music.mp3/.wav/...).
//  • SFX:   fire-and-forget one-shots; each plays on its own output so any
//           number can overlap without format-matching headaches.
//  • Synth fallback: if a named SFX/loop has no file, PlaySfx/PlaySfxLoop
//           render a procedural sound from SynthAudio.cs's SynthPresets
//           catalog instead of silently doing nothing (see SynthAudio.cs —
//           replace the example presets with your own, or add real files
//           later; a real file always takes precedence).
//  • Loops: PlaySfxLoop/StopSfxLoop hold a sustained tone/noise (engine hum,
//           beam, ambient bed) with a short fade in/out — for held actions,
//           not one-shots.
//  • Volume: driven live from GameSettings (SfxVolume / MusicVolume).
//
//  Fully defensive: if a file is missing (and no synth preset exists either)
//  or a device can't open, it logs and no-ops — the program runs fine with
//  no audio assets present.
//
//  Drop these next to the executable to hear sound:
//      music.mp3 (or .wav/.aiff)   — looping background track
//      fire.wav                    — played on the fire action
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EDes
{
    public sealed class AudioManager : IDisposable
    {
        private readonly object _lock = new();
        private readonly Random _rng = new();

        // Music
        private AudioFileReader? _musicReader;
        private WaveOutEvent?    _musicOut;

        // Active one-shot SFX voices (pruned as they finish). Reader is the thing to
        // dispose when done — an AudioFileReader for real files, null for synth (the
        // ISampleProvider needs no disposal).
        private readonly List<(WaveOutEvent Out, IDisposable? Reader)> _sfx = new();
        private const int MAX_SFX_VOICES = 24;

        // Active looping synth voices, keyed by the caller's logical name — see
        // PlaySfxLoop/StopSfxLoop. Vol is the fade target/rate; Stopping marks a
        // voice that's fading out for removal instead of being reused.
        private sealed class LoopVoice
        {
            public required WaveOutEvent Out;
            public required VolumeSampleProvider Vol;
            public float Target, Rate;
            public bool Stopping;
        }
        private readonly Dictionary<string, LoopVoice> _loops = new(StringComparer.OrdinalIgnoreCase);

        // Resolved asset paths (null if not present)
        public string? FireSfxPath { get; }

        private float _lastMusicVol = -1f;
        private bool  _disposed;

        public AudioManager()
        {
            string dir = AppContext.BaseDirectory;
            FireSfxPath = FindFirst(dir, "fire.wav", "fire.mp3");

            string? music = FindFirst(dir, "music.mp3", "music.wav", "music.aiff", "music.ogg");
            if (music != null) TryStartMusic(music);
            else App.Log("[Audio] No music file next to exe — music disabled.");
        }

        // ── Music ──────────────────────────────────────────────────────────────
        private void TryStartMusic(string path)
        {
            try
            {
                _musicReader = new AudioFileReader(path) { Volume = 0f };
                _musicOut    = new WaveOutEvent();
                _musicOut.Init(_musicReader);
                // Loop: when playback stops at end-of-file, rewind and play again.
                _musicOut.PlaybackStopped += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (_disposed || _musicReader == null || _musicOut == null) return;
                        if (_musicReader.Position >= _musicReader.Length)
                        {
                            _musicReader.Position = 0;
                            _musicOut.Play();
                        }
                    }
                };
                _musicOut.Play();
                App.Log($"[Audio] Music: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                App.Log($"[Audio] Music failed: {ex.Message}");
                _musicReader = null; _musicOut = null;
            }
        }

        // ── SFX ──────────────────────────────────────────────────────────────
        /// <summary>Play a one-shot sound by file path. If the file doesn't exist, falls
        /// back to a procedural synth preset keyed by the file's NAME (see SynthAudio.cs) —
        /// only truly no-ops if neither a file nor a preset exists for that name. Safe to
        /// call every frame.</summary>
        public void PlaySfx(string? path, float volume)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
            {
                if (SynthPresets.Map.TryGetValue(Path.GetFileName(path), out var preset))
                    PlaySynthOneShot(preset, volume);
                return;
            }
            try
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    PruneFinished();
                    if (_sfx.Count >= MAX_SFX_VOICES) return;   // avoid runaway

                    var reader = new AudioFileReader(path) { Volume = Math.Clamp(volume, 0f, 1f) };
                    var output = new WaveOutEvent();
                    output.Init(reader);
                    output.PlaybackStopped += (s, _) =>
                    {
                        lock (_lock)
                        {
                            for (int i = _sfx.Count - 1; i >= 0; i--)
                                if (ReferenceEquals(_sfx[i].Out, s)) { Dispose(_sfx[i]); _sfx.RemoveAt(i); }
                        }
                    };
                    output.Play();
                    _sfx.Add((output, reader));
                }
            }
            catch (Exception ex) { App.Log($"[Audio] SFX failed: {ex.Message}"); }
        }

        private void PlaySynthOneShot(SynthPreset preset, float volume)
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    PruneFinished();
                    if (_sfx.Count >= MAX_SFX_VOICES) return;

                    var provider = preset.Build(_rng, Math.Clamp(volume, 0f, 1f));
                    var output   = new WaveOutEvent();
                    output.Init(provider);
                    output.PlaybackStopped += (s, _) =>
                    {
                        lock (_lock)
                        {
                            for (int i = _sfx.Count - 1; i >= 0; i--)
                                if (ReferenceEquals(_sfx[i].Out, s)) { Dispose(_sfx[i]); _sfx.RemoveAt(i); }
                        }
                    };
                    output.Play();
                    _sfx.Add((output, null));
                }
            }
            catch (Exception ex) { App.Log($"[Audio] Synth SFX failed: {ex.Message}"); }
        }

        // ── Looping SFX (held actions: engine, beam, ambient bed) ────────────
        private const float LOOP_FADE_PER_SEC = 3.0f;   // ~0.33s fade in/out

        /// <summary>Start (or re-target the volume of) a looping sound identified by
        /// <paramref name="name"/>. Only synth presets of kind Sustained are supported —
        /// this is for procedural loops, not looping audio files. Safe to call every frame
        /// while the action is held; it only starts the voice once.</summary>
        public void PlaySfxLoop(string name, float volume)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_loops.TryGetValue(name, out var live))
                {
                    live.Target = Math.Clamp(volume, 0f, 1f);
                    live.Stopping = false;
                    return;
                }
                if (!SynthPresets.Map.TryGetValue(name, out var preset) || preset.Kind != SynthKind.Sustained)
                    return;

                var provider = new VolumeSampleProvider(preset.Build(_rng)) { Volume = 0f };
                var output   = new WaveOutEvent();
                output.Init(provider);
                output.Play();
                _loops[name] = new LoopVoice { Out = output, Vol = provider, Target = Math.Clamp(volume, 0f, 1f), Rate = LOOP_FADE_PER_SEC };
            }
        }

        /// <summary>Fade out and stop a loop started with PlaySfxLoop. Safe to call even if
        /// it's not currently playing.</summary>
        public void StopSfxLoop(string name)
        {
            lock (_lock)
            {
                if (_loops.TryGetValue(name, out var live))
                { live.Target = 0f; live.Stopping = true; }
            }
        }

        /// <summary>Advance loop fades and remove any that finished fading out. Call once
        /// per frame alongside Update(musicVolume).</summary>
        public void UpdateLoops(float dt)
        {
            lock (_lock)
            {
                if (_disposed || _loops.Count == 0) return;
                List<string>? dead = null;
                foreach (var kv in _loops)
                {
                    var v = kv.Value;
                    float cur = v.Vol.Volume;
                    if (MathF.Abs(cur - v.Target) < 0.001f)
                    {
                        if (v.Stopping && v.Target <= 0f) (dead ??= new()).Add(kv.Key);
                        continue;
                    }
                    float step = v.Rate * dt;
                    v.Vol.Volume = cur < v.Target ? Math.Min(v.Target, cur + step) : Math.Max(v.Target, cur - step);
                }
                if (dead != null)
                    foreach (var key in dead)
                    {
                        try { _loops[key].Out.Dispose(); } catch { }
                        _loops.Remove(key);
                    }
            }
        }

        // ── Per-frame volume sync ──────────────────────────────────────────────
        public void Update(float musicVolume)
        {
            lock (_lock)
            {
                if (_disposed || _musicReader == null) return;
                float v = Math.Clamp(musicVolume, 0f, 1f);
                if (MathF.Abs(v - _lastMusicVol) > 0.001f)
                {
                    _musicReader.Volume = v;
                    _lastMusicVol = v;
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private void PruneFinished()
        {
            for (int i = _sfx.Count - 1; i >= 0; i--)
                if (_sfx[i].Out.PlaybackState == PlaybackState.Stopped)
                { Dispose(_sfx[i]); _sfx.RemoveAt(i); }
        }

        private static string? FindFirst(string dir, params string[] names)
        {
            foreach (var n in names)
            {
                string p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static void Dispose((WaveOutEvent Out, IDisposable? Reader) v)
        {
            try { v.Out.Dispose(); } catch { }
            try { v.Reader?.Dispose(); } catch { }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _musicOut?.Dispose(); } catch { }
                try { _musicReader?.Dispose(); } catch { }
                foreach (var v in _sfx) Dispose(v);
                _sfx.Clear();
                foreach (var v in _loops.Values) { try { v.Out.Dispose(); } catch { } }
                _loops.Clear();
            }
        }
    }
}
