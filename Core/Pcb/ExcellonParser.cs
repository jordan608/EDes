// ═══════════════════════════════════════════════════════════════════════════
//  ExcellonParser.cs — drill / route file reader
//
//  Excellon is older and looser than Gerber, and the two things that most often
//  ruin an import are handled explicitly here:
//
//    UNITS      The header may say INCH or METRIC (or nothing at all, in which
//               case inch is the historical default). KiCad writes METRIC with
//               explicit decimal points; Altium often writes INCH with implied
//               decimals.
//    ZEROES     Without decimal points, coordinates are integers with an
//               implied format. "INCH,TZ" means trailing zeros kept / leading
//               suppressed and vice-versa. Getting this backwards scales the
//               whole drill map by 10x or 100x — so when the header does not
//               say, the format is inferred from the digit count (2.4 inch,
//               3.3 metric are the conventional defaults).
//
//  Supported: M48 header, INCH/METRIC[,LZ|TZ], Tn C<dia> tool definitions,
//  tool selection, X/Y hits (modal), G85 slots, plated/non-plated hints from
//  the file name, M30/M00 end.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;
using System.IO;

namespace EDes.Pcb
{
    public static class ExcellonParser
    {
        private const int MAX_TOOLS = 1000;

        /// <summary>Parse a drill file, appending holes to the board.
        /// Returns the number of holes added.</summary>
        public static int Parse(string path, PcbBoard board)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                board.Notes.Add($"{Path.GetFileName(path)}: unreadable ({ex.GetType().Name})");
                return 0;
            }

            var   toolDia   = new float[MAX_TOOLS];
            bool  metric    = false, unitsStated = false;
            bool  leadingZerosOmitted = true;      // "LZ" means leading kept; default: suppressed
            bool  inHeader  = false;
            int   tool      = -1;
            float x = 0, y = 0;
            int   added     = 0;
            bool  plated    = !Path.GetFileNameWithoutExtension(path)
                                   .Contains("NPTH", StringComparison.OrdinalIgnoreCase);

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;

                if (line.StartsWith("M48")) { inHeader = true;  continue; }
                if (line == "%" || line.StartsWith("M95")) { inHeader = false; continue; }
                if (line.StartsWith("M30") || line.StartsWith("M00")) break;

                if (line.StartsWith("METRIC")) { metric = true;  unitsStated = true; ReadZeroMode(line, ref leadingZerosOmitted); continue; }
                if (line.StartsWith("INCH"))   { metric = false; unitsStated = true; ReadZeroMode(line, ref leadingZerosOmitted); continue; }

                // Tool definition: T01C0.300  (may also carry F/S feeds — ignored)
                if (line[0] == 'T')
                {
                    int i = 1, s = 1;
                    while (i < line.Length && char.IsDigit(line[i])) i++;
                    if (!int.TryParse(line.AsSpan(s, i - s), out int tnum) ||
                        tnum < 0 || tnum >= MAX_TOOLS) continue;

                    int ci = line.IndexOf('C', i);
                    if (ci >= 0)
                    {
                        int e = ci + 1;
                        while (e < line.Length && (char.IsDigit(line[e]) || line[e] == '.')) e++;
                        if (float.TryParse(line.AsSpan(ci + 1, e - ci - 1), NumberStyles.Float,
                                           CultureInfo.InvariantCulture, out float toolSize))
                            toolDia[tnum] = metric ? toolSize : toolSize * 25.4f;
                    }
                    if (!inHeader) tool = tnum;      // body: this is a tool change
                    continue;
                }

                if (inHeader) continue;              // any other header line: ignore

                // Hits: X..Y.. optionally followed by G85X..Y.. for a slot.
                if (line[0] != 'X' && line[0] != 'Y' && !line.StartsWith("G85")) continue;

                int slotAt = line.IndexOf("G85", StringComparison.Ordinal);
                string first = slotAt >= 0 ? line.Substring(0, slotAt) : line;
                string? second = slotAt >= 0 ? line.Substring(slotAt + 3) : null;

                if (!ReadXY(first, metric, leadingZerosOmitted, ref x, ref y)) continue;

                float dia = tool >= 0 ? toolDia[tool] : 0.3f;
                var hole = new PcbHole { X = x, Y = y, Dia = dia > 0 ? dia : 0.3f, Plated = plated };

                if (second != null)
                {
                    float sx = x, sy = y;
                    if (ReadXY(second, metric, leadingZerosOmitted, ref sx, ref sy))
                    {
                        hole.Slot = true;
                        hole.X1   = sx;
                        hole.Y1   = sy;
                        x = sx; y = sy;
                    }
                }

                board.Holes.Add(hole);
                added++;
            }

            if (!unitsStated)
                board.Notes.Add($"{Path.GetFileName(path)}: no INCH/METRIC header — assumed inch");
            return added;
        }

        private static void ReadZeroMode(string line, ref bool leadingZerosOmitted)
        {
            if (line.Contains(",LZ")) leadingZerosOmitted = false;   // leading zeros present
            if (line.Contains(",TZ")) leadingZerosOmitted = true;    // trailing zeros present
        }

        private static bool ReadXY(string token, bool metric, bool leadingZerosOmitted,
                                   ref float x, ref float y)
        {
            bool got = false;
            int i = 0;
            while (i < token.Length)
            {
                char c = token[i];
                if (c != 'X' && c != 'Y') { i++; continue; }

                int s = ++i;
                while (i < token.Length && (char.IsDigit(token[i]) ||
                       token[i] == '-' || token[i] == '+' || token[i] == '.')) i++;
                var span = token.AsSpan(s, i - s);
                if (span.Length == 0) continue;

                float v = Decode(span, metric, leadingZerosOmitted);
                if (c == 'X') x = v; else y = v;
                got = true;
            }
            return got;
        }

        /// <summary>Decode one coordinate to mm. Explicit decimal points win; otherwise
        /// the conventional implied format for the unit is applied (2.4 inch / 3.3 mm),
        /// padding according to which end of the number was suppressed.</summary>
        private static float Decode(ReadOnlySpan<char> span, bool metric, bool leadingZerosOmitted)
        {
            if (span.IndexOf('.') >= 0)
                return float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out float d)
                     ? (metric ? d : d * 25.4f) : 0f;

            bool neg = span[0] == '-';
            if (neg || span[0] == '+') span = span[1..];

            int intDigits = metric ? 3 : 2;
            int decDigits = metric ? 3 : 4;
            int total     = intDigits + decDigits;

            // Pad the suppressed end back out to the full width before scaling.
            Span<char> buf = stackalloc char[total];
            buf.Fill('0');
            if (leadingZerosOmitted)
            {
                int copy = Math.Min(span.Length, total);
                span[^copy..].CopyTo(buf[(total - copy)..]);      // right-align
            }
            else
            {
                int copy = Math.Min(span.Length, total);
                span[..copy].CopyTo(buf);                          // left-align
            }

            if (!long.TryParse(buf, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
                return 0f;

            double v = raw / Math.Pow(10, decDigits);
            if (neg) v = -v;
            return (float)(metric ? v : v * 25.4);
        }
    }
}
