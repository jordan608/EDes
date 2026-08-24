// ═══════════════════════════════════════════════════════════════════════════
//  CircuitChecks.cs — the teaching circuits must actually be right
//
//  These are worked examples shown to someone learning the subject, so a wrong
//  answer here is worse than a wrong answer anywhere else in the app: it teaches
//  the mistake. Every preset is therefore checked against the law it claims to
//  demonstrate, with numbers computed independently of the solver.
//
//  The diode gets the most attention because it is the one non-linear element,
//  and because the series LED circuit is precisely where Ohm's law alone gives
//  the wrong answer -- the resistor sees the supply MINUS the forward drop, not
//  the supply.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Linq;
using EDes.Sim;

namespace PcbParserTests
{
    internal static class CircuitChecks
    {
        private static int _failures;

        private static void Ok(string what, bool pass)
        {
            if (!pass) _failures++;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        private static bool Near(double a, double b, double eps = 1e-6)
            => Math.Abs(a - b) <= eps * Math.Max(1.0, Math.Abs(b));

        public static int Run()
        {
            var presets = CircuitPresets.Build();

            // ── Every preset is well formed ───────────────────────────────────
            {
                Ok($"there are several circuits, not four ({presets.Length})",
                   presets.Length >= 8);
                bool allNamed = presets.All(p => p.Name.Length > 0 && p.Law.Length > 0);
                Ok("every circuit states its name and its law", allNamed);

                // The instruction is the lesson. A preset without one is a picture.
                bool allTry = presets.All(p => p.Try.Length > 0);
                Ok("every circuit has a concrete thing to try", allTry);

                bool tunable = presets.All(p => p.Resistors.Length > 0);
                Ok("every circuit has at least one tunable part", tunable);

                // The old panel had exactly three resistor boxes. Something here must
                // exceed that, or the variable-length input work was pointless.
                int most = presets.Max(p => p.Resistors.Length);
                Ok($"at least one circuit needs more than three inputs ({most})", most > 3);
            }

            // ── Series: drops sum to the supply, current is shared ────────────
            {
                var a = new Resistor("R1", 100);
                var b = new Resistor("R2", 220);
                var s = new SeriesGroup(a, b);
                s.ApplyVoltage(12.0);

                Ok($"series Rt adds ({s.Resistance():0.##})", Near(s.Resistance(), 320));
                Ok($"the same current flows in both ({a.Current * 1000:0.###} mA)",
                   Near(a.Current, b.Current));
                Ok($"the drops sum to the supply ({a.Voltage + b.Voltage:0.######})",
                   Near(a.Voltage + b.Voltage, 12.0));
                Ok($"and each drop is I x R ({a.Voltage:0.####})",
                   Near(a.Voltage, a.Current * 100) && Near(b.Voltage, b.Current * 220));
            }

            // ── Parallel: voltage is shared, currents sum, Rt is below both ───
            {
                var a = new Resistor("R1", 100);
                var b = new Resistor("R2", 100);
                var p = new ParallelGroup(a, b);
                p.ApplyVoltage(10.0);

                Ok($"two equal resistors in parallel halve ({p.Resistance():0.##})",
                   Near(p.Resistance(), 50));
                Ok("both see the full voltage",
                   Near(a.Voltage, 10.0) && Near(b.Voltage, 10.0));
                Ok($"the branch currents sum to the total ({p.Current:0.####} A)",
                   Near(a.Current + b.Current, p.Current));
            }

            // ── The divider, against the textbook formula ────────────────────
            {
                var top = new Resistor("RTOP", 26_000);
                var bot = new Resistor("RBOT", 10_000);
                new SeriesGroup(top, bot).ApplyVoltage(12.0);

                double expect = 12.0 * 10_000.0 / 36_000.0;
                Ok($"a 26k/10k divider on 12V gives {expect:0.###}V "
                 + $"(got {bot.Voltage:0.###})", Near(bot.Voltage, expect, 1e-9));
                Ok("...which is the ~3.3V the preset's instruction promises",
                   Math.Abs(bot.Voltage - 3.3) < 0.05);
            }

            // ── The current divider: the OTHER resistor's value on top ───────
            {
                var a = new Resistor("R1", 100);
                var b = new Resistor("R2", 300);
                var p = new ParallelGroup(a, b);
                p.ApplyVoltage(12.0);

                double share = a.Current / p.Current;
                Ok($"R1 takes R2/(R1+R2) = 0.75 of the current ({share:0.###})",
                   Near(share, 0.75, 1e-9));
            }

            // ── Wheatstone: check the PRESET, not just the formula ────────
            {
                // Built from the real preset. The previous version of this check assembled
                // its own pair of dividers, which verified the arithmetic but said nothing
                // about whether the shipped circuit is wired that way -- so it would have
                // passed with the bridge mis-built.
                var bridge = CircuitPresets.Build().First(x => x.Name.Contains("WHEATSTONE"));
                var r = bridge.Resistors;
                Ok($"the bridge preset has four arms ({r.Length})", r.Length == 4);

                // Balanced: R1/R2 == R3/RX.
                foreach (var x in r) x.R = 1000;
                bridge.Root.ApplyVoltage(12.0);
                double outBalanced = r[1].Voltage - r[3].Voltage;
                Ok($"balanced, the two mid-points agree ({outBalanced:0.######} V)",
                   Near(outBalanced, 0.0, 1e-9));

                // The preset's own default: RX high, so the bridge is off balance.
                r[3].R = 1200;
                bridge.Root.ApplyVoltage(12.0);
                double outOff = r[1].Voltage - r[3].Voltage;
                Ok($"with RX at 1200 it goes off balance ({outOff:0.####} V)",
                   Math.Abs(outOff) > 0.1);

                // And the balance condition is a RATIO, not equality: 2k/2k against
                // 1k/1k is still balanced. This is the part a naive check misses.
                r[0].R = 2000; r[1].R = 2000; r[2].R = 1000; r[3].R = 1000;
                bridge.Root.ApplyVoltage(12.0);
                Ok($"balance is a RATIO -- 2k/2k vs 1k/1k is balanced "
                 + $"({r[1].Voltage - r[3].Voltage:0.######} V)",
                   Near(r[1].Voltage - r[3].Voltage, 0.0, 1e-9));
            }

            // ── The diode: the whole reason it exists ────────────────────────
            {
                // 12V, 470 ohm, 2V LED. Ohm's law ALONE would say 12/470 = 25.5mA;
                // the right answer is (12-2)/470 = 21.3mA. Getting this wrong is exactly
                // the mistake the preset is there to prevent.
                var r = new Resistor("R1", 470);
                var d = new Diode("LED", 2.0);
                new SeriesGroup(r, d).ApplyVoltage(12.0);

                double expect = (12.0 - 2.0) / 470.0;
                Ok($"the LED current is (V-Vf)/R = {expect * 1000:0.##} mA "
                 + $"(got {r.Current * 1000:0.##} mA)", Near(r.Current, expect, 1e-9));
                Ok($"NOT the naive V/R = {12.0 / 470.0 * 1000:0.##} mA",
                   !Near(r.Current, 12.0 / 470.0, 1e-6));
                Ok($"the LED holds its forward drop ({d.Voltage:0.###} V)",
                   Near(d.Voltage, 2.0, 1e-9));
                Ok($"and the resistor takes the rest ({r.Voltage:0.###} V)",
                   Near(r.Voltage + d.Voltage, 12.0, 1e-9));

                // Below Vf: OFF, not "a small current". A trickle would teach the opposite.
                var r2 = new Resistor("R1", 470);
                var d2 = new Diode("LED", 2.0);
                new SeriesGroup(r2, d2).ApplyVoltage(1.5);
                Ok($"below Vf the LED does not conduct at all ({r2.Current:0.###e+0} A)",
                   r2.Current == 0.0);
                Ok("and nothing is dissipated", r2.Power == 0.0 && d2.Power == 0.0);

                // Exactly at Vf is the boundary: still off, since there is no headroom.
                var r3 = new Resistor("R1", 470);
                new SeriesGroup(r3, new Diode("LED", 2.0)).ApplyVoltage(2.0);
                Ok($"exactly at Vf there is no headroom, so no current ({r3.Current:0.###})",
                   r3.Current == 0.0);

                // The same resistor on a different supply gives a different current --
                // which is the point that separates a diode from a resistance.
                var r4 = new Resistor("R1", 470);
                new SeriesGroup(r4, new Diode("LED", 2.0)).ApplyVoltage(5.0);
                Ok($"the same resistor on 5V gives {r4.Current * 1000:0.##} mA, "
                 + $"not a scaled {expect * 5.0 / 12.0 * 1000:0.##}",
                   Near(r4.Current, 3.0 / 470.0, 1e-9));
            }

            // ── Ranges: real parts must be settable ──────────────────────────
            {
                Ok($"resistors span real parts ({Resistor.MinOhms} .. {Resistor.MaxOhms:0.##e+0})",
                   Resistor.MinOhms <= 0.1 && Resistor.MaxOhms >= 1e6);

                var big = new Resistor("R", 4.7e6);
                Ok($"a 4M7 resistor survives construction ({big.R:0.###e+0})",
                   Near(big.R, 4.7e6));

                var tiny = new Resistor("R", 0.1);
                Ok($"so does an 0.1 ohm shunt ({tiny.R})", Near(tiny.R, 0.1));

                // A zero or negative value must not produce a divide-by-zero downstream.
                var zero = new Resistor("R", 0.0);
                zero.ApplyVoltage(5.0);
                Ok($"a zero-ohm request is clamped, not divided by ({zero.Current:0.##} A)",
                   double.IsFinite(zero.Current) && zero.R >= Resistor.MinOhms);
            }

            // ── No preset produces a NaN at either end of the supply range ───
            {
                bool bad = false;
                string worst = "";
                foreach (double v in new[] { Supply.MinVolts, 1.0, 12.0,
                                             Supply.MaxVolts })
                foreach (var p in CircuitPresets.Build())
                {
                    p.Root.ApplyVoltage(v);
                    foreach (var r in p.Resistors)
                        if (!double.IsFinite(r.Voltage) || !double.IsFinite(r.Current)
                                                        || !double.IsFinite(r.Power))
                        { bad = true; worst = $"{p.Name} at {v}V"; }
                }
                Ok($"every circuit solves finitely across the whole supply range"
                 + (bad ? $" -- FAILED on {worst}" : ""), !bad);
            }

            // ── Every element in every preset can be LAID OUT ────────────────
            {
                // The layout walk is a switch over element types, and a type it does not
                // know about falls straight through -- emitting nothing. That is what
                // happened when the Diode was added: the LED preset drew a GAP at exactly
                // the component the lesson is about, and the circuit looked broken.
                //
                // CircuitScene itself needs the display stack, so what is checked here is
                // the property that made the bug possible: that every element type reachable
                // from a preset is one the walk has a case for. Kept as a list in the test
                // because the walk cannot be called from here -- if a new element type is
                // added and this list is not updated, this fails and says so.
                var known = new[] { typeof(Resistor), typeof(Diode),
                                    typeof(SeriesGroup), typeof(ParallelGroup) };

                static void Collect(CircuitElement e, System.Collections.Generic.List<Type> into)
                {
                    into.Add(e.GetType());
                    if (e is SeriesGroup s)   foreach (var c in s.Children) Collect(c, into);
                    if (e is ParallelGroup p) foreach (var c in p.Children) Collect(c, into);
                }

                var seen = new System.Collections.Generic.List<Type>();
                foreach (var p in CircuitPresets.Build()) Collect(p.Root, seen);

                var unknown = seen.Distinct().Where(t => !known.Contains(t)).ToArray();
                Ok($"every element type in every preset is one the layout can draw "
                 + $"({seen.Distinct().Count()} types"
                 + (unknown.Length > 0 ? ", UNHANDLED: " + string.Join(", ",
                        unknown.Select(t => t.Name)) : "") + ")",
                   unknown.Length == 0);

                // And the diode really is reachable -- otherwise the check above passes
                // trivially and proves nothing about the case that broke.
                Ok("a Diode is actually present in the preset set",
                   seen.Contains(typeof(Diode)));
            }

            return _failures;
        }
    }
}
