// ═══════════════════════════════════════════════════════════════════════════
//  VoxelFont.cs — selectable HUD text renderer
//
//  The Voxon SDK's built-in text (vxl_printalph / LedHostCS.DrawTxt) is a single
//  fixed alphabet with no typeface option. To let the player choose a font, this
//  renders text three ways behind one entry point:
//
//    Classic — the SDK's built-in vector alphabet (unchanged look, free).
//    Blocky  — a 5×7 voxel bitmap font (our own glyph table).
//    Bold    — the same glyph table drawn with thicker cells.
//
//  Blocky/Bold draw in world space (any size / position / colour / density) as a
//  single voxel batch, so they billboard and scale exactly like the rest of the
//  HUD. Draw at a fixed Y (e.g. y=0.1) to build a HUD plane every entity draws
//  its own text onto — score, labels, callouts — all through this one entry
//  point, so per-game font/thickness settings apply everywhere at once.
//
//  Usage:
//    VoxelFont.Thickness = 1.5f;   // once per frame, from a settings toggle
//    VoxelFont.Draw(ledHost, ref vs, HudFont.Bold,
//        new point3d(-1.5f, 0.1f, 1.0f), size: 0.12f, col: 0x00FFCC, "SCORE 1200");
// ═══════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using Voxon;

namespace EDes
{
    public enum HudFont { Classic = 0, Blocky = 1, Bold = 2 }

    public static class VoxelFont
    {
        /// <summary>Global HUD text size multiplier. Shared storage set once per frame;
        /// applied by the caller wrappers (GameManager.UiText, GlitchEffect.Emit) so it
        /// scales every font path exactly once. Draw() itself does NOT apply it.</summary>
        public static float SizeScale = 1f;

        /// <summary>Stroke weight for the voxel fonts — more sub-voxels per glyph cell.
        /// Set once per frame from the Text settings. No effect on the Classic SDK font.</summary>
        public static float Thickness = 1f;

        /// <summary>Readable floor for the glyph size — Draw() clamps up to this so text
        /// never renders too small to read. 0 = no clamp (caller's size is used as-is).</summary>
        public static float MinTextRadius = 0f;

        // Pre-allocated batch scratch (avoids per-frame GC). Sized for a long line
        // at the densest (Bold, high-thickness) fill.
        private const int MAX_VOX = 120_000;
        private static readonly float[] _bx = new float[MAX_VOX];
        private static readonly float[] _by = new float[MAX_VOX];
        private static readonly float[] _bz = new float[MAX_VOX];
        private static readonly int[]   _bc = new int[MAX_VOX];

        /// <summary>Draw text at <paramref name="pos"/> (top-left origin) in the chosen
        /// font. <paramref name="size"/> is the glyph size in world units (matches the
        /// SDK's rad). Text extends +x (right) and +z (down).</summary>
        public static void Draw(LedHostCS ledHost, ref vxl_state_t vs, HudFont font,
                                point3d pos, float size, int col, string text)
        {
            if (string.IsNullOrEmpty(text) || col == 0) return;
            if (size < MinTextRadius) size = MinTextRadius;

            if (font == HudFont.Classic)
            {
                point3d r = new point3d(size, 0f, 0f);
                point3d d = new point3d(0f, 0f, size);
                ledHost.DrawTxt(ref vs, ref pos, ref r, ref d, size, col, text);
                return;
            }

            DrawVoxelText(ledHost, ref vs, font, pos, size, col, text);
        }

        // ── Voxel bitmap rendering ────────────────────────────────────────────
        // The glyph walk lives in EmitGlyphs and hands every voxel to a sink, so
        // the same code serves two callers: Draw() (fills the local batch and
        // flushes it with one DrawVox_Batch) and Emit() (lets an app route HUD
        // text through its own bounds-clipping / budget-limited batch instead).
        private static int _emitN;
        private static int _emitCol;

        private static readonly System.Func<float, float, float, bool> LocalSink = (x, y, z) =>
        {
            if (_emitN >= MAX_VOX) return false;
            _bx[_emitN] = x; _by[_emitN] = y; _bz[_emitN] = z; _bc[_emitN] = _emitCol;
            _emitN++;
            return true;
        };

        private static void DrawVoxelText(LedHostCS ledHost, ref vxl_state_t vs,
                                          HudFont font, point3d pos, float size, int col, string text)
        {
            _emitN   = 0;
            _emitCol = col;
            EmitGlyphs(font, pos, size, text, LocalSink);
            if (_emitN > 0)
                ledHost.DrawVox_Batch(ref vs, ref _bx[0], ref _by[0], ref _bz[0], ref _bc[0], _emitN, 0);
        }

        /// <summary>Emit the voxels of a string through a caller-supplied sink instead
        /// of drawing it, so an app can route HUD text through its own batch (bounds
        /// clipping, voxel budget, one native call for the whole frame). Return false
        /// from <paramref name="add"/> to stop early. Classic is the SDK's own vector
        /// font and cannot be routed this way — it falls back to the Blocky glyphs.</summary>
        public static void Emit(HudFont font, point3d pos, float size, string text,
                                System.Func<float, float, float, bool> add)
            => EmitGlyphs(font == HudFont.Classic ? HudFont.Blocky : font, pos, size, text, add);

        private static void EmitGlyphs(HudFont font, point3d pos, float size, string text,
                                       System.Func<float, float, float, bool> add)
        {
            float cellW   = size * 0.15f;          // width of one glyph cell (5 across)
            float cellH   = size * 0.18f;          // height of one glyph cell (7 down)
            float advance = size * 0.95f;          // per-character step in +x
            // Fill resolution per lit cell — higher = denser/bolder stroke. Bold adds
            // one step over Blocky, and the Thickness knob scales both.
            int   baseSub = font == HudFont.Bold ? 3 : 2;
            int   nsub    = System.Math.Clamp((int)System.MathF.Round(baseSub * Thickness), 1, 6);
            float subW    = cellW / nsub;
            float subH    = cellH / nsub;

            float x0 = pos.x;
            for (int ci = 0; ci < text.Length; ci++)
            {
                char ch = char.ToUpperInvariant(text[ci]);
                float charX = x0 + ci * advance;
                if (!Glyphs.TryGetValue(ch, out var rows)) continue;   // space / unknown → gap

                for (int row = 0; row < 7; row++)
                {
                    string pattern = rows[row];
                    for (int colc = 0; colc < 5 && colc < pattern.Length; colc++)
                    {
                        if (pattern[colc] != '#') continue;
                        float cx = charX + colc * cellW;
                        float cz = pos.z + row * cellH;
                        // Fill the cell with an nsub×nsub block so adjacent lit cells
                        // read as a solid stroke rather than sparse dots.
                        for (int sx = 0; sx < nsub; sx++)
                        for (int sz = 0; sz < nsub; sz++)
                        {
                            if (!add(cx + sx * subW, pos.y, cz + sz * subH)) return;
                        }
                    }
                }
            }
        }

        /// <summary>Approximate rendered width of a string in world units (for centring
        /// or right-aligning). Matches the Blocky/Bold advance.</summary>
        public static float MeasureWidth(string text, float size)
            => string.IsNullOrEmpty(text) ? 0f : text.Length * size * 0.95f;

        // ── 5×7 glyph table (uppercase + digits + the punctuation the game uses) ──
        // '#' = lit cell. Unknown chars (incl. space) render as a blank advance.
        private static readonly Dictionary<char, string[]> Glyphs = new()
        {
            ['0'] = new[]{ ".###.","#...#","#..##","#.#.#","##..#","#...#",".###." },
            ['1'] = new[]{ "..#..",".##..","..#..","..#..","..#..","..#..",".###." },
            ['2'] = new[]{ ".###.","#...#","....#","...#.","..#..",".#...","#####" },
            ['3'] = new[]{ "#####","...#.","..#..","...#.","....#","#...#",".###." },
            ['4'] = new[]{ "...#.","..##.",".#.#.","#..#.","#####","...#.","...#." },
            ['5'] = new[]{ "#####","#....","####.","....#","....#","#...#",".###." },
            ['6'] = new[]{ "..##.",".#...","#....","####.","#...#","#...#",".###." },
            ['7'] = new[]{ "#####","....#","...#.","..#..",".#...",".#...",".#..." },
            ['8'] = new[]{ ".###.","#...#","#...#",".###.","#...#","#...#",".###." },
            ['9'] = new[]{ ".###.","#...#","#...#",".####","....#","...#.",".##.." },

            ['A'] = new[]{ ".###.","#...#","#...#","#####","#...#","#...#","#...#" },
            ['B'] = new[]{ "####.","#...#","#...#","####.","#...#","#...#","####." },
            ['C'] = new[]{ ".###.","#...#","#....","#....","#....","#...#",".###." },
            ['D'] = new[]{ "###..","#..#.","#...#","#...#","#...#","#..#.","###.." },
            ['E'] = new[]{ "#####","#....","#....","###..","#....","#....","#####" },
            ['F'] = new[]{ "#####","#....","#....","###..","#....","#....","#...." },
            ['G'] = new[]{ ".###.","#...#","#....","#.###","#...#","#...#",".###." },
            ['H'] = new[]{ "#...#","#...#","#...#","#####","#...#","#...#","#...#" },
            ['I'] = new[]{ ".###.","..#..","..#..","..#..","..#..","..#..",".###." },
            ['J'] = new[]{ "..###","...#.","...#.","...#.","#..#.","#..#.",".##.." },
            ['K'] = new[]{ "#...#","#..#.","#.#..","##...","#.#..","#..#.","#...#" },
            ['L'] = new[]{ "#....","#....","#....","#....","#....","#....","#####" },
            ['M'] = new[]{ "#...#","##.##","#.#.#","#.#.#","#...#","#...#","#...#" },
            ['N'] = new[]{ "#...#","#...#","##..#","#.#.#","#..##","#...#","#...#" },
            ['O'] = new[]{ ".###.","#...#","#...#","#...#","#...#","#...#",".###." },
            ['P'] = new[]{ "####.","#...#","#...#","####.","#....","#....","#...." },
            ['Q'] = new[]{ ".###.","#...#","#...#","#...#","#.#.#","#..#.",".##.#" },
            ['R'] = new[]{ "####.","#...#","#...#","####.","#.#..","#..#.","#...#" },
            ['S'] = new[]{ ".###.","#...#","#....",".###.","....#","#...#",".###." },
            ['T'] = new[]{ "#####","..#..","..#..","..#..","..#..","..#..","..#.." },
            ['U'] = new[]{ "#...#","#...#","#...#","#...#","#...#","#...#",".###." },
            ['V'] = new[]{ "#...#","#...#","#...#","#...#","#...#",".#.#.","..#.." },
            ['W'] = new[]{ "#...#","#...#","#...#","#.#.#","#.#.#","##.##","#...#" },
            ['X'] = new[]{ "#...#","#...#",".#.#.","..#..",".#.#.","#...#","#...#" },
            ['Y'] = new[]{ "#...#","#...#",".#.#.","..#..","..#..","..#..","..#.." },
            ['Z'] = new[]{ "#####","....#","...#.","..#..",".#...","#....","#####" },

            [' '] = new[]{ ".....",".....",".....",".....",".....",".....","....." },
            ['!'] = new[]{ "..#..","..#..","..#..","..#..","..#..",".....","..#.." },
            [':'] = new[]{ ".....","..#..","..#..",".....","..#..","..#..","....." },
            ['.'] = new[]{ ".....",".....",".....",".....",".....","..#..","..#.." },
            [','] = new[]{ ".....",".....",".....",".....","..#..","..#..",".#..." },
            ['-'] = new[]{ ".....",".....",".....","#####",".....",".....","....." },
            ['+'] = new[]{ ".....","..#..","..#..","#####","..#..","..#..","....." },
            ['/'] = new[]{ "....#","....#","...#.","..#..",".#...","#....","#...." },
            ['('] = new[]{ "...#.","..#..",".#...",".#...",".#...","..#..","...#." },
            [')'] = new[]{ ".#...","..#..","...#.","...#.","...#.","..#..",".#..." },
            ['%'] = new[]{ "##..#","##..#","...#.","..#..",".#...","#..##","#..##" },
            ['×'] = new[]{ ".....","#...#",".#.#.","..#..",".#.#.","#...#","....." },
        };
    }
}
