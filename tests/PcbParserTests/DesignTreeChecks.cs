// Design-folder-tree import checks: build a realistic nested design folder in temp
// (gerbers in a sub-folder, a drill file, a placement CSV, a BOM, a schematic PDF, a
// STEP model, plus a backup folder that must be ignored) and assert the importer
// picks up every part of it. This is the behaviour that matters when the input is a
// whole design tree rather than one flat fab-output folder.

using EDes.Pcb;

namespace PcbParserTests;

public static class DesignTreeChecks
{
    public static int Run()
    {
        int failures = 0;

        void Check(string what, double actual, double expected, double tol = 0.001)
        {
            bool ok = Math.Abs(actual - expected) <= tol;
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {actual:0.####}, expected {expected:0.####}");
        }

        void CheckTrue(string what, bool ok)
        {
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        }

        string root = Path.Combine(Path.GetTempPath(), "edes_designtree");
        if (Directory.Exists(root)) Directory.Delete(root, true);

        string gerberDir = Path.Combine(root, "02_PCB", "Gerbers");
        string backupDir = Path.Combine(gerberDir, "backup");
        string asmDir    = Path.Combine(root, "03_Assembly");
        string schDir    = Path.Combine(root, "01_Schematic");
        string cadDir    = Path.Combine(root, "04_3D");
        foreach (var d in new[] { gerberDir, backupDir, asmDir, schDir, cadDir })
            Directory.CreateDirectory(d);

        // ── Gerber: one copper layer, 20 mm track ────────────────────────────
        string gbr = Path.Combine(gerberDir, "board-F_Cu.gbr");
        File.WriteAllText(gbr, string.Join("\n", new[]
        {
            "%FSLAX46Y46*%", "%MOMM*%", "%ADD10C,0.200000*%", "D10*",
            "X0Y0D02*", "X20000000Y0D01*", "M02*",
        }));

        // Same file copied into a backup folder: must be skipped, not double-counted.
        File.Copy(gbr, Path.Combine(backupDir, "board-F_Cu.gbr"));

        // ── Drill ────────────────────────────────────────────────────────────
        File.WriteAllText(Path.Combine(gerberDir, "board.drl"), string.Join("\n", new[]
        {
            "M48", "METRIC,TZ", "T1C0.900", "%", "T1", "X5.0Y5.0", "X15.0Y5.0", "M30",
        }));

        // ── Placement (KiCad-style columns, mm) ──────────────────────────────
        File.WriteAllText(Path.Combine(asmDir, "board-pos.csv"), string.Join("\n", new[]
        {
            "Ref,Val,Package,PosX,PosY,Rot,Side",
            "R1,10k,R_0603,5.0,5.0,90,top",
            "C1,100nF,C_0603,8.5,5.0,0,top",
            "U1,,SOIC-8,12.0,7.5,180,bottom",
        }));

        // ── BOM (fills in U1's value, which the placement file left blank) ────
        File.WriteAllText(Path.Combine(asmDir, "board_BOM.csv"), string.Join("\n", new[]
        {
            "Designator,Value,Footprint,Qty",
            "\"R1\",10k,R_0603,1",
            "\"C1\",100nF,C_0603,1",
            "\"U1\",ATTINY85,SOIC-8,1",
        }));

        // ── A two-page schematic PDF (minimal, just enough page objects) ──────
        File.WriteAllText(Path.Combine(schDir, "board_schematic.pdf"),
            "%PDF-1.4\n1 0 obj<</Type /Pages /Count 2>>endobj\n" +
            "2 0 obj<</Type /Page /Parent 1 0 R>>endobj\n" +
            "3 0 obj<</Type /Page /Parent 1 0 R>>endobj\n%%EOF");

        // ── A STEP model (cannot be loaded; must still be inventoried) ────────
        File.WriteAllText(Path.Combine(cadDir, "board.step"), "ISO-10303-21;\nHEADER;\nENDSEC;\nEND-ISO-10303-21;");

        File.WriteAllText(Path.Combine(root, "README.md"), "Design notes.");

        // ── Import the whole tree ────────────────────────────────────────────
        var board = new PcbBoard();
        bool ok = PcbImporter.Import(root, board, 20_000);

        Console.WriteLine();
        Console.WriteLine("--- import notes:");
        foreach (var n in board.Notes) Console.WriteLine("    " + n);
        Console.WriteLine();

        CheckTrue("tree import succeeded", ok);
        Check("copper layers", board.Layers.Count, 1);
        Check("holes", board.Holes.Count, 2);
        Check("hole diameter (mm)", board.Holes[0].Dia, 0.9);
        Check("components placed", board.Components.Count, 3);
        Check("bom rows", board.BomLines.Count, 3);

        // Placement values and sides.
        var r1 = board.Components.Find(c => c.Designator == "R1");
        var u1 = board.Components.Find(c => c.Designator == "U1");
        Check("R1 X (mm)", r1.X, 5.0);
        Check("R1 rotation", r1.Rotation, 90);
        CheckTrue("R1 is top side", !r1.Bottom);
        CheckTrue("U1 is bottom side", u1.Bottom);
        CheckTrue("U1 value filled in from the BOM", u1.Value == "ATTINY85");

        // Documents: schematic (2 pages), the STEP model, the readme.
        int sch = 0, cad = 0, pages = 0;
        foreach (var d in board.Documents)
        {
            if (d.Kind == DocKind.Schematic) { sch++; pages += d.Pages; }
            if (d.Kind == DocKind.Cad3D) cad++;
        }
        Check("schematic documents", sch, 1);
        Check("schematic pages counted", pages, 2);
        Check("3D CAD documents", cad, 1);
        CheckTrue("a STEP conversion note was recorded",
                  board.Notes.Exists(n => n.Contains("STEP")));

        // The backup copy must not have produced a second layer.
        CheckTrue("duplicate in backup/ was skipped",
                  board.Notes.Exists(n => n.Contains("duplicate")) || board.Layers.Count == 1);
        CheckTrue("folders were walked", board.SourceFolders.Count >= 4);

        // Bounds must cover the track and the parts.
        board.ComputeBounds();
        CheckTrue("board has geometry", board.HasGeometry);
        Check("board width covers the 20 mm track", board.WidthMm, 20.2, 0.5);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "DESIGN TREE CHECKS PASSED"
                                        : $"{failures} DESIGN TREE CHECK(S) FAILED");
        return failures;
    }
}
