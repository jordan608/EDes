// ═══════════════════════════════════════════════════════════════════════════
//  SchematicChecks.cs — reading the schematic out of the PDF print
//
//  The risky part of this parser is not the arithmetic, it is what it chooses to
//  IGNORE. Two decisions carry the whole result:
//
//    • fills are discarded, because the biggest filled path on the page is the
//      sheet background and a filled region on a transparent display hides
//      everything behind it;
//    • subpath starts lift the pen, because without that every `m` inside a path
//      grows a spurious diagonal across the sheet.
//
//  Both are checked on hand-built streams where the right answer is known exactly,
//  then the whole thing is run against the real Altium print.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using EDes.Pcb;

namespace PcbParserTests
{
    internal static class SchematicChecks
    {
        private static int _failures;

        private static void Ok(string what, bool pass)
        {
            if (!pass) _failures++;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        /// <summary>Wrap a content stream in the minimum PDF the parser needs. It scans for
        /// stream/endstream rather than walking the xref, so no object graph is required --
        /// which is exactly what makes this test possible without a PDF writer.</summary>
        private static string WritePdf(string content, string dir, string name)
        {
            byte[] raw = Encoding.Latin1.GetBytes(content);
            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                // zlib header + deflate, which is what real PDFs carry.
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    ds.Write(raw, 0, raw.Length);
                deflated = ms.ToArray();
            }

            string path = Path.Combine(dir, name);
            using var fs = File.Create(path);
            void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }

            Ascii("%PDF-1.4\n1 0 obj\n<< /Length " + deflated.Length
                + " /Filter /FlateDecode >>\nstream\n");
            fs.Write(deflated, 0, deflated.Length);
            Ascii("\nendstream\nendobj\n%%EOF\n");
            return path;
        }

        private static string Dir()
        {
            string d = Path.Combine(Path.GetTempPath(), "edes_sch_checks");
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Enough strokes to clear the "this is a logo, not a sheet" threshold,
        /// so a test sheet is accepted for the reason a real one is.</summary>
        private static string Filler(int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
                sb.Append($"n {i} 500 m {i + 1} 501 l S\n");
            return sb.ToString();
        }

        public static int Run()
        {
            var notes = new List<string>();
            string dir = Dir();

            // ── A stroked line comes through with its coordinates ─────────────
            {
                string p = WritePdf(Filler(25) + "n 10 20 m 110 220 l S\n", dir, "line.pdf");
                var sheets = PdfSchematic.Load(p, notes);
                Ok($"a sheet was read ({sheets.Count})", sheets.Count == 1);

                var hit = sheets.Count > 0
                    ? sheets[0].Lines.Where(l => Math.Abs(l.X1 - 10) < 1e-3
                                              && Math.Abs(l.Y1 - 20) < 1e-3).ToList()
                    : new List<SchLine>();
                Ok($"the stroked line is present with its exact endpoints ({hit.Count})",
                   hit.Count == 1 && Math.Abs(hit[0].X2 - 110) < 1e-3
                                  && Math.Abs(hit[0].Y2 - 220) < 1e-3);
            }

            // ── FILLS ARE DROPPED. The load-bearing decision. ─────────────────
            {
                // A full-page background fill plus one real stroke. This is the actual shape
                // of the Altium output: the sheet is an `f*` rect covering everything.
                string p = WritePdf(
                    "n 0 0 m 0 451 l 688 451 l 688 0 l h f*\n" +
                    Filler(25) +
                    "n 300 300 m 400 300 l S\n", dir, "fill.pdf");

                var sheets = PdfSchematic.Load(p, notes);
                Ok("a page with a background fill still yields a sheet", sheets.Count == 1);

                if (sheets.Count > 0)
                {
                    // Nothing may reach across the whole page: that would be the background.
                    bool pageWide = sheets[0].Lines.Any(l => Math.Abs(l.X2 - l.X1) > 600f);
                    Ok("the full-page background fill was NOT drawn", !pageWide);

                    bool kept = sheets[0].Lines.Any(l => Math.Abs(l.X1 - 300) < 1e-3
                                                      && Math.Abs(l.X2 - 400) < 1e-3);
                    Ok("...while the real stroke beside it was kept", kept);
                }
            }

            // ── Pen lift. The other load-bearing decision. ────────────────────
            {
                // Two separate strokes in ONE path. If subpath starts are ignored, a third
                // segment appears joining the end of the first to the start of the second --
                // a diagonal straight across the drawing.
                string p = WritePdf(Filler(25) +
                    "n 0 0 m 10 0 l 100 100 m 110 100 l S\n", dir, "lift.pdf");

                var sheets = PdfSchematic.Load(p, notes);
                var mine = sheets[0].Lines.Where(l =>
                    (Math.Abs(l.X1 - 0) < 1e-3 && Math.Abs(l.Y1) < 1e-3) ||
                    (Math.Abs(l.X1 - 100) < 1e-3) ||
                    (Math.Abs(l.X1 - 10) < 1e-3 && Math.Abs(l.Y1) < 1e-3)).ToList();

                bool spurious = mine.Any(l => Math.Abs(l.X1 - 10) < 1e-3
                                           && Math.Abs(l.Y1) < 1e-3
                                           && Math.Abs(l.X2 - 100) < 1e-3);
                Ok($"a `m` inside a path lifts the pen -- no diagonal across the sheet "
                 + $"({mine.Count} segments from the pair)", !spurious);
                Ok("and both intended strokes survive",
                   mine.Any(l => Math.Abs(l.X2 - 10) < 1e-3) &&
                   mine.Any(l => Math.Abs(l.X2 - 110) < 1e-3));
            }

            // ── The CTM is honoured, and unwound by Q ─────────────────────────
            {
                string p = WritePdf(Filler(25) +
                    "q 2 0 0 2 100 100 cm n 0 0 m 10 0 l S Q n 0 0 m 5 0 l S\n",
                    dir, "ctm.pdf");
                var lines = PdfSchematic.Load(p, notes)[0].Lines;

                bool scaled = lines.Any(l => Math.Abs(l.X1 - 100) < 1e-3
                                          && Math.Abs(l.Y1 - 100) < 1e-3
                                          && Math.Abs(l.X2 - 120) < 1e-3);
                Ok("a `cm` scales and translates the path", scaled);

                bool restored = lines.Any(l => Math.Abs(l.X1) < 1e-3
                                            && Math.Abs(l.X2 - 5) < 1e-3);
                Ok("and `Q` puts the matrix back", restored);
            }

            // ── Text, with position and size ──────────────────────────────────
            {
                string p = WritePdf(Filler(25) +
                    "BT 5.3137 0 0 5.3137 84 6.4 Tm /TT1 1 Tf (R1) Tj ET\n" +
                    "BT 10 0 0 10 200 300 Tm /TT1 1 Tf [(NE)-20(T1)] TJ ET\n",
                    dir, "text.pdf");
                var sheet = PdfSchematic.Load(p, notes)[0];

                var r1 = sheet.Texts.FirstOrDefault(t => t.Text == "R1");
                Ok($"a Tj string is read at its matrix position "
                 + $"({r1.X:0.#},{r1.Y:0.#} size {r1.Size:0.##})",
                   r1.Text == "R1" && Math.Abs(r1.X - 84) < 1e-2
                                   && Math.Abs(r1.Size - 5.3137f) < 1e-2);

                // TJ arrays are how most writers emit text; dropping the kerning numbers
                // must not drop the characters around them.
                var net = sheet.Texts.FirstOrDefault(t => t.Text == "NET1");
                Ok($"a TJ array joins its pieces and discards the kerning ('{net.Text}')",
                   net.Text == "NET1");
            }

            // ── Escapes, because designators really contain parentheses ───────
            {
                string p = WritePdf(Filler(25) +
                    @"BT 5 0 0 5 10 10 Tm /TT1 1 Tf (R1 \(A\)) Tj ET" + "\n",
                    dir, "esc.pdf");
                var texts = PdfSchematic.Load(p, notes)[0].Texts;
                Ok($"escaped parens survive ('{texts.FirstOrDefault().Text}')",
                   texts.Any(t => t.Text == "R1 (A)"));
            }

            // ── Junk must not throw ──────────────────────────────────────────
            {
                string p = Path.Combine(dir, "junk.pdf");
                File.WriteAllText(p, "%PDF-1.4\nthis is not a pdf\nstream\ngarbage\nendstream\n");
                var before = notes.Count;
                var sheets = PdfSchematic.Load(p, notes);
                Ok($"junk reports rather than throwing ({sheets.Count} sheets, "
                 + $"{notes.Count - before} note(s))",
                   sheets.Count == 0 && notes.Count > before);

                var missing = PdfSchematic.Load(Path.Combine(dir, "nope.pdf"), notes);
                Ok("a missing file reports rather than throwing", missing.Count == 0);
            }

            // ── Which PDFs are schematics ─────────────────────────────────────
            {
                Ok("a Schematic Prints path is recognised",
                   PdfSchematic.LooksLikeSchematic(
                       @"C:\out\Schematic Prints\Board.PDF"));
                // The same folder holds the BOM, the assembly drawing and the DRC report.
                // Importing those as drawings would fill the volume with a table.
                Ok("the BOM is not",
                   !PdfSchematic.LooksLikeSchematic(@"C:\out\Bill of Materials\Board.PDF"));
                Ok("nor the assembly drawing",
                   !PdfSchematic.LooksLikeSchematic(@"C:\out\Assembly Drawings\Board.PDF"));
                Ok("nor a STEP file",
                   !PdfSchematic.LooksLikeSchematic(@"C:\out\ExportSTEP\Board.step"));
            }

            // ── The real thing ────────────────────────────────────────────────
            Console.WriteLine();
            RunReal();

            return _failures;
        }

        private static void RunReal()
        {
            string? root = TestData.BoardFolder;
            if (root == null)
            {
                Console.WriteLine($"SKIP  real schematic — {TestData.SkipReason}");
                return;
            }

            string? pdf = null;
            try
            {
                pdf = Directory.GetFiles(root, "*.pdf", SearchOption.AllDirectories)
                               .FirstOrDefault(PdfSchematic.LooksLikeSchematic);
            }
            catch { }

            if (pdf == null)
            {
                Console.WriteLine("SKIP  real schematic — no schematic print in the fixture");
                return;
            }

            var notes = new List<string>();
            var sheets = PdfSchematic.Load(pdf, notes);
            foreach (string n in notes) Console.WriteLine($"      note: {n}");

            Ok($"the real schematic print yields sheets ({sheets.Count})", sheets.Count >= 1);
            if (sheets.Count == 0) return;

            foreach (var s in sheets)
                Console.WriteLine($"      {s.Name}: {s.Lines.Count} lines, "
                                + $"{s.Texts.Count} strings, "
                                + $"{s.WidthPt:0} x {s.HeightPt:0} pt");

            var sheet = sheets.OrderByDescending(x => x.Lines.Count).First();

            Ok($"it has real drawing content ({sheet.Lines.Count} lines)",
               sheet.Lines.Count > 100);
            Ok($"and real text ({sheet.Texts.Count} strings)", sheet.Texts.Count > 50);

            // An A-size sheet in points: A4 landscape is 842x595, A3 1191x842. Anything
            // wildly outside that means the coordinates are being mangled.
            Ok($"the sheet is a plausible paper size ({sheet.WidthPt:0} x {sheet.HeightPt:0} pt)",
               sheet.WidthPt > 200 && sheet.WidthPt < 2000 &&
               sheet.HeightPt > 150 && sheet.HeightPt < 2000);

            // No segment may span the whole sheet diagonally -- that is the signature of
            // the pen-lift bug, and on a real drawing it is unmistakable.
            float diag = MathF.Sqrt(sheet.WidthPt * sheet.WidthPt
                                  + sheet.HeightPt * sheet.HeightPt);
            int longOnes = sheet.Lines.Count(l =>
            {
                float dx = l.X2 - l.X1, dy = l.Y2 - l.Y1;
                return MathF.Sqrt(dx * dx + dy * dy) > diag * 0.9f;
            });
            Ok($"no segment runs the full diagonal of the sheet ({longOnes})", longOnes == 0);

            // Designators are the proof the text is not just border numbering.
            var strings = sheet.Texts.Select(t => t.Text.Trim()).ToList();
            bool designators = strings.Any(t => t.Length is >= 2 and <= 5
                                             && char.IsLetter(t[0]) && char.IsDigit(t[^1]));
            Console.WriteLine("      sample: "
                + string.Join(" | ", strings.Where(t => t.Length > 0).Take(12)));
            Ok("recognisable component designators are among the text", designators);

            // Every coordinate finite: one NaN here propagates into every drawn voxel.
            bool finite = sheet.Lines.All(l => float.IsFinite(l.X1) && float.IsFinite(l.Y1)
                                            && float.IsFinite(l.X2) && float.IsFinite(l.Y2));
            Ok("every coordinate is finite", finite);
        }
    }
}
