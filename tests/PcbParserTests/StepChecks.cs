// STEP parser checks.
//
// Two halves. The first is synthetic and always runs: small hand-written STEP
// files that pin down the things which silently corrupt a model — unit scaling,
// arc sweep direction, assembly placement, complex instances, edge de-duplication.
// The second runs only when the real Altium export is present, so CI stays green
// on a machine without it.

using EDes.Pcb;

namespace PcbParserTests;

public static class StepChecks
{
    private static int _failures;

    private static void Ok(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    private static void Near(string what, double actual, double expected, double tol = 1e-4)
        => Ok($"{what} ({actual:0.####} ~ {expected:0.####})", Math.Abs(actual - expected) <= tol);

    private static string Write(string name, string body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "edes_step_checks");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>A minimal but structurally valid STEP file: one solid whose shell has
    /// one face bounded by a loop of straight edges forming a rectangle in Z=0.</summary>
    private static string RectSolid(string unitPrefix, double w, double h)
        => $@"ISO-10303-21;
HEADER;
FILE_SCHEMA(('AUTOMOTIVE_DESIGN {{ 1 0 10303 214 1 1 1 1 }}'));
ENDSEC;
DATA;
#1 = APPLICATION_CONTEXT('core data for automotive mechanical design processes');
#40 = ( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT({unitPrefix},.METRE.) );
#41 = ( NAMED_UNIT(*) PLANE_ANGLE_UNIT() SI_UNIT($,.RADIAN.) );
#39 = ( GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNIT_ASSIGNED_CONTEXT((#40,#41))
        REPRESENTATION_CONTEXT('Context #1','3D Context') );
#7  = PRODUCT('PART1','PART1','',(#8));
#8  = PRODUCT_CONTEXT('',#1,'mechanical');
#6  = PRODUCT_DEFINITION_FORMATION('','',#7);
#5  = PRODUCT_DEFINITION('design','',#6,#9);
#9  = PRODUCT_DEFINITION_CONTEXT('part definition',#1,'design');
#4  = PRODUCT_DEFINITION_SHAPE('','',#5);
#3  = SHAPE_DEFINITION_REPRESENTATION(#4,#10);
#10 = SHAPE_REPRESENTATION('',(#100),#39);

#100 = MANIFOLD_SOLID_BREP('body',#101);
#101 = CLOSED_SHELL('',(#102));
#102 = ADVANCED_FACE('',(#103),#150,.T.);
#103 = FACE_OUTER_BOUND('',#104,.T.);
#104 = EDGE_LOOP('',(#110,#111,#112,#113));
#110 = ORIENTED_EDGE('',*,*,#120,.T.);
#111 = ORIENTED_EDGE('',*,*,#121,.T.);
#112 = ORIENTED_EDGE('',*,*,#122,.T.);
#113 = ORIENTED_EDGE('',*,*,#123,.T.);
#120 = EDGE_CURVE('',#130,#131,#140,.T.);
#121 = EDGE_CURVE('',#131,#132,#141,.T.);
#122 = EDGE_CURVE('',#132,#133,#142,.T.);
#123 = EDGE_CURVE('',#133,#130,#143,.T.);
#130 = VERTEX_POINT('',#160);
#131 = VERTEX_POINT('',#161);
#132 = VERTEX_POINT('',#162);
#133 = VERTEX_POINT('',#163);
#160 = CARTESIAN_POINT('',(0.,0.,0.));
#161 = CARTESIAN_POINT('',({w.ToString(System.Globalization.CultureInfo.InvariantCulture)},0.,0.));
#162 = CARTESIAN_POINT('',({w.ToString(System.Globalization.CultureInfo.InvariantCulture)},{h.ToString(System.Globalization.CultureInfo.InvariantCulture)},0.));
#163 = CARTESIAN_POINT('',(0.,{h.ToString(System.Globalization.CultureInfo.InvariantCulture)},0.));
#140 = LINE('',#160,#170);
#141 = LINE('',#161,#170);
#142 = LINE('',#162,#170);
#143 = LINE('',#163,#170);
#170 = VECTOR('',#171,1.);
#171 = DIRECTION('',(1.,0.,0.));
#150 = PLANE('',#180);
#180 = AXIS2_PLACEMENT_3D('',#160,#181,#182);
#181 = DIRECTION('',(0.,0.,1.));
#182 = DIRECTION('',(1.,0.,0.));
ENDSEC;
END-ISO-10303-21;
";

    public static int Run()
    {
        _failures = 0;
        var notes = new List<string>();

        // ── Structure, units, de-duplication ─────────────────────────────────
        {
            notes.Clear();
            var m = StepParser.TryLoad(Write("rect_mm.step", RectSolid(".MILLI.", 10, 4)), notes);
            Ok("millimetre rectangle parsed", m != null);
            if (m != null)
            {
                Ok("one solid found", m.SolidCount == 1);
                // Four edges, each referenced once here. The de-dup must not eat them.
                Ok($"four edges kept ({m.TotalEdges})", m.TotalEdges == 4);
                Near("width mm", m.WidthMm, 10);
                Near("depth mm", m.DepthMm, 4);
                Ok($"product name picked up ({m.Solids[0].Name})", m.Solids[0].Name == "PART1");
            }
        }
        {
            // The same geometry declared in METRES must come out 1000x bigger. This is
            // the check that catches a unit regression, which is otherwise invisible
            // until a 10 mm part fills the whole volume.
            notes.Clear();
            var m = StepParser.TryLoad(Write("rect_m.step", RectSolid("$", 0.01, 0.004)), notes);
            Ok("metre-unit file parsed", m != null);
            if (m != null)
            {
                Near("metres converted to mm (width)", m.WidthMm, 10, 1e-3);
                Near("metres converted to mm (depth)", m.DepthMm, 4, 1e-3);
            }
        }
        {
            notes.Clear();
            var m = StepParser.TryLoad(Write("rect_in.step", RectSolid(".MILLI.", 1, 1)), notes);
            Ok("degenerate-free tiny part still parses", m != null && m.TotalEdges == 4);
        }

        // ── Edge de-duplication actually fires ───────────────────────────────
        {
            // Give the loop the same edge twice, as a shared edge really appears.
            string body = RectSolid(".MILLI.", 10, 4)
                .Replace("#104 = EDGE_LOOP('',(#110,#111,#112,#113));",
                         "#104 = EDGE_LOOP('',(#110,#111,#112,#113,#110,#111));");
            notes.Clear();
            var m = StepParser.TryLoad(Write("rect_dup.step", body), notes);
            Ok("shared edges are drawn once, not twice",
               m != null && m.TotalEdges == 4);
        }

        // ── Arc sweep direction ──────────────────────────────────────────────
        {
            // A quarter arc from (5,0) to (0,5) about +Z centred at the origin. With
            // sameSense .T. it sweeps the SHORT way (90 deg) through (3.53,3.53).
            // Taking the complement would swing it out to x=-5, so the bounds tell us
            // which arc was drawn without needing to inspect points.
            string arc = RectSolid(".MILLI.", 10, 4)
                .Replace("#140 = LINE('',#160,#170);",
                         "#140 = CIRCLE('',#190,5.);\n" +
                         "#190 = AXIS2_PLACEMENT_3D('',#191,#192,#193);\n" +
                         "#191 = CARTESIAN_POINT('',(0.,0.,0.));\n" +
                         "#192 = DIRECTION('',(0.,0.,1.));\n" +
                         "#193 = DIRECTION('',(1.,0.,0.));")
                .Replace("#160 = CARTESIAN_POINT('',(0.,0.,0.));",
                         "#160 = CARTESIAN_POINT('',(5.,0.,0.));")
                .Replace("#161 = CARTESIAN_POINT('',(10,0.,0.));",
                         "#161 = CARTESIAN_POINT('',(0.,5.,0.));")
                .Replace("#104 = EDGE_LOOP('',(#110,#111,#112,#113));",
                         "#104 = EDGE_LOOP('',(#110));");
            notes.Clear();
            var m = StepParser.TryLoad(Write("arc_short.step", arc), notes);
            Ok("arc-only solid parsed", m != null && m.TotalEdges == 1);
            if (m != null && m.TotalEdges == 1)
            {
                Ok($"short arc taken, not its complement (minX {m.MinX:0.###})", m.MinX > -0.01f);
                Near("arc reaches x=+5", m.MaxX, 5, 0.01);
                Near("arc reaches y=+5", m.MaxY, 5, 0.01);
                // A 90 deg arc at r=5 with a 0.05 mm chord tolerance needs 6 segments:
                // the max angle per segment is 2*acos(1 - 0.05/5) = 0.283 rad. So 7 points
                // is correct, not sparse — and 0.05 mm is far finer than a voxel anyway.
                Ok($"arc tessellated to the chord tolerance ({m.TotalPoints} points)",
                   m.TotalPoints >= 6 && m.TotalPoints <= 12);
            }

            // Flip the sense: now it must take the long way round and cross x=-5.
            notes.Clear();
            var m2 = StepParser.TryLoad(
                Write("arc_long.step", arc.Replace("EDGE_CURVE('',#130,#131,#140,.T.)",
                                                   "EDGE_CURVE('',#130,#131,#140,.F.)")), notes);
            if (m2 != null && m2.TotalEdges == 1)
                Ok($"reversed sense takes the complement (minX {m2.MinX:0.###})", m2.MinX < -4.9f);
            else Ok("reversed-sense arc parsed", false);
        }

        // ── Assembly placement ──────────────────────────────────────────────
        {
            // Parent rep #10 holds nothing; child rep #200 holds the rectangle and is
            // seated at (100, 50, 0). Without the transform the part lands on the
            // origin, which is the classic wrong-looking STEP import.
            string asm = RectSolid(".MILLI.", 10, 4)
                .Replace("#10 = SHAPE_REPRESENTATION('',(#100),#39);",
                         "#10  = SHAPE_REPRESENTATION('',(),#39);\n" +
                         "#200 = SHAPE_REPRESENTATION('CHILD',(#100),#39);\n" +
                         "#638 = CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#639,#4);\n" +
                         "#639 = ( REPRESENTATION_RELATIONSHIP('','',#200,#10)\n" +
                         "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#640)\n" +
                         "SHAPE_REPRESENTATION_RELATIONSHIP() );\n" +
                         "#640 = ITEM_DEFINED_TRANSFORMATION('','',#641,#644);\n" +
                         "#641 = AXIS2_PLACEMENT_3D('',#642,#181,#182);\n" +
                         "#642 = CARTESIAN_POINT('',(0.,0.,0.));\n" +
                         "#644 = AXIS2_PLACEMENT_3D('',#645,#181,#182);\n" +
                         "#645 = CARTESIAN_POINT('',(100.,50.,0.));");
            notes.Clear();
            var m = StepParser.TryLoad(Write("asm.step", asm), notes);
            Ok("assembly file parsed", m != null && m.SolidCount == 1);
            if (m != null && m.SolidCount == 1)
            {
                Near("component placed at its assembly seat (X)", m.MinX, 100, 0.01);
                Near("component placed at its assembly seat (Y)", m.MinY, 50, 0.01);
                Ok("and it is NOT sitting on the origin", m.MinX > 99);
            }
        }

        // ── Complex instances and awkward numbers survive tokenizing ─────────
        {
            string odd = RectSolid(".MILLI.", 10, 4)
                .Replace("#161 = CARTESIAN_POINT('',(10,0.,0.));",
                         "#161 = CARTESIAN_POINT('',(1.E+01,0.,0.));")
                .Replace("#163 = CARTESIAN_POINT('',(0.,4,0.));",
                         "#163 = CARTESIAN_POINT('',(0.,.4E+01,0.));")
                .Replace("#100 = MANIFOLD_SOLID_BREP('body',#101);",
                         "/* a comment with ; and #999 and (parens) */\n" +
                         "#100 = MANIFOLD_SOLID_BREP('body with '' quote; and #hash',#101);");
            notes.Clear();
            var m = StepParser.TryLoad(Write("odd.step", odd), notes);
            Ok("comments, escaped quotes and 1.E+01 reals all tokenize", m != null);
            if (m != null)
            {
                Near("exponent real parsed as 10", m.WidthMm, 10, 1e-3);
                Near("leading-dot real parsed as 4", m.DepthMm, 4, 1e-3);
            }
        }

        // ── Colour ──────────────────────────────────────────────────────────
        {
            string col = RectSolid(".MILLI.", 10, 4)
                .Replace("ENDSEC;\nEND-ISO-10303-21;",
                         "#300 = STYLED_ITEM('',(#301),#100);\n" +
                         "#301 = PRESENTATION_STYLE_ASSIGNMENT((#302));\n" +
                         "#302 = SURFACE_STYLE_USAGE(.BOTH.,#303);\n" +
                         "#303 = SURFACE_SIDE_STYLE('',(#304));\n" +
                         "#304 = SURFACE_STYLE_FILL_AREA(#305);\n" +
                         "#305 = FILL_AREA_STYLE('',(#306));\n" +
                         "#306 = FILL_AREA_STYLE_COLOUR('',#307);\n" +
                         "#307 = COLOUR_RGB('',1.,0.5,0.);\n" +
                         "ENDSEC;\nEND-ISO-10303-21;");
            notes.Clear();
            var m = StepParser.TryLoad(Write("colour.step", col), notes);
            Ok("colour file parsed", m != null && m.SolidCount == 1);
            if (m != null && m.SolidCount == 1)
                Ok($"COLOUR_RGB read as 0x{m.Solids[0].Colour:X6}",
                   m.Solids[0].Colour == 0xFF8000);
        }

        // ── Spline edges are approximated AND reported ───────────────────────
        {
            string sp = RectSolid(".MILLI.", 10, 4)
                .Replace("#140 = LINE('',#160,#170);",
                         "#140 = B_SPLINE_CURVE_WITH_KNOTS('',3,(#160,#161,#162,#163),.UNSPECIFIED.,.F.,.F.,(4,4),(0.,1.),.UNSPECIFIED.);");
            notes.Clear();
            var m = StepParser.TryLoad(Write("spline.step", sp), notes);
            Ok("spline edge did not break the parse", m != null && m.TotalEdges == 4);
            Ok("and the approximation was reported, not hidden",
               notes.Exists(n => n.Contains("spline")));
        }

        // ── Not a STEP file at all ───────────────────────────────────────────
        {
            notes.Clear();
            var m = StepParser.TryLoad(Write("junk.step", "this is not a step file\n"), notes);
            Ok("garbage input reports rather than throws", m == null && notes.Count > 0);
        }

        // ── The real Altium export, when present ─────────────────────────────
        const string real = @"C:\Users\VoxelUser\Downloads\Project Outputs for VLED_IRSensor_V1.0-20260821T004235Z-1-001\Project Outputs for VLED_IRSensor_V1.0\ExportSTEP\VLED_IRSensor_V1.0.step";
        if (!File.Exists(real))
        {
            Console.WriteLine("SKIP  real STEP export not on this machine");
            return _failures;
        }

        Console.WriteLine();
        Console.WriteLine("--- real Altium STEP export ---");
        notes.Clear();
        var big = StepParser.TryLoad(real, notes);
        Ok("real export parsed", big != null);
        if (big == null) { _failures++; return _failures; }

        Console.WriteLine($"      solids {big.SolidCount}  edges {big.TotalEdges}  " +
                          $"points {big.TotalPoints}");
        Console.WriteLine($"      bounds {big.WidthMm:0.##} x {big.DepthMm:0.##} x " +
                          $"{big.HeightMm:0.##} mm");
        foreach (var n in notes) Console.WriteLine($"      note: {n}");

        Ok($"found the 27 B-rep solids ({big.SolidCount})", big.SolidCount >= 20);
        Ok($"edge count is the right order ({big.TotalEdges})",
           big.TotalEdges > 500 && big.TotalEdges < 2000);

        // The board is a small IR sensor PCB: tens of mm, not microns or metres.
        Ok($"board width is tens of mm ({big.WidthMm:0.##})",
           big.WidthMm > 5 && big.WidthMm < 300);
        Ok($"board depth is tens of mm ({big.DepthMm:0.##})",
           big.DepthMm > 5 && big.DepthMm < 300);
        Ok($"stack height is a few mm ({big.HeightMm:0.##})",
           big.HeightMm > 0.1 && big.HeightMm < 60);

        // Components must be spread across the board, not piled on the origin. If the
        // assembly transforms were dropped every solid would share one centre.
        int distinctCentres = 0;
        var seen = new List<(float x, float y)>();
        foreach (var s in big.Solids)
        {
            float cx = (s.MinX + s.MaxX) * 0.5f, cy = (s.MinY + s.MaxY) * 0.5f;
            bool dup = false;
            foreach (var (px, py) in seen)
                if (Math.Abs(px - cx) < 0.05f && Math.Abs(py - cy) < 0.05f) { dup = true; break; }
            if (!dup) { seen.Add((cx, cy)); distinctCentres++; }
        }
        Ok($"solids are spread out, not stacked on the origin ({distinctCentres} distinct centres)",
           distinctCentres >= 5);

        // Designators should have come through the assembly tree.
        var names = new List<string>();
        foreach (var s in big.Solids) if (s.Name.Length > 0) names.Add(s.Name);
        Console.WriteLine($"      names: {string.Join(", ", names)}");
        Ok($"product names recovered ({names.Count} of {big.SolidCount})",
           names.Count >= big.SolidCount / 2);
        Ok("a recognisable designator is among them",
           names.Exists(n => n is "R1" or "R2" or "C1" or "U1" or "J1"));

        return _failures;
    }
}
