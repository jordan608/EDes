// ═══════════════════════════════════════════════════════════════════════════
//  ParticleManager.cs — Simple SoA particle pool
//
//  Structure-of-Arrays (SoA) layout:
//    One float[] per field rather than one object[] of structs.
//    This keeps each field's data contiguous in memory → better cache usage
//    when iterating over all particles in Update() or Draw().
//
//  Swap-remove on death:
//    When a particle dies, copy the LAST active particle into its slot and
//    decrement _count. This is O(1) and avoids shifting the array.
//    Order of particles is not preserved — that's fine for visual effects.
//
//  Budget control:
//    ParticleBudget in GameSettings limits how many are drawn per frame.
//    Update() still runs for all alive particles; only Draw() is capped.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes
{
    public class ParticleManager
    {
        // ── Pool size ─────────────────────────────────────────────────────────
        // Tune this to your budget. 1000 is a safe starting point.
        private const int MAX = 1000;
        private int _count = 0;

        // ── SoA fields ────────────────────────────────────────────────────────
        private readonly float[] _x    = new float[MAX];
        private readonly float[] _y    = new float[MAX];
        private readonly float[] _z    = new float[MAX];
        private readonly float[] _vx   = new float[MAX];  // velocity X
        private readonly float[] _vy   = new float[MAX];  // velocity Y
        private readonly float[] _vz   = new float[MAX];  // velocity Z
        private readonly float[] _life = new float[MAX];  // remaining lifetime (seconds)
        private readonly float[] _maxL = new float[MAX];  // initial lifetime (for fade)
        private readonly int[]   _col  = new int[MAX];    // base colour 0xRRGGBB

        /// <summary>Number of currently alive particles.</summary>
        public int Count => _count;

        // ── Spawn ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Spawns one particle. Silently does nothing if the pool is full.
        /// </summary>
        public void Spawn(float x, float y, float z,
                          float vx, float vy, float vz,
                          float lifetime, int color)
        {
            if (_count >= MAX) return;
            int i    = _count++;
            _x[i]    = x;   _y[i]  = y;   _z[i]  = z;
            _vx[i]   = vx;  _vy[i] = vy;  _vz[i] = vz;
            _life[i] = _maxL[i] = lifetime;
            _col[i]  = color;
        }

        /// <summary>
        /// Convenience: spawn a radial burst of <paramref name="count"/> particles
        /// centred at (x,y,z). Useful for explosions and pickups.
        /// </summary>
        public void SpawnBurst(float x, float y, float z,
                               float minSpeed, float maxSpeed,
                               float minLife,  float maxLife,
                               int colorA, int colorB,
                               int count, Random rand)
        {
            for (int n = 0; n < count; n++)
            {
                // Random direction in a full sphere
                double phi   = rand.NextDouble() * Math.PI * 2;
                double theta = Math.Acos(rand.NextDouble() * 2 - 1);
                float  spd   = minSpeed + (float)rand.NextDouble() * (maxSpeed - minSpeed);

                float vx = (float)(Math.Sin(theta) * Math.Cos(phi)) * spd;
                float vy = (float)(Math.Sin(theta) * Math.Sin(phi)) * spd;
                float vz = (float) Math.Cos(theta)                  * spd;
                float lt = minLife + (float)rand.NextDouble() * (maxLife - minLife);
                int   col = LerpColor(colorA, colorB, (float)rand.NextDouble());

                Spawn(x, y, z, vx, vy, vz, lt, col);
            }
        }

        // ── Update ────────────────────────────────────────────────────────────

        /// <summary>Advance physics and remove dead particles. Call once per frame.</summary>
        public void Update(float dt)
        {
            for (int i = 0; i < _count; )
            {
                _life[i] -= dt;
                if (_life[i] <= 0f)
                {
                    // Swap-remove: fill this slot with the last alive particle
                    int last = --_count;
                    if (i != last)
                    {
                        _x[i]  = _x[last];  _y[i]  = _y[last];  _z[i]  = _z[last];
                        _vx[i] = _vx[last]; _vy[i] = _vy[last]; _vz[i] = _vz[last];
                        _life[i] = _life[last]; _maxL[i] = _maxL[last];
                        _col[i]  = _col[last];
                    }
                    continue; // do NOT increment — recheck this slot (now holds swapped particle)
                }

                // Simple Euler integration — add gravity or drag here if needed
                _x[i] += _vx[i] * dt;
                _y[i] += _vy[i] * dt;
                _z[i] += _vz[i] * dt;

                // Optional: drag
                // _vx[i] *= 0.98f; _vy[i] *= 0.98f; _vz[i] *= 0.98f;

                // Optional: gravity
                // _vz[i] -= 2.5f * dt;

                i++;
            }
        }

        // ── Draw ──────────────────────────────────────────────────────────────

        /// <summary>Draw up to <paramref name="budget"/> particles this frame.</summary>
        public void Draw(LedHostCS ledHost, ref vxl_state_t vs, int budget)
        {
            int draw = Math.Min(_count, budget);
            for (int i = 0; i < draw; i++)
            {
                // Fade in during first 10% of life, fade out during last 30%
                float ageFrac = 1f - _life[i] / _maxL[i];   // 0 = just born, 1 = about to die
                float fade = ageFrac < 0.1f ? ageFrac * 10f
                           : ageFrac > 0.7f ? (1f - ageFrac) / 0.3f
                           : 1f;

                int col = DimColor(_col[i], fade);
                if (col == 0) continue;  // skip invisible particles (saves a DrawVox call)

                ledHost.DrawVox(ref vs, _x[i], _y[i], _z[i], col);
            }
        }

        // ── Colour helpers ────────────────────────────────────────────────────

        public static int DimColor(int col, float bri)
        {
            bri = Math.Clamp(bri, 0f, 1f);
            int r = (int)(((col >> 16) & 0xFF) * bri);
            int g = (int)(((col >>  8) & 0xFF) * bri);
            int b = (int)(( col        & 0xFF) * bri);
            return (r << 16) | (g << 8) | b;
        }

        public static int LerpColor(int a, int b, float t)
        {
            int ar = (a >> 16) & 0xFF, ag = (a >> 8) & 0xFF, ab = a & 0xFF;
            int br = (b >> 16) & 0xFF, bg = (b >> 8) & 0xFF, bb = b & 0xFF;
            return ((int)(ar + (br - ar) * t) << 16)
                 | ((int)(ag + (bg - ag) * t) <<  8)
                 |  (int)(ab + (bb - ab) * t);
        }
    }
}
