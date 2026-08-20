// ═══════════════════════════════════════════════════════════════════════════
//  VoxelModel.cs — Load a .glb model (or a generated fallback) as lit voxels
//
//  Pipeline:
//    1. Look for the first *.glb next to the executable.
//    2. If found, import it with Assimp and SURFACE-voxelize every triangle:
//       sample the triangle densely, snap each sample to a voxel grid cell,
//       and keep position + interpolated normal + colour per unique cell.
//    3. If no .glb (or load fails), generate a colourful sphere so there is
//       always something to test the lighting against.
//
//  Output is stored as parallel arrays (structure-of-arrays) ready to feed to
//  the lighting pass and ledHost.DrawVox_Batch.
//
//  Coordinates: positions are stored CENTRED on the model origin, in the model's
//  own units, with axes remapped to the Voxon frame (X=right, Y=depth, Z=up —
//  glTF is Y-up so we swap Y/Z). VoxelModelRenderer applies a uniform fit scale each
//  frame from the live DisplayVolume, so detected hardware bounds are respected.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Assimp;

namespace EDes
{
    public sealed class VoxelModel
    {
        // Parallel arrays — index [0..Count). Positions are centred, model units.
        public float[] Rx = Array.Empty<float>();
        public float[] Ry = Array.Empty<float>();
        public float[] Rz = Array.Empty<float>();
        public float[] Nx = Array.Empty<float>();
        public float[] Ny = Array.Empty<float>();
        public float[] Nz = Array.Empty<float>();
        public int[]   BaseColor = Array.Empty<int>();
        public int     Count;

        /// Largest half-extent across the three axes (for uniform fit scaling).
        public float MaxHalf = 1f;

        /// Human-readable source description for diagnostics/logging.
        public string Source = "(none)";

        // Default voxels across the longest axis (density 1.0).
        public const int BASE_GRID = 64;

        /// Map a density multiplier (e.g. 0.25–3.0) to a voxel grid resolution.
        public static int GridForDensity(float density)
            => Math.Clamp((int)MathF.Round(BASE_GRID * density), 8, 192);

        // ── Public entry point ────────────────────────────────────────────────
        public static VoxelModel LoadOrDefault(int gridAcross = BASE_GRID)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                string[] glbs = Directory.GetFiles(dir, "*.glb");
                Array.Sort(glbs, StringComparer.OrdinalIgnoreCase);
                if (glbs.Length > 0)
                {
                    var m = LoadGlb(glbs[0], gridAcross);
                    if (m != null && m.Count > 0)
                    {
                        m.Source = $"{Path.GetFileName(glbs[0])} ({m.Count} voxels)";
                        App.Log($"[VoxelModel] Loaded {m.Source}");
                        return m;
                    }
                    App.Log("[VoxelModel] GLB produced no voxels — using fallback sphere");
                }
                else
                {
                    App.Log("[VoxelModel] No *.glb next to exe — using fallback sphere");
                }
            }
            catch (Exception ex)
            {
                App.Log($"[VoxelModel] GLB load failed: {ex.Message} — using fallback sphere");
            }

            var fb = GenerateSphere(gridAcross);
            fb.Source = $"fallback sphere ({fb.Count} voxels)";
            return fb;
        }

        // ── GLB import + surface voxelization ─────────────────────────────────
        private static VoxelModel? LoadGlb(string path, int gridAcross)
        {
            using var ctx = new AssimpContext();
            Scene scene = ctx.ImportFile(path,
                PostProcessSteps.Triangulate |
                PostProcessSteps.PreTransformVertices |   // bake node transforms → world coords
                PostProcessSteps.GenerateSmoothNormals |
                PostProcessSteps.JoinIdenticalVertices);

            if (scene == null || scene.MeshCount == 0) return null;

            // Pass A — global bounds (Voxon axes: x=glX, y=glZ, z=glY)
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (var mesh in scene.Meshes)
                foreach (var v in mesh.Vertices)
                {
                    float x = v.X, y = v.Z, z = v.Y;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }

            float cx = 0.5f * (minX + maxX), cy = 0.5f * (minY + maxY), cz = 0.5f * (minZ + maxZ);
            float hx = 0.5f * (maxX - minX), hy = 0.5f * (maxY - minY), hz = 0.5f * (maxZ - minZ);
            float maxHalf = MathF.Max(hx, MathF.Max(hy, hz));
            if (maxHalf < 1e-6f) return null;

            float pitch = (2f * maxHalf) / gridAcross;
            float invPitch = 1f / pitch;

            string glbDir = Path.GetDirectoryName(path) ?? "";
            var texCache  = new Dictionary<int, TextureImage?>();
            var cells = new Dictionary<long, int>(8192);
            var acc   = new VoxelAccumulator();

            // Pass B — sample every triangle
            foreach (var mesh in scene.Meshes)
            {
                bool hasCol = mesh.HasVertexColors(0);
                var  cols   = hasCol ? mesh.VertexColorChannels[0] : null;
                bool hasNrm = mesh.HasNormals;

                // Base-colour texture + UVs for this mesh (preferred colour source)
                TextureImage? tex = GetTexture(scene, glbDir, mesh.MaterialIndex, texCache);
                bool hasUV = mesh.HasTextureCoords(0);
                var  uvs   = hasUV ? mesh.TextureCoordinateChannels[0] : null;

                // Material diffuse fallback colour for this mesh
                int matColor = 0xFFFFFF;
                if (mesh.MaterialIndex >= 0 && mesh.MaterialIndex < scene.MaterialCount)
                {
                    var mat = scene.Materials[mesh.MaterialIndex];
                    if (mat.HasColorDiffuse)
                        matColor = PackColor(mat.ColorDiffuse.R, mat.ColorDiffuse.G, mat.ColorDiffuse.B);
                }

                foreach (var face in mesh.Faces)
                {
                    if (face.IndexCount != 3) continue;
                    int i0 = face.Indices[0], i1 = face.Indices[1], i2 = face.Indices[2];

                    // Vertices in Voxon axes, centred.
                    GetVert(mesh, i0, cx, cy, cz, out float p0x, out float p0y, out float p0z);
                    GetVert(mesh, i1, cx, cy, cz, out float p1x, out float p1y, out float p1z);
                    GetVert(mesh, i2, cx, cy, cz, out float p2x, out float p2y, out float p2z);

                    // Face normal fallback (if the mesh has no normals).
                    float fnx, fny, fnz;
                    if (hasNrm)
                    { fnx = fny = fnz = 0f; }   // use per-vertex below
                    else
                        TriNormal(p0x,p0y,p0z, p1x,p1y,p1z, p2x,p2y,p2z, out fnx, out fny, out fnz);

                    // Sampling density from the longest edge.
                    float e1 = Dist(p0x,p0y,p0z, p1x,p1y,p1z);
                    float e2 = Dist(p0x,p0y,p0z, p2x,p2y,p2z);
                    float e3 = Dist(p1x,p1y,p1z, p2x,p2y,p2z);
                    int steps = (int)MathF.Ceiling(MathF.Max(e1, MathF.Max(e2, e3)) * invPitch);
                    if (steps < 1) steps = 1; if (steps > 200) steps = 200;
                    float inv = 1f / steps;

                    for (int a = 0; a <= steps; a++)
                    for (int b = 0; b <= steps - a; b++)
                    {
                        float wa = a * inv, wb = b * inv, wc = 1f - wa - wb;
                        float sx = wa*p0x + wb*p1x + wc*p2x;
                        float sy = wa*p0y + wb*p1y + wc*p2y;
                        float sz = wa*p0z + wb*p1z + wc*p2z;

                        int ix = (int)MathF.Round(sx * invPitch);
                        int iy = (int)MathF.Round(sy * invPitch);
                        int iz = (int)MathF.Round(sz * invPitch);
                        long key = CellKey(ix, iy, iz);
                        if (cells.ContainsKey(key)) continue;
                        cells[key] = acc.Count;

                        // Normal
                        float nx, ny, nz;
                        if (hasNrm)
                        {
                            GetNorm(mesh, i0, out float n0x, out float n0y, out float n0z);
                            GetNorm(mesh, i1, out float n1x, out float n1y, out float n1z);
                            GetNorm(mesh, i2, out float n2x, out float n2y, out float n2z);
                            nx = wa*n0x + wb*n1x + wc*n2x;
                            ny = wa*n0y + wb*n1y + wc*n2y;
                            nz = wa*n0z + wb*n1z + wc*n2z;
                            Normalize(ref nx, ref ny, ref nz);
                        }
                        else { nx = fnx; ny = fny; nz = fnz; }

                        // Colour: texture → vertex colour → material → gradient.
                        int col;
                        if (tex != null && hasUV)
                        {
                            var t0 = uvs![i0]; var t1 = uvs[i1]; var t2 = uvs[i2];
                            float uu = wa*t0.X + wb*t1.X + wc*t2.X;
                            float vv = wa*t0.Y + wb*t1.Y + wc*t2.Y;
                            col = tex.Sample(uu, vv);
                        }
                        else if (hasCol)
                        {
                            var d0 = cols![i0]; var d1 = cols[i1]; var d2 = cols[i2];
                            col = PackColor(wa*d0.R + wb*d1.R + wc*d2.R,
                                            wa*d0.G + wb*d1.G + wc*d2.G,
                                            wa*d0.B + wb*d1.B + wc*d2.B);
                        }
                        else if (matColor != 0xFFFFFF) col = matColor;
                        else col = GradientColor(ix * pitch, iy * pitch, iz * pitch, maxHalf);

                        acc.Add(ix * pitch, iy * pitch, iz * pitch, nx, ny, nz, col);
                    }
                }
            }

            if (acc.Count == 0) return null;
            var model = acc.ToModel();
            model.MaxHalf = maxHalf;
            return model;
        }

        // ── Fallback: a colourful solid sphere with radial normals ────────────
        private static VoxelModel GenerateSphere(int gridAcross)
        {
            const float R = 1.0f;
            float pitch = (2f * R) / gridAcross;
            int n = gridAcross / 2;
            var acc = new VoxelAccumulator();

            for (int ix = -n; ix <= n; ix++)
            for (int iy = -n; iy <= n; iy++)
            for (int iz = -n; iz <= n; iz++)
            {
                float x = ix * pitch, y = iy * pitch, z = iz * pitch;
                float r = MathF.Sqrt(x*x + y*y + z*z);
                if (r > R) continue;
                float nx = x, ny = y, nz = z;
                if (r > 1e-6f) { nx/=r; ny/=r; nz/=r; } else { nz = 1f; }
                acc.Add(x, y, z, nx, ny, nz, GradientColor(x, y, z, R));
            }

            var m = acc.ToModel();
            m.MaxHalf = R;
            return m;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void GetVert(Mesh mesh, int i, float cx, float cy, float cz,
                                    out float x, out float y, out float z)
        {
            var v = mesh.Vertices[i];          // glTF: X right, Y up, Z forward
            x = v.X - cx;                      // Voxon X = glX
            y = v.Z - cy;                      // Voxon Y (depth) = glZ
            z = v.Y - cz;                      // Voxon Z (up)    = glY
        }

        private static void GetNorm(Mesh mesh, int i, out float x, out float y, out float z)
        {
            var nrm = mesh.Normals[i];
            x = nrm.X; y = nrm.Z; z = nrm.Y;   // same axis remap as positions
        }

        private static void TriNormal(float ax,float ay,float az, float bx,float by,float bz,
                                      float cx,float cy,float cz,
                                      out float nx, out float ny, out float nz)
        {
            float ux=bx-ax, uy=by-ay, uz=bz-az;
            float vx=cx-ax, vy=cy-ay, vz=cz-az;
            nx = uy*vz - uz*vy; ny = uz*vx - ux*vz; nz = ux*vy - uy*vx;
            Normalize(ref nx, ref ny, ref nz);
        }

        private static void Normalize(ref float x, ref float y, ref float z)
        {
            float l = MathF.Sqrt(x*x + y*y + z*z);
            if (l > 1e-6f) { x/=l; y/=l; z/=l; } else { x=0; y=0; z=1; }
        }

        private static float Dist(float ax,float ay,float az, float bx,float by,float bz)
        {
            float dx=bx-ax, dy=by-ay, dz=bz-az;
            return MathF.Sqrt(dx*dx + dy*dy + dz*dz);
        }

        // ── Texture loading ────────────────────────────────────────────────────
        // Returns the base-colour texture for a material (cached), or null if the
        // material is untextured / the image couldn't be decoded.
        private static TextureImage? GetTexture(Scene scene, string dir, int matIndex,
                                                Dictionary<int, TextureImage?> cache)
        {
            if (matIndex < 0 || matIndex >= scene.MaterialCount) return null;
            if (cache.TryGetValue(matIndex, out var cached)) return cached;

            TextureImage? result = null;
            var mat = scene.Materials[matIndex];

            // Prefer glTF PBR base-colour, fall back to the legacy diffuse slot.
            if (!mat.GetMaterialTexture(TextureType.BaseColor, 0, out TextureSlot slot)
                || string.IsNullOrEmpty(slot.FilePath))
                mat.GetMaterialTexture(TextureType.Diffuse, 0, out slot);

            string fp = slot.FilePath ?? "";
            if (!string.IsNullOrEmpty(fp))
            {
                try
                {
                    if (fp.StartsWith("*") && int.TryParse(fp.Substring(1), out int ti)
                        && ti >= 0 && ti < scene.TextureCount)
                        result = DecodeEmbedded(scene.Textures[ti]);
                    else
                    {
                        string full = Path.IsPathRooted(fp) ? fp : Path.Combine(dir, fp);
                        if (File.Exists(full)) using (var bmp = new Bitmap(full)) result = DecodeBitmap(bmp);
                    }
                }
                catch (Exception ex) { App.Log($"[VoxelModel] texture decode failed: {ex.Message}"); }
            }

            cache[matIndex] = result;
            return result;
        }

        private static TextureImage? DecodeEmbedded(EmbeddedTexture et)
        {
            if (et.IsCompressed && et.CompressedData != null)
            {
                using var ms = new MemoryStream(et.CompressedData);
                using var bmp = new Bitmap(ms);
                return DecodeBitmap(bmp);
            }
            if (et.HasNonCompressedData && et.Width > 0 && et.Height > 0)
            {
                int w = et.Width, h = et.Height;
                var texels = et.NonCompressedData;
                var px = new int[w * h];
                for (int i = 0; i < px.Length && i < texels.Length; i++)
                    px[i] = (texels[i].R << 16) | (texels[i].G << 8) | texels[i].B;
                return new TextureImage(px, w, h);
            }
            return null;
        }

        // Reads a Bitmap into a packed-RGB pixel array via LockBits (fast, one-shot).
        private static TextureImage DecodeBitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            var bytes = new byte[Math.Abs(stride) * h];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bmp.UnlockBits(data);

            var px = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;            // BGRA byte order
                    px[y * w + x] = (bytes[o + 2] << 16) | (bytes[o + 1] << 8) | bytes[o];
                }
            }
            return new TextureImage(px, w, h);
        }

        // Decoded texture with repeat-wrapped nearest sampling.
        private sealed class TextureImage
        {
            private readonly int[] _px;
            private readonly int   _w, _h;
            public TextureImage(int[] px, int w, int h) { _px = px; _w = w; _h = h; }

            public int Sample(float u, float v)
            {
                u -= MathF.Floor(u);               // wrap (repeat)
                v -= MathF.Floor(v);               // glTF V points down → row = v*h
                int x = (int)(u * _w); if (x >= _w) x = _w - 1; if (x < 0) x = 0;
                int y = (int)(v * _h); if (y >= _h) y = _h - 1; if (y < 0) y = 0;
                return _px[y * _w + x];
            }
        }

        private static long CellKey(int ix, int iy, int iz)
            => ((long)(ix + 100000) << 40) | ((long)(iy + 100000) << 20) | (long)(iz + 100000);

        private static int PackColor(float r, float g, float b)
        {
            int ir = (int)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f);
            int ig = (int)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f);
            int ib = (int)(Math.Clamp(b, 0f, 1f) * 255f + 0.5f);
            return (ir << 16) | (ig << 8) | ib;
        }

        // Position-based rainbow so colourless models still show varied voxels.
        private static int GradientColor(float x, float y, float z, float halfExtent)
        {
            float inv = halfExtent > 1e-6f ? 0.5f / halfExtent : 0.5f;
            float r = Math.Clamp(0.5f + x * inv, 0f, 1f);
            float g = Math.Clamp(0.5f + y * inv, 0f, 1f);
            float b = Math.Clamp(0.5f + z * inv, 0f, 1f);
            return PackColor(r, g, b);
        }

        // Growable SoA builder used during voxelization.
        private sealed class VoxelAccumulator
        {
            private readonly List<float> _x = new(), _y = new(), _z = new();
            private readonly List<float> _nx = new(), _ny = new(), _nz = new();
            private readonly List<int>   _c = new();
            public int Count => _c.Count;

            public void Add(float x, float y, float z, float nx, float ny, float nz, int col)
            {
                _x.Add(x);  _y.Add(y);  _z.Add(z);
                _nx.Add(nx); _ny.Add(ny); _nz.Add(nz);
                _c.Add(col);
            }

            public VoxelModel ToModel() => new VoxelModel
            {
                Rx = _x.ToArray(),  Ry = _y.ToArray(),  Rz = _z.ToArray(),
                Nx = _nx.ToArray(), Ny = _ny.ToArray(), Nz = _nz.ToArray(),
                BaseColor = _c.ToArray(),
                Count = _c.Count,
            };
        }
    }
}
