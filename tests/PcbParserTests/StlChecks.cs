// STL reading and STEP-tessellation checks.
//
// The STL reader exists so a converted STEP can be LIT, which means the normals
// matter more than the vertices — a cloud of correctly-placed points with wrong
// orientation shades wrongly everywhere. So these checks are mostly about
// normals: recomputed from winding, oriented, grouped, and never NaN.

using EDes.Pcb;

namespace PcbParserTests;

public static class StlChecks
{
    private static int _failures;

    private static void Ok(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "edes_stl_checks");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>An ASCII STL of one triangle in the Z=0 plane, wound counter-clockwise so
    /// its true normal is +Z.</summary>
    private static string AsciiTri(string storedNormal = "0 0 1") => $@"solid test
  facet normal {storedNormal}
    outer loop
      vertex 0 0 0
      vertex 1 0 0
      vertex 0 1 0
    endloop
  endfacet
endsolid test
";

    private static string WriteBinary(string name, (float[] n, float[] a, float[] b, float[] c)[] tris)
    {
        string path = Path.Combine(Dir(), name);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(new byte[80]);
        bw.Write((uint)tris.Length);
        foreach (var t in tris)
        {
            foreach (float f in t.n) bw.Write(f);
            foreach (float f in t.a) bw.Write(f);
            foreach (float f in t.b) bw.Write(f);
            foreach (float f in t.c) bw.Write(f);
            bw.Write((ushort)0);
        }
        return path;
    }

    private static string Write(string name, string body)
    {
        string path = Path.Combine(Dir(), name);
        File.WriteAllText(path, body);
        return path;
    }

    public static int Run()
    {
        _failures = 0;
        var notes = new List<string>();

        // ── ASCII ────────────────────────────────────────────────────────────
        {
            notes.Clear();
            var faces = StlMesh.TryLoad(Write("one.stl", AsciiTri()), notes);
            Ok("an ASCII STL loads", faces != null && faces.Count == 1);
            if (faces != null && faces.Count == 1)
            {
                var f = faces[0];
                Ok($"one triangle ({f.TriCount})", f.TriCount == 1);
                Ok($"normal is +Z ({f.NX:0.00},{f.NY:0.00},{f.NZ:0.00})",
                   Math.Abs(f.NZ - 1f) < 1e-5 && Math.Abs(f.NX) < 1e-5 && Math.Abs(f.NY) < 1e-5);
                Ok("and is marked as set", f.HasNormalSet);
                Ok("vertices came through", Math.Abs(f.X[1] - 1f) < 1e-5f);
            }
        }

        // ── The stored normal decides orientation when it disagrees ──────────
        {
            // Same winding, but the file insists the facet points -Z. Exporters do emit
            // inconsistent windings, and the file's own normal is the better authority for
            // which SIDE is outward.
            notes.Clear();
            var faces = StlMesh.TryLoad(Write("flip.stl", AsciiTri("0 0 -1")), notes);
            Ok("a contradicting stored normal flips the result",
               faces != null && faces.Count == 1 && faces[0].NZ < -0.9f);
        }

        // ── A zero stored normal must not produce NaN ────────────────────────
        {
            // Plenty of exporters write 0 0 0. Trusting it would divide by zero and poison
            // the whole group's averaged normal.
            notes.Clear();
            var faces = StlMesh.TryLoad(Write("zeronormal.stl", AsciiTri("0 0 0")), notes);
            Ok("a zero stored normal falls back to the winding, no NaN",
               faces != null && faces.Count == 1 &&
               !float.IsNaN(faces[0].NZ) && Math.Abs(faces[0].NZ - 1f) < 1e-5);
        }

        // ── Binary ───────────────────────────────────────────────────────────
        {
            notes.Clear();
            string path = WriteBinary("two.stl", new[]
            {
                (new[] { 0f, 0f, 1f }, new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }),
                (new[] { 0f, 0f, 1f }, new[] { 1f, 0f, 0f }, new[] { 1f, 1f, 0f }, new[] { 0f, 1f, 0f }),
            });
            var faces = StlMesh.TryLoad(path, notes);
            int tris = 0;
            if (faces != null) foreach (var f in faces) tris += f.TriCount;
            Ok($"a BINARY STL loads ({tris} triangles)", faces != null && tris == 2);
            Ok("two coplanar triangles share ONE face group",
               faces != null && faces.Count == 1);
        }

        // ── Grouping: many directions must not collapse, one must not split ──
        {
            // A fan around Z: 24 facets in 24 distinct directions. Grouping must keep them
            // apart, or a tessellated cylinder would shade as a single flat disc.
            var tris = new List<(float[], float[], float[], float[])>();
            for (int i = 0; i < 24; i++)
            {
                double a0 = i * Math.PI * 2 / 24, a1 = (i + 1) * Math.PI * 2 / 24;
                tris.Add((new[] { 0f, 0f, 0f },
                          new[] { 0f, 0f, 0f },
                          new[] { (float)Math.Cos(a0), (float)Math.Sin(a0), 1f },
                          new[] { (float)Math.Cos(a1), (float)Math.Sin(a1), 1f }));
            }
            notes.Clear();
            var faces = StlMesh.TryLoad(WriteBinary("cone.stl", tris.ToArray()), notes);
            int total = 0;
            if (faces != null) foreach (var f in faces) total += f.TriCount;
            Ok($"a 24-facet cone keeps its distinct directions ({faces?.Count} groups)",
               faces != null && faces.Count > 8);
            Ok($"and loses no triangles in the grouping ({total})", total == 24);
            bool allUnit = true;
            if (faces != null)
                foreach (var f in faces)
                {
                    double len = Math.Sqrt(f.NX * f.NX + f.NY * f.NY + f.NZ * f.NZ);
                    if (Math.Abs(len - 1.0) > 1e-3) allUnit = false;
                }
            Ok("every group normal is unit length", allUnit);
        }

        // ── Degenerate triangles are dropped, not kept as NaN ────────────────
        {
            notes.Clear();
            var faces = StlMesh.TryLoad(WriteBinary("degen.stl", new[]
            {
                // Zero area: all three vertices identical.
                (new[] { 0f, 0f, 0f }, new[] { 1f, 1f, 1f }, new[] { 1f, 1f, 1f }, new[] { 1f, 1f, 1f }),
                (new[] { 0f, 0f, 1f }, new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }),
            }), notes);
            int total = 0;
            if (faces != null) foreach (var f in faces) total += f.TriCount;
            Ok($"a zero-area triangle is dropped, the good one kept ({total})",
               faces != null && total == 1 && !float.IsNaN(faces[0].NX));
        }

        // ── Truncated and junk input must report, not throw ──────────────────
        {
            notes.Clear();
            string path = Path.Combine(Dir(), "trunc.stl");
            var bytes = File.ReadAllBytes(WriteBinary("src.stl", new[]
            {
                (new[] { 0f, 0f, 1f }, new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }),
            }));
            File.WriteAllBytes(path, bytes[..(bytes.Length - 20)]);   // cut mid-triangle
            var faces = StlMesh.TryLoad(path, notes);
            Ok("a truncated binary STL does not throw",
               faces == null || faces.Count >= 0);

            notes.Clear();
            var junk = StlMesh.TryLoad(Write("junk.stl", "this is not an stl at all\n"), notes);
            Ok("junk input reports rather than throwing", junk == null && notes.Count > 0);
        }

        // ── The converter degrades gracefully with nothing installed ─────────
        {
            notes.Clear();
            string tool = StepConverter.Discover("", out string how);
            Console.WriteLine($"      tessellator: {(tool.Length > 0 ? tool : "none")} — {how}");

            if (tool.Length == 0)
            {
                Ok("with no tessellator, the reason names what to install",
                   how.Contains("pip install gmsh"));

                var stl = StepConverter.EnsureStl(Write("x.step", "ISO-10303-21;\n"), 0.4f, "",
                                                 notes);
                Ok("EnsureStl returns null rather than throwing", stl == null);
                Ok("and says so in the notes", notes.Count > 0);
            }
            else
            {
                Ok("a tessellator was found and reported", how.Length > 0);
            }

            // An explicit command must always win, so an unusual toolchain is reachable.
            string forced = StepConverter.Discover("C:/my/converter.exe", out string how2);
            Ok($"an explicit command overrides discovery ({how2})",
               forced == "C:/my/converter.exe");
        }

        return _failures;
    }
}
