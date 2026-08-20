// ═══════════════════════════════════════════════════════════════════════════
//  Ohm.cs — the circuit model (Ohm's law + series/parallel combination)
//
//  A small recursive element tree, not a nodal-analysis matrix solver: the
//  circuits are fixed preset topologies, so the direct translation of the
//  textbook rules is exact, trivial to reason about, and cheap.
//
//      root.ApplyVoltage(sourceVolts);     // solves the whole circuit
//
//  Series  → same CURRENT through every child, resistances add.
//  Parallel→ same VOLTAGE across every child, conductances add.
//
//  Solving is NOT done in the render loop — CircuitScene solves only when
//  something the user can change actually changed (see its dirty flag).
// ═══════════════════════════════════════════════════════════════════════════

using System;

namespace EDes.Sim
{
    public abstract class CircuitElement
    {
        /// <summary>Volts across this element (set by the last solve).</summary>
        public double Voltage;
        /// <summary>Amps through this element (set by the last solve).</summary>
        public double Current;

        public abstract double Resistance();
        public abstract void ApplyVoltage(double v);
        public abstract void ApplyCurrent(double i);

        /// <summary>Watts dissipated — V × I for every element type.</summary>
        public double Power => Voltage * Current;
    }

    public sealed class Resistor : CircuitElement
    {
        public string Name;
        public double R;

        public Resistor(string name, double ohms) { Name = name; R = Math.Max(0.01, ohms); }

        public override double Resistance() => R;
        public override void ApplyVoltage(double v) { Voltage = v; Current = v / R; }
        public override void ApplyCurrent(double i) { Current = i; Voltage = i * R; }
    }

    public sealed class SeriesGroup : CircuitElement
    {
        public readonly CircuitElement[] Children;
        public SeriesGroup(params CircuitElement[] children) { Children = children; }

        public override double Resistance()
        {
            double sum = 0;
            foreach (var c in Children) sum += c.Resistance();
            return sum;
        }

        public override void ApplyCurrent(double i)
        {
            Current = i;
            Voltage = i * Resistance();
            foreach (var c in Children) c.ApplyCurrent(i);
        }

        public override void ApplyVoltage(double v)
        {
            Voltage = v;
            ApplyCurrent(v / Resistance());
        }
    }

    public sealed class ParallelGroup : CircuitElement
    {
        public readonly CircuitElement[] Children;
        public ParallelGroup(params CircuitElement[] children) { Children = children; }

        public override double Resistance()
        {
            double g = 0;
            foreach (var c in Children) g += 1.0 / c.Resistance();
            return g <= 0 ? double.PositiveInfinity : 1.0 / g;
        }

        public override void ApplyVoltage(double v)
        {
            Voltage = v;
            Current = v / Resistance();
            foreach (var c in Children) c.ApplyVoltage(v);
        }

        public override void ApplyCurrent(double i)
        {
            Current = i;
            ApplyVoltage(i * Resistance());
        }
    }

    /// <summary>One built-in circuit: a topology plus the resistors the user can tune.</summary>
    public sealed class CircuitPreset
    {
        public required string          Name      { get; init; }
        public required CircuitElement  Root      { get; init; }
        /// <summary>Tunable resistors, in the order they appear left→right.</summary>
        public required Resistor[]      Resistors { get; init; }
        /// <summary>The rule this circuit demonstrates — shown in the volume.</summary>
        public required string          Law       { get; init; }
    }

    public static class CircuitPresets
    {
        /// <summary>The four built-in circuits. Fresh instances each call so the
        /// caller owns (and can freely re-tune) the resistors.</summary>
        public static CircuitPreset[] Build()
        {
            var a = new Resistor("R1", 100);
            var single = new CircuitPreset
            {
                Name = "SINGLE RESISTOR", Root = a, Resistors = new[] { a },
                Law  = "V = I x R",
            };

            var b1 = new Resistor("R1", 100);
            var b2 = new Resistor("R2", 220);
            var series = new CircuitPreset
            {
                Name = "SERIES", Root = new SeriesGroup(b1, b2), Resistors = new[] { b1, b2 },
                Law  = "Rt = R1 + R2   (same I)",
            };

            var c1 = new Resistor("R1", 100);
            var c2 = new Resistor("R2", 220);
            var parallel = new CircuitPreset
            {
                Name = "PARALLEL", Root = new ParallelGroup(c1, c2), Resistors = new[] { c1, c2 },
                Law  = "1/Rt = 1/R1 + 1/R2   (same V)",
            };

            var d1 = new Resistor("R1", 100);
            var d2 = new Resistor("R2", 220);
            var d3 = new Resistor("R3", 470);
            var seriesParallel = new CircuitPreset
            {
                Name = "SERIES-PARALLEL",
                Root = new SeriesGroup(d1, new ParallelGroup(d2, d3)),
                Resistors = new[] { d1, d2, d3 },
                Law  = "Rt = R1 + (R2 || R3)",
            };

            return new[] { single, series, parallel, seriesParallel };
        }
    }
}
