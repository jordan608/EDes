using EDes.Pcb;

int failures = 0;

void Check(string what, double actual, double expected, double tol = 0.001)
{
    bool ok = Math.Abs(actual - expected) <= tol;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {actual:0.####}, expected {expected:0.####}");
}

void CheckInt(string what, int actual, int expected)
{
    bool ok = actual == expected;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {actual}, expected {expected}");
}

string dir = Path.Combine(Path.GetTempPath(), "edes_pcbtest");
Directory.CreateDirectory(dir);

// ── Fixture 1: KiCad-style metric Gerber, 4.6 format ─────────────────────────
// A 10 mm horizontal track of 0.25 mm width, a 2x1 mm rect pad flash, a 90 deg
// arc, and a triangular filled region.
string gbr = Path.Combine(dir, "test-F_Cu.gbr");
File.WriteAllText(gbr, string.Join("\n", new[]
{
    "G04 test board*",
    "%FSLAX46Y46*%",
    "%MOMM*%",
    "%ADD10C,0.250000*%",
    "%ADD11R,2.000000X1.000000*%",
    "D10*",
    "X0Y0D02*",
    "X10000000Y0D01*",          // 10.0 mm to the right
    "D11*",
    "X5000000Y5000000D03*",     // pad flash at (5, 5)
    "D10*",
    "G02*",
    "X10000000Y0D02*",
    "X20000000Y10000000I10000000J0D01*",   // CW arc 180deg->90deg about (20,0) = 90deg sweep
    "G01*",
    "G36*",
    "X0Y20000000D02*",
    "X10000000Y20000000D01*",
    "X5000000Y25000000D01*",
    "X0Y20000000D01*",
    "G37*",
    "M02*",
}));

var board = new PcbBoard();
var layer = board.GetOrAddLayer("test-F_Cu.gbr", PcbLayerKind.CopperTop);
bool parsed = GerberParser.Parse(gbr, layer, board);
Console.WriteLine($"gerber parsed = {parsed}, segs={layer.Segs.Count}, pads={layer.Pads.Count}, regions={layer.Regions.Count}");

CheckInt("gerber pad count", layer.Pads.Count, 1);
Check("pad X (mm)", layer.Pads[0].X, 5.0);
Check("pad Y (mm)", layer.Pads[0].Y, 5.0);
Check("pad W (mm)", layer.Pads[0].W, 2.0);
Check("pad H (mm)", layer.Pads[0].H, 1.0);

// First segment must be the 10 mm track at 0.25 mm wide.
Check("track length (mm)", layer.Segs[0].Length, 10.0);
Check("track width (mm)", layer.Segs[0].W, 0.25);
Check("min width (mm)", layer.MinWidth, 0.25);

CheckInt("region count", layer.Regions.Count, 1);
CheckInt("region vertices", layer.Regions[0].Count, 4);

// The arc: from (10,0) to (20,10) about (20,0) is a quarter circle of r=10,
// so its flattened length must be about 10*pi/2 = 15.708 mm.
double arcLen = 0;
for (int i = 1; i < layer.Segs.Count; i++) arcLen += layer.Segs[i].Length;
// Subtract the region edges (regions are not segments) — only arc segments remain.
Check("arc flattened length (mm)", arcLen, 15.708, 0.05);   // r=10, quarter circle

board.ComputeBounds();
Check("board min X", board.MinX, -0.125, 0.01);       // half the track width
Check("board max X", board.MaxX, 20.125, 0.01);
Check("board max Y", board.MaxY, 25.0, 0.01);

// ── Fixture 2: metric Excellon with explicit decimals ────────────────────────
string drlM = Path.Combine(dir, "test-metric.drl");
File.WriteAllText(drlM, string.Join("\n", new[]
{
    "M48", "METRIC,TZ", "T1C0.800", "T2C1.200", "%",
    "T1", "X10.0Y5.0", "X12.5Y5.0",
    "T2", "X20.0Y15.0",
    "M30",
}));

var b2 = new PcbBoard();
int holes = ExcellonParser.Parse(drlM, b2);
CheckInt("metric drill count", holes, 3);
Check("metric hole1 X", b2.Holes[0].X, 10.0);
Check("metric hole1 dia", b2.Holes[0].Dia, 0.8);
Check("metric hole3 dia", b2.Holes[2].Dia, 1.2);

// ── Fixture 3: inch Excellon, implied 2.4 format, leading zeros suppressed ───
// X0100 -> 0.0100 inch -> 0.254 mm ; T1C0.0394 inch -> 1.0 mm
string drlI = Path.Combine(dir, "test-inch.drl");
File.WriteAllText(drlI, string.Join("\n", new[]
{
    "M48", "INCH,TZ", "T1C0.0394", "%",
    "T1", "X0100Y0200", "X10000Y10000",
    "M30",
}));

var b3 = new PcbBoard();
int holes3 = ExcellonParser.Parse(drlI, b3);
CheckInt("inch drill count", holes3, 2);
Check("inch hole1 X (mm)", b3.Holes[0].X, 0.254, 0.002);
Check("inch hole1 Y (mm)", b3.Holes[0].Y, 0.508, 0.002);
Check("inch hole2 X (mm)", b3.Holes[1].X, 25.4, 0.01);
Check("inch tool dia (mm)", b3.Holes[0].Dia, 1.0, 0.01);

// ── Fixture 4: slot (G85) ────────────────────────────────────────────────────
string drlS = Path.Combine(dir, "test-slot.drl");
File.WriteAllText(drlS, string.Join("\n", new[]
{
    "M48", "METRIC,TZ", "T1C1.000", "%",
    "T1", "X5.0Y5.0G85X9.0Y5.0",
    "M30",
}));
var b4 = new PcbBoard();
ExcellonParser.Parse(drlS, b4);
CheckInt("slot count", b4.Holes.Count, 1);
Console.WriteLine($"slot flag = {b4.Holes[0].Slot}");
if (!b4.Holes[0].Slot) failures++;
Check("slot end X", b4.Holes[0].X1, 9.0);

// ── Fixture 5: inch Gerber in legacy 2.4 format ──────────────────────────────
string gbrI = Path.Combine(dir, "legacy.gtl");
File.WriteAllText(gbrI, string.Join("\n", new[]
{
    "%FSLAX24Y24*%", "%MOIN*%", "%ADD10C,0.010*%", "D10*",
    "X0Y0D02*", "X10000Y0D01*",   // 1.0000 inch = 25.4 mm
    "M02*",
}));
var b5 = new PcbBoard();
var l5 = b5.GetOrAddLayer("legacy.gtl", PcbLayerKind.CopperTop);
GerberParser.Parse(gbrI, l5, b5);
CheckInt("legacy seg count", l5.Segs.Count, 1);
Check("legacy track length (mm)", l5.Segs[0].Length, 25.4, 0.01);
Check("legacy track width (mm)", l5.Segs[0].W, 0.254, 0.001);

// ── Analysis helpers ─────────────────────────────────────────────────────────
var table = b2.DrillTable();
CheckInt("drill table groups", table.Count, 2);
Check("largest drill first", table[0].Dia, 1.2);
CheckInt("0.8mm count", table[1].Count, 2);

Console.WriteLine();
Console.WriteLine("=== design folder tree ===");
failures += PcbParserTests.DesignTreeChecks.Run();

Console.WriteLine();
Console.WriteLine("=== real Altium project outputs ===");
failures += PcbParserTests.RealBoardCheck.Run();

Console.WriteLine();
Console.WriteLine("=== derived copper connectivity ===");
failures += PcbParserTests.NetChecks.Run();

Console.WriteLine();
Console.WriteLine("=== via layer spans ===");
failures += PcbParserTests.ViaSpanChecks.Run();

Console.WriteLine();
Console.WriteLine("=== STL / STEP tessellation ===");
failures += PcbParserTests.StlChecks.Run();

Console.WriteLine();
Console.WriteLine("=== Fusion bridge ===");
failures += PcbParserTests.FusionChecks.Run();

Console.WriteLine();
Console.WriteLine("=== schematic PDF ===");
failures += PcbParserTests.SchematicChecks.Run();

Console.WriteLine();
Console.WriteLine("=== teaching circuits ===");
failures += PcbParserTests.CircuitChecks.Run();

Console.WriteLine();
Console.WriteLine("=== CAD point light + STEP tessellation ===");
failures += PcbParserTests.LightChecks.Run();

Console.WriteLine();
Console.WriteLine("=== HUD text band placement ===");
failures += PcbParserTests.LayoutChecks.Run();

Console.WriteLine();
Console.WriteLine("=== STEP ===");
failures += PcbParserTests.StepChecks.Run();

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
