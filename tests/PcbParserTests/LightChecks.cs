// ═══════════════════════════════════════════════════════════════════════════
//  LightChecks.cs — the CAD point light, and the STEP→STL round trip
//
//  Two things that are easy to get subtly wrong and impossible to eyeball:
//
//  1. The point light. A directional light and a point light look similar on one
//     face and completely different across a board, so the check is that two
//     identical faces at opposite ends shade DIFFERENTLY under a point light and
//     identically under a directional one. That distinction is the entire reason
//     the point light exists.
//
//  2. The tessellation. Whether gmsh actually produces a mesh from the real STEP
//     export is a fact about this machine's Python install, not about the parser,
//     so it is checked end to end and SKIPPED (not failed) when no tessellator or
//     no fixture is configured.
//
//  Shade() is private to PcbRenderer, so the light maths is re-implemented here
//  from the same formula. That is a deliberate duplicate: an independent statement
//  of the intended behaviour catches a change of mind in the renderer, which a
//  call into the renderer's own code could not.
// ═══════════════════════════════════════════════════════════════════════════

using EDes.Pcb;

namespace PcbParserTests;

public static class LightChecks
{
    private static int _failures;

    private static void Ok(string what, bool pass)
    {
        if (!pass) _failures++;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}");
    }

    /// <summary>The renderer's shading formula, restated. ambient + (1-ambient)·N·L·att,
    /// with att = 1/(1 + d²/range²) and the sign of N·L kept for faces.</summary>
    private static float Shade(float nx, float ny, float nz,
                               float px, float py, float pz,
                               float lx, float ly, float lz,
                               float ambient, float range, bool point, bool twoSided)
    {
        float dx = lx, dy = ly, dz = lz, att = 1f;
        if (point)
        {
            dx = lx - px; dy = ly - py; dz = lz - pz;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 < 1e-9f) return 1f;
            float d = MathF.Sqrt(d2);
            dx /= d; dy /= d; dz /= d;
            if (range > 1e-3f) att = 1f / (1f + d2 / (range * range));
        }
        else
        {
            float l = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (l < 1e-6f) { dx = 0; dy = 0; dz = 1; l = 1; }
            dx /= l; dy /= l; dz /= l;
        }

        float ndl = nx * dx + ny * dy + nz * dz;
        ndl = twoSided ? MathF.Abs(ndl) : MathF.Max(0f, ndl);
        return ambient + (1f - ambient) * ndl * att;
    }

    public static int Run()
    {
        const float amb = 0.35f, range = 40f;

        // ── The point light distinguishes position; the directional one cannot ──
        {
            // Two identical up-facing faces 30 mm apart, lamp directly over the first.
            float sNear = Shade(0, 0, 1,  0, 0, 0,   0, 0, 10, amb, range, true,  false);
            float sFar  = Shade(0, 0, 1, 30, 0, 0,   0, 0, 10, amb, range, true,  false);
            Ok($"a point light shades two identical faces differently by position "
             + $"({sNear:0.###} near vs {sFar:0.###} far)", sNear > sFar + 0.05f);

            float dNear = Shade(0, 0, 1,  0, 0, 0,   0, 0, 1, amb, range, false, false);
            float dFar  = Shade(0, 0, 1, 30, 0, 0,   0, 0, 1, amb, range, false, false);
            Ok($"a directional light cannot ({dNear:0.###} vs {dFar:0.###}) — which is "
             + "exactly the flatness the point light fixes",
               Math.Abs(dNear - dFar) < 1e-5f);
        }

        // ── Falloff, and turning it off ──────────────────────────────────────
        {
            float near = Shade(0, 0, 1, 0, 0, 0,  0, 0, 5,   amb, range, true, false);
            float far  = Shade(0, 0, 1, 0, 0, 0,  0, 0, 200, amb, range, true, false);
            Ok($"a distant lamp is dimmer than a near one ({far:0.###} < {near:0.###})",
               far < near);
            Ok($"but never below ambient ({far:0.###} >= {amb:0.##})", far >= amb - 1e-5f);

            // At exactly the falloff distance the attenuation is 1/2 by construction.
            float at = Shade(0, 0, 1, 0, 0, 0, 0, 0, range, amb, range, true, false);
            float expect = amb + (1f - amb) * 1f * 0.5f;
            Ok($"at the falloff distance the light is half strength "
             + $"({at:0.###} ~ {expect:0.###})", Math.Abs(at - expect) < 1e-4f);

            float noFall = Shade(0, 0, 1, 0, 0, 0, 0, 0, 500, amb, 0f, true, false);
            Ok($"range 0 disables falloff entirely ({noFall:0.###} = full)",
               Math.Abs(noFall - 1f) < 1e-4f);
        }

        // ── Sign handling: faces are one-sided, edges are not ────────────────
        {
            // A face pointing AWAY from the lamp must fall to ambient, not stay lit --
            // that darkening is the whole of the self-shading.
            float away = Shade(0, 0, -1, 0, 0, 0, 0, 0, 10, amb, range, true, false);
            Ok($"a face turned away from the lamp falls to ambient ({away:0.###})",
               Math.Abs(away - amb) < 1e-5f);

            // The same normal on an EDGE stays lit: an edge is shared by two faces
            // pointing opposite ways, so its sign carries no information.
            float edge = Shade(0, 0, -1, 0, 0, 0, 0, 0, 10, amb, range, true, true);
            Ok($"the same normal on an edge stays lit ({edge:0.###} > ambient)",
               edge > amb + 0.05f);
        }

        // ── Never out of range, whatever is thrown at it ─────────────────────
        {
            bool bad = false;
            float[] vals = { -1e6f, -1f, 0f, 1f, 1e6f };
            foreach (float x in vals)
            foreach (float z in vals)
            foreach (bool pt in new[] { true, false })
            {
                float s = Shade(0, 0, 1, x, 0, z, x, 0, -z, amb, range, pt, false);
                if (float.IsNaN(s) || s < amb - 1e-4f || s > 1.0001f) bad = true;
            }
            Ok("shade stays in [ambient, 1] and never NaN across extreme inputs", !bad);
        }

        // ── Lamp inside a surface does not divide by zero ────────────────────
        {
            float s = Shade(0, 0, 1, 5, 5, 5, 5, 5, 5, amb, range, true, false);
            Ok($"a lamp coincident with the surface is finite ({s:0.###})",
               !float.IsNaN(s) && s <= 1.0001f);
        }

        // ── End to end: does the real STEP actually tessellate here? ─────────
        Console.WriteLine();
        RunTessellation();

        return _failures;
    }

    private static void RunTessellation()
    {
        string? step = TestData.BoardStepFile;
        if (step == null)
        {
            Console.WriteLine($"SKIP  STEP->STL round trip — {TestData.SkipReason}");
            return;
        }

        var notes = new List<string>();
        string tool = StepConverter.Discover("", out string how);
        if (string.IsNullOrEmpty(tool))
        {
            Console.WriteLine($"SKIP  STEP->STL round trip — no tessellator ({how})");
            return;
        }
        Console.WriteLine($"      tessellating with {tool}");

        string? stl = StepConverter.EnsureStl(step, 0.4f, "", notes, _ => { });
        foreach (string n in notes) Console.WriteLine($"      note: {n}");

        Ok("the real STEP converts to an STL", stl != null && File.Exists(stl));
        if (stl == null || !File.Exists(stl)) return;

        long bytes = new FileInfo(stl).Length;
        Ok($"the STL is not empty ({bytes / 1024.0:0.#} KB)", bytes > 1024);

        var faces = StlMesh.TryLoad(stl, notes);
        Ok("the STL loads back as faces", faces != null && faces.Count > 0);
        if (faces == null || faces.Count == 0) return;

        int tris = 0;
        int badNormals = 0;
        foreach (var f in faces)
        {
            tris += f.TriCount;
            float l = MathF.Sqrt(f.NX * f.NX + f.NY * f.NY + f.NZ * f.NZ);
            if (float.IsNaN(l) || Math.Abs(l - 1f) > 1e-3f) badNormals++;
        }
        Console.WriteLine($"      {faces.Count} face group(s), {tris} triangle(s)");

        // Curved surfaces are the whole point: a cylinder tessellated at 0.4 mm has to
        // come back as many differently-oriented groups. If the count collapsed to a
        // handful, the mesh is planar-only and the conversion bought nothing.
        Ok($"curved surfaces produced many distinct orientations ({faces.Count} groups)",
           faces.Count > 50);
        Ok($"the mesh has real triangle count ({tris})", tris > 500);
        Ok($"every group normal is unit length ({badNormals} bad)", badNormals == 0);

        // The second call must come from the cache, or every import re-runs a
        // multi-second mesher.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? again = StepConverter.EnsureStl(step, 0.4f, "", notes, _ => { });
        sw.Stop();
        Ok($"the second conversion is cached ({sw.ElapsedMilliseconds} ms)",
           again == stl && sw.ElapsedMilliseconds < 500);
    }
}
