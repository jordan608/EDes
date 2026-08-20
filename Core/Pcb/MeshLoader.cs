// ═══════════════════════════════════════════════════════════════════════════
//  MeshLoader.cs — mesh files to point clouds (STL / OBJ / PLY / GLB / FBX / DAE)
//
//  A volumetric display draws points, so a mesh is turned into a surface point
//  cloud rather than a triangle soup: area-weighted stratified sampling, so a
//  large flat board face and a tiny connector pin both get points in proportion
//  to their real area, and the total stays inside a fixed budget.
//
//  Sampling is DETERMINISTIC (a fixed barycentric lattice per triangle, no RNG),
//  so reloading the same file gives the identical cloud — which matters when you
//  are comparing two revisions of a board on the display.
//
//  Units and axes: mechanical exports from PCB tools are millimetres with Z up
//  and the board in the XY plane. The cloud is stored in that board frame
//  (Z = height, positive up); PcbRenderer is what maps height onto the
//  display's -Z-is-up convention.
//
//  STEP (.step / .stp) is NOT loadable here — it is a boundary-representation
//  CAD format that needs a geometry kernel (OpenCascade et al.), and Assimp
//  does not read it. Rather than fail silently, TryLoad reports the conversion
//  command that does work:
//      FreeCAD:  freecadcmd -c "…Mesh export…"      (see docs/PCB_IMPORT.md)
//      or export STL/STEP-to-STL from the MCAD tool that produced it.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using Assimp;

namespace EDes.Pcb
{
    public static class MeshLoader
    {
        /// <summary>Extensions Assimp handles well enough for mechanical models.</summary>
        public static readonly string[] MeshExtensions =
            { ".stl", ".obj", ".ply", ".glb", ".gltf", ".fbx", ".dae", ".3ds", ".off" };

        public static readonly string[] StepExtensions = { ".step", ".stp" };

        public static bool IsMesh(string path)
            => Array.IndexOf(MeshExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        public static bool IsStep(string path)
            => Array.IndexOf(StepExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        /// <summary>Load and sample a mesh into a point cloud of at most maxPoints.
        /// Returns null and appends a note on failure.</summary>
        public static MeshCloud? TryLoad(string path, int maxPoints, List<string> notes)
        {
            if (IsStep(path))
            {
                notes.Add($"{Path.GetFileName(path)}: STEP needs a CAD kernel — export STL/GLB " +
                          "first (see docs/PCB_IMPORT.md)");
                return null;
            }

            try
            {
                using var ctx = new AssimpContext();
                Scene scene = ctx.ImportFile(path,
                    PostProcessSteps.Triangulate |
                    PostProcessSteps.PreTransformVertices |     // bake node transforms
                    PostProcessSteps.JoinIdenticalVertices);

                if (scene == null || scene.MeshCount == 0)
                {
                    notes.Add($"{Path.GetFileName(path)}: no meshes found");
                    return null;
                }

                // Pass A — total area, so the point budget can be shared fairly.
                double totalArea = 0;
                foreach (var mesh in scene.Meshes)
                    foreach (var face in mesh.Faces)
                    {
                        if (face.IndexCount < 3) continue;
                        totalArea += TriArea(mesh, face.Indices[0], face.Indices[1], face.Indices[2]);
                    }

                if (totalArea <= 0)
                {
                    notes.Add($"{Path.GetFileName(path)}: degenerate geometry");
                    return null;
                }

                double areaPerPoint = totalArea / Math.Max(1, maxPoints);

                var xs = new List<float>(maxPoints);
                var ys = new List<float>(maxPoints);
                var zs = new List<float>(maxPoints);

                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

                // Pass B — stratified sample each triangle.
                foreach (var mesh in scene.Meshes)
                {
                    foreach (var face in mesh.Faces)
                    {
                        if (face.IndexCount < 3) continue;
                        for (int t = 2; t < face.IndexCount; t++)   // fan, in case of >3
                        {
                            int i0 = face.Indices[0], i1 = face.Indices[t - 1], i2 = face.Indices[t];
                            var a = mesh.Vertices[i0];
                            var b = mesh.Vertices[i1];
                            var c = mesh.Vertices[i2];

                            double area = TriArea(mesh, i0, i1, i2);
                            int n = (int)Math.Ceiling(area / areaPerPoint);
                            n = Math.Clamp(n, 1, 4096);

                            // Barycentric lattice: k rows give k(k+1)/2 points >= n.
                            int k = 1;
                            while (k * (k + 1) / 2 < n) k++;

                            for (int r = 0; r <= k; r++)
                            for (int s = 0; s <= k - r; s++)
                            {
                                float u = k == 0 ? 0.33f : r / (float)k;
                                float v = k == 0 ? 0.33f : s / (float)k;
                                float w = 1f - u - v;
                                if (w < -1e-4f) continue;

                                float px = a.X * w + b.X * u + c.X * v;
                                float py = a.Y * w + b.Y * u + c.Y * v;
                                float pz = a.Z * w + b.Z * u + c.Z * v;

                                if (xs.Count >= maxPoints) goto done;

                                xs.Add(px); ys.Add(py); zs.Add(pz);
                                if (px < minX) minX = px; if (px > maxX) maxX = px;
                                if (py < minY) minY = py; if (py > maxY) maxY = py;
                                if (pz < minZ) minZ = pz; if (pz > maxZ) maxZ = pz;
                            }
                        }
                    }
                }
            done:
                if (xs.Count == 0)
                {
                    notes.Add($"{Path.GetFileName(path)}: produced no points");
                    return null;
                }

                return new MeshCloud
                {
                    Name  = Path.GetFileName(path),
                    X     = xs.ToArray(),
                    Y     = ys.ToArray(),
                    Z     = zs.ToArray(),
                    Count = xs.Count,
                    MinX  = minX, MinY = minY, MinZ = minZ,
                    MaxX  = maxX, MaxY = maxY, MaxZ = maxZ,
                };
            }
            catch (Exception ex)
            {
                notes.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} — {ex.Message}");
                return null;
            }
        }

        private static double TriArea(Mesh mesh, int i0, int i1, int i2)
        {
            var a = mesh.Vertices[i0];
            var b = mesh.Vertices[i1];
            var c = mesh.Vertices[i2];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double cx = uy * vz - uz * vy;
            double cy = uz * vx - ux * vz;
            double cz = ux * vy - uy * vx;
            return 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }
    }
}
