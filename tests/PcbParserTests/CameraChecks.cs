// ═══════════════════════════════════════════════════════════════════════════
//  CameraChecks.cs — SceneCamera's Transform/InverseTransform pair
//
//  Pure math, no SDK dependency, so this runs exactly like every other suite
//  here: no hardware, no simulator window.
//
//  The one property worth a regression test is the pan/rotate ordering bug:
//  Transform used to add Pan to the point BEFORE the rotation basis, which
//  meant a panned assembly swung through an arc around the display's fixed
//  origin instead of spinning in place wherever it had been moved to. Pan is
//  now applied LAST, in display space, so the world origin's display
//  position — the rotation pivot — must stay put through any rotation.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using EDes.Sim;
using Voxon;

namespace PcbParserTests
{
    internal static class CameraChecks
    {
        private static int _failures;

        private static void Ok(string what, bool pass)
        {
            if (!pass) _failures++;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        private static bool Close(point3d a, point3d b, float tol = 1e-4f)
            => MathF.Abs(a.x - b.x) < tol && MathF.Abs(a.y - b.y) < tol
            && MathF.Abs(a.z - b.z) < tol;

        internal static int Run()
        {
            _failures = 0;

            // ── The bug this exists to catch: rotation must not move the pivot ────
            {
                var cam = new SceneCamera();
                cam.PanX = 5f; cam.PanY = -2f; cam.PanZ = 1.5f;   // slide the scene away

                // World origin is where the assembly's own local origin sits — after
                // panning, THIS is "wherever the assembly has been moved to". Its
                // display position must be the rotation pivot, so it cannot move
                // when only the rotation changes.
                var before = cam.Transform(0f, 0f, 0f);
                cam.RotateLocal(0.3f, 0.5f, -0.2f);               // an arbitrary spin
                var after = cam.Transform(0f, 0f, 0f);

                Ok($"a panned assembly's own origin does not relocate when it spins "
                 + $"(({before.x:0.###},{before.y:0.###},{before.z:0.###}) -> "
                 + $"({after.x:0.###},{after.y:0.###},{after.z:0.###}))",
                   Close(before, after));
            }

            // ── Transform / InverseTransform remain exact inverses ─────────────────
            // Exact, not approximate: the basis is orthonormal, so this must hold to
            // float precision through pan, zoom AND rotation together, not just one
            // at a time.
            {
                var cam = new SceneCamera();
                cam.PanX = 1.7f; cam.PanY = -0.4f; cam.PanZ = 0.9f;
                cam.Zoom = 1.6f;
                cam.RotateLocal(0.4f, -0.6f, 0.2f);

                float wx = 3.1f, wy = -2.2f, wz = 0.7f;
                var d = cam.Transform(wx, wy, wz);
                var back = cam.InverseTransform(d);

                Ok($"InverseTransform undoes Transform through pan+zoom+rotate "
                 + $"({back.x:0.###},{back.y:0.###},{back.z:0.###})",
                   MathF.Abs(back.x - wx) < 1e-3f && MathF.Abs(back.y - wy) < 1e-3f
                   && MathF.Abs(back.z - wz) < 1e-3f);
            }

            // ── Pan still scales with Zoom, as it did before the reorder ───────────
            {
                var cam = new SceneCamera { PanX = 2f };
                var atOne = cam.Transform(0f, 0f, 0f);
                cam.Zoom = 3f;
                var atThree = cam.Transform(0f, 0f, 0f);

                Ok($"zooming in also zooms in on the pan offset ({atOne.x:0.#} -> {atThree.x:0.#})",
                   MathF.Abs(atThree.x - atOne.x * 3f) < 1e-4f);
            }

            // ── Direction() stays pan/zoom-free, as documented ─────────────────────
            {
                var cam = new SceneCamera { PanX = 9f, Zoom = 4f };
                var dir = cam.Direction(1f, 0f, 0f);

                Ok($"Direction ignores pan and zoom (len {MathF.Sqrt(dir.x*dir.x+dir.y*dir.y+dir.z*dir.z):0.###})",
                   MathF.Abs(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z - 1f) < 1e-4f);
            }

            return _failures;
        }
    }
}
