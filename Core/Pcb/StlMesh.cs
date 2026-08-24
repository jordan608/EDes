// ═══════════════════════════════════════════════════════════════════════════
//  StlMesh.cs — STL triangles, with the normals lighting needs
//
//  WHY NOT MeshLoader / Assimp: MeshLoader turns a mesh into a surface POINT
//  CLOUD. Points carry no orientation, so a cloud cannot be lit — and lighting
//  is the entire reason a round object needs tessellating in the first place.
//  StepParser's flat-shading path already takes triangles-with-a-normal
//  (CadFace), so a converted STEP lands there instead and gets shaded exactly
//  like the planar faces do.
//
//  Both STL flavours are read. The binary one is not optional: every tessellator
//  worth using writes binary by default, and an 80-byte header followed by a
//  triangle count is trivially distinguishable from "solid ".
//
//  Normals are RECOMPUTED from the vertices rather than trusted. STL stores a
//  normal per facet, but exporters emit zeroes, unnormalised values and
//  inward-facing ones often enough that trusting the file means shading that is
//  wrong on some parts and right on others — which is harder to diagnose than
//  uniformly wrong. The winding is the authority; the stored normal is used only
//  to decide orientation when the two disagree.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EDes.Pcb
{
    public static class StlMesh
    {
        /// <summary>Read an STL into CadFace triangles grouped for flat shading.
        ///
        /// Faces are grouped by NORMAL, not one CadFace per triangle. A tessellated
        /// cylinder is hundreds of triangles sharing a handful of directions, and one
        /// CadFace each would mean hundreds of objects to shade and iterate where a
        /// handful will do. Grouping is what keeps a converted STEP affordable.</summary>
        public static List<CadFace>? TryLoad(string path, List<string> notes,
                                             int maxTriangles = 400_000)
        {
            try
            {
                var tris = IsBinary(path) ? ReadBinary(path, maxTriangles, notes)
                                          : ReadAscii(path, maxTriangles, notes);
                if (tris == null || tris.Count == 0)
                {
                    notes.Add($"{Path.GetFileName(path)}: no triangles in the STL");
                    return null;
                }
                return TriangleGrouping.Group(tris);
            }
            catch (Exception ex)
            {
                notes.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} — {ex.Message}");
                return null;
            }
        }

        /// <summary>Binary or ASCII? The size test is the reliable one: an ASCII file that
        /// happens to start with "solid" is common, but only a binary file's length is
        /// exactly 84 + 50 * count.</summary>
        private static bool IsBinary(string path)
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 84) return false;

            var header = new byte[84];
            if (fs.Read(header, 0, 84) != 84) return false;

            uint count = BitConverter.ToUInt32(header, 80);
            long expected = 84L + 50L * count;
            if (fs.Length == expected) return true;

            // Not an exact match: fall back to the text marker. Some writers pad the file.
            string start = Encoding.ASCII.GetString(header, 0, 5).ToLowerInvariant();
            return start != "solid";
        }

        private static List<Tri> ReadBinary(string path, int maxTriangles, List<string> notes)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            br.ReadBytes(80);
            uint count = br.ReadUInt32();

            var tris = new List<Tri>((int)Math.Min(count, 100_000));
            for (uint i = 0; i < count; i++)
            {
                if (fs.Position + 50 > fs.Length) break;   // truncated file: keep what we have

                var t = new Tri
                {
                    NX = br.ReadSingle(), NY = br.ReadSingle(), NZ = br.ReadSingle(),
                    AX = br.ReadSingle(), AY = br.ReadSingle(), AZ = br.ReadSingle(),
                    BX = br.ReadSingle(), BY = br.ReadSingle(), BZ = br.ReadSingle(),
                    CX = br.ReadSingle(), CY = br.ReadSingle(), CZ = br.ReadSingle(),
                };
                br.ReadUInt16();                            // attribute byte count
                tris.Add(t);

                if (tris.Count >= maxTriangles)
                {
                    notes.Add($"{Path.GetFileName(path)}: stopped at {maxTriangles:N0} " +
                              $"triangles of {count:N0} — coarsen the conversion tolerance");
                    break;
                }
            }
            return tris;
        }

        private static List<Tri> ReadAscii(string path, int maxTriangles, List<string> notes)
        {
            var tris = new List<Tri>();
            var v = new List<float[]>(3);
            float nx = 0, ny = 0, nz = 0;

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("facet", StringComparison.OrdinalIgnoreCase))
                {
                    v.Clear();
                    ReadTriple(line, out nx, out ny, out nz);
                    continue;
                }
                if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadTriple(line, out float x, out float y, out float z))
                        v.Add(new[] { x, y, z });
                    continue;
                }
                if (!line.StartsWith("endfacet", StringComparison.OrdinalIgnoreCase)) continue;

                if (v.Count >= 3)
                {
                    tris.Add(new Tri
                    {
                        AX = v[0][0], AY = v[0][1], AZ = v[0][2],
                        BX = v[1][0], BY = v[1][1], BZ = v[1][2],
                        CX = v[2][0], CY = v[2][1], CZ = v[2][2],
                        NX = nx, NY = ny, NZ = nz,
                    });
                }
                v.Clear();

                if (tris.Count >= maxTriangles)
                {
                    notes.Add($"{Path.GetFileName(path)}: stopped at {maxTriangles:N0} triangles" +
                              " — coarsen the conversion tolerance");
                    break;
                }
            }
            return tris;
        }

        /// <summary>Last three numbers on the line. Taken from the END rather than by field
        /// index because "facet normal" has two words before the numbers and "vertex" has
        /// one, and some writers add extra whitespace or a leading sign column.</summary>
        private static bool ReadTriple(string line, out float x, out float y, out float z)
        {
            x = y = z = 0;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;

            return float.TryParse(parts[^3], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                 & float.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                 & float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        // Group() and FaceNormal() moved to TriangleGrouping so the WIRE path (the
        // Fusion bridge) uses the identical implementation. Two readers of the same
        // kind of data drifting apart is how one model ends up shaded differently
        // depending on whether it arrived from disk or over a socket.
    }
}
