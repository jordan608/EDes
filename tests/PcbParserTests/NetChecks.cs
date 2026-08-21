// Derived-connectivity checks.
//
// Gerber carries no nets, so PcbNets works them out from geometry. That makes it
// the kind of code that is confidently wrong rather than obviously broken: an
// over-eager join merges half the board into one net and still looks like a
// result. So the topology is built by hand here, where the right answer is known.

using EDes.Pcb;

namespace PcbParserTests;

public static class NetChecks
{
    private static int _failures;

    private static void Ok(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    private static PcbLayer Copper(PcbBoard b, string name, PcbLayerKind kind)
        => b.GetOrAddLayer(name, kind);

    private static void Seg(PcbLayer l, float x0, float y0, float x1, float y1, float w = 0.25f)
        => l.Segs.Add(new PcbSeg(x0, y0, x1, y1, w));

    public static int Run()
    {
        _failures = 0;

        // ── Touching endpoints join; a gap does not ───────────────────────────
        {
            var b = new PcbBoard();
            var top = Copper(b, "top.gtl", PcbLayerKind.CopperTop);
            Seg(top, 0, 0, 10, 0);          // A
            Seg(top, 10, 0, 10, 10);        // B — shares an endpoint with A
            Seg(top, 20, 20, 30, 20);       // C — nowhere near either

            var n = PcbNets.Build(b);
            Ok($"a shared endpoint joins two segments ({n.SegNet(0, 0)} == {n.SegNet(0, 1)})",
               n.SegNet(0, 0) == n.SegNet(0, 1));
            Ok($"a distant segment stays on its own net ({n.SegNet(0, 2)})",
               n.SegNet(0, 2) != n.SegNet(0, 0));
            Ok($"two nets in total ({n.NetCount})", n.NetCount == 2);
        }

        // ── Crossing without touching must NOT join ──────────────────────────
        {
            // This is the join that would merge the whole board if it were allowed: two
            // traces crossing at right angles, sharing no endpoint. On a real board that
            // is two different nets on two different layers.
            var b = new PcbBoard();
            var top = Copper(b, "top.gtl", PcbLayerKind.CopperTop);
            Seg(top, -10, 0, 10, 0);
            Seg(top, 0, -10, 0, 10);

            var n = PcbNets.Build(b);
            Ok($"a mid-span crossing does NOT join ({n.NetCount} nets)",
               n.NetCount == 2 && n.SegNet(0, 0) != n.SegNet(0, 1));
        }

        // ── A plated via joins across layers ─────────────────────────────────
        {
            var b = new PcbBoard();
            var top = Copper(b, "top.gtl", PcbLayerKind.CopperTop);
            var bot = Copper(b, "bot.gbl", PcbLayerKind.CopperBottom);
            Seg(top, 0, 0, 5, 0);           // ends at (5,0)
            Seg(bot, 5, 0, 5, 5);           // starts at (5,0), other layer

            // Without the via these are two nets even though they share a coordinate:
            // different layers are not connected by geometry alone.
            var noVia = PcbNets.Build(b);
            Ok($"different layers are NOT joined without a via ({noVia.NetCount} nets)",
               noVia.NetCount == 2);

            b.Holes.Add(new PcbHole { X = 5, Y = 0, Dia = 0.3f, Plated = true });
            var withVia = PcbNets.Build(b);
            Ok($"a PLATED via joins them into one net ({withVia.NetCount} net)",
               withVia.NetCount == 1 &&
               withVia.SegNet(0, 0) == withVia.SegNet(1, 0));
        }

        // ── An unplated hole joins nothing ───────────────────────────────────
        {
            var b = new PcbBoard();
            var top = Copper(b, "top.gtl", PcbLayerKind.CopperTop);
            var bot = Copper(b, "bot.gbl", PcbLayerKind.CopperBottom);
            Seg(top, 0, 0, 5, 0);
            Seg(bot, 5, 0, 5, 5);
            b.Holes.Add(new PcbHole { X = 5, Y = 0, Dia = 3.2f, Plated = false });

            var n = PcbNets.Build(b);
            Ok($"an UNPLATED hole conducts nothing, so no join ({n.NetCount} nets)",
               n.NetCount == 2);
        }

        // ── A pad joins the traces that land on it ───────────────────────────
        {
            var b = new PcbBoard();
            var top = Copper(b, "top.gtl", PcbLayerKind.CopperTop);
            top.Pads.Add(new PcbPad(0, 0, 1.0f, 1.0f, PadShape.Rect));
            Seg(top, 0.3f, 0.3f, 8, 8);     // endpoint INSIDE the pad
            Seg(top, 20, 20, 25, 25);       // unrelated

            var n = PcbNets.Build(b);
            Ok("a trace ending inside a pad joins it",
               n.PadNet(0, 0) == n.SegNet(0, 0));
            Ok("and does not drag in an unrelated trace",
               n.SegNet(0, 1) != n.SegNet(0, 0));
        }

        // ── A chain joins end to end, not just neighbours ─────────────────────
        {
            var b = new PcbBoard();
            var top = Copper(b, "top.gtl", PcbLayerKind.CopperTop);
            for (int i = 0; i < 40; i++) Seg(top, i, 0, i + 1, 0);

            var n = PcbNets.Build(b);
            bool allOne = true;
            for (int i = 1; i < 40; i++)
                if (n.SegNet(0, i) != n.SegNet(0, 0)) { allOne = false; break; }
            Ok($"a 40-segment chain is ONE net, not 40 ({n.NetCount})",
               allOne && n.NetCount == 1);
            Ok($"and its size is reported as 40 ({n.Size(n.SegNet(0, 0))})",
               n.Size(n.SegNet(0, 0)) == 40);
        }

        // ── Grid-boundary robustness ─────────────────────────────────────────
        {
            // Endpoints that meet exactly ON a spatial-hash cell boundary must still join.
            // Without the neighbourhood sweep this would depend on where the grid fell —
            // nets would split at arbitrary coordinates, which is the worst kind of bug
            // because it is invisible until someone traces a specific net.
            var b = new PcbBoard();
            var top = Copper(b, "top.gtl", PcbLayerKind.CopperTop);
            float onBoundary = PcbNets.TOL_MM * 100f;   // an exact multiple of the cell
            Seg(top, onBoundary - 1f, 0, onBoundary, 0);
            Seg(top, onBoundary, 0, onBoundary + 1f, 0);

            var n = PcbNets.Build(b);
            Ok($"endpoints meeting on a hash cell boundary still join ({n.NetCount})",
               n.NetCount == 1);
        }

        // ── Real net names win over derived ones ─────────────────────────────
        {
            var b = new PcbBoard();
            var top = Copper(b, "top.gtl", PcbLayerKind.CopperTop);
            Seg(top, 0, 0, 5, 0);
            Seg(top, 5, 0, 10, 0);
            top.SetSegNetName(1, "GND");

            var n = PcbNets.Build(b);
            Ok($"a name from the Gerber is used for the whole net ({n.Name(n.SegNet(0, 0))})",
               n.Name(n.SegNet(0, 0)) == "GND");

            var b2 = new PcbBoard();
            var t2 = Copper(b2, "top.gtl", PcbLayerKind.CopperTop);
            Seg(t2, 0, 0, 5, 0);
            var n2 = PcbNets.Build(b2);
            Ok($"without one, the name admits it is derived ({n2.Name(0)})",
               n2.Name(0).Contains("derived"));
        }

        // ── Non-copper layers are not part of any net ─────────────────────────
        {
            var b = new PcbBoard();
            var silk = Copper(b, "top.gto", PcbLayerKind.Silkscreen);
            Seg(silk, 0, 0, 5, 0);
            Seg(silk, 5, 0, 10, 0);

            var n = PcbNets.Build(b);
            Ok($"silkscreen is not copper, so it forms no nets ({n.NetCount})",
               n.NetCount == 0 && n.SegNet(0, 0) == -1);
        }

        // ── An empty board must not throw ────────────────────────────────────
        {
            var n = PcbNets.Build(new PcbBoard());
            Ok("an empty board yields no nets and does not throw", n.NetCount == 0);
        }

        return _failures;
    }
}
