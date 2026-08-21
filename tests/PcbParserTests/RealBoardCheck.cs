// Import of a REAL Altium "Project Outputs" folder, if one is present on this
// machine. Skipped (not failed) when the folder is absent, so the suite still
// runs anywhere — but when it is there, this is the only check that exercises
// genuine fab output rather than hand-written fixtures.

using EDes.Pcb;

namespace PcbParserTests;

public static class RealBoardCheck
{
    private const string Root =
        @"C:\Users\VoxelUser\Downloads\Project Outputs for VLED_IRSensor_V1.0-20260821T004235Z-1-001\Project Outputs for VLED_IRSensor_V1.0";

    public static int Run()
    {
        if (!Directory.Exists(Root))
        {
            Console.WriteLine("SKIP  real board folder not present on this machine");
            return 0;
        }

        int failures = 0;
        void CheckTrue(string what, bool ok)
        {
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        }

        var board = new PcbBoard();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var seenProgress = new List<string>();
        bool ok = PcbImporter.Import(Root, board, 60_000, f => seenProgress.Add(f));
        sw.Stop();

        // The per-file inventory — printed in full, because its whole purpose is to be
        // read when something is missing.
        Console.WriteLine();
        Console.WriteLine("--- import inventory ---");
        foreach (var f in board.ImportLog)
            Console.WriteLine($"  {(f.Used ? "USE" : "---")} {f.Role,-10} " +
                              $"{(f.Folder.Length > 0 ? f.Folder + "/" : "")}{f.Name}" +
                              $"{(f.Ms >= 20 ? $"  [{f.Ms} ms]" : "")}" +
                              $"{(f.Detail.Length > 0 ? "  :: " + f.Detail : "")}");
        Console.WriteLine($"  ({board.ImportLog.Count} files, {board.ImportMs} ms total)");
        Console.WriteLine();

        Console.WriteLine($"import: {ok} in {sw.ElapsedMilliseconds} ms");
        foreach (var n in board.Notes) Console.WriteLine("    note: " + n);

        Console.WriteLine($"board: {board.WidthMm:0.00} x {board.HeightMm:0.00} mm  " +
                          $"(x {board.MinX:0.00}..{board.MaxX:0.00}, y {board.MinY:0.00}..{board.MaxY:0.00})");
        Console.WriteLine($"layers: {board.Layers.Count}, copper {board.CopperLayerCount()}, " +
                          $"holes {board.Holes.Count}, parts {board.Components.Count}, " +
                          $"bom {board.BomLines.Count}, docs {board.Documents.Count}");
        Console.WriteLine($"min track {board.MinTrackWidth():0.000} mm, min drill {board.MinDrill():0.000} mm");

        foreach (var l in board.Layers)
            Console.WriteLine($"    {l.Kind,-13} vis={l.Visible,-5} {l.Name,-34} " +
                              $"segs {l.Segs.Count,5} pads {l.Pads.Count,4} pours {l.Regions.Count,3}");
        foreach (var g in board.DrillTable())
            Console.WriteLine($"    drill {g.Dia:0.000} mm x {g.Count}");
        foreach (var c in board.Components)
            Console.WriteLine($"    part {c.Designator,-5} {c.Value,-10} {c.Footprint,-20} " +
                              $"({c.X:0.000}, {c.Y:0.000}) rot {c.Rotation,3} {(c.Bottom ? "bottom" : "top")}");
        foreach (var d in board.Documents)
            Console.WriteLine($"    doc {d.Kind,-10} {d.Name}" + (d.Pages > 0 ? $" ({d.Pages}p)" : ""));
        Console.WriteLine($"    drc: parsed={board.Drc.Parsed} rules={board.Drc.Rules} " +
                          $"violations={board.Drc.Violations}");

        Console.WriteLine();
        CheckTrue("import succeeded", ok);
        CheckTrue("found both copper layers", board.CopperLayerCount() >= 2);
        CheckTrue("found drills", board.Holes.Count > 0);

        // The drill file is ";FILE_FORMAT=4:4" metric: hole coordinates must land in
        // millimetres on a board of this size, not 10x small or 10x large.
        CheckTrue("drill coordinates are plausible mm (1..100)",
                  board.Holes.TrueForAll(h => Math.Abs(h.X) < 100 && Math.Abs(h.Y) < 100));
        CheckTrue("smallest drill is a sane 0.3 mm", Math.Abs(board.MinDrill() - 0.3f) < 0.01f);

        // Pick-and-place is in mil: 326.142 mil = 8.284 mm. A mm misreading would put
        // parts hundreds of mm out and blow the board bounds up with it.
        CheckTrue("placement parsed", board.Components.Count >= 5);
        var r2 = board.Components.Find(c => c.Designator == "R2");
        CheckTrue("R2 X converted from mil to mm (~8.28)", Math.Abs(r2.X - 8.284f) < 0.02f);
        CheckTrue("R2 value came through", r2.Value == "4.7K");
        CheckTrue("no duplicate designators from the .csv/.txt pair",
                  board.Components.Count ==
                  new HashSet<string>(board.Components.ConvertAll(c => c.Designator)).Count);

        // A board this size must be tens of mm, not hundreds or fractions.
        CheckTrue("board width is tens of mm", board.WidthMm is > 5 and < 200);
        CheckTrue("board height is tens of mm", board.HeightMm is > 5 and < 200);

        // ── The inventory itself ─────────────────────────────────────────────
        CheckTrue("every file examined got an inventory entry",
                  board.ImportLog.Count > 20);
        CheckTrue("progress was reported per file", seenProgress.Count > 10);
        CheckTrue("the STEP file appears in the inventory as STEP",
                  board.ImportLog.Exists(f => f.Role == "STEP" && f.Used &&
                                              f.Name.EndsWith(".step", StringComparison.OrdinalIgnoreCase)));
        CheckTrue("the drill file appears as a drill with a hole count",
                  board.ImportLog.Exists(f => f.Role == "drill" && f.Used));
        CheckTrue("gerbers appear with their layer kind",
                  board.ImportLog.FindAll(f => f.Role == "gerber" && f.Used).Count >= 10);
        CheckTrue("nothing is left with an empty role",
                  board.ImportLog.TrueForAll(f => f.Role.Length > 0));
        CheckTrue("import time was recorded", board.ImportMs > 0);

        CheckTrue("xlsx BOM was read", board.BomLines.Count > 0);
        CheckTrue("DRC report parsed", board.Drc.Parsed && board.Drc.Rules > 5);
        CheckTrue("STEP still catalogued as a document",
                  board.Documents.Exists(d => d.Kind == DocKind.Cad3D));

        // ── STEP, end to end through the folder importer ─────────────────────
        // The parser has its own unit tests; what this adds is the designator link,
        // which can only be checked once the placement file has been read too.
        {
            int linked = 0, points = 0;
            double lengthMm = 0;
            foreach (var solid in board.Solids)
            {
                if (solid.Designator.Length > 0) linked++;
                points += solid.PointCount;
                foreach (var e in solid.Edges)
                    for (int i = 1; i < e.Count; i++)
                    {
                        double dx = e.X[i] - e.X[i - 1];
                        double dy = e.Y[i] - e.Y[i - 1];
                        double dz = e.Z[i] - e.Z[i - 1];
                        lengthMm += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    }
            }

            Console.WriteLine($"STEP: {board.Solids.Count} solid(s), {linked} linked, " +
                              $"{points} edge point(s), {lengthMm:0.0} mm of edge");

            // Voxel cost is what decides whether this is usable at all: total edge
            // length, scaled into the volume, divided by the voxel spacing.
            double scale = 4.0 * 0.88 / (0.5 * Math.Sqrt(
                board.WidthMm * board.WidthMm + board.HeightMm * board.HeightMm));
            double voxels = lengthMm * scale / 0.03;
            Console.WriteLine($"      ~{voxels:N0} voxels at the default budget of 150,000");

            CheckTrue("STEP solids were imported", board.Solids.Count > 10);
            CheckTrue("solids carry edges", points > 500);
            CheckTrue("most solids matched a designator",
                      linked >= board.Solids.Count / 2);
            CheckTrue("the wireframe fits the voxel budget with room to spare",
                      voxels > 0 && voxels < 100_000);

            var designators = new SortedSet<string>();
            foreach (var solid in board.Solids)
                if (solid.Designator.Length > 0) designators.Add(solid.Designator);
            Console.WriteLine($"      linked designators: {string.Join(", ", designators)}");
            CheckTrue("linked designators exist in the placement data",
                      designators.Count > 0 &&
                      board.Components.TrueForAll(_ => true) &&
                      designators.All(d => board.Components.Exists(
                          c => string.Equals(c.Designator, d, StringComparison.OrdinalIgnoreCase))));
        }
        CheckTrue("schematic PDF catalogued",
                  board.Documents.Exists(d => d.Kind == DocKind.Schematic));
        CheckTrue("aperture library was NOT parsed as a layer",
                  !board.Layers.Exists(l => l.Name.EndsWith(".apr", StringComparison.OrdinalIgnoreCase) ||
                                            l.Name.EndsWith(".APR_LIB", StringComparison.OrdinalIgnoreCase)));
        CheckTrue("mechanical drawing layers default to hidden",
                  board.Layers.TrueForAll(l => l.Kind != PcbLayerKind.Mechanical || !l.Visible));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "REAL BOARD CHECKS PASSED" : $"{failures} REAL BOARD CHECK(S) FAILED");
        return failures;
    }
}
