// ═══════════════════════════════════════════════════════════════════════════
//  CadLight.cs — how lit is this surface, facing this way
//
//  One implementation, because three call sites now need it: the PCB viewer's edge
//  pass, its surface pass, and the Fusion scene. Two of those already had to agree
//  or a model would shade differently depending on which pass drew it; adding a
//  third renderer with its own copy is how that guarantee gets quietly lost.
//
//  A POINT light rather than only a direction, because a directional light gives
//  every face on a board the same L — so two identical parts at opposite corners
//  shade identically and the whole thing reads flat. A point light gives each face
//  its own direction AND its own distance, which is what makes one side of a
//  component brighter than the other.
//
//  Callers differ in one respect only: whether the normal's SIGN counts. An edge is
//  shared by two faces pointing opposite ways so its sign carries nothing, while a
//  face has one outward normal and the sign is exactly what makes it turn away from
//  the light. That is a property of what is being shaded, not of the light, so it is
//  an argument to Shade rather than a second light type.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;

namespace EDes.Pcb
{
    public readonly struct CadLight
    {
        public readonly bool  Point;
        public readonly float X, Y, Z;        // mm in the model frame
        public readonly float InvRange2;      // 0 = no distance falloff
        public readonly float Ambient;
        public readonly bool  On;

        private CadLight(bool on, bool point, float x, float y, float z,
                         float invRange2, float ambient)
        {
            On = on; Point = point; X = x; Y = y; Z = z;
            InvRange2 = invRange2; Ambient = ambient;
        }

        /// <summary>Off: everything sits at the ambient floor.</summary>
        public static CadLight Off(float ambient)
            => new CadLight(false, false, 0, 0, 1, 0f, Math.Clamp(ambient, 0f, 1f));

        /// <summary>A directional light from an angle. Normalised once here rather than per
        /// edge, and a zero vector falls back to lighting from above instead of dividing by
        /// zero and blackening the model.</summary>
        public static CadLight Directional(float dx, float dy, float dz, float ambient)
        {
            float l = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (l < 1e-6f) { dx = 0f; dy = 0f; dz = 1f; l = 1f; }
            return new CadLight(true, false, dx / l, dy / l, dz / l, 0f,
                                Math.Clamp(ambient, 0f, 1f));
        }

        /// <summary>A point light at a position in model millimetres, with a half-strength
        /// distance. A range of 0 or less disables falloff, leaving direction without
        /// distance — useful when the falloff is fighting a seven-colour palette and only the
        /// directional cue is wanted.</summary>
        public static CadLight AtPoint(float x, float y, float z, float rangeMm, float ambient)
        {
            float inv2 = rangeMm > 1e-3f ? 1f / (rangeMm * rangeMm) : 0f;
            return new CadLight(true, true, x, y, z, inv2, Math.Clamp(ambient, 0f, 1f));
        }

        /// <summary>Build from a board's extents and the fraction-based settings.
        ///
        /// The position is in fractions of the board's own half-extents and tallest solid, so
        /// one setting aims the same way on a 16 mm sensor board and a 300 mm backplane.</summary>
        public static CadLight ForBoard(PcbBoard board, bool on, float ambient, bool point,
                                        float dx, float dy, float dz,
                                        float fx, float fy, float fz, float rangeFrac)
        {
            if (!on) return Off(ambient);
            if (!point) return Directional(dx, dy, dz, ambient);

            float halfW = MathF.Max(0.5f, board.WidthMm  * 0.5f);
            float halfH = MathF.Max(0.5f, board.HeightMm * 0.5f);
            float tall  = MathF.Max(1f, Tallest(board.Solids));

            float diag = MathF.Sqrt(halfW * halfW * 4f + halfH * halfH * 4f);

            return AtPoint(board.CentreX + fx * halfW,
                           board.CentreY + fy * halfH,
                           fz * tall,
                           rangeFrac * diag,
                           ambient);
        }

        /// <summary>Build from a bare list of solids, for a scene with no board under it.
        ///
        /// Same fraction semantics, with the extents taken from the solids themselves — so
        /// the Fusion scene aims its light the same way the PCB viewer does without needing a
        /// PcbBoard it does not have.</summary>
        public static CadLight ForSolids(IReadOnlyList<CadSolid> solids, bool on, float ambient,
                                         bool point, float dx, float dy, float dz,
                                         float fx, float fy, float fz, float rangeFrac)
        {
            if (!on) return Off(ambient);
            if (!point) return Directional(dx, dy, dz, ambient);

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (var s in solids)
            {
                if (!s.Visible || s.MaxX <= s.MinX) continue;
                if (s.MinX < minX) minX = s.MinX;
                if (s.MinY < minY) minY = s.MinY;
                if (s.MaxX > maxX) maxX = s.MaxX;
                if (s.MaxY > maxY) maxY = s.MaxY;
                if (s.MaxZ > maxZ) maxZ = s.MaxZ;
            }
            if (maxX <= minX) return Directional(dx, dy, dz, ambient);

            float halfW = MathF.Max(0.5f, (maxX - minX) * 0.5f);
            float halfH = MathF.Max(0.5f, (maxY - minY) * 0.5f);
            float tall  = MathF.Max(1f, maxZ);
            float diag  = MathF.Sqrt(halfW * halfW * 4f + halfH * halfH * 4f);

            return AtPoint((minX + maxX) * 0.5f + fx * halfW,
                           (minY + maxY) * 0.5f + fy * halfH,
                           fz * tall,
                           rangeFrac * diag,
                           ambient);
        }

        private static float Tallest(IReadOnlyList<CadSolid> solids)
        {
            float top = 0f;
            foreach (var s in solids)
                if (s.MaxZ > top && s.MaxZ < 1e6f) top = s.MaxZ;
            return top;
        }

        /// <summary>0..1 shade for a surface at (px,py,pz) mm facing (nx,ny,nz).
        /// twoSided ignores the normal's sign; see the header.</summary>
        public float Shade(float nx, float ny, float nz,
                           float px, float py, float pz, bool twoSided)
        {
            if (!On) return Ambient;

            float lx = X, ly = Y, lz = Z, att = 1f;
            if (Point)
            {
                lx = X - px; ly = Y - py; lz = Z - pz;
                float d2 = lx * lx + ly * ly + lz * lz;
                if (d2 < 1e-9f) return 1f;                 // sitting inside the lamp
                float d = MathF.Sqrt(d2);
                lx /= d; ly /= d; lz /= d;
                if (InvRange2 > 0f) att = 1f / (1f + d2 * InvRange2);
            }

            float ndl = nx * lx + ny * ly + nz * lz;
            ndl = twoSided ? MathF.Abs(ndl) : MathF.Max(0f, ndl);
            return Ambient + (1f - Ambient) * ndl * att;
        }
    }
}
