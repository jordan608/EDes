// ═══════════════════════════════════════════════════════════════════════════
//  SpriteBurstRenderer.cs — 2D sprite animations rendered as voxel billboards
//
//  Loads a folder of PNG frames (SpriteFrameSet, via SpriteLibrary) and plays
//  it back on one or more BILLBOARD PLANES revolved through the volume, so a
//  cheap 2D animation reads as a real 3D effect from every sweep angle instead
//  of only being visible edge-on from one direction. Good for explosions,
//  muzzle flashes, pickups, impact bursts — anything you'd rather paint once
//  in an image editor than hand-code as procedural voxel geometry.
//
//  How a single pixel becomes 1+ voxels:
//    Each plane is a vertical plane revolved about the up-axis (Z) by a yaw θ:
//      pixel (u, v) → centre + (u·cosθ, u·sinθ, v)
//    so θ=0° is the XZ plane and θ=90° is the YZ plane. BillboardMode picks
//    the plane SET — fewer planes = fewer voxels (cost = pixels × planes):
//      Single   →  0°                (×1, cheapest; thin from the ends)
//      Pair45   → −45°, +45°         (×2, good default)
//      Cross    →  0°, 90° (XZ+YZ)   (×2, even 360° coverage)
//      TriSpoke →  0°, 60°, 120°     (×3, most uniform)
//      Lathe    →  N(u) evenly-spaced angles, N scaled by the pixel's own
//                  radius u (so outer pixels don't thin out per unit arc-
//                  length the way a flat plane count would) — a true revolve,
//                  most voxels, most convincing from every angle.
//
//  Two-tone retint: SpriteFrameSet bakes TWO reference hues per set — one from
//  its brighter pixels, one from its darker pixels (see BaseHueDeg/BaseHueDeg2).
//  Spawn() computes how far each needs to rotate to land on YOUR primary/
//  secondary colour, and every pixel is retinted by a blend of the two
//  weighted by its own brightness (ColorHsv.ShiftHueBlend) — so one grey/white
//  source image reads in whatever two colours you call it with, and near-white
//  "hot" pixels (low saturation) pass through unchanged either way.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes
{
    public enum SpriteBillboardMode { Single, Pair45, Cross, TriSpoke, Lathe }

    public sealed class SpriteBurstRenderer
    {
        private struct ActiveBurst
        {
            public float X, Y, Z;
            public int   SpriteIndex;
            public float Size;         // world-space span multiplier
            public float Timer, Life;  // seconds
            public SpriteBillboardMode Billboard;
            public float HueShiftBright, HueShiftDark;
            public bool  IsActive;
        }

        private const int MAX_BURSTS = 32;
        private readonly ActiveBurst[] _pool = new ActiveBurst[MAX_BURSTS];

        /// <summary>Lathe mode's angular samples per unit radius. Higher = smoother
        /// revolve at the rim, more voxels.</summary>
        public float LatheDensity { get; set; } = 6f;
        /// <summary>Hard backstop so one wide pixel in Lathe mode can't blow the shared
        /// voxel batch on its own regardless of LatheDensity.</summary>
        public int LatheMaxPlanesPerPixel { get; set; } = 36;
        /// <summary>Multiplies every drawn voxel's colour — a global brightness knob.</summary>
        public float BrightnessMult { get; set; } = 1f;

        // Shared voxel batch, flushed once per Draw() call.
        private const int VOX_BATCH = 20_000;
        private readonly float[] _vx = new float[VOX_BATCH];
        private readonly float[] _vy = new float[VOX_BATCH];
        private readonly float[] _vz = new float[VOX_BATCH];
        private readonly int[]   _vc = new int  [VOX_BATCH];
        private int _vn;

        /// <summary>Start a new burst. <paramref name="spriteName"/> is a folder name from
        /// SpriteLibrary.Names (or pass an index directly via SpawnByIndex). Falls back to
        /// secondary==primary (no two-tone split) when secondaryColor is omitted.</summary>
        public void Spawn(float x, float y, float z, string spriteName, float size, float life,
                          int primaryColor, int secondaryColor = -1,
                          SpriteBillboardMode billboard = SpriteBillboardMode.Pair45)
            => SpawnByIndex(x, y, z, SpriteLibrary.IndexOf(spriteName), size, life,
                            primaryColor, secondaryColor, billboard);

        public void SpawnByIndex(float x, float y, float z, int spriteIndex, float size, float life,
                                 int primaryColor, int secondaryColor = -1,
                                 SpriteBillboardMode billboard = SpriteBillboardMode.Pair45)
        {
            if (spriteIndex < 0) return;   // name not found — nothing to play
            int secondary = secondaryColor >= 0 ? secondaryColor : primaryColor;

            int slot = -1;
            for (int i = 0; i < MAX_BURSTS; i++)
                if (!_pool[i].IsActive) { slot = i; break; }
            if (slot < 0) return;   // pool full — drop the new burst rather than steal an old one

            ref var b = ref _pool[slot];
            b.X = x; b.Y = y; b.Z = z;
            b.SpriteIndex = spriteIndex;
            b.Size = size;
            b.Timer = 0f;
            b.Life = MathF.Max(0.05f, life);
            b.Billboard = billboard;
            b.IsActive = true;

            var set = SpriteLibrary.Get(spriteIndex);
            b.HueShiftBright = 0f;
            b.HueShiftDark   = 0f;
            if (set != null)
            {
                static float Norm(float d) => ((d + 180f) % 360f + 360f) % 360f - 180f;
                ColorHsv.RgbToHsv(primaryColor, out float huePrimary, out float satPrimary, out _);
                float shiftBright = satPrimary >= 0.05f ? Norm(huePrimary - set.BaseHueDeg) : 0f;
                ColorHsv.RgbToHsv(secondary, out float hueSecondary, out float satSecondary, out _);
                float shiftDark = satSecondary >= 0.05f ? Norm(hueSecondary - set.BaseHueDeg2) : shiftBright;
                b.HueShiftBright = shiftBright;
                b.HueShiftDark   = shiftDark;
            }
        }

        /// <summary>Advance all active bursts and free any that finished playing.</summary>
        public void Update(float dt)
        {
            for (int i = 0; i < MAX_BURSTS; i++)
            {
                if (!_pool[i].IsActive) continue;
                _pool[i].Timer += dt;
                if (_pool[i].Timer >= _pool[i].Life) _pool[i].IsActive = false;
            }
        }

        /// <summary>Draw every active burst's current frame and flush the voxel batch.
        /// Call once per frame, inside the FrameStart/FrameEnd window.</summary>
        public void Draw(LedHostCS ledHost, ref vxl_state_t vs)
        {
            _vn = 0;
            for (int i = 0; i < MAX_BURSTS; i++)
            {
                if (!_pool[i].IsActive) continue;
                var set = SpriteLibrary.Get(_pool[i].SpriteIndex);
                if (set == null || set.FrameCount == 0) continue;
                float p = Math.Clamp(_pool[i].Timer / _pool[i].Life, 0f, 0.9999f);
                DrawOne(ref _pool[i], p, set);
            }
            FlushVox(ledHost, ref vs);
        }

        private void DrawOne(ref ActiveBurst b, float p, SpriteFrameSet set)
        {
            bool lathe = b.Billboard == SpriteBillboardMode.Lathe;
            float[] yaws = b.Billboard switch
            {
                SpriteBillboardMode.Pair45   => Yaw45,
                SpriteBillboardMode.Cross    => YawCross,
                SpriteBillboardMode.TriSpoke => YawTri,
                _                            => YawSingle,
            };
            int fixedPlanes = yaws.Length;

            int fc  = set.FrameCount;
            int f   = Math.Clamp((int)(p * fc), 0, fc - 1);
            int cnt = set.PixelCount(f);
            float span = b.Size * 2.2f;

            for (int i = 0; i < cnt; i++)
            {
                // Sample the pixel's position/colour exactly ONCE regardless of how many
                // angular slices it revolves into below.
                float u = set.U(f, i) * span;
                float v = set.V(f, i) * span;

                int planes = lathe
                    ? Math.Clamp((int)MathF.Round(1f + MathF.Abs(u) * LatheDensity), 1, LatheMaxPlanesPerPixel)
                    : fixedPlanes;
                if (_vn + planes > VOX_BATCH) return;   // shared batch cap bounds the worst case

                int c = ColorHsv.ShiftHueBlend(set.Col(f, i), b.HueShiftDark, b.HueShiftBright);

                if (lathe)
                {
                    // Rotate by incremental complex multiplication (2 trig calls total for
                    // this pixel) instead of calling Cos/Sin per slice.
                    float step = 2f * MathF.PI / planes;
                    float cs = MathF.Cos(step), sn = MathF.Sin(step);
                    float ct = 1f, st = 0f;
                    for (int pl = 0; pl < planes; pl++)
                    {
                        _vx[_vn] = b.X + u * ct;
                        _vy[_vn] = b.Y + u * st;
                        _vz[_vn] = b.Z + v;
                        _vc[_vn] = c;
                        _vn++;
                        float nct = ct * cs - st * sn, nst = ct * sn + st * cs;
                        ct = nct; st = nst;
                    }
                }
                else
                {
                    for (int pl = 0; pl < planes; pl++)
                    {
                        float ct = MathF.Cos(yaws[pl]), st = MathF.Sin(yaws[pl]);
                        _vx[_vn] = b.X + u * ct;
                        _vy[_vn] = b.Y + u * st;
                        _vz[_vn] = b.Z + v;
                        _vc[_vn] = c;
                        _vn++;
                    }
                }
            }
        }

        // Billboard plane yaw sets (radians), revolved about the up-axis. See header.
        private static readonly float[] YawSingle = { 0f };
        private static readonly float[] Yaw45     = { -MathF.PI / 4f, MathF.PI / 4f };
        private static readonly float[] YawCross  = { 0f, MathF.PI / 2f };
        private static readonly float[] YawTri    = { 0f, MathF.PI / 3f, 2f * MathF.PI / 3f };

        private void FlushVox(LedHostCS ledHost, ref vxl_state_t vs)
        {
            if (_vn > 0)
            {
                if (BrightnessMult != 1f)
                    for (int i = 0; i < _vn; i++) _vc[i] = Bri(_vc[i]);
                ledHost.DrawVox_Batch(ref vs, ref _vx[0], ref _vy[0], ref _vz[0], ref _vc[0], _vn, 0);
            }
            _vn = 0;
        }

        private int Bri(int col)
        {
            float m = BrightnessMult;
            if (m == 1f || col == 0) return col;
            int r = Math.Clamp((int)(((col >> 16) & 0xFF) * m), 0, 255);
            int g = Math.Clamp((int)(((col >> 8)  & 0xFF) * m), 0, 255);
            int b = Math.Clamp((int)(( col        & 0xFF) * m), 0, 255);
            return (r << 16) | (g << 8) | b;
        }
    }
}
