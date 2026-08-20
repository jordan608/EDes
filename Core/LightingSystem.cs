// ═══════════════════════════════════════════════════════════════════════════
//  LightingSystem.cs — Multi-light scene lighting
//
//  Pipeline per frame (driven by the engine, GameLoop):
//    lighting.Update(dt)        (decay transient lights)
//    lighting.ApplyConfig(cfg)  (apply the UI lighting settings)
//    lighting.BeginFrame()      (collect + project active lights)
//
//  Per model (repeat for each lit 3D model):
//    lighting.BeginModel()                              (clear 6-face shell maps)
//    Pass 1: foreach voxel → lighting.SubmitToShells()  (build exterior depth maps)
//    Pass 2: foreach voxel → if IsExterior() → QueryColor() → DrawVox()
//
//  Lights:
//    SceneLights[0..3] — persistent, user-configurable (via Settings UI)
//    Transient pool    — short-lived flashes (explosions, impacts) — 16 slots
//    Up to 8 lights processed per frame
//
//  6-face shell maps (interior culling):
//    Six 64×64 orthographic depth maps (±X, ±Y, ±Z).
//    A voxel is "exterior" if it is at the frontmost depth in at least one
//    direction. Interior voxels (hidden inside the model) are skipped.
//    Saves ~70% of draw calls on typical ship models.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.CompilerServices;
using Voxon;

namespace EDes
{
    // ── Light types ───────────────────────────────────────────────────────────
    public enum LightType { Point, Directional }

    public class LightSource
    {
        public bool      Enabled   = true;
        public LightType Type      = LightType.Point;
        public float     X, Y, Z;             // position (Point) or ignored (Directional)
        public float     DirX, DirY, DirZ;   // direction (Directional only)
        public int       Color     = 0xFFFFFF;
        public float     Intensity = 1.0f;
        public float     Radius    = 12.0f;   // distance at which intensity halves

        public LightSource() { }
        public LightSource(float x, float y, float z, int color, float intensity, float radius)
        {
            X = x; Y = y; Z = z; Color = color; Intensity = intensity; Radius = radius;
        }
    }

    public class LightingSystem
    {
        // ── Singleton ────────────────────────────────────────────────────────
        public static LightingSystem Instance { get; private set; } = null!;

        // ── Scene lights ──────────────────────────────────────────────────────
        public const int MAX_SCENE_LIGHTS = 4;
        public readonly LightSource[] SceneLights = new LightSource[MAX_SCENE_LIGHTS];

        // ── Global shading ────────────────────────────────────────────────────
        public bool  Enabled          = true;   // master toggle — false = flat passthrough
        public float AmbientIntensity = 0.12f;
        public float GlobalBrightness = 1.0f;

        // ── Simple lighting: a single directional "sun" ───────────────────────
        // Additive with the scene spotlights; toggled independently from the UI.
        public readonly LightSource SunLight = new LightSource
        {
            Type = LightType.Directional, Enabled = false,
            DirX = 0f, DirY = -1f, DirZ = 0.3f, Color = 0xFFFFFF, Intensity = 1f,
        };

        // ── 6-face shell maps for interior culling ────────────────────────────
        // 64×64 per face. Low enough to fill cheaply; high enough to cull most
        // interior geometry on typical ship models.
        private const int   SHELL_RES    = 64;
        private const float SHELL_EXTENT = 6.0f;
        private const float SHELL_BIAS   = 0.05f;
        private static readonly float _sHRoE = (SHELL_RES - 1) / (2f * SHELL_EXTENT);
        private readonly float[] _shell = new float[6 * SHELL_RES * SHELL_RES];

        // ── Active light cache (rebuilt each BeginFrame) ──────────────────────
        private const int MAX_ACTIVE = 8;
        private int           _numActive = 0;
        private readonly LightSource[] _active = new LightSource[MAX_ACTIVE];
        private readonly float[] _activeLR = new float[MAX_ACTIVE]; // pre-normalised colour
        private readonly float[] _activeLG = new float[MAX_ACTIVE];
        private readonly float[] _activeLB = new float[MAX_ACTIVE];
        // Light direction bases (recomputed in BeginFrame)
        private readonly float[] _fwdX = new float[MAX_ACTIVE], _fwdY = new float[MAX_ACTIVE], _fwdZ = new float[MAX_ACTIVE];
        private readonly float[] _rgtX = new float[MAX_ACTIVE], _rgtY = new float[MAX_ACTIVE], _rgtZ = new float[MAX_ACTIVE];
        private readonly float[] _upX  = new float[MAX_ACTIVE], _upY  = new float[MAX_ACTIVE], _upZ  = new float[MAX_ACTIVE];

        public int FrameId { get; private set; }

        // ── Active-light accessors (for the GPU lighting path) ────────────────
        // Valid after BeginFrame(). Colours are already normalised to 0..1.
        public int   ActiveCount         => _numActive;
        public LightSource ActiveLight(int k) => _active[k];
        public float ActiveColorR(int k) => _activeLR[k];
        public float ActiveColorG(int k) => _activeLG[k];
        public float ActiveColorB(int k) => _activeLB[k];

        // ── Transient light pool (explosions, impacts) ────────────────────────
        private const int MAX_TRANSIENT = 16;
        private struct TransientSlot { public LightSource Light; public float TimeLeft; public float InitialIntensity; }
        private readonly TransientSlot[] _transients = new TransientSlot[MAX_TRANSIENT];

        // ── Constructor ───────────────────────────────────────────────────────
        public LightingSystem()
        {
            Instance = this;

            // Four equal white point lights at the corners of the display volume.
            // Symmetric starting point — adjust colours/intensities in the UI.
            float xb = Math.Min(DisplayVolume.HalfXY, 4f);
            SceneLights[0] = new LightSource( xb,  xb, -2f, 0xFFFFFF, 0.8f, 16f);
            SceneLights[1] = new LightSource(-xb,  xb, -2f, 0xFFFFFF, 0.8f, 16f);
            SceneLights[2] = new LightSource( xb, -xb, -2f, 0xFFFFFF, 0.8f, 16f);
            SceneLights[3] = new LightSource(-xb, -xb, -2f, 0xFFFFFF, 0.8f, 16f);

            for (int i = 0; i < MAX_TRANSIENT; i++)
                _transients[i].Light = new LightSource { Enabled = false };
        }

        // ── Transient lights ──────────────────────────────────────────────────

        /// <summary>
        /// Add a short-lived coloured point light — explosion flash, impact spark, etc.
        /// If the pool is full the weakest active light is replaced.
        /// </summary>
        public void AddTransientLight(float x, float y, float z,
                                      int color, float intensity, float duration,
                                      float radius = 5f)
        {
            if (duration <= 0f || intensity <= 0f) return;
            // Find an expired slot first
            for (int i = 0; i < MAX_TRANSIENT; i++)
            {
                if (!_transients[i].Light.Enabled || _transients[i].TimeLeft <= 0f)
                { Write(i, x, y, z, color, intensity, duration, radius); return; }
            }
            // Pool full — replace the weakest (lowest intensity × time remaining)
            int worst = 0; float worstScore = float.MaxValue;
            for (int i = 0; i < MAX_TRANSIENT; i++)
            {
                float score = _transients[i].Light.Intensity * _transients[i].TimeLeft;
                if (score < worstScore) { worstScore = score; worst = i; }
            }
            Write(worst, x, y, z, color, intensity, duration, radius);
        }

        private void Write(int i, float x, float y, float z, int col,
                           float intensity, float duration, float radius)
        {
            _transients[i].Light.Type      = LightType.Point;
            _transients[i].Light.X         = x;
            _transients[i].Light.Y         = y;
            _transients[i].Light.Z         = z;
            _transients[i].Light.Color     = col;
            _transients[i].Light.Intensity = intensity;
            _transients[i].Light.Radius    = radius;
            _transients[i].Light.Enabled   = true;
            _transients[i].TimeLeft        = duration;
            _transients[i].InitialIntensity = intensity;
        }

        // ── Per-frame top-level calls ─────────────────────────────────────────

        /// <summary>Decay transient lights. Called by the engine each frame.</summary>
        public void Update(float dt)
        {
            for (int i = 0; i < MAX_TRANSIENT; i++)
            {
                ref var s = ref _transients[i];
                if (!s.Light.Enabled) continue;
                s.TimeLeft -= dt;
                if (s.TimeLeft <= 0f) { s.Light.Enabled = false; continue; }
                s.Light.Intensity = s.InitialIntensity * (s.TimeLeft / (s.TimeLeft + 0.001f));
                s.Light.Intensity *= (1f - dt * 1.5f);
                if (s.Light.Intensity < 0f) s.Light.Intensity = 0f;
            }
        }

        /// <summary>
        /// Copy a UI-editable LightingConfig snapshot into the live engine.
        /// The engine calls this each frame (before BeginFrame) so every game gets
        /// configured lighting without touching the config itself.
        /// </summary>
        public void ApplyConfig(LightingConfig cfg)
        {
            Enabled          = cfg.Enabled;
            AmbientIntensity = cfg.Ambient;
            GlobalBrightness = cfg.Brightness;

            SunLight.Enabled = cfg.SunEnabled;
            // Normalise the direction — the directional N·L path assumes a unit
            // vector, but the UI lets each axis range freely. Fall back to straight
            // down if the user zeroed all three components.
            float sx = cfg.SunDirX, sy = cfg.SunDirY, sz = cfg.SunDirZ;
            float slen = MathF.Sqrt(sx*sx + sy*sy + sz*sz);
            if (slen < 1e-6f) { sx = 0f; sy = -1f; sz = 0f; slen = 1f; }
            SunLight.DirX = sx / slen; SunLight.DirY = sy / slen; SunLight.DirZ = sz / slen;
            SunLight.Color = cfg.SunColor; SunLight.Intensity = cfg.SunIntensity;

            int n = Math.Min(MAX_SCENE_LIGHTS, cfg.Spots.Length);
            for (int i = 0; i < n; i++)
            {
                var dst = SceneLights[i];
                var src = cfg.Spots[i];
                dst.Type      = LightType.Point;
                dst.Enabled   = src.Enabled;
                dst.X         = src.X;
                dst.Y         = src.Y;
                dst.Z         = src.Z;
                dst.Radius    = src.Radius;
                dst.Intensity = src.Intensity;
                dst.Color     = src.Color;
            }
        }

        /// <summary>Collect active lights and rebuild projection bases. Call once at start of Draw().</summary>
        public void BeginFrame()
        {
            FrameId++;
            _numActive = 0;
            for (int i = 0; i < MAX_SCENE_LIGHTS && _numActive < MAX_ACTIVE; i++)
                if (SceneLights[i] is { Enabled: true }) _active[_numActive++] = SceneLights[i];
            if (SunLight.Enabled && _numActive < MAX_ACTIVE) _active[_numActive++] = SunLight;
            for (int i = 0; i < MAX_TRANSIENT && _numActive < MAX_ACTIVE; i++)
                if (_transients[i].Light.Enabled && _transients[i].TimeLeft > 0f)
                    _active[_numActive++] = _transients[i].Light;

            const float inv255 = 1f / 255f;
            for (int k = 0; k < _numActive; k++)
            {
                int c = _active[k].Color;
                _activeLR[k] = ((c >> 16) & 0xFF) * inv255;
                _activeLG[k] = ((c >>  8) & 0xFF) * inv255;
                _activeLB[k] = ( c        & 0xFF) * inv255;
            }
            // Build orthonormal bases for shadow projection
            for (int k = 0; k < _numActive; k++)
            {
                var ls = _active[k];
                float fx, fy, fz;
                if (ls.Type == LightType.Directional) { fx = ls.DirX; fy = ls.DirY; fz = ls.DirZ; }
                else
                {
                    fx = -ls.X; fy = -ls.Y; fz = -ls.Z;
                    float len = MathF.Sqrt(fx*fx + fy*fy + fz*fz);
                    if (len < 1e-6f) { fy = -1f; fx = fz = 0f; } else { fx/=len; fy/=len; fz/=len; }
                }
                _fwdX[k] = fx; _fwdY[k] = fy; _fwdZ[k] = fz;
                float rx = -fy, ry = fx, rlen = MathF.Sqrt(rx*rx + ry*ry);
                if (rlen < 1e-6f) { rx = 1f; ry = 0f; } else { rx/=rlen; ry/=rlen; }
                _rgtX[k] = rx; _rgtY[k] = ry; _rgtZ[k] = 0f;
                float ux=fy*0-fz*ry, uy=fz*rx-fx*0, uz=fx*ry-fy*rx;
                float ulen = MathF.Sqrt(ux*ux+uy*uy+uz*uz);
                if (ulen > 1e-6f) { ux/=ulen; uy/=ulen; uz/=ulen; }
                _upX[k] = ux; _upY[k] = uy; _upZ[k] = uz;
            }
        }

        // ── Per-model calls ───────────────────────────────────────────────────

        /// <summary>Clear 6-face shell maps. Call before Pass 1 of each model.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginModel() => Array.Fill(_shell, float.MaxValue);

        /// <summary>Submit a voxel to all 6 shell depth maps (Pass 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SubmitToShells(float wx, float wy, float wz)
        {
            ShellMin(0,  wy,  wz,  wx); ShellMin(1, -wy,  wz, -wx);
            ShellMin(2,  wx,  wz,  wy); ShellMin(3, -wx,  wz, -wy);
            ShellMin(4,  wx,  wy,  wz); ShellMin(5, -wx,  wy, -wz);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ShellMin(int face, float u, float v, float d)
        {
            int px = (int)((u + SHELL_EXTENT) * _sHRoE + 0.5f);
            int py = (int)((v + SHELL_EXTENT) * _sHRoE + 0.5f);
            if ((uint)px >= (uint)SHELL_RES || (uint)py >= (uint)SHELL_RES) return;
            int idx = face * SHELL_RES * SHELL_RES + py * SHELL_RES + px;
            if (d < _shell[idx]) _shell[idx] = d;
        }

        /// <summary>Returns true if the point is on the exterior shell (Pass 2 gate).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsExterior(float wx, float wy, float wz)
        {
            return ShellFront(0,  wy,  wz,  wx) || ShellFront(1, -wy,  wz, -wx)
                || ShellFront(2,  wx,  wz,  wy) || ShellFront(3, -wx,  wz, -wy)
                || ShellFront(4,  wx,  wy,  wz) || ShellFront(5, -wx,  wy, -wz);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ShellFront(int face, float u, float v, float d)
        {
            int px = (int)((u + SHELL_EXTENT) * _sHRoE + 0.5f);
            int py = (int)((v + SHELL_EXTENT) * _sHRoE + 0.5f);
            if ((uint)px >= (uint)SHELL_RES || (uint)py >= (uint)SHELL_RES) return true;
            return d <= _shell[face * SHELL_RES * SHELL_RES + py * SHELL_RES + px] + SHELL_BIAS;
        }

        /// <summary>
        /// Compute the final lit colour for a surface point. Returns 0xRRGGBB.
        /// nx/ny/nz = surface normal (unit vector pointing outward from the surface).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int QueryColor(float wx, float wy, float wz,
                              float nx, float ny, float nz, int baseColor)
        {
            if (!Enabled) return baseColor;   // master toggle off — flat passthrough

            float baseR = ((baseColor >> 16) & 0xFF) * (1f / 255f);
            float baseG = ((baseColor >>  8) & 0xFF) * (1f / 255f);
            float baseB = ( baseColor        & 0xFF) * (1f / 255f);

            float accumR = AmbientIntensity * baseR;
            float accumG = AmbientIntensity * baseG;
            float accumB = AmbientIntensity * baseB;

            for (int k = 0; k < _numActive; k++)
            {
                var ls = _active[k];
                float ldx, ldy, ldz, att;
                if (ls.Type == LightType.Directional)
                { ldx = -ls.DirX; ldy = -ls.DirY; ldz = -ls.DirZ; att = 1f; }
                else
                {
                    ldx = ls.X - wx; ldy = ls.Y - wy; ldz = ls.Z - wz;
                    float dist = MathF.Sqrt(ldx*ldx + ldy*ldy + ldz*ldz + 1e-9f);
                    ldx /= dist; ldy /= dist; ldz /= dist;
                    float r = dist / ls.Radius;
                    att = 1f / (1f + r * r);
                }
                float ndotl = nx * ldx + ny * ldy + nz * ldz;
                if (ndotl <= 0f) continue;
                float c = ndotl * att * ls.Intensity;
                accumR += _activeLR[k] * c * baseR;
                accumG += _activeLG[k] * c * baseG;
                accumB += _activeLB[k] * c * baseB;
            }

            accumR *= GlobalBrightness; accumG *= GlobalBrightness; accumB *= GlobalBrightness;
            int ir = (int)(accumR * 255f + 0.5f); if (ir > 255) ir = 255;
            int ig = (int)(accumG * 255f + 0.5f); if (ig > 255) ig = 255;
            int ib = (int)(accumB * 255f + 0.5f); if (ib > 255) ib = 255;
            return (ir << 16) | (ig << 8) | ib;
        }
    }
}
