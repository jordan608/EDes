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
//               whole drill map by 10x or 100x.
//    DIGITS     Altium states the split explicitly in a comment,
//               ";FILE_FORMAT=4:4", and DOES NOT use the conventional default.
//               That comment is authoritative when present; only without it does
//               the conventional 2.4 inch / 3.3 metric split apply. Ignoring it
//               reads X0008128 as 0.812 mm instead of 8.128 mm.
//
//  Supported: M48 header, INCH/METRIC[,LZ|TZ], ";FILE_FORMAT=i:d", Tn C<dia>
//  tool definitions (with Altium F/S feed fields), tool selection, X/Y hits
//  (modal), G85 slots, ";TYPE=PLATED" / ";TYPE=NON_PLATED" sections as well as
//  NPTH filename hints, M30/M00 end.
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
            var   toolPlated = new bool[MAX_TOOLS];
            bool  metric    = false, unitsStated = false;
            bool  leadingZerosOmitted = true;      // "LZ" means leading kept; default: suppressed
            bool  inHeader  = false;
            int   tool      = -1;
            float x = 0, y = 0;
            int   added     = 0;
            bool  plated    = !Path.GetFileNameWithoutExtension(path)
                                   .Contains("NPTH", StringComparison.OrdinalIgnoreCase);

            // -1 = not stated, so the conventional per-unit default is used.
            int intDigits = -1, decDigits = -1;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                // Comments carry two things we must not ignore: the coordinate format
                // and the plated/non-plated section marker.
                if (line.StartsWith(";"))
                {
                    int fmt = line.IndexOf("FILE_FORMAT", StringComparison.OrdinalIgnoreCase);
                    if (fmt >= 0)
                    {
                        int eq = line.IndexOf('=', fmt);
                        if (eq > 0)
                        {
                            string spec = line[(eq + 1)..].Trim();
                            char[] seps = { ':', '.', ',' };
                            int at = spec.IndexOfAny(seps);
                            if (at > 0 &&
                                int.TryParse(spec[..at], out int i2) &&
                                int.TryParse(spec[(at + 1)..].Trim(), out int d2) &&
                                i2 is > 0 and < 9 && d2 is > 0 and < 9)
                            {
                                intDigits = i2;
                                decDigits = d2;
                            }
                        }
                    }

                    if (line.Contains("NON_PLATED", StringComparison.OrdinalIgnoreCase))
                        plated = false;
                    else if (line.Contains("TYPE=PLATED", StringComparison.OrdinalIgnoreCase))
                        plated = true;

                    continue;
                }

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
                    if (inHeader) toolPlated[tnum] = plated;   // section in force here
                    else          tool = tnum;                 // body: a tool change
                    continue;
                }

                if (inHeader) continue;              // any other header line: ignore

                // Hits: X..Y.. optionally followed by G85X..Y.. for a slot.
                if (line[0] != 'X' && line[0] != 'Y' && !line.StartsWith("G85")) continue;

                int slotAt = line.IndexOf("G85", StringComparison.Ordinal);
                string first = slotAt >= 0 ? line.Substring(0, slotAt) : line;
                string? second = slotAt >= 0 ? line.Substring(slotAt + 3) : null;

                if (!ReadXY(first, metric, leadingZerosOmitted, intDigits, decDigits,
                            ref x, ref y)) continue;

                float dia = tool >= 0 ? toolDia[tool] : 0.3f;
                var hole = new PcbHole
                {
                    X = x, Y = y,
                    Dia = dia > 0 ? dia : 0.3f,
                    Plated = tool >= 0 ? toolPlated[tool] : plated,
                };

                if (second != null)
                {
                    float sx = x, sy = y;
                    if (ReadXY(second, metric, leadingZerosOmitted, intDigits, decDigits,
                               ref sx, ref sy))
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
            if (intDigits < 0 && added > 0)
                board.Notes.Add($"{Path.GetFileName(path)}: no FILE_FORMAT — assumed " +
                                (metric ? "3:3 metric" : "2:4 inch"));
            return added;
        }

        private static void ReadZeroMode(string line, ref bool leadingZerosOmitted)
        {
            if (line.Contains(",LZ")) leadingZerosOmitted = false;   // leading zeros present
            if (line.Contains(",TZ")) leadingZerosOmitted = true;    // trailing zeros present
        }

        private static bool ReadXY(string token, bool metric, bool leadingZerosOmitted,
                                   int intDigits, int decDigits, ref float x, ref float y)
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

                float v = Decode(span, metric, leadingZerosOmitted, intDigits, decDigits);
                if (c == 'X') x = v; else y = v;
                got = true;
            }
            return got;
        }

        /// <summary>Decode one coordinate to mm. Explicit decimal points win; otherwise
        /// the conventional implied format for the unit is applied (2.4 inch / 3.3 mm),
        /// padding according to which end of the number was suppressed.</summary>
        private static float Decode(ReadOnlySpan<char> span, bool metric, bool leadingZerosOmitted,
                                    int statedInt = -1, int statedDec = -1)
        {
            if (span.IndexOf('.') >= 0)
                return float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out float d)
                     ? (metric ? d : d * 25.4f) : 0f;

            bool neg = span[0] == '-';
            if (neg || span[0] == '+') span = span[1..];

            // A stated FILE_FORMAT wins over the conventional default.
            int intDigits = statedInt > 0 ? statedInt : (metric ? 3 : 2);
            int decDigits = statedDec > 0 ? statedDec : (metric ? 3 : 4);
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
