// ═══════════════════════════════════════════════════════════════════════════
//  GerberParser.cs — RS-274X (extended Gerber) reader
//
//  Scope: the subset that CAD tools actually emit for fabrication, which is
//  what matters for loading real boards:
//
//    %FSLAXnnYnn*%   coordinate format (integer/decimal digit counts)
//    %MOMM*% / %MOIN*%   units (everything is converted to mm on the way in)
//    %ADDnn<C|R|O|P>,...*%   aperture definitions (circle/rect/obround/polygon)
//    %LPD*% / %LPC*%     polarity — clear polarity is recorded as a note, not
//                        rendered as a knockout (a volumetric display has no
//                        painter's-algorithm layering to subtract from)
//    Dnn*                select aperture
//    G01 / G02 / G03     linear, CW arc, CCW arc
//    G74 / G75           single / multi-quadrant arc mode
//    G36 / G37           region (polygon fill) start/end
//    X..Y..I..J..D01/2/3 draw / move / flash
//    M02                 end of file
//
//  Deliberately NOT supported: aperture macros (%AM), step-and-repeat (%SR),
//  and block apertures (%AB). Each is recorded as a note so the operator knows
//  something was skipped rather than silently seeing a board with holes in it.
//  Arcs are flattened to segments at ARC_SEG_DEG, since the renderer only ever
//  draws point-sampled lines anyway.
//
//  Coordinate handling is the part that silently corrupts boards if you get it
//  wrong: Gerber coordinates are integers scaled by the FS decimal count, are
//  MODAL (an omitted X keeps the previous X), and may be either absolute (G90,
//  universal in practice) or incremental (G91, legacy).
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;
using System.IO;

namespace EDes.Pcb
{
    public static class GerberParser
    {
        private const float ARC_SEG_DEG = 6f;      // arc flattening resolution
        private const int   MAX_APERTURE = 1000;

        private struct Aperture
        {
            public PadShape Shape;
            public float    W, H;      // mm
            public bool     Defined;
        }

        /// <summary>Parse one Gerber file into the given layer. Returns false if the
        /// file did not look like Gerber at all.</summary>
        public static bool Parse(string path, PcbLayer layer, PcbBoard board)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                board.Notes.Add($"{Path.GetFileName(path)}: unreadable ({ex.GetType().Name})");
                return false;
            }

            // ── Parser state ──────────────────────────────────────────────────
            int   intDigits = 2, decDigits = 4;     // %FSLAX24Y24*% is the common default
            float unitScale = 25.4f;                // inch until told otherwise
            bool  sawFormat = false, sawUnits = false, sawAnyCommand = false;
            bool  absolute  = true;
            int   interp    = 1;                    // 1=linear, 2=CW arc, 3=CCW arc
            bool  multiQuadrant = true;             // G75 (modern default)
            bool  inRegion  = false;
            bool  clearPolarity = false, notedClear = false, notedMacro = false;

            var apertures = new Aperture[MAX_APERTURE];
            int current   = -1;
            float x = 0, y = 0;
            PcbRegion? region = null;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                // ── Extended commands: %...% (may be on one line) ─────────────
                if (line.StartsWith("%"))
                {
                    string cmd = line.Trim('%').TrimEnd('*');

                    if (cmd.StartsWith("FS"))
                    {
                        // e.g. FSLAX34Y34 — L=leading zeros omitted, A=absolute
                        absolute = !cmd.Contains('I');
                        int ix = cmd.IndexOf('X');
                        if (ix >= 0 && ix + 2 < cmd.Length &&
                            char.IsDigit(cmd[ix + 1]) && char.IsDigit(cmd[ix + 2]))
                        {
                            intDigits = cmd[ix + 1] - '0';
                            decDigits = cmd[ix + 2] - '0';
                            sawFormat = true;
                        }
                        continue;
                    }
                    if (cmd.StartsWith("MO"))
                    {
                        unitScale = cmd.Contains("MM") ? 1f : 25.4f;
                        sawUnits  = true;
                        continue;
                    }
                    if (cmd.StartsWith("AD"))
                    {
                        ParseAperture(cmd, apertures, unitScale, board, path);
                        continue;
                    }
                    if (cmd.StartsWith("AM") || cmd.StartsWith("AB"))
                    {
                        if (!notedMacro)
                        {
                            board.Notes.Add($"{Path.GetFileName(path)}: aperture macros/blocks skipped");
                            notedMacro = true;
                        }
                        continue;
                    }
                    if (cmd.StartsWith("SR"))
                    {
                        board.Notes.Add($"{Path.GetFileName(path)}: step-and-repeat ignored");
                        continue;
                    }
                    if (cmd.StartsWith("LP"))
                    {
                        clearPolarity = cmd.EndsWith("C");
                        if (clearPolarity && !notedClear)
                        {
                            board.Notes.Add($"{Path.GetFileName(path)}: clear polarity drawn as normal");
                            notedClear = true;
                        }
                        continue;
                    }
                    continue;       // any other extended command: ignore
                }

                // ── Body: one or more commands terminated by * ────────────────
                foreach (string tokenRaw in line.Split('*'))
                {
                    string token = tokenRaw.Trim();
                    if (token.Length == 0) continue;

                    if (token.StartsWith("M02") || token.StartsWith("M0")) { }   // end / stop

                    // G-codes may prefix a coordinate in the same token.
                    int gi;
                    while ((gi = token.IndexOf('G')) == 0 && token.Length >= 3 &&
                           char.IsDigit(token[1]) && char.IsDigit(token[2]))
                    {
                        int g = (token[1] - '0') * 10 + (token[2] - '0');
                        token = token.Substring(3);
                        switch (g)
                        {
                            case 1:  interp = 1; break;
                            case 2:  interp = 2; break;
                            case 3:  interp = 3; break;
                            case 36: inRegion = true;  region = new PcbRegion(); break;
                            case 37:
                                inRegion = false;
                                if (region != null && region.Count > 2) layer.Regions.Add(region);
                                region = null;
                                break;
                            case 74: multiQuadrant = false; break;
                            case 75: multiQuadrant = true;  break;
                            case 90: absolute = true;  break;
                            case 91: absolute = false; break;
                        }
                        sawAnyCommand = true;
                    }
                    if (token.Length == 0) continue;

                    // Aperture selection: Dnn (nn >= 10)
                    if (token[0] == 'D' && int.TryParse(token.AsSpan(1), out int dsel) && dsel >= 10)
                    {
                        if (dsel < MAX_APERTURE) current = dsel;
                        sawAnyCommand = true;
                        continue;
                    }

                    // Coordinate + operation
                    float nx = x, ny = y, ci = 0, cj = 0;
                    int   op = 0;
                    bool  gotCoord = false;

                    int p = 0;
                    while (p < token.Length)
                    {
                        char c = token[p];
                        if (c == 'X' || c == 'Y' || c == 'I' || c == 'J')
                        {
                            int s = ++p;
                            while (p < token.Length && (char.IsDigit(token[p]) ||
                                   token[p] == '-' || token[p] == '+' || token[p] == '.')) p++;
                            float v = DecodeCoord(token.AsSpan(s, p - s), intDigits, decDigits) * unitScale;
                            switch (c)
                            {
                                case 'X': nx = absolute ? v : x + v; gotCoord = true; break;
                                case 'Y': ny = absolute ? v : y + v; gotCoord = true; break;
                                case 'I': ci = v; break;
                                case 'J': cj = v; break;
                            }
                            continue;
                        }
                        if (c == 'D')
                        {
                            int s = ++p;
                            while (p < token.Length && char.IsDigit(token[p])) p++;
                            int.TryParse(token.AsSpan(s, p - s), out op);
                            continue;
                        }
                        p++;
                    }

                    if (op == 0 && !gotCoord) continue;
                    sawAnyCommand = true;

                    float width = current >= 0 && apertures[current].Defined
                                ? MathF.Max(apertures[current].W, 0.01f)
                                : 0.15f;    // sane default so unknown apertures stay visible

                    switch (op)
                    {
                        case 1:     // D01 — draw
                            if (inRegion && region != null)
                            {
                                if (region.Count == 0) { region.X.Add(x); region.Y.Add(y); }
                                AddArcOrLine(region, x, y, nx, ny, ci, cj, interp, multiQuadrant);
                            }
                            else if (interp == 1)
                            {
                                AddSeg(layer, x, y, nx, ny, width);
                            }
                            else
                            {
                                EmitArc(layer, x, y, nx, ny, ci, cj, interp == 2, multiQuadrant, width);
                            }
                            x = nx; y = ny;
                            break;

                        case 2:     // D02 — move
                            if (inRegion && region != null && region.Count > 2)
                            {
                                layer.Regions.Add(region);
                                region = new PcbRegion();
                            }
                            x = nx; y = ny;
                            break;

                        case 3:     // D03 — flash the current aperture
                            if (current >= 0 && apertures[current].Defined)
                            {
                                var ap = apertures[current];
                                layer.Pads.Add(new PcbPad(nx, ny, ap.W, ap.H, ap.Shape));
                            }
                            x = nx; y = ny;
                            break;

                        default:    // coordinate with no operator: modal move
                            x = nx; y = ny;
                            break;
                    }
                }
            }

            if (region != null && region.Count > 2) layer.Regions.Add(region);

            if (!sawAnyCommand) return false;
            if (!sawFormat) board.Notes.Add($"{Path.GetFileName(path)}: no %FS — assumed {intDigits}.{decDigits}");
            if (!sawUnits)  board.Notes.Add($"{Path.GetFileName(path)}: no %MO — assumed inch");
            return true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void AddSeg(PcbLayer layer, float x0, float y0, float x1, float y1, float w)
        {
            var s = new PcbSeg(x0, y0, x1, y1, w);
            layer.Segs.Add(s);
            layer.TrackLength += s.Length;
            if (w < layer.MinWidth) layer.MinWidth = w;
        }

        /// <summary>Gerber coordinates are integers scaled by the format's decimal
        /// count — unless the file (non-standard but common) wrote a real decimal.</summary>
        private static float DecodeCoord(ReadOnlySpan<char> span, int intDigits, int decDigits)
        {
            if (span.Length == 0) return 0f;
            if (span.IndexOf('.') >= 0)
                return float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out float d)
                     ? d : 0f;

            if (!long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
                return 0f;
            return (float)(raw / Math.Pow(10, decDigits));
        }

        private static void ParseAperture(string cmd, Aperture[] apertures, float unitScale,
                                          PcbBoard board, string path)
        {
            // ADD10C,0.2   |   ADD11R,1.0X2.0   |   ADD12O,1X2   |   ADD13P,2X6
            int i = 2;                                   // skip "AD"
            if (i < cmd.Length && cmd[i] == 'D') i++;
            int numStart = i;
            while (i < cmd.Length && char.IsDigit(cmd[i])) i++;
            if (!int.TryParse(cmd.AsSpan(numStart, i - numStart), out int code) ||
                code < 0 || code >= MAX_APERTURE) return;
            if (i >= cmd.Length) return;

            char shape = cmd[i++];
            string args = i < cmd.Length ? cmd.Substring(i).TrimStart(',') : "";
            var parts = args.Split('X');

            float P(int idx) =>
                idx < parts.Length &&
                float.TryParse(parts[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                    ? v * unitScale : 0f;

            var ap = new Aperture { Defined = true };
            switch (shape)
            {
                case 'C': ap.Shape = PadShape.Circle;  ap.W = ap.H = P(0); break;
                case 'R': ap.Shape = PadShape.Rect;    ap.W = P(0); ap.H = P(1); break;
                case 'O': ap.Shape = PadShape.Obround; ap.W = P(0); ap.H = P(1); break;
                case 'P': ap.Shape = PadShape.Polygon; ap.W = ap.H = P(0); break;
                default:
                    // A macro-defined aperture: keep it visible as a circle of its
                    // first parameter rather than dropping the pad entirely.
                    ap.Shape = PadShape.Circle;
                    ap.W = ap.H = P(0) > 0 ? P(0) : 0.5f * unitScale;
                    board.Notes.Add($"{Path.GetFileName(path)}: aperture D{code} macro approximated");
                    break;
            }
            if (ap.H <= 0) ap.H = ap.W;
            apertures[code] = ap;
        }

        /// <summary>Flatten an arc into the layer as short segments.</summary>
        private static void EmitArc(PcbLayer layer, float x0, float y0, float x1, float y1,
                                    float i, float j, bool cw, bool multiQuadrant, float width)
        {
            float cx = x0 + i, cy = y0 + j;
            float r  = MathF.Sqrt(i * i + j * j);
            if (r < 1e-6f) { AddSeg(layer, x0, y0, x1, y1, width); return; }

            float a0 = MathF.Atan2(y0 - cy, x0 - cx);
            float a1 = MathF.Atan2(y1 - cy, x1 - cx);
            float sweep = Sweep(a0, a1, cw, multiQuadrant, x0, y0, x1, y1);

            int steps = Math.Clamp((int)(MathF.Abs(sweep) * 180f / MathF.PI / ARC_SEG_DEG), 1, 720);
            float px = x0, py = y0;
            for (int s = 1; s <= steps; s++)
            {
                float a = a0 + sweep * s / steps;
                float nx = cx + MathF.Cos(a) * r;
                float ny = cy + MathF.Sin(a) * r;
                AddSeg(layer, px, py, nx, ny, width);
                px = nx; py = ny;
            }
        }

        /// <summary>Same flattening, but appending into a region contour.</summary>
        private static void AddArcOrLine(PcbRegion region, float x0, float y0, float x1, float y1,
                                         float i, float j, int interp, bool multiQuadrant)
        {
            if (interp == 1) { region.X.Add(x1); region.Y.Add(y1); return; }

            float cx = x0 + i, cy = y0 + j;
            float r  = MathF.Sqrt(i * i + j * j);
            if (r < 1e-6f) { region.X.Add(x1); region.Y.Add(y1); return; }

            float a0 = MathF.Atan2(y0 - cy, x0 - cx);
            float a1 = MathF.Atan2(y1 - cy, x1 - cx);
            float sweep = Sweep(a0, a1, interp == 2, multiQuadrant, x0, y0, x1, y1);
            int steps = Math.Clamp((int)(MathF.Abs(sweep) * 180f / MathF.PI / ARC_SEG_DEG), 1, 720);

            for (int s = 1; s <= steps; s++)
            {
                float a = a0 + sweep * s / steps;
                region.X.Add(cx + MathF.Cos(a) * r);
                region.Y.Add(cy + MathF.Sin(a) * r);
            }
        }

        private static float Sweep(float a0, float a1, bool cw, bool multiQuadrant,
                                   float x0, float y0, float x1, float y1)
        {
            float sweep = a1 - a0;
            if (cw)  { while (sweep > 0)  sweep -= 2f * MathF.PI; }
            else     { while (sweep < 0)  sweep += 2f * MathF.PI; }

            // Single-quadrant mode (G74) can never sweep more than 90 degrees, and a
            // full circle is written as start == end.
            if (!multiQuadrant)
            {
                if (MathF.Abs(sweep) > MathF.PI / 2f)
                    sweep = MathF.Sign(sweep) * (2f * MathF.PI - MathF.Abs(sweep)) * -1f;
                if (MathF.Abs(sweep) > MathF.PI / 2f + 1e-3f)
                    sweep = MathF.Sign(sweep) * MathF.PI / 2f;
            }
            else if (MathF.Abs(sweep) < 1e-6f &&
                     MathF.Abs(x1 - x0) < 1e-6f && MathF.Abs(y1 - y0) < 1e-6f)
            {
                sweep = cw ? -2f * MathF.PI : 2f * MathF.PI;   // full circle
            }
            return sweep;
        }
    }
}
