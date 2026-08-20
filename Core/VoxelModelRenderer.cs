// ═══════════════════════════════════════════════════════════════════════════
//  VoxelModelRenderer.cs — Reusable lit voxel-model draw service
//
//  Owns a VoxelModel (loaded from a *.glb or fallback), re-voxelizes it when the
//  density grid changes (debounced), and each frame:
//    Pass 1: rotate + scale + translate every voxel; rotate normals; fill the
//            lighting shell maps (interior culling).
//    Pass 2: keep exterior voxels; shade on CPU (QueryColor) or GPU (ComputeSharp);
//            apply optional black-cull / dark-boost; pack into batch buffers.
//    Then:   one ledHost.DrawVox_Batch for the whole model.
//
//  Engine service — any IVoxonGame can use it via GameContext. It is handed the
//  LightingSystem + a transform + post-process flags each frame; it does not read
//  GameSettings itself.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using ComputeSharp;
using Voxon;

namespace EDes
{
    public sealed class VoxelModelRenderer : IDisposable
    {
        private VoxelModel _model = null!;
        private int  _currentGrid;
        private int  _pendingGrid;
        private long _pendingSinceMs;

        // Reusable buffers (sized to voxel count — no per-frame allocation).
        private float[] _wx = null!, _wy = null!, _wz = null!;
        private float[] _rnx = null!, _rny = null!, _rnz = null!;   // rotated normals
        private float[] _bx = null!, _by = null!, _bz = null!;
        private int[]   _bcol = null!;

        // GPU lighting (lazy — created on first use when requested + available).
        private GpuLighting? _gpu;
        private int[]?       _litColors;
        private readonly GpuLight[] _gpuLights = new GpuLight[GpuLighting.MAX_LIGHTS];
        private bool _gpuUnavailableLogged;

        public string Source    => _model.Source;
        public int    LastDrawn { get; private set; }

        public VoxelModelRenderer(int gridAcross) => Build(gridAcross);

        // ── Draw — the whole lit, transformed, batched model ──────────────────
        // offX/offY/offZ are the model's world offset (e.g. player position);
        // the renderer centres the model in the usable Z range on top of offZ.
        // Returns the number of voxels actually drawn.
        public int Draw(LedHostCS ledHost, ref vxl_state_t vs, LightingSystem lighting,
                        int targetGrid,
                        float offX, float offY, float offZ,
                        float yaw, float pitch, float userScale,
                        bool useGpu, bool cullBlack, bool boostDark, float boostStrength)
        {
            // Density change → re-voxelize, debounced ~200 ms so a slider drag
            // doesn't re-import the model on every intermediate value.
            if (targetGrid != _currentGrid)
            {
                if (targetGrid != _pendingGrid)
                { _pendingGrid = targetGrid; _pendingSinceMs = Environment.TickCount64; }
                else if (Environment.TickCount64 - _pendingSinceMs > 200)
                    Build(targetGrid);
            }

            int count = _model.Count;
            if (count == 0) { LastDrawn = 0; return 0; }

            // Uniform fit: longest axis fills the volume (aspect preserved, normals
            // stay valid). Centre vertically in the usable Z range.
            const float zBottom = -0.45f;
            float zTop  = DisplayVolume.TopZ;
            float zHalf = 0.5f * (zTop - zBottom);
            float zMid  = 0.5f * (zTop + zBottom);
            float fit   = MathF.Min(DisplayVolume.GameHalfXY, zHalf) / _model.MaxHalf
                          * MathF.Max(0.05f, userScale);

            float ox = offX, oy = offY, oz = zMid + offZ;
            float cy = MathF.Cos(yaw),   sy = MathF.Sin(yaw);
            float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);

            // Pass 1 — rotate + scale + translate; rotate normals; fill shell maps
            lighting.BeginModel();
            for (int i = 0; i < count; i++)
            {
                Rotate(_model.Rx[i], _model.Ry[i], _model.Rz[i], cy, sy, cp, sp,
                       out float prx, out float pry, out float prz);
                float wx = prx * fit + ox;
                float wy = pry * fit + oy;
                float wz = prz * fit + oz;
                _wx[i] = wx; _wy[i] = wy; _wz[i] = wz;

                Rotate(_model.Nx[i], _model.Ny[i], _model.Nz[i], cy, sy, cp, sp,
                       out _rnx[i], out _rny[i], out _rnz[i]);

                lighting.SubmitToShells(wx, wy, wz);
            }

            // Optional GPU pass — shade every voxel at once into _litColors.
            bool gpu = MaybeRunGpu(lighting, useGpu, count);

            // Pass 2 — compact exterior voxels into the batch buffers
            int drawn = 0;
            for (int i = 0; i < count; i++)
            {
                float wx = _wx[i], wy = _wy[i], wz = _wz[i];
                if (!lighting.IsExterior(wx, wy, wz)) continue;

                int lit = gpu
                    ? _litColors![i]
                    : lighting.QueryColor(wx, wy, wz, _rnx[i], _rny[i], _rnz[i], _model.BaseColor[i]);

                if (cullBlack && (lit & 0xFFFFFF) == 0) continue;     // skip pure black
                if (boostDark) lit = BoostDarkColor(lit, boostStrength);

                _bx[drawn] = wx; _by[drawn] = wy; _bz[drawn] = wz;
                _bcol[drawn] = lit;
                drawn++;
            }

            LastDrawn = drawn;
            if (drawn == 0) return 0;

            ledHost.DrawVox_Batch(ref vs, ref _bx[0], ref _by[0], ref _bz[0], ref _bcol[0], drawn, 0);
            return drawn;
        }

        // ── Build — (re)load + voxelize + (re)allocate buffers ────────────────
        private void Build(int gridAcross)
        {
            _model = VoxelModel.LoadOrDefault(gridAcross);
            _currentGrid = gridAcross;
            _pendingGrid = gridAcross;

            int n = Math.Max(1, _model.Count);
            _wx = new float[n]; _wy = new float[n]; _wz = new float[n];
            _rnx = new float[n]; _rny = new float[n]; _rnz = new float[n];
            _bx = new float[n]; _by = new float[n]; _bz = new float[n];
            _bcol = new int[n];

            _gpu?.Dispose();   // re-init for the new voxel count on next use
            _gpu = null;
            _litColors = null;
        }

        // ── GPU lighting ───────────────────────────────────────────────────────
        private bool MaybeRunGpu(LightingSystem lighting, bool useGpu, int count)
        {
            if (!useGpu || !lighting.Enabled || count == 0) return false;

            if (_gpu == null)
            {
                if (!GpuLighting.IsAvailable)
                {
                    if (!_gpuUnavailableLogged)
                    { App.Log("[GPU] No DX12 device — using CPU lighting."); _gpuUnavailableLogged = true; }
                    return false;
                }
                try
                {
                    _gpu = new GpuLighting();
                    _gpu.Init(_model);
                    _litColors = new int[_model.Count];
                    App.Log("[GPU] Lighting offloaded to GPU.");
                }
                catch (Exception ex)
                {
                    App.Log($"[GPU] Init failed: {ex.Message} — using CPU lighting.");
                    _gpu = null;
                    return false;
                }
            }

            int nl = BuildGpuLights(lighting);
            try
            {
                _gpu.Compute(_wx, _wy, _wz, _rnx, _rny, _rnz, _gpuLights, nl,
                             lighting.AmbientIntensity, lighting.GlobalBrightness, _litColors!);
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"[GPU] Compute failed: {ex.Message} — using CPU lighting.");
                return false;
            }
        }

        private int BuildGpuLights(LightingSystem lighting)
        {
            int nl = Math.Min(lighting.ActiveCount, GpuLighting.MAX_LIGHTS);
            for (int k = 0; k < nl; k++)
            {
                var ls = lighting.ActiveLight(k);
                _gpuLights[k] = new GpuLight
                {
                    Pos           = new Float3(ls.X, ls.Y, ls.Z),
                    Color         = new Float3(lighting.ActiveColorR(k),
                                               lighting.ActiveColorG(k),
                                               lighting.ActiveColorB(k)),
                    Intensity     = ls.Intensity,
                    Radius        = ls.Radius,
                    IsDirectional = ls.Type == LightType.Directional ? 1 : 0,
                    Dir           = new Float3(ls.DirX, ls.DirY, ls.DirZ),
                };
            }
            return nl;
        }

        // Lift a colour toward white; darker colours lift more, scaled by strength.
        private static int BoostDarkColor(int c, float strength)
        {
            float r = ((c >> 16) & 0xFF) * (1f / 255f);
            float g = ((c >>  8) & 0xFF) * (1f / 255f);
            float b = ( c        & 0xFF) * (1f / 255f);
            float lum = MathF.Max(r, MathF.Max(g, b));
            float k = strength * (1f - lum);
            r += (1f - r) * k; g += (1f - g) * k; b += (1f - b) * k;
            int ir = (int)(r * 255f + 0.5f); if (ir > 255) ir = 255;
            int ig = (int)(g * 255f + 0.5f); if (ig > 255) ig = 255;
            int ib = (int)(b * 255f + 0.5f); if (ib > 255) ib = 255;
            return (ir << 16) | (ig << 8) | ib;
        }

        // Rotate a vector: yaw about Z (vertical), then pitch about X.
        private static void Rotate(float x, float y, float z,
                                   float cy, float sy, float cp, float sp,
                                   out float ox, out float oy, out float oz)
        {
            float x1 = x * cy - y * sy;
            float y1 = x * sy + y * cy;
            ox = x1;
            oy = y1 * cp - z * sp;
            oz = y1 * sp + z * cp;
        }

        public void Dispose() => _gpu?.Dispose();
    }
}
