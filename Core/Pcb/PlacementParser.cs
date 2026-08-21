// ═══════════════════════════════════════════════════════════════════════════
//  PlacementParser.cs — pick-and-place / centroid files, and the BOM
//
//  These two files are what turn "a stack of copper layers" into "a board with
//  parts on it". A centroid file gives every component's designator, XY, rotation
//  and side; the BOM gives each designator a value and a footprint. Together they
//  let the display label real parts in 3D, which is the thing a flat Gerber
//  viewer cannot do at all.
//
//  Both formats are wildly inconsistent between tools, so parsing is driven by
//  the HEADER ROW rather than by column position:
//
//    KiCad  .pos     Ref  Val  Package  PosX  PosY  Rot  Side     (mm or in, stated
//                    in a comment line, and the header may be prefixed with #)
//    KiCad  .csv     Ref,Val,Package,PosX,PosY,Rot,Side
//    Altium .csv     Designator,...,Mid X,Mid Y,Rotation,Layer    (units suffixed
//                    per value, e.g. "12.7mm" or "500mil")
//    Eagle / generic Designator,X,Y,Rotation,Side / Layer
//
//  Everything is converted to millimetres. A value with a unit suffix wins over
//  the file-level unit, because Altium writes both.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EDes.Pcb
{
    public static class PlacementParser
    {
        /// <summary>Header names we accept for each field, lower-cased and stripped of
        /// spaces/underscores. First match wins.</summary>
        private static readonly string[] RefNames   = { "ref", "reference", "designator", "refdes", "component", "part" };
        private static readonly string[] ValNames   = { "val", "value", "comment", "partvalue" };
        private static readonly string[] PkgNames   = { "package", "footprint", "pattern", "footprintname" };
        private static readonly string[] XNames     = { "posx", "midx", "x", "refx", "centerx", "centrex" };
        private static readonly string[] YNames     = { "posy", "midy", "y", "refy", "centery", "centrey" };
        private static readonly string[] RotNames   = { "rot", "rotation", "angle" };
        private static readonly string[] SideNames  = { "side", "layer", "tb" };

        /// <summary>Parse a centroid/pick-and-place file. Returns how many components
        /// were added to the board.</summary>
        public static int Parse(string path, PcbBoard board)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                board.Notes.Add($"{Path.GetFileName(path)}: unreadable ({ex.GetType().Name})");
                return 0;
            }

            bool fileIsInch = LooksLikeInchFile(lines);
            int  added      = 0;

            // Find the header row: the first line that names both a designator column
            // and an X column once split.
            string[]? header = null;
            int       headerAt = -1;
            for (int i = 0; i < lines.Length && i < 200; i++)
            {
                string line = lines[i].TrimStart('#', ' ', '\t');
                if (line.Length == 0) continue;
                var cols = SplitRow(line);
                if (cols.Length < 4) continue;
                if (IndexOfAny(cols, RefNames) >= 0 && IndexOfAny(cols, XNames) >= 0)
                {
                    header   = cols;
                    headerAt = i;
                    break;
                }
            }

            if (header == null)
            {
                board.Notes.Add($"{Path.GetFileName(path)}: no recognisable placement header");
                return 0;
            }

            int ci  = IndexOfAny(header, RefNames);
            int cv  = IndexOfAny(header, ValNames);
            int cp  = IndexOfAny(header, PkgNames);
            int cx  = IndexOfAny(header, XNames);
            int cy  = IndexOfAny(header, YNames);
            int cr  = IndexOfAny(header, RotNames);
            int cs  = IndexOfAny(header, SideNames);

            if (cy < 0)
            {
                board.Notes.Add($"{Path.GetFileName(path)}: placement file has no Y column");
                return 0;
            }

            for (int i = headerAt + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                var cols = SplitRow(line);
                if (cols.Length <= Math.Max(cx, cy)) continue;

                string designator = Get(cols, ci);
                if (designator.Length == 0) continue;

                if (!TryLength(Get(cols, cx), fileIsInch, out float x)) continue;
                if (!TryLength(Get(cols, cy), fileIsInch, out float y)) continue;

                float rot = 0;
                float.TryParse(Get(cols, cr).TrimEnd('d', 'e', 'g', 'D', 'E', 'G', ' '),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out rot);

                string side = Get(cols, cs).ToLowerInvariant();
                bool bottom = side.Contains("bot") || side == "b" || side.Contains("bottom");

                board.Components.Add(new PcbComponent
                {
                    Designator = designator,
                    Value      = Get(cols, cv),
                    Footprint  = Get(cols, cp),
                    X          = x,
                    Y          = y,
                    Rotation   = rot,
                    Bottom     = bottom,
                });
                added++;
            }

            if (added == 0)
                board.Notes.Add($"{Path.GetFileName(path)}: placement header found but no rows parsed");
            return added;
        }

        /// <summary>Parse a BOM, filling in Value/Footprint for components already placed
        /// and counting distinct parts. Returns the number of BOM rows understood.</summary>
        public static int ParseBom(string path, PcbBoard board)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { return 0; }

            string[]? header = null;
            int headerAt = -1;
            for (int i = 0; i < lines.Length && i < 100; i++)
            {
                var cols = SplitRow(lines[i].TrimStart('#', ' '));
                if (cols.Length < 2) continue;
                // A BOM must name designators (possibly plural) and usually a value.
                if (IndexOfAny(cols, RefNames) >= 0 || IndexOfAny(cols, new[] { "designators", "refs" }) >= 0)
                {
                    header   = cols;
                    headerAt = i;
                    break;
                }
            }
            if (header == null) return 0;

            int cRef = IndexOfAny(header, RefNames);
            if (cRef < 0) cRef = IndexOfAny(header, new[] { "designators", "refs" });
            int cVal = IndexOfAny(header, ValNames);
            int cPkg = IndexOfAny(header, PkgNames);
            int cQty = IndexOfAny(header, new[] { "qty", "quantity", "count" });

            int rows = 0;
            var byDesignator = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < board.Components.Count; i++)
                byDesignator[board.Components[i].Designator] = i;

            for (int i = headerAt + 1; i < lines.Length; i++)
            {
                var cols = SplitRow(lines[i]);
                if (cols.Length <= cRef) continue;

                string refCell = Get(cols, cRef);
                if (refCell.Length == 0) continue;

                string value = Get(cols, cVal);
                string pkg   = Get(cols, cPkg);
                int    qty   = 0;
                int.TryParse(Get(cols, cQty), out qty);

                // A BOM row can list many designators: "R1,R2,R3" or "R1 R2 R3".
                foreach (string dRaw in refCell.Split(new[] { ',', ' ', ';' },
                                                      StringSplitOptions.RemoveEmptyEntries))
                {
                    string d = dRaw.Trim();
                    if (d.Length == 0) continue;
                    if (byDesignator.TryGetValue(d, out int idx))
                    {
                        var c = board.Components[idx];
                        if (c.Value.Length == 0)     c.Value     = value;
                        if (c.Footprint.Length == 0) c.Footprint = pkg;
                        board.Components[idx] = c;
                    }
                }

                board.BomLines.Add(new PcbBomLine
                {
                    Designators = refCell,
                    Value       = value,
                    Footprint   = pkg,
                    Quantity    = qty > 0 ? qty
                                : refCell.Split(new[] { ',', ' ', ';' },
                                                StringSplitOptions.RemoveEmptyEntries).Length,
                });
                rows++;
            }
            return rows;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Split a CSV / whitespace-delimited row, honouring double quotes.</summary>
        public static string[] SplitRow(string line)
        {
            var cells = new List<string>(12);
            bool quoted = false;
            var cur = new System.Text.StringBuilder(32);
            bool commaSeen = line.Contains(',');

            foreach (char c in line)
            {
                if (c == '"') { quoted = !quoted; continue; }
                bool isSep = quoted ? false : (commaSeen ? c == ',' : (c == ' ' || c == '\t'));
                if (isSep)
                {
                    if (cur.Length > 0 || commaSeen) { cells.Add(cur.ToString().Trim()); cur.Clear(); }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) cells.Add(cur.ToString().Trim());
            return cells.ToArray();
        }

        private static string Get(string[] cols, int index)
            => index >= 0 && index < cols.Length ? cols[index] : "";

        private static int IndexOfAny(string[] header, string[] names)
        {
            for (int i = 0; i < header.Length; i++)
            {
                string h = Normalise(header[i]);
                foreach (string n in names)
                    if (h == n) return i;
            }
            // Second pass: allow "mid x" style headers to match by containment.
            for (int i = 0; i < header.Length; i++)
            {
                string h = Normalise(header[i]);
                foreach (string n in names)
                    if (h.Length > 0 && h.Contains(n)) return i;
            }
            return -1;
        }

        private static string Normalise(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>A length cell to millimetres. A per-value unit suffix (mm / mil / in)
        /// beats the file-level unit, because Altium writes suffixes and KiCad does not.</summary>
        public static bool TryLength(string cell, bool fileIsInch, out float mm)
        {
            mm = 0;
            if (string.IsNullOrWhiteSpace(cell)) return false;

            string s = cell.Trim();
            float scale = fileIsInch ? 25.4f : 1f;

            if (s.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
            { s = s[..^2]; scale = 1f; }
            else if (s.EndsWith("mil", StringComparison.OrdinalIgnoreCase))
            { s = s[..^3]; scale = 0.0254f; }
            else if (s.EndsWith("in", StringComparison.OrdinalIgnoreCase))
            { s = s[..^2]; scale = 25.4f; }
            else if (s.EndsWith("\""))
            { s = s[..^1]; scale = 25.4f; }

            if (!float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                return false;
            mm = v * scale;
            return true;
        }

        private static bool LooksLikeInchFile(string[] lines)
        {
            for (int i = 0; i < lines.Length && i < 40; i++)
            {
                string l = lines[i].ToLowerInvariant();
                if (l.Contains("unit") || l.StartsWith("#") || l.StartsWith("##"))
                {
                    if (l.Contains("inch") || l.Contains("mils") || l.Contains("in\"")) return true;
                    if (l.Contains("mm") || l.Contains("millimet")) return false;
                }
            }
            return false;      // mm is the modern default
        }
    }
}
