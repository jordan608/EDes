// ═══════════════════════════════════════════════════════════════════════════
//  CadProbe.cs — "which body is the cursor nearest to (or inside)"
//
//  Pure geometry, no camera or placement involved: the cursor position and every
//  body's bounding box are both already in Fusion's own millimetres, so this is
//  a plain point-to-AABB distance comparison. Kept separate from EDesApp so it
//  can be unit-tested directly, the same reason CadPlacement's math lives on its
//  own rather than inline in the renderer.
//
//  AABB distance, not a real point-in-mesh test: a body's bounding box is already
//  computed (MinX..MaxZ) and a mesh test would need to walk every triangle. The
//  cursor is a coarse "point roughly at this part" tool, not a precision pick —
//  the pick list and the legend already exist for an exact selection.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using EDes.Pcb;

namespace EDes.Cad
{
    public static class CadProbe
    {
        /// <summary>Index into `solids` of whichever VISIBLE body's bounding box is nearest
        /// the point (0 if the point is inside it), or -1 if there are no visible bodies.
        /// Hidden bodies (Fusion's own visibility) are skipped — a cursor should not land on
        /// something that is not being drawn.</summary>
        public static int NearestBody(IReadOnlyList<CadSolid> solids, float x, float y, float z)
        {
            int best = -1;
            float bestDist2 = float.MaxValue;

            for (int i = 0; i < solids.Count; i++)
            {
                var s = solids[i];
                if (!s.Visible || s.MaxX <= s.MinX) continue;

                float dx = MathF.Max(0f, MathF.Max(s.MinX - x, x - s.MaxX));
                float dy = MathF.Max(0f, MathF.Max(s.MinY - y, y - s.MaxY));
                float dz = MathF.Max(0f, MathF.Max(s.MinZ - z, z - s.MaxZ));
                float dist2 = dx * dx + dy * dy + dz * dz;

                if (dist2 < bestDist2) { bestDist2 = dist2; best = i; }
                if (dist2 <= 0f) return i;   // inside this body's box: cannot do better
            }
            return best;
        }
    }
}
