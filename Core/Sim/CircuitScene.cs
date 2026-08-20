// ═══════════════════════════════════════════════════════════════════════════
//  CircuitScene.cs — solve + layout (everything the renderer just reads)
//
//  Three stages, strictly separated (see PROJECT_GUIDE section 11.5):
//
//    SOLVE   root.ApplyVoltage(V) gives per-element V / I / P.
//    LAYOUT  walk the element tree ONCE into a flat List<WireSegment> of small
//            structs holding nothing but positions + what the renderer needs.
//    RENDER  (CircuitRenderer) iterates that list every frame. No allocation,
//            no virtual dispatch, no solving.
//
//  Solve+layout run only when the dirty flag is set (preset change, resistor
//  or voltage edit, or the display bounds changing) — never per frame. Every
//  native draw call on this SDK is real work; recomputing unchanged geometry
//  30x/s would just burn frame time for nothing.
//
//  Geometry (world units, -Z is up):
//     x in [-W, +W]  the circuit's left-to-right span
//     z = TopZ       the component wire (more negative = higher up)
//     z = BotZ       the return wire
//     y              lanes: parallel branches fan into separate depths, which
//                    is the entire reason to show this volumetrically
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using Voxon;

namespace EDes.Sim
{
    /// <summary>One drawable piece of the circuit. A flat struct on purpose.</summary>
    public struct WireSegment
    {
        public point3d   Start, End;
        public double    Current;      // amps through this piece
        public Resistor? Body;         // non-null: draw a resistor here
        public bool      IsBattery;    // draw the source symbol
    }

    public sealed class CircuitScene
    {
        // ── Tunable state (UI/keys write, dirty flag guards the recompute) ────
        public CircuitPreset[] Presets { get; } = CircuitPresets.Build();
        public int    PresetIndex { get; private set; }
        public double SourceVolts { get; private set; } = 12.0;
        public int    Selected    { get; private set; }      // index into Active.Resistors

        public CircuitPreset Active => Presets[PresetIndex];

        // ── Solved / laid-out results (renderer reads these) ─────────────────
        public readonly List<WireSegment> Segments = new(64);
        public double TotalResistance { get; private set; }
        public double TotalCurrent    { get; private set; }
        public double TotalPower      { get; private set; }
        /// <summary>Largest single-resistor power in the CURRENT circuit — the heat
        /// gradient is normalised to this so it always uses its full range.</summary>
        public double MaxPower        { get; private set; } = 1e-9;

        // ── Layout extents, derived from the live display bounds ──────────────
        public float W    { get; private set; }
        public float TopZ { get; private set; }
        public float BotZ { get; private set; }
        public float LaneSpread { get; private set; }

        private bool  _dirty = true;
        private float _builtRadius, _builtZHalf;

        // ── Mutators (all just mark dirty) ────────────────────────────────────

        public void SetPreset(int index)
        {
            index = ((index % Presets.Length) + Presets.Length) % Presets.Length;
            if (index == PresetIndex) return;
            PresetIndex = index;
            Selected    = 0;
            _dirty      = true;
        }

        public void SetSourceVolts(double v)
        {
            v = Math.Clamp(v, 1.0, 24.0);
            if (Math.Abs(v - SourceVolts) < 1e-9) return;
            SourceVolts = v;
            _dirty      = true;
        }

        public void SelectNext(int dir)
        {
            int n = Active.Resistors.Length;
            Selected = ((Selected + dir) % n + n) % n;
        }

        public void ScaleSelected(double factor)
        {
            var r = Active.Resistors[Selected];
            r.R    = Math.Clamp(r.R * factor, 1.0, 10_000.0);
            _dirty = true;
        }

        /// <summary>Set resistor i of the active circuit (no-op if it has fewer).</summary>
        public void SetResistor(int index, double ohms)
        {
            var rs = Active.Resistors;
            if (index < 0 || index >= rs.Length) return;
            ohms = Math.Clamp(ohms, 1.0, 10_000.0);
            if (Math.Abs(rs[index].R - ohms) < 1e-9) return;
            rs[index].R = ohms;
            _dirty      = true;
        }

        public void MarkDirty() => _dirty = true;

        // ── Solve + layout ────────────────────────────────────────────────────

        /// <summary>Recompute if anything changed. Cheap no-op otherwise.
        /// Bounds come from the live display size so the circuit is always laid
        /// out to fit the hardware it is actually running on.</summary>
        public void RecomputeIfDirty(float radius, float zHalf)
        {
            if (!_dirty &&
                MathF.Abs(radius - _builtRadius) < 1e-4f &&
                MathF.Abs(zHalf  - _builtZHalf)  < 1e-4f) return;

            _builtRadius = radius;
            _builtZHalf  = zHalf;
            _dirty       = false;

            // Fit inside the volume with margin. The scope panel owns the lower
            // half of the volume (positive Z), so the circuit sits in the upper.
            W          = radius * 0.60f;
            TopZ       = -zHalf * 0.58f;
            BotZ       = -zHalf * 0.12f;
            LaneSpread = radius * 0.28f;

            Solve();
            BuildSegments();
        }

        private void Solve()
        {
            var root = Active.Root;
            root.ApplyVoltage(SourceVolts);

            TotalResistance = root.Resistance();
            TotalCurrent    = root.Current;
            TotalPower      = root.Power;

            MaxPower = 1e-9;
            foreach (var r in Active.Resistors)
                if (r.Power > MaxPower) MaxPower = r.Power;
        }

        private void BuildSegments()
        {
            Segments.Clear();

            var topLeft  = new point3d(-W, 0f, TopZ);
            var topRight = new point3d( W, 0f, TopZ);
            var botLeft  = new point3d(-W, 0f, BotZ);
            var botRight = new point3d( W, 0f, BotZ);

            double it = TotalCurrent;

            // Source on the left edge, return wire along the bottom.
            Add(botLeft, topLeft, it, null, isBattery: true);
            Emit(Active.Root, topLeft, topRight);
            Add(topRight, botRight, it, null);
            Add(botRight, botLeft,  it, null);
        }

        private void Add(point3d a, point3d b, double current, Resistor? body,
                         bool isBattery = false)
            => Segments.Add(new WireSegment
            {
                Start = a, End = b, Current = current, Body = body, IsBattery = isBattery,
            });

        /// <summary>Lay one element out across the span a→b, recursing into groups.
        /// Series subdivides the span; parallel fans its children into Y lanes and
        /// adds a splice on each side so it reads as a wire splitting and rejoining.</summary>
        private void Emit(CircuitElement e, point3d a, point3d b)
        {
            switch (e)
            {
                case Resistor r:
                    Add(a, b, r.Current, r);
                    return;

                case SeriesGroup s:
                {
                    int n = s.Children.Length;
                    for (int i = 0; i < n; i++)
                        Emit(s.Children[i], Lerp(a, b, i / (float)n), Lerp(a, b, (i + 1) / (float)n));
                    return;
                }

                case ParallelGroup p:
                {
                    const float spliceFrac = 0.20f;
                    point3d fanIn  = Lerp(a, b, spliceFrac);
                    point3d fanOut = Lerp(a, b, 1f - spliceFrac);
                    int n = p.Children.Length;

                    for (int i = 0; i < n; i++)
                    {
                        // Lanes centred on y=0: -spread … +spread
                        float t = n == 1 ? 0.5f : i / (float)(n - 1);
                        float y = (t - 0.5f) * 2f * LaneSpread;

                        var la = new point3d(fanIn.x,  y, fanIn.z);
                        var lb = new point3d(fanOut.x, y, fanOut.z);
                        double ic = p.Children[i].Current;

                        Add(a,  la, ic, null);       // splice in
                        Emit(p.Children[i], la, lb); // the branch itself
                        Add(lb, b,  ic, null);       // splice out
                    }
                    return;
                }
            }
        }

        public static point3d Lerp(point3d a, point3d b, float t)
            => new point3d(a.x + (b.x - a.x) * t,
                           a.y + (b.y - a.y) * t,
                           a.z + (b.z - a.z) * t);
    }
}
