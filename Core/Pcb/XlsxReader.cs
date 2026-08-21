// ═══════════════════════════════════════════════════════════════════════════
//  XlsxReader.cs — just enough .xlsx to read a BOM
//
//  Altium writes its Bill of Materials as .xlsx, so a BOM reader that only
//  handles CSV misses the file the design actually ships. An .xlsx is a ZIP of
//  XML: the shared-string table plus one XML file per sheet. Reading the cell
//  values out of that is about a hundred lines with the framework's own ZIP and
//  XML support — far preferable to taking a spreadsheet library as a dependency
//  for one job.
//
//  What it handles: shared strings, inline strings, numeric cells, and column
//  letters (so gaps in a row do not shift the columns). What it does NOT do:
//  formulas (the cached value is used), styles, dates (read as their serial
//  number), or multiple sheets (the first sheet only). That is all a BOM needs.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace EDes.Pcb
{
    public static class XlsxReader
    {
        /// <summary>Read the first worksheet as rows of cell strings. Returns an empty
        /// list if the file is not a readable xlsx.</summary>
        public static List<string[]> ReadRows(string path, int maxRows = 5000)
        {
            var rows = new List<string[]>();
            try
            {
                using var zip = ZipFile.OpenRead(path);

                var shared = ReadSharedStrings(zip);
                var sheet  = FindFirstSheet(zip);
                if (sheet == null) return rows;

                using var stream = sheet.Open();
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver   = null,
                });

                var cells = new List<string>(32);
                int  colOfCell = 0;
                string cellRef = "", cellType = "";
                bool inValue = false, inInlineStr = false;
                string value = "";

                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "row":
                                cells.Clear();
                                break;

                            case "c":
                                cellRef  = reader.GetAttribute("r") ?? "";
                                cellType = reader.GetAttribute("t") ?? "";
                                value    = "";
                                colOfCell = ColumnIndex(cellRef);
                                break;

                            case "v":
                                inValue = true;
                                break;

                            case "t":
                                if (cellType == "inlineStr") inInlineStr = true;
                                break;
                        }
                    }
                    else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    {
                        if (inValue || inInlineStr) value += reader.Value;
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        switch (reader.Name)
                        {
                            case "v": inValue = false; break;
                            case "t": inInlineStr = false; break;

                            case "c":
                            {
                                string text = cellType == "s" && int.TryParse(value, out int si) &&
                                              si >= 0 && si < shared.Count
                                            ? shared[si]
                                            : value;

                                // Pad so a sparse row keeps its column alignment.
                                while (cells.Count < colOfCell) cells.Add("");
                                if (colOfCell >= 0 && colOfCell < cells.Count) cells[colOfCell] = text;
                                else cells.Add(text);
                                break;
                            }

                            case "row":
                                rows.Add(cells.ToArray());
                                if (rows.Count >= maxRows) return rows;
                                break;
                        }
                    }
                }
            }
            catch { /* not a readable xlsx — caller falls back to cataloguing it */ }
            return rows;
        }

        private static ZipArchiveEntry? FindFirstSheet(ZipArchive zip)
        {
            // sheet1.xml is the convention; otherwise take the lowest-numbered sheet.
            ZipArchiveEntry? best = null;
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null ||
                    string.Compare(e.FullName, best.FullName, StringComparison.OrdinalIgnoreCase) < 0)
                    best = e;
            }
            return best;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return list;

            try
            {
                using var stream = entry.Open();
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver   = null,
                });

                var current = new System.Text.StringBuilder();
                bool inItem = false;
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (reader.Name == "si") { inItem = true; current.Clear(); }
                    }
                    else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    {
                        // A shared string can be split across several <t> runs.
                        if (inItem) current.Append(reader.Value);
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "si")
                    {
                        list.Add(current.ToString());
                        inItem = false;
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>"C14" -> 2. Column letters are base-26, so a sparse row keeps its
        /// alignment instead of shifting every value one column left.</summary>
        public static int ColumnIndex(string cellRef)
        {
            int n = 0;
            foreach (char c in cellRef)
            {
                if (c is >= 'A' and <= 'Z') n = n * 26 + (c - 'A' + 1);
                else if (c is >= 'a' and <= 'z') n = n * 26 + (c - 'a' + 1);
                else break;
            }
            return Math.Max(0, n - 1);
        }
    }
}
