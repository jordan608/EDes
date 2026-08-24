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

    /// <summary>Supply range for the teaching circuits.
    ///
    /// Here rather than on CircuitScene because it is a fact about the circuits, not about
    /// the scene that draws them -- and because the scene drags in the whole display stack,
    /// which put the constant out of reach of the checks that need to sweep it.
    ///
    /// It was 1..24, which excluded both ends of ordinary bench work and made the LED
    /// preset instruction "drop the supply below Vf" impossible to follow: Vf is 2V and
    /// the floor was 1V in 1V steps.</summary>
    public static class Supply
    {
        public const double MinVolts = 0.1, MaxVolts = 60.0;
    }

    public sealed class Resistor : CircuitElement
    {
        public string Name;
        public double R;

        /// <summary>The full range of real parts: 0.1 ohm shunts to 10M pull-ups.
        ///
        /// It used to be 1 .. 10k, which excluded most of the values anyone actually
        /// designs with -- a 100k divider, a 1M bias resistor, a 0.1 ohm current sense --
        /// so any lesson involving them could not be set up at all.</summary>
        public const double MinOhms = 0.1;
        public const double MaxOhms = 10e6;

        public Resistor(string name, double ohms)
        {
            Name = name;
            R    = Math.Clamp(ohms, MinOhms, MaxOhms);
        }

        public override double Resistance() => R;
        public override void ApplyVoltage(double v) { Voltage = v; Current = v / R; }
        public override void ApplyCurrent(double i) { Current = i; Voltage = i * R; }
    }

    /// <summary>A diode as a fixed forward drop -- the model every textbook starts with.
    ///
    /// NOT a resistance, which is the entire point of it being here. In series with a
    /// resistor the current is set by the LEFTOVER voltage, (Vsupply - Vf) / R, so the
    /// same resistor gives a different current on a different supply. That is the first
    /// place Ohm's law alone gives the wrong answer, and it cannot be demonstrated with
    /// resistors only.
    ///
    /// Resistance() reports a LINEARISED equivalent at the current operating point,
    /// because the series/parallel solver is built on resistances. That is exact for the
    /// series case -- the one that matters here -- and it is why the diode is only used
    /// in a series preset. Reverse bias is an open circuit, represented as a very large
    /// resistance rather than infinity so a parallel branch containing one still solves
    /// instead of producing a NaN.</summary>
    public sealed class Diode : CircuitElement
    {
        public string Name;
        /// <summary>Forward drop in volts: ~0.7 silicon, ~2.0 red LED, ~3.2 blue.</summary>
        public double Vf;

        private const double OpenOhms = 1e9;
        private double _r = OpenOhms;

        public Diode(string name, double vf) { Name = name; Vf = Math.Max(0.0, vf); }

        public override double Resistance() => _r;

        public override void ApplyVoltage(double v)
        {
            Voltage = Math.Min(v, Vf);
            // Below the forward drop it does not conduct at all. Above it, the drop is
            // fixed and the rest of the circuit decides the current -- so the equivalent
            // resistance is whatever makes that consistent, resolved by the series group
            // when it re-applies the current below.
            Current = v > Vf ? Current : 0.0;
            _r      = Current > 1e-12 ? Voltage / Current : OpenOhms;
        }

        public override void ApplyCurrent(double i)
        {
            Current = Math.Max(0.0, i);
            Voltage = Current > 1e-12 ? Vf : 0.0;
            _r      = Current > 1e-12 ? Vf / Current : OpenOhms;
        }
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

            // Fixed voltage drops (diodes) come off the top before the resistances get
            // what is left. Without this a diode would be treated as a resistance and the
            // series LED circuit -- the whole reason the Diode element exists -- would
            // solve to the wrong current.
            double drops = 0.0, ohms = 0.0;
            foreach (var c in Children)
            {
                if (c is Diode d) drops += d.Vf;
                else              ohms  += c.Resistance();
            }

            if (drops > 0.0)
            {
                // Below the total forward drop nothing conducts. Not "a very small
                // current": an LED under its Vf is off, and showing a trickle would
                // teach the opposite of the lesson.
                double headroom = v - drops;
                double i = headroom > 0.0 && ohms > 0.0 ? headroom / ohms : 0.0;
                ApplyCurrent(i);
                return;
            }

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

        /// <summary>A concrete thing to try, with numbers in it. Optional but expected:
        /// a circuit on a display with no instruction is a picture, not a lesson, and
        /// "adjust the resistors" teaches nothing that "set RTOP to 26k" does.</summary>
        public string Note { get; init; } = "";

        /// <summary>What this preset is FOR, in one line. Shown in the 2D panel next to
        /// the inputs rather than in the volume, because it is read once while setting
        /// the circuit up and would otherwise spend scarce text rows every frame.</summary>
        public string Try  { get; init; } = "";

        /// <summary>Index of the element the lesson is about (the divider's output leg,
        /// say), or -1. Used to draw attention to it, not to change the solve.</summary>
        public int Highlight { get; init; } = -1;
    }
}
