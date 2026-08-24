// ═══════════════════════════════════════════════════════════════════════════
//  Ohm.Presets.cs — the built-in teaching circuits
//
//  Split out of Ohm.cs because the SOLVER and the CURRICULUM are different
//  things that change for different reasons: the solver is finished, the set of
//  worked examples is not.
//
//  Every preset states the law it demonstrates and what to try, because a
//  circuit on a display with no explanation is a picture rather than a lesson.
//  The "try" line is deliberately a concrete instruction with a number in it --
//  "set R2 to 1k and watch the divider output" teaches something; "adjust the
//  resistors" does not.
//
//  Ordered easiest-first, so walking 1..N is a course rather than a menu.
// ═══════════════════════════════════════════════════════════════════════════

using System;

namespace EDes.Sim
{
    public static class CircuitPresets
    {
        /// <summary>Every built-in circuit. Fresh instances each call so the caller owns
        /// (and can freely re-tune) the resistors.</summary>
        public static CircuitPreset[] Build()
        {
            return new[]
            {
                Single(), Series(), Parallel(), SeriesParallel(),
                Divider(), CurrentDivider(), Ladder(), Bridge(), Led(),
            };
        }

        // ── 1. One resistor: V = I x R, and nothing else ──────────────────────
        private static CircuitPreset Single()
        {
            var r = new Resistor("R1", 100);
            return new CircuitPreset
            {
                Name = "SINGLE RESISTOR", Root = r, Resistors = new[] { r },
                Law  = "V = I x R",
                Try  = "Halve R1 to 50 and watch the current double. " +
                       "Power goes up 2x, not 4x -- V is fixed, so P = V x I.",
            };
        }

        // ── 2. Series: current is shared, voltage divides ─────────────────────
        private static CircuitPreset Series()
        {
            var a = new Resistor("R1", 100);
            var b = new Resistor("R2", 220);
            return new CircuitPreset
            {
                Name = "SERIES", Root = new SeriesGroup(a, b), Resistors = new[] { a, b },
                Law  = "Rt = R1 + R2   (same I through both)",
                Try  = "Make R2 ten times R1. It takes ten times the voltage and " +
                       "dissipates ten times the power, on the same current.",
            };
        }

        // ── 3. Parallel: voltage is shared, current divides ───────────────────
        private static CircuitPreset Parallel()
        {
            var a = new Resistor("R1", 100);
            var b = new Resistor("R2", 220);
            return new CircuitPreset
            {
                Name = "PARALLEL", Root = new ParallelGroup(a, b), Resistors = new[] { a, b },
                Law  = "1/Rt = 1/R1 + 1/R2   (same V across both)",
                Try  = "Set both to 100. Rt is 50 -- LESS than either one. " +
                       "Two equal resistors in parallel always halve.",
            };
        }

        // ── 4. Both at once ───────────────────────────────────────────────────
        private static CircuitPreset SeriesParallel()
        {
            var a = new Resistor("R1", 100);
            var b = new Resistor("R2", 220);
            var c = new Resistor("R3", 470);
            return new CircuitPreset
            {
                Name = "SERIES-PARALLEL",
                Root = new SeriesGroup(a, new ParallelGroup(b, c)),
                Resistors = new[] { a, b, c },
                Law  = "Rt = R1 + (R2 || R3)",
                Try  = "Raise R3 towards 10M. The parallel pair approaches R2 alone, " +
                       "because almost no current takes the R3 path.",
            };
        }

        // ── 5. The voltage divider — the most used circuit in electronics ──────
        private static CircuitPreset Divider()
        {
            var top = new Resistor("RTOP", 10_000);
            var bot = new Resistor("RBOT", 10_000);
            return new CircuitPreset
            {
                Name = "VOLTAGE DIVIDER",
                Root = new SeriesGroup(top, bot),
                Resistors = new[] { top, bot },
                Law  = "Vout = Vin x RBOT / (RTOP + RBOT)",
                Try  = "Equal values give half the supply. For 3.3V from 12V, " +
                       "make RTOP 2.6 times RBOT -- try 26k and 10k.",
                Highlight = 1,   // RBOT: the output is measured across it
            };
        }

        // ── 6. The current divider — the parallel dual of the above ───────────
        private static CircuitPreset CurrentDivider()
        {
            var a = new Resistor("R1", 100);
            var b = new Resistor("R2", 300);
            return new CircuitPreset
            {
                Name = "CURRENT DIVIDER",
                Root = new ParallelGroup(a, b),
                Resistors = new[] { a, b },
                Law  = "I1 / It = R2 / (R1 + R2)   (current prefers the LOW road)",
                Try  = "With 100 and 300, R1 carries three quarters of the current. " +
                       "Note it is the OTHER resistor's value on top -- the mirror of " +
                       "the voltage divider.",
            };
        }

        // ── 7. A three-stage series ladder ────────────────────────────────────
        private static CircuitPreset Ladder()
        {
            var a = new Resistor("R1", 1_000);
            var b = new Resistor("R2", 2_200);
            var c = new Resistor("R3", 4_700);
            return new CircuitPreset
            {
                Name = "SERIES LADDER",
                Root = new SeriesGroup(a, b, c),
                Resistors = new[] { a, b, c },
                Law  = "Kirchhoff: the three voltage drops sum to the supply",
                Try  = "Add up the three voltages in the readout -- they equal the " +
                       "source exactly, whatever you set the resistors to.",
            };
        }

        // ── 8. Wheatstone bridge ──────────────────────────────────────────────
        //
        // Modelled as two independent dividers, which is exactly what it is when the
        // bridge output is unloaded -- the usual case, because you measure it with a
        // high-impedance meter. A LOADED bridge needs nodal analysis and this solver
        // does not do that; the readout says so rather than quietly being wrong.
        private static CircuitPreset Bridge()
        {
            var a = new Resistor("R1", 1_000);
            var b = new Resistor("R2", 1_000);
            var c = new Resistor("R3", 1_000);
            var d = new Resistor("RX", 1_200);
            return new CircuitPreset
            {
                Name = "WHEATSTONE BRIDGE",
                Root = new ParallelGroup(new SeriesGroup(a, b), new SeriesGroup(c, d)),
                Resistors = new[] { a, b, c, d },
                Law  = "Balanced when R1/R2 = R3/RX   (then Vout = 0)",
                Try  = "RX is the unknown. It reads 1200 against three 1k arms, so the " +
                       "bridge is off balance -- set RX to 1000 and the two mid-points " +
                       "sit at the same voltage.",
                Note = "Unloaded bridge: the output is the DIFFERENCE of the two " +
                       "mid-points. A loaded bridge needs nodal analysis, which this " +
                       "solver does not do.",
            };
        }

        // ── 9. LED with a series resistor ─────────────────────────────────────
        //
        // The reason a Diode element exists at all. It is the first circuit anyone
        // builds, and it is the first one where Ohm's law alone gives the wrong answer:
        // the LED is not a resistor, it is a roughly fixed voltage drop, so the resistor
        // sees the SUPPLY MINUS that drop.
        private static CircuitPreset Led()
        {
            var r = new Resistor("R1", 470);
            var d = new Diode("LED", 2.0);
            return new CircuitPreset
            {
                Name = "LED + SERIES RESISTOR",
                Root = new SeriesGroup(r, d),
                Resistors = new[] { r },
                Law  = "I = (Vsupply - Vf) / R      (Vf ~ 2V for a red LED)",
                Try  = "At 12V through 470 ohm the LED gets about 21mA. Drop the supply " +
                       "towards 2V and the current collapses to nothing -- below Vf the " +
                       "LED simply does not conduct.",
                Note = "The LED is a fixed 2V drop, not a resistance. That is why the " +
                       "current is set by the LEFTOVER voltage, and why the same resistor " +
                       "gives a different current on a different supply.",
            };
        }
    }
}
