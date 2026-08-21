// ═══════════════════════════════════════════════════════════════════════════
//  ScopeRenderer.cs — the volumetric oscilloscope panel
//
//  Draws a bench-scope face on a single constant-Y plane (default y = 0.1):
//  graticule, up to four traces, a trigger marker, and a measurement row.
//
//  Deliberate design decisions:
//
//  • The panel is NOT camera-transformed. Traces and readouts stay pinned to
//    the y = 0.1 plane so the scope remains readable while you fly the rest of
//    the scene around it — the same reason a bench scope is bolted to a bench.
//    (Everything else in the app goes through SceneCamera.Transform.)
//
//  • One sample per voxel column. The window length is chosen from the panel
//    width divided by the voxel pitch, so the trace is never over- or
//    under-sampled for the display it is on: no aliasing, no wasted voxels.
//
//  • Consecutive samples are joined by a vertical fill, exactly like a real
//    scope's interpolated trace, so a fast edge reads as an edge instead of
//    two disconnected dots.
//
//  • Trigger is a software rising/falling-edge search over the snapshot: find
//    the most recent crossing of the trigger level and align the window so the
//    edge sits at a fixed fraction across the screen. Without it, a periodic
//    waveform slides sideways every frame and is unreadable.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes.Sim
{
    /// <summary>Where the scope face lives in the volume (a constant-Y plane).</summary>
    public struct ScopePanel
    {
        public float Y;              // the plane the whole face is drawn on
        public float X0, X1;         // left / right edge
        public float ZTop, ZBottom;  // remember: -Z is up, so ZTop < ZBottom
        public float HeaderZ;        // row above the face for the source/scale line
        public float CentreZ => (ZTop + ZBottom) * 0.5f;
        public float HalfH   => (ZBottom - ZTop) * 0.5f;
        public float Width   => X1 - X0;
    }

    public sealed class ScopeRenderer
    {
        public const int MAX_CH = ScopeSource.MAX_CHANNELS;

        private static readonly int[] ChannelColours =
        {
            Palette.Trace,   // ch1 green
            0xFFD24A,        // ch2 amber
            0x6AC8FF,        // ch3 blue
            0xFF7AD9,        // ch4 magenta
        };

        // Snapshot scratch — allocated once, reused every frame.
        private readonly float[] _win  = new float[ScopeSource.CAPACITY];
        private readonly float[] _scan = new float[ScopeSource.CAPACITY];

        /// <summary>Stats from the most recent Draw, per channel (for the UI panel).</summary>
        public readonly ScopeStats[] Stats = new ScopeStats[MAX_CH];

        /// <summary>Draw the scope face.</summary>
        /// <param name="voltsPerDiv">vertical scale; the face is 8 divisions tall</param>
        /// <param name="channelMask">bit N enables channel N</param>
        /// <param name="triggerCh">channel the trigger watches, -1 = free run</param>
        public void Draw(VoxelBatch batch, Hud hud, ScopeSource src, in ScopePanel panel,
                         float voltsPerDiv, uint channelMask, int triggerCh, float triggerLevel,
                         bool triggerRising, bool showHeader, float textSize)
        {
            const int VDIV = 8, HDIV = 10;

            DrawGraticule(batch, panel, VDIV, HDIV);

            // One sample per voxel column across the face.
            int columns = Math.Clamp((int)(panel.Width / batch.Spacing), 32, ScopeSource.CAPACITY);
            float voltsFullScale = MathF.Max(0.001f, voltsPerDiv * VDIV * 0.5f);   // +/- half-scale
            float gain = panel.HalfH / voltsFullScale;

            int channels = Math.Clamp(src.ChannelCount, 1, MAX_CH);
            for (int ch = 0; ch < channels; ch++)
            {
                if ((channelMask & (1u << ch)) == 0) { Stats[ch] = default; continue; }

                int n = SnapshotWindow(src, ch, columns, triggerCh == ch, triggerLevel, triggerRising);
                if (n <= 1) { Stats[ch] = default; continue; }

                Stats[ch] = ScopeStats.Compute(_win, n, src.SampleRateHz);
                DrawTrace(batch, panel, _win, n, gain, ChannelColours[ch]);
                if (batch.BudgetHit) break;
            }

            // Trigger level marker: a dashed line across the face.
            if (triggerCh >= 0)
            {
                float z = panel.CentreZ - triggerLevel * gain;
                if (z > panel.ZTop && z < panel.ZBottom)
                    DrawDashed(batch, panel.Y, panel.X0, panel.X1, z,
                               Palette.Scale(ChannelColours[Math.Clamp(triggerCh, 0, MAX_CH - 1)], 0.6f));
            }

            // Only the header line belongs to the panel. The per-channel measurement
            // rows are drawn by the app into its reserved footer band (see EDesApp),
            // so they cannot collide with whatever sits below the face.
            if (showHeader)
                DrawHeader(hud, src, panel, voltsPerDiv, textSize);
        }

        /// <summary>Colour for a channel, for the app's measurement rows.</summary>
        public static int ChannelColour(int ch) => ChannelColours[Math.Clamp(ch, 0, MAX_CH - 1)];

        /// <summary>Time per division of the window last drawn, for a readout.</summary>
        public double SecondsPerDiv(ScopeSource src)
            => src.SampleRateHz > 1f ? _lastColumns / 10.0 / src.SampleRateHz : 0;

        // ── Window selection (with software trigger) ──────────────────────────
        private int SnapshotWindow(ScopeSource src, int ch, int columns,
                                   bool useTrigger, float level, bool rising)
        {
            if (!useTrigger)
                return src.Snapshot(ch, _win, columns);

            // Grab twice the window so there is room to slide back to an edge.
            int scanLen = Math.Min(columns * 2, ScopeSource.CAPACITY);
            int got     = src.Snapshot(ch, _scan, scanLen);
            if (got <= 1) return 0;

            // Search backward for the most recent qualifying edge, leaving 20% of
            // the window as pre-trigger so the edge is visible, not clipped.
            int pre  = (int)(columns * 0.2f);
            int best = -1;
            for (int i = got - 1; i > 0; i--)
            {
                bool edge = rising ? (_scan[i - 1] <= level && _scan[i] > level)
                                   : (_scan[i - 1] >= level && _scan[i] < level);
                if (!edge) continue;
                if (i - pre < 0 || i - pre + columns > got) continue;   // window would run off
                best = i;
                break;
            }

            int start = best < 0 ? Math.Max(0, got - columns) : best - pre;
            int count = Math.Min(columns, got - start);
            Array.Copy(_scan, start, _win, 0, count);
            return count;
        }

        // ── Face ──────────────────────────────────────────────────────────────
        private static void DrawGraticule(VoxelBatch batch, in ScopePanel p, int vdiv, int hdiv)
        {
            // Border.
            batch.RectXZ(p.Y, p.X0, p.ZTop, p.X1, p.ZBottom, Palette.Scale(Palette.Graticule, 1.6f));

            // Interior division ticks, dim so they frame the trace without competing.
            for (int i = 1; i < hdiv; i++)
            {
                float x = p.X0 + p.Width * i / hdiv;
                batch.Line(new point3d(x, p.Y, p.ZTop), new point3d(x, p.Y, p.ZBottom),
                           Palette.Graticule, 3f);
            }
            for (int i = 1; i < vdiv; i++)
            {
                float z = p.ZTop + (p.ZBottom - p.ZTop) * i / vdiv;
                batch.Line(new point3d(p.X0, p.Y, z), new point3d(p.X1, p.Y, z),
                           Palette.Graticule, 3f);
            }

            // Centre cross, brighter — the 0 V / centre-time reference.
            int c = Palette.Scale(Palette.Graticule, 2.2f);
            batch.Line(new point3d(p.X0, p.Y, p.CentreZ), new point3d(p.X1, p.Y, p.CentreZ), c, 1.5f);
            float cx = (p.X0 + p.X1) * 0.5f;
            batch.Line(new point3d(cx, p.Y, p.ZTop), new point3d(cx, p.Y, p.ZBottom), c, 1.5f);
        }

        private static void DrawTrace(VoxelBatch batch, in ScopePanel p, float[] s, int n,
                                      float gain, int col)
        {
            float zc   = p.CentreZ;
            float step = p.Width / (n - 1);
            float prevZ = 0;

            for (int i = 0; i < n; i++)
            {
                float x = p.X0 + i * step;
                float z = zc - s[i] * gain;               // -Z is up: +volts goes up

                bool clipped = z < p.ZTop || z > p.ZBottom;
                z = Math.Clamp(z, p.ZTop, p.ZBottom);
                int  cc = clipped ? Palette.Warning : col; // saturated input reads red

                if (i == 0) { batch.Add(x, p.Y, z, cc); prevZ = z; continue; }

                // Join to the previous sample so edges read as edges.
                if (MathF.Abs(z - prevZ) > batch.Spacing)
                    batch.Line(new point3d(x, p.Y, prevZ), new point3d(x, p.Y, z), cc);
                else
                    batch.Add(x, p.Y, z, cc);

                prevZ = z;
                if (batch.BudgetHit) return;
            }
        }

        private static void DrawDashed(VoxelBatch batch, float y, float x0, float x1, float z, int col)
        {
            float dash = batch.Spacing * 4f;
            for (float x = x0; x < x1; x += dash * 2f)
                batch.Line(new point3d(x, y, z), new point3d(MathF.Min(x + dash, x1), y, z), col);
        }

        // ── Header ────────────────────────────────────────────────────────────
        private void DrawHeader(Hud hud, ScopeSource src, in ScopePanel p, float voltsPerDiv,
                                float textSize)
        {
            string timePerDiv = src.SampleRateHz > 1f
                ? Hud.Eng(_lastColumns / 10.0 / src.SampleRateHz, "S")
                : "--";

            hud.Text(new point3d(p.X0, p.Y, p.HeaderZ), textSize,
                     src.Connected ? Palette.Trace : Palette.TextDim,
                     "SCOPE  " + src.Status);
            hud.TextRight(p.X1, p.Y, p.HeaderZ, textSize, Palette.TextDim,
                     Hud.Eng(voltsPerDiv, "V") + "/DIV  " + timePerDiv + "/DIV  " +
                     Hud.Eng(src.SampleRateHz, "HZ"));
        }

        // Column count of the last window — used for the time/div readout.
        private int _lastColumns = 1;

        /// <summary>Called by Draw via the column computation; kept separate so the
        /// readout can quote the real time base.</summary>
        public void NoteColumns(int columns) => _lastColumns = Math.Max(1, columns);
    }
}
