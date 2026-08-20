using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace EDes
{
    /// <summary>
    /// Lightweight per-frame phase profiler. Toggled at runtime with 'O' (see GameLoop).
    /// While enabled it accumulates wall time per phase (engine update/draw stages, plus
    /// whatever Custom0..Custom3 slots your game repurposes) plus a voxel counter, and
    /// streams one CSV row per frame to profiles\profile_&lt;timestamp&gt;.csv next to the
    /// exe. A one-line rolling average is also pushed to the log every ~5 s so numbers can
    /// be read on hardware without pulling the CSV.
    ///
    /// Design constraints:
    /// - ZERO overhead when disabled: Scope() returns a no-op struct, one branch.
    /// - Zero allocation per frame while enabled (pre-built StringBuilder, no LINQ).
    /// - Single-threaded by contract: only the game-loop thread calls into it.
    /// - A phase can be entered multiple times per frame — times ACCUMULATE within it.
    ///
    /// The display has a finite voxel throughput per frame (see README's Voxel Budgeting
    /// section) — this is the tool for finding out where a frame's time and voxel count
    /// actually go once you're past "it feels slow."
    /// </summary>
    public static class FrameProfiler
    {
        public enum Phase
        {
            GameUpdate,      // IVoxonGame.Update
            LightingUpdate,  // LightingSystem.Update (transient decay)
            LightingApply,   // LightingSystem.ApplyConfig + BeginFrame
            GameDraw,        // IVoxonGame.Draw
            Submit,          // ledHost.FrameEnd (voxel submission flush)
            Preview,         // ledHost.Rend2D (2D preview render)
            // Free slots for your own subsystems (particle update, model voxelize,
            // background effect, whatever your game's Draw breaks down into) — rename
            // the enum members if you want them to show up under a real name in the
            // CSV header / log summary, or just use them as-is.
            Custom0,
            Custom1,
            Custom2,
            Custom3,
        }

        private static readonly int PhaseCount = Enum.GetValues(typeof(Phase)).Length;
        private static readonly string[] PhaseNames = Enum.GetNames(typeof(Phase));

        public static bool Enabled { get; private set; }

        // Per-frame accumulators (Stopwatch ticks).
        private static readonly long[] _accum = new long[PhaseCount];
        private static long _frameCount;
        private static long _lastFrameStamp;   // for whole-frame wall time

        // Rolling 5-second summary.
        private static readonly long[] _sumAccum = new long[PhaseCount];
        private static long   _sumFrames;
        private static long   _sumVox;
        private static long   _lastSummaryStamp;
        private const  double SummaryEverySec = 5.0;

        // CSV output.
        private static StreamWriter? _csv;
        private static string _csvPath = "";
        private static readonly StringBuilder _row = new StringBuilder(512);
        private const int FlushEvery = 120;   // frames between explicit flushes

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        // ── Scoped timing ────────────────────────────────────────────────────
        // `using var _ = FrameProfiler.Scope(Phase.X);` at the top of a block —
        // handles every exit path, no allocation (struct), no-op when disabled.
        public readonly struct PhaseScope : IDisposable
        {
            private readonly int  _phase;   // -1 = disabled no-op
            private readonly long _t0;
            internal PhaseScope(int phase, long t0) { _phase = phase; _t0 = t0; }
            public void Dispose()
            {
                if (_phase >= 0)
                    _accum[_phase] += Stopwatch.GetTimestamp() - _t0;
            }
        }

        public static PhaseScope Scope(Phase p)
            => Enabled ? new PhaseScope((int)p, Stopwatch.GetTimestamp())
                       : new PhaseScope(-1, 0);

        // ── Runtime toggle ('O' in GameLoop) ──────────────────────────────────
        public static void Toggle()
        {
            if (Enabled) Stop(); else Start();
        }

        private static void Start()
        {
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "profiles");
                Directory.CreateDirectory(dir);
                _csvPath = Path.Combine(dir, $"profile_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _csv = new StreamWriter(_csvPath, append: false, Encoding.ASCII);

                _row.Clear().Append("frame,dt_ms,frame_ms");
                for (int i = 0; i < PhaseCount; i++) _row.Append(',').Append(PhaseNames[i]).Append("_ms");
                _row.Append(",vox_count,vps");
                _csv.WriteLine(_row.ToString());
            }
            catch (Exception ex)
            {
                App.Log($"[Profiler] CSV open FAILED ({ex.Message}) — profiling to log only.");
                _csv = null;
            }

            Array.Clear(_accum, 0, _accum.Length);
            Array.Clear(_sumAccum, 0, _sumAccum.Length);
            _frameCount = 0; _sumFrames = 0; _sumVox = 0;
            _lastFrameStamp   = Stopwatch.GetTimestamp();
            _lastSummaryStamp = _lastFrameStamp;
            Enabled = true;
            App.Log($"[Profiler] ON → {(_csv != null ? _csvPath : "(log only)")}");
        }

        private static void Stop()
        {
            Enabled = false;
            try { _csv?.Flush(); _csv?.Dispose(); } catch { }
            _csv = null;
            App.Log($"[Profiler] OFF — {_frameCount} frames captured"
                  + (_csvPath.Length > 0 ? $" → {_csvPath}" : ""));
        }

        /// <summary>Close out the frame: write the CSV row, roll the summary, reset
        /// accumulators. Call ONCE per loop iteration, after the preview render.</summary>
        public static void EndFrame(float deltaTime, long voxCount, float vps)
        {
            if (!Enabled) return;

            long now     = Stopwatch.GetTimestamp();
            long frameTk = now - _lastFrameStamp;
            _lastFrameStamp = now;
            _frameCount++;

            if (_csv != null)
            {
                _row.Clear();
                _row.Append(_frameCount).Append(',')
                    .Append((deltaTime * 1000f).ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                    .Append((frameTk * TicksToMs).ToString("F3", CultureInfo.InvariantCulture));
                for (int i = 0; i < PhaseCount; i++)
                    _row.Append(',').Append((_accum[i] * TicksToMs).ToString("F3", CultureInfo.InvariantCulture));
                _row.Append(',').Append(voxCount)
                    .Append(',').Append(vps.ToString("F0", CultureInfo.InvariantCulture));
                _csv.WriteLine(_row.ToString());
                if (_frameCount % FlushEvery == 0) _csv.Flush();
            }

            // Rolling summary → log every ~5 s (readable on hardware mid-session).
            for (int i = 0; i < PhaseCount; i++) _sumAccum[i] += _accum[i];
            _sumFrames++; _sumVox += voxCount;
            if ((now - _lastSummaryStamp) * TicksToMs >= SummaryEverySec * 1000.0 && _sumFrames > 0)
            {
                _row.Clear();
                _row.Append("[Profiler] avg/frame over ").Append(_sumFrames).Append(": ");
                for (int i = 0; i < PhaseCount; i++)
                {
                    double ms = _sumAccum[i] * TicksToMs / _sumFrames;
                    if (ms < 0.05) continue;                       // hide idle phases
                    _row.Append(PhaseNames[i]).Append('=')
                        .Append(ms.ToString("F2", CultureInfo.InvariantCulture)).Append("ms ");
                }
                _row.Append("vox=").Append(_sumVox / _sumFrames);
                App.Log(_row.ToString());
                Array.Clear(_sumAccum, 0, _sumAccum.Length);
                _sumFrames = 0; _sumVox = 0;
                _lastSummaryStamp = now;
            }

            Array.Clear(_accum, 0, _accum.Length);
        }
    }
}
