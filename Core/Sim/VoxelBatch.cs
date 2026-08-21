// ═══════════════════════════════════════════════════════════════════════════
//  VoxelBatch.cs — budget-aware voxel accumulator + display-bounds clipper
//
//  Everything this app draws (wires, resistor zigzags, flow dots, the scope
//  trace, the graticule) goes through here, so three rules hold globally:
//
//    1. ONE native call per frame. Points accumulate into pre-allocated
//       parallel arrays and are flushed with a single DrawVox_Batch.
//    2. A HARD max-voxel limit. Add() refuses points past Limit and counts
//       the drop, so a dense effect can never blow the frame budget. Draw
//       order therefore = priority order (circuit first, chrome last).
//    3. NOTHING outside the display volume. The volume is a cylinder:
//       x²+y² ≤ radius² and |z| ≤ zHalf, taken live from the SDK
//       (LedHostCS.GetAspectRatioX / vs.boundr) — never hardcoded.
//
//  Spacing between the points of a line comes from the real voxel pitch
//  (2·radius / vs.xsiz) divided by the UI's voxel-density setting, so
//  "density" means what it says at any display size.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes.Sim
{
    public sealed class VoxelBatch
    {
        /// <summary>Hard ceiling on the settings slider.
        ///
        /// The scratch arrays are NOT sized to this. At 5M the four of them would be 80 MB
        /// of pinned-ish Large Object Heap allocated at construction whether or not the
        /// budget was ever raised, which is a real cost for a ceiling most boards never
        /// approach. They grow to whatever BeginFrame asks for instead, and never shrink —
        /// so memory tracks what is actually being drawn, and a busy frame does not pay
        /// for a reallocation twice.</summary>
        public const int MAX_CAPACITY = 5_000_000;

        /// <summary>Starting capacity — enough for a typical board without a single
        /// resize, so the common case never allocates after startup.</summary>
        private const int INITIAL_CAPACITY = 200_000;

        private float[] _x = new float[INITIAL_CAPACITY];
        private float[] _y = new float[INITIAL_CAPACITY];
        private float[] _z = new float[INITIAL_CAPACITY];
        private int[]   _c = new int[INITIAL_CAPACITY];
        private int _n;

        /// <summary>Voxels the scratch arrays can currently hold.</summary>
        public int Capacity => _x.Length;

        // ── Live frame parameters (set by BeginFrame) ─────────────────────────
        public int   Limit   { get; private set; } = MAX_CAPACITY;
        public float Radius  { get; private set; } = 4f;    // cylinder radius (X/Y)
        public float ZHalf   { get; private set; } = 2f;    // half height (Z)
        public float Spacing { get; private set; } = 0.03f; // world units between points

        /// <summary>Voxels accepted this frame.</summary>
        public int Count   => _n;
        /// <summary>Voxels refused this frame (budget or out of bounds).</summary>
        public int Dropped { get; private set; }
        public bool BudgetHit => _n >= Limit;

        public void BeginFrame(int limit, float radius, float zHalf, float spacing)
        {
            _n      = 0;
            Dropped = 0;
            Limit   = Math.Clamp(limit, 1_000, MAX_CAPACITY);
            Radius  = radius;
            ZHalf   = zHalf;
            Spacing = MathF.Max(0.002f, spacing);

            // Grow to the requested budget, never shrink. Shrinking would mean
            // reallocating every time an adaptive throttle eased off, i.e. exactly when
            // the frame is already struggling.
            if (_x.Length < Limit) Grow(Limit);
        }

        private void Grow(int need)
        {
            // Round up in powers of two from the current size, so repeatedly nudging the
            // budget slider does not reallocate on every step.
            int cap = _x.Length;
            while (cap < need && cap < MAX_CAPACITY) cap = Math.Min(MAX_CAPACITY, cap * 2);

            _x = new float[cap];
            _y = new float[cap];
            _z = new float[cap];
            _c = new int[cap];
        }

        /// <summary>True if the point is inside the physical display volume.</summary>
        public bool InBounds(float x, float y, float z)
            => x * x + y * y <= Radius * Radius && z <= ZHalf && z >= -ZHalf;

        /// <summary>Add one voxel. Returns false if dropped (out of bounds or over budget).</summary>
        public bool Add(float x, float y, float z, int col)
        {
            if (_n >= Limit || !InBounds(x, y, z)) { Dropped++; return false; }
            _x[_n] = x; _y[_n] = y; _z[_n] = z; _c[_n] = col;
            _n++;
            return true;
        }

        public bool Add(point3d p, int col) => Add(p.x, p.y, p.z, col);

        // ── Primitives ────────────────────────────────────────────────────────

        /// <summary>Point-sampled line at the frame's spacing (× spacingMul).</summary>
        public void Line(point3d a, point3d b, int col, float spacingMul = 1f)
        {
            float dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
            float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len <= 1e-6f) { Add(a, col); return; }

            float step  = Spacing * MathF.Max(0.25f, spacingMul);
            int   steps = (int)(len / step) + 1;
            if (steps > 20_000) steps = 20_000;      // pathological-input guard

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                if (!Add(a.x + dx * t, a.y + dy * t, a.z + dz * t, col) && BudgetHit) return;
            }
        }

        /// <summary>A small solid blob — flow dots, node junctions, scope cursor.</summary>
        public void Blob(point3d c, float radius, int col)
        {
            float step = Spacing;
            int   r    = Math.Max(0, (int)(radius / step));
            for (int iz = -r; iz <= r; iz++)
            for (int iy = -r; iy <= r; iy++)
            for (int ix = -r; ix <= r; ix++)
            {
                if (ix * ix + iy * iy + iz * iz > r * r) continue;
                if (!Add(c.x + ix * step, c.y + iy * step, c.z + iz * step, col) && BudgetHit) return;
            }
        }

        /// <summary>One ring in the plane spanned by axisU/axisV — the cheapest
        /// primitive that reads unmistakably as a curve/sphere in wireframe.</summary>
        public void Ring(point3d centre, float radius, point3d axisU, point3d axisV,
                         int col, float spacingMul = 1f)
        {
            float step  = Spacing * MathF.Max(0.25f, spacingMul);
            int   segs  = Math.Clamp((int)(2f * MathF.PI * radius / step), 8, 4096);
            for (int i = 0; i < segs; i++)
            {
                float a = i * 2f * MathF.PI / segs;
                float cu = MathF.Cos(a) * radius, cv = MathF.Sin(a) * radius;
                if (!Add(centre.x + axisU.x * cu + axisV.x * cv,
                         centre.y + axisU.y * cu + axisV.y * cv,
                         centre.z + axisU.z * cu + axisV.z * cv, col) && BudgetHit) return;
            }
        }

        /// <summary>Axis-aligned rectangle outline in the plane y = constant.</summary>
        public void RectXZ(float y, float x0, float z0, float x1, float z1, int col,
                           float spacingMul = 1f)
        {
            Line(new point3d(x0, y, z0), new point3d(x1, y, z0), col, spacingMul);
            Line(new point3d(x1, y, z0), new point3d(x1, y, z1), col, spacingMul);
            Line(new point3d(x1, y, z1), new point3d(x0, y, z1), col, spacingMul);
            Line(new point3d(x0, y, z1), new point3d(x0, y, z0), col, spacingMul);
        }

        // ── Flush ─────────────────────────────────────────────────────────────

        /// <summary>Submit everything accumulated this frame in ONE native call.</summary>
        public void Flush(LedHostCS ledHost, ref vxl_state_t vs)
        {
            if (_n == 0) return;
            ledHost.DrawVox_Batch(ref vs, ref _x[0], ref _y[0], ref _z[0], ref _c[0], _n, 0);
        }
    }
}
