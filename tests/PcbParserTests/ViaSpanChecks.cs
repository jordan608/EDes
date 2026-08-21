// Via layer-span checks.
//
// A via barrel that spans the wrong layers is invisible in a screenshot but wrong
// on the board — it either draws a connection that does not exist or hides one
// that does. So the span rules get pinned down here rather than eyeballed in the
// volume.

using EDes.Pcb;

namespace PcbParserTests;

public static class ViaSpanChecks
{
    private static int _failures;

    private static void Ok(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    private static string Write(string name, string body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "edes_via_checks");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, body);
        return path;
    }

    private const string Drill = @"M48
METRIC,LZ
;TYPE=PLATED
T01F00S00C0.3000
%
T01
X0005000Y0005000
X0006000Y0005000
M30
";

    public static int Run()
    {
        _failures = 0;

        // ── Span resolution ──────────────────────────────────────────────────
        {
            // Unstated span on a 4-layer board is a through via, top to bottom. The
            // alternative — guessing a span — would hide real connections.
            var through = new PcbHole { Dia = 0.3f, Plated = true };
            PcbBoard.ViaSpan(through, 4, out int f, out int l);
            Ok($"unstated span is through ({f}-{l})", f == 1 && l == 4);
            Ok("and is not classified blind", !through.IsBlind(4));

            var blind = new PcbHole { Dia = 0.3f, Plated = true, SpanFrom = 1, SpanTo = 2 };
            PcbBoard.ViaSpan(blind, 4, out f, out l);
            Ok($"stated 1-2 span honoured ({f}-{l})", f == 1 && l == 2);
            Ok("1-2 on a 4-layer board is blind", blind.IsBlind(4));

            var buried = new PcbHole { Dia = 0.3f, Plated = true, SpanFrom = 2, SpanTo = 3 };
            Ok("2-3 on a 4-layer board is buried, so also not through", buried.IsBlind(4));

            var reversed = new PcbHole { Dia = 0.3f, Plated = true, SpanFrom = 4, SpanTo = 1 };
            PcbBoard.ViaSpan(reversed, 4, out f, out l);
            Ok($"a reversed pair sorts ({f}-{l})", f == 1 && l == 4);
            Ok("4-1 reaches both outers, so it is NOT blind", !reversed.IsBlind(4));

            // A drill file naming more layers than the Gerbers actually provided must
            // clamp, not index past the end of the copper stack.
            var overrun = new PcbHole { Dia = 0.3f, Plated = true, SpanFrom = 1, SpanTo = 6 };
            PcbBoard.ViaSpan(overrun, 2, out f, out l);
            Ok($"a span beyond the real stack clamps ({f}-{l})", f == 1 && l == 2);

            PcbBoard.ViaSpan(through, 1, out f, out l);
            Ok($"a single-copper board does not go out of range ({f}-{l})", f == 1 && l == 1);

            var full = new PcbHole { Dia = 0.3f, Plated = true, SpanFrom = 1, SpanTo = 2 };
            Ok("1-2 on a 2-layer board is through, not blind", !full.IsBlind(2));
        }

        // ── Span from the file name ──────────────────────────────────────────
        {
            var b = new PcbBoard();
            ExcellonParser.Parse(Write("Board - Drill (1-2).TXT", Drill), b);
            Ok($"layer pair read from an Altium blind-drill name " +
               $"({b.Holes[0].SpanFrom}-{b.Holes[0].SpanTo})",
               b.Holes.Count == 2 && b.Holes[0].SpanFrom == 1 && b.Holes[0].SpanTo == 2);

            var k = new PcbBoard();
            ExcellonParser.Parse(Write("board-2-3.drl", Drill), k);
            Ok($"layer pair read from a KiCad blind-drill name " +
               $"({k.Holes[0].SpanFrom}-{k.Holes[0].SpanTo})",
               k.Holes.Count == 2 && k.Holes[0].SpanFrom == 2 && k.Holes[0].SpanTo == 3);

            // A plain through-drill name must NOT acquire a span. The "V1.0" matters:
            // a digit-dot-digit run must not read as a layer pair.
            var t = new PcbBoard();
            ExcellonParser.Parse(Write("Widget_V1.0.TXT", Drill), t);
            Ok($"a plain drill name stays unstated ({t.Holes[0].SpanFrom}-{t.Holes[0].SpanTo})",
               t.Holes.Count == 2 && t.Holes[0].SpanFrom == 0 && t.Holes[0].SpanTo == 0);

            // The adversarial case claimed in the commit message but never actually
            // tested: a part number that CONTAINS a digit-dash-digit run. If this fails,
            // the conservative filename parser is not conservative enough and every
            // through via on such a board silently becomes blind.
            var adv = new PcbBoard();
            ExcellonParser.Parse(Write("RS485-2-4_V1.0.TXT", Drill), adv);
            Ok($"a part number containing a digit-dash-digit run is NOT a layer pair " +
               $"({adv.Holes[0].SpanFrom}-{adv.Holes[0].SpanTo})",
               adv.Holes[0].SpanFrom == 0 && adv.Holes[0].SpanTo == 0);

            // But a name that genuinely IS a layer pair must still be read, or the
            // conservatism above would have cost the feature.
            var pair = new PcbBoard();
            ExcellonParser.Parse(Write("Board2-4.TXT", Drill), pair);
            Ok($"a real layer pair is still read ({pair.Holes[0].SpanFrom}-{pair.Holes[0].SpanTo})",
               pair.Holes[0].SpanFrom == 2 && pair.Holes[0].SpanTo == 4);
        }

        // ── Span from a Gerber X2 attribute in the header ────────────────────
        {
            string withAttr = Drill.Replace("METRIC,LZ",
                "; #@! TF.FileFunction,Plated,1,3,PTH\nMETRIC,LZ");
            var b = new PcbBoard();
            // Deliberately a plain name, so only the attribute can supply the span.
            ExcellonParser.Parse(Write("attr_drill.TXT", withAttr), b);
            Ok($"TF.FileFunction span read from the header " +
               $"({b.Holes[0].SpanFrom}-{b.Holes[0].SpanTo})",
               b.Holes.Count == 2 && b.Holes[0].SpanFrom == 1 && b.Holes[0].SpanTo == 3);
        }

        // ── The copper stack a span is expressed against ─────────────────────
        {
            // The bug this guards: a 2-copper board can easily have a dozen visible
            // layers once silk, mask, paste and mechanical are counted. A via must span
            // the COPPER, so the copper stack must contain exactly the copper layers.
            var b = new PcbBoard();
            b.GetOrAddLayer("top.gto",  PcbLayerKind.Silkscreen);
            b.GetOrAddLayer("top.gts",  PcbLayerKind.SolderMask);
            b.GetOrAddLayer("top.gtl",  PcbLayerKind.CopperTop);
            b.GetOrAddLayer("in1.g1",   PcbLayerKind.CopperInner);
            b.GetOrAddLayer("bot.gbl",  PcbLayerKind.CopperBottom);
            b.GetOrAddLayer("bot.gbs",  PcbLayerKind.SolderMask);
            b.GetOrAddLayer("out.gko",  PcbLayerKind.Outline);

            var stack = b.CopperStack();
            Ok($"copper stack holds only copper ({stack.Count} of {b.Layers.Count} layers)",
               stack.Count == 3);
            Ok($"and the board reports {b.CopperLayerCount()} copper layers",
               b.CopperLayerCount() == 3);

            foreach (int li in stack)
                Ok($"    stack entry {li} is a copper layer ({b.Layers[li].Kind})",
                   b.Layers[li].Kind is PcbLayerKind.CopperTop
                                      or PcbLayerKind.CopperInner
                                      or PcbLayerKind.CopperBottom);
        }

        return _failures;
    }
}
