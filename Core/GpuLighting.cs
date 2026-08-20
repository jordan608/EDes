// ═══════════════════════════════════════════════════════════════════════════
//  GpuLighting.cs — Optional GPU lighting path (ComputeSharp / DirectX 12)
//
//  Computes the same N·L + inverse-square attenuation lighting as
//  LightingSystem.QueryColor, but for every voxel in parallel on the GPU.
//
//  Usage per frame (see VoxelModelRenderer):
//      gpu.Compute(worldX, worldY, worldZ, lights, lightCount,
//                  ambient, brightness, litColorsOut);
//  The CPU still does shell culling + batching using the returned colours.
//
//  Static per-model data (normals, base colours) is uploaded once in Init().
//  World positions + the small light list are uploaded each frame; the lit
//  colour array is read back. Falls back to CPU automatically when no DX12
//  device is present (see IsAvailable).
// ═══════════════════════════════════════════════════════════════════════════

using System;
using ComputeSharp;

namespace EDes
{
    // GPU-side light record. Colour is pre-normalised 0..1; Dir is the light's
    // forward direction (shader uses −Dir as the direction toward the light).
    public struct GpuLight
    {
        public Float3 Pos;
        public Float3 Color;
        public float  Intensity;
        public float  Radius;
        public int    IsDirectional;
        public Float3 Dir;
    }

    // ── The compute shader ────────────────────────────────────────────────────
    [ThreadGroupSize(DefaultThreadGroupSizes.X)]
    [GeneratedComputeShaderDescriptor]
    public readonly partial struct LightingShader : IComputeShader
    {
        private readonly ReadWriteBuffer<Float3> positions;
        private readonly ReadWriteBuffer<Float3> normals;
        private readonly ReadOnlyBuffer<int>     baseColors;
        private readonly ReadOnlyBuffer<GpuLight> lights;
        private readonly ReadWriteBuffer<int>    outColors;
        private readonly int   lightCount;
        private readonly float ambient;
        private readonly float brightness;

        public LightingShader(
            ReadWriteBuffer<Float3> positions, ReadWriteBuffer<Float3> normals,
            ReadOnlyBuffer<int> baseColors, ReadOnlyBuffer<GpuLight> lights,
            ReadWriteBuffer<int> outColors, int lightCount, float ambient, float brightness)
        {
            this.positions = positions;
            this.normals = normals;
            this.baseColors = baseColors;
            this.lights = lights;
            this.outColors = outColors;
            this.lightCount = lightCount;
            this.ambient = ambient;
            this.brightness = brightness;
        }

        public void Execute()
        {
            int i = ThreadIds.X;

            Float3 p = positions[i];
            Float3 n = normals[i];
            int bc = baseColors[i];
            float baseR = ((bc >> 16) & 0xFF) * (1f / 255f);
            float baseG = ((bc >>  8) & 0xFF) * (1f / 255f);
            float baseB = ( bc        & 0xFF) * (1f / 255f);

            float accR = ambient * baseR;
            float accG = ambient * baseG;
            float accB = ambient * baseB;

            for (int k = 0; k < lightCount; k++)
            {
                GpuLight L = lights[k];
                float ldx, ldy, ldz, att;
                if (L.IsDirectional != 0)
                {
                    ldx = -L.Dir.X; ldy = -L.Dir.Y; ldz = -L.Dir.Z; att = 1f;
                }
                else
                {
                    ldx = L.Pos.X - p.X; ldy = L.Pos.Y - p.Y; ldz = L.Pos.Z - p.Z;
                    float dist = Hlsl.Sqrt(ldx*ldx + ldy*ldy + ldz*ldz + 1e-9f);
                    ldx /= dist; ldy /= dist; ldz /= dist;
                    float r = dist / L.Radius;
                    att = 1f / (1f + r * r);
                }
                float ndotl = n.X*ldx + n.Y*ldy + n.Z*ldz;
                if (ndotl <= 0f) continue;
                float c = ndotl * att * L.Intensity;
                accR += L.Color.X * c * baseR;
                accG += L.Color.Y * c * baseG;
                accB += L.Color.Z * c * baseB;
            }

            accR *= brightness; accG *= brightness; accB *= brightness;
            int ir = (int)(Hlsl.Clamp(accR, 0f, 1f) * 255f + 0.5f);
            int ig = (int)(Hlsl.Clamp(accG, 0f, 1f) * 255f + 0.5f);
            int ib = (int)(Hlsl.Clamp(accB, 0f, 1f) * 255f + 0.5f);
            outColors[i] = (ir << 16) | (ig << 8) | ib;
        }
    }

    // ── Manager wrapping device + buffers ─────────────────────────────────────
    public sealed class GpuLighting : IDisposable
    {
        public const int MAX_LIGHTS = 8;

        /// <summary>True if a DirectX 12 device is available for compute.</summary>
        public static bool IsAvailable
        {
            get { try { _ = GraphicsDevice.GetDefault(); return true; } catch { return false; } }
        }

        private GraphicsDevice? _device;
        private ReadWriteBuffer<Float3>? _normals;    // rotated each frame
        private ReadOnlyBuffer<int>?     _baseColors;
        private ReadWriteBuffer<Float3>? _positions;
        private ReadOnlyBuffer<GpuLight>? _lights;
        private ReadWriteBuffer<int>?    _outColors;

        private int        _count;
        private Float3[]   _posScratch = Array.Empty<Float3>();
        private Float3[]   _norScratch = Array.Empty<Float3>();
        private readonly GpuLight[] _lightScratch = new GpuLight[MAX_LIGHTS];

        public bool Ready { get; private set; }

        public void Init(VoxelModel model)
        {
            _device = GraphicsDevice.GetDefault();
            _count  = model.Count;

            _baseColors = _device.AllocateReadOnlyBuffer(model.BaseColor);
            _normals    = _device.AllocateReadWriteBuffer<Float3>(_count);
            _positions  = _device.AllocateReadWriteBuffer<Float3>(_count);
            _outColors  = _device.AllocateReadWriteBuffer<int>(_count);
            _lights     = _device.AllocateReadOnlyBuffer<GpuLight>(MAX_LIGHTS);
            _posScratch = new Float3[_count];
            _norScratch = new Float3[_count];
            Ready = true;
        }

        /// <summary>
        /// Compute lit colours for all voxels. Positions/normals are world-space,
        /// already rotated/scaled (length ≥ Count). litOut receives packed
        /// 0xRRGGBB (length ≥ Count).
        /// </summary>
        public void Compute(float[] wx, float[] wy, float[] wz,
                            float[] nx, float[] ny, float[] nz,
                            GpuLight[] lights, int lightCount,
                            float ambient, float brightness, int[] litOut)
        {
            if (!Ready || _device == null) return;

            for (int i = 0; i < _count; i++)
            {
                _posScratch[i] = new Float3(wx[i], wy[i], wz[i]);
                _norScratch[i] = new Float3(nx[i], ny[i], nz[i]);
            }
            _positions!.CopyFrom(_posScratch);
            _normals!.CopyFrom(_norScratch);

            int n = Math.Min(lightCount, MAX_LIGHTS);
            Array.Clear(_lightScratch, 0, MAX_LIGHTS);
            Array.Copy(lights, _lightScratch, n);
            _lights!.CopyFrom(_lightScratch);

            _device.For(_count, new LightingShader(
                _positions!, _normals!, _baseColors!, _lights!, _outColors!,
                n, ambient, brightness));

            _outColors!.CopyTo(litOut);
        }

        public void Dispose()
        {
            _normals?.Dispose();
            _baseColors?.Dispose();
            _positions?.Dispose();
            _lights?.Dispose();
            _outColors?.Dispose();
            Ready = false;
        }
    }
}
