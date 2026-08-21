// ═══════════════════════════════════════════════════════════════════════════
//  PcbImporter.cs — turn a folder or a file into a PcbBoard
//
//  Point it at a fabrication output folder and it works out what each file is:
//  Gerber layers, the Excellon drill file, and any mechanical meshes. That is
//  the realistic workflow — nobody wants to select twelve files by hand, and
//  every CAD tool names them differently.
//
//  Layer identification uses BOTH conventions, because tools disagree:
//    KiCad     board-F_Cu.gbr, board-B_SilkS.gbr, board-Edge_Cuts.gbr
//    Altium    board.GTL, board.GBL, board.GTO, board.GM1
//    Eagle     board.cmp, board.sol, board.plc
//  Unrecognised Gerber still imports — as an Unknown layer, so nothing is
//  silently dropped.
//
//  Folder TREES are the normal case: a real design folder has gerbers, drills,
//  3D models, schematics, drawings, a BOM and a placement file scattered across
//  sub-folders. Import walks the whole tree, classifies every file by extension
//  AND by the folder it sits in, and de-duplicates repeats (the same gerber set
//  often appears in both an output folder and a release folder). Nothing has to
//  be named or arranged in a particular way.
//
//  What each kind of file contributes:
//    Gerber / drill   the layer stack and the holes            (drawn)
//    Mesh             the mechanical model                      (drawn)
//    Placement        every part's designator, XY, side         (drawn + labelled)
//    BOM              values and footprints for those parts     (labels + counts)
//    Schematics,      an inventory: what the design package
//    drawings, docs   contains, surfaced as a readout
//
//  Import runs on the GAME thread (from EDesApp), never on the UI thread: the
//  parse is CPU work measured in tens of milliseconds for a real board, and the
//  SDK owns that thread's timing. The UI only ever sets a requested path.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;

namespace EDes.Pcb
{
    public static class PcbImporter
    {
        private static readonly string[] GerberExtensions =
        {
            ".gbr", ".ger", ".gb", ".art", ".pho",
            ".gtl", ".gbl", ".gto", ".gbo", ".gts", ".gbs", ".gtp", ".gbp",
            ".gm1", ".gm2", ".gko", ".g1", ".g2", ".g3", ".g4",
            ".cmp", ".sol", ".plc", ".pls", ".stc", ".sts",
        };

        private static readonly string[] DrillExtensions =
        { ".drl", ".xln", ".nc", ".tap", ".exc", ".drd" };

        /// <summary>Folders that never contain anything we want, so a big design tree
        /// does not spend time (or memory) on backups and version-control internals.</summary>
        private static readonly string[] SkipFolders =
        {
            ".git", ".svn", "node_modules", "backup", "backups", "autosave", "auto-save",
            "-backups", "__macosx", "obj", "bin", "cache", "temp", "tmp", "recycle",
        };

        private static readonly string[] SchematicExtensions =
        { ".kicad_sch", ".sch", ".schdoc", ".sch1", ".asc" };
        private static readonly string[] DrawingExtensions =
        { ".dxf", ".dwg", ".svg", ".pdf" };
        private static readonly string[] NetlistExtensions =
        { ".net", ".ipc", ".d356", ".ipc356", ".cnl", ".netlist" };
        private static readonly string[] ArchiveExtensions =
        { ".zip", ".rar", ".7z", ".gz", ".tgz" };
        private static readonly string[] SpreadsheetExtensions =
        { ".csv", ".tsv", ".xls", ".xlsx", ".ods", ".txt" };

        /// <summary>Import everything at path (a file or a folder) into board.
        /// The board is cleared first. Returns true if anything was loaded.</summary>
        public static bool Import(string path, PcbBoard board, int meshPointBudget)
        {
            board.Clear();
            if (string.IsNullOrWhiteSpace(path))
            {
                board.Notes.Add("No path set.");
                return false;
            }

            var files = new List<string>();
            try
            {
                if (Directory.Exists(path))
                {
                    CollectTree(path, files, board);
                    board.SourceName = new DirectoryInfo(path).Name;
                }
                else if (File.Exists(path))
                {
                    files.Add(path);
                    board.SourceName = Path.GetFileName(path);
                }
                else
                {
                    board.Notes.Add($"Not found: {path}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                board.Notes.Add($"{ex.GetType().Name}: {ex.Message}");
                return false;
            }

            // Shallowest paths first: when the same file appears in several folders
            // (an output set copied into a release folder), the top-level copy wins.
            files.Sort((a, b) =>
            {
                int da = Depth(a), db = Depth(b);
                return da != db ? da.CompareTo(db)
                                : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            int gerbers = 0, drills = 0, meshes = 0, holes = 0, parts = 0, bomRows = 0, docs = 0;
            int skippedDuplicates = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Placement and BOM are handled in a second pass: the BOM can only fill in
            // values once the parts it refers to have been placed.
            var placementFiles = new List<string>();
            var bomFiles       = new List<string>();

            foreach (string file in files)
            {
                string ext  = Path.GetExtension(file).ToLowerInvariant();
                string name = Path.GetFileName(file);

                // De-duplicate by name + size, not by content: identical fab output
                // copied around a tree is the common case and hashing is wasted work.
                string key;
                try   { key = name + "|" + new FileInfo(file).Length; }
                catch { key = name; }
                if (!seen.Add(key)) { skippedDuplicates++; continue; }

                if (MeshLoader.IsStep(file))
                {
                    // Records the "convert to STL" note, and is also inventoried so the
                    // readout shows the design DOES ship a 3D model.
                    MeshLoader.TryLoad(file, meshPointBudget, board.Notes);
                    AddDocument(board, file, DocKind.Cad3D);
                    docs++;
                    continue;
                }

                if (MeshLoader.IsMesh(file))
                {
                    var cloud = MeshLoader.TryLoad(file, meshPointBudget, board.Notes);
                    if (cloud != null) { board.Meshes.Add(cloud); meshes++; }
                    continue;
                }

                if (LooksLikePlacement(file)) { placementFiles.Add(file); continue; }
                if (LooksLikeBom(file))       { bomFiles.Add(file);       continue; }

                if (Array.IndexOf(DrillExtensions, ext) >= 0 || LooksLikeDrill(file))
                {
                    int n = ExcellonParser.Parse(file, board);
                    if (n > 0) { drills++; holes += n; }
                    else AddDocument(board, file, DocKind.Other);
                    continue;
                }

                if (Array.IndexOf(GerberExtensions, ext) >= 0 || LooksLikeGerber(file))
                {
                    var kind  = ClassifyLayer(file);
                    var layer = board.GetOrAddLayer(name, kind);
                    if (GerberParser.Parse(file, layer, board)) gerbers++;
                    else board.Layers.Remove(layer);
                    continue;
                }

                // Everything else is inventory, not geometry.
                var docKind = ClassifyDocument(file);
                if (docKind.HasValue) { AddDocument(board, file, docKind.Value); docs++; }
            }

            foreach (string f in placementFiles)
            {
                int n = PlacementParser.Parse(f, board);
                if (n > 0) { parts += n; AddDocument(board, f, DocKind.Placement); }
            }
            foreach (string f in bomFiles)
            {
                int n = PlacementParser.ParseBom(f, board);
                if (n > 0) { bomRows += n; AddDocument(board, f, DocKind.Bom); }
                else AddDocument(board, f, DocKind.Other);
            }

            // Stack copper layers top-to-bottom, everything else around them.
            board.Layers.Sort((a, b) => StackOrder(a.Kind).CompareTo(StackOrder(b.Kind)));
            board.ComputeBounds();

            if (gerbers + drills + meshes + parts == 0)
            {
                board.Notes.Add("No geometry found — expected Gerber (.gbr/.gtl/...), " +
                                "drill (.drl), mesh (.stl/.glb) or a placement file." +
                                (docs > 0 ? $" ({docs} document(s) were catalogued.)" : ""));
                return docs > 0;
            }

            var summary = $"{gerbers} gerber layer(s), {holes} hole(s) from {drills} drill file(s), " +
                          $"{meshes} mesh(es), {parts} part(s), {bomRows} BOM row(s), {docs} document(s)";
            if (skippedDuplicates > 0) summary += $", {skippedDuplicates} duplicate(s) skipped";
            board.Notes.Insert(0, summary);
            return true;
        }

        // ── Tree walking ──────────────────────────────────────────────────────

        /// <summary>Walk the folder tree, collecting candidate files and recording which
        /// folders contributed. Unreadable folders are skipped, not fatal — a design tree
        /// on a network share regularly has one folder you cannot enter.</summary>
        private static void CollectTree(string root, List<string> files, PcbBoard board,
                                        int depth = 0)
        {
            const int MAX_DEPTH = 12;
            const int MAX_FILES = 20_000;

            if (depth > MAX_DEPTH || files.Count >= MAX_FILES) return;

            try
            {
                foreach (string f in Directory.GetFiles(root))
                {
                    files.Add(f);
                    if (files.Count >= MAX_FILES)
                    {
                        board.Notes.Add($"Stopped at {MAX_FILES} files — narrow the path.");
                        return;
                    }
                }
                board.SourceFolders.Add(root);

                foreach (string dir in Directory.GetDirectories(root))
                {
                    string leaf = Path.GetFileName(dir).ToLowerInvariant();
                    bool skip = false;
                    foreach (string s in SkipFolders)
                        if (leaf == s || leaf.EndsWith(s)) { skip = true; break; }
                    if (skip) continue;

                    CollectTree(dir, files, board, depth + 1);
                }
            }
            catch (Exception ex)
            {
                board.Notes.Add($"{Path.GetFileName(root)}: {ex.GetType().Name}");
            }
        }

        private static int Depth(string path)
        {
            int n = 0;
            foreach (char c in path) if (c == Path.DirectorySeparatorChar) n++;
            return n;
        }

        // ── Document inventory ────────────────────────────────────────────────

        private static void AddDocument(PcbBoard board, string file, DocKind kind)
        {
            long bytes = 0;
            try { bytes = new FileInfo(file).Length; } catch { }

            board.Documents.Add(new PcbDocument
            {
                Path  = file,
                Name  = Path.GetFileName(file),
                Kind  = kind,
                Bytes = bytes,
                Pages = kind is DocKind.Schematic or DocKind.Drawing ? PdfPageCount(file) : 0,
            });
        }

        /// <summary>Approximate PDF page count by counting page objects. Cheap, no PDF
        /// library, and only used to say "3 sheets" in a readout — it is inventory, not
        /// something the render depends on. Returns 0 for non-PDFs or on any doubt.</summary>
        private static int PdfPageCount(string file)
        {
            if (!file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return 0;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 32 * 1024 * 1024) return 0;         // do not slurp huge files

                string text = File.ReadAllText(file, System.Text.Encoding.Latin1);
                int count = 0, at = 0;
                while (true)
                {
                    int a = text.IndexOf("/Type /Page", at, StringComparison.Ordinal);
                    int b = text.IndexOf("/Type/Page", at, StringComparison.Ordinal);
                    int hit = a < 0 ? b : (b < 0 ? a : Math.Min(a, b));
                    if (hit < 0) break;

                    // Skip past the matched token, then check the NEXT character:
                    // "/Type /Pages" is the page-tree node and must not be counted,
                    // "/Type /Page" followed by anything else is a real page.
                    //   "/Type /Page" is 11 chars (space at index 5), "/Type/Page" is 10.
                    int after = hit + (text[hit + 5] == ' ' ? 11 : 10);
                    if (after >= text.Length || text[after] != 's') count++;
                    at = after;
                }
                return count;
            }
            catch { return 0; }
        }

        private static DocKind? ClassifyDocument(string file)
        {
            string ext  = Path.GetExtension(file).ToLowerInvariant();
            string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

            if (Array.IndexOf(SchematicExtensions, ext) >= 0) return DocKind.Schematic;
            if (Array.IndexOf(NetlistExtensions,  ext) >= 0)  return DocKind.Netlist;
            if (Array.IndexOf(ArchiveExtensions,  ext) >= 0)  return DocKind.Archive;
            if (ext == ".wrl" || ext == ".iges" || ext == ".igs" || ext == ".sat")
                return DocKind.Cad3D;

            if (Array.IndexOf(DrawingExtensions, ext) >= 0)
            {
                // A PDF in a design folder is usually either the schematic or a
                // mechanical drawing; the name is the only clue available.
                if (name.Contains("sch")) return DocKind.Schematic;
                if (name.Contains("datasheet") || name.Contains("ds_")) return DocKind.Datasheet;
                return DocKind.Drawing;
            }

            if (ext is ".md" or ".rst" or ".doc" or ".docx" or ".rtf") return DocKind.Other;
            if (Array.IndexOf(SpreadsheetExtensions, ext) >= 0) return DocKind.Other;
            return null;      // unknown/binary: not worth listing
        }

        // ── Placement / BOM sniffing ──────────────────────────────────────────

        private static bool LooksLikePlacement(string file)
        {
            string ext  = Path.GetExtension(file).ToLowerInvariant();
            string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

            if (ext == ".pos") return true;
            if (ext is not (".csv" or ".txt" or ".tsv")) return false;

            if (name.Contains("pick") || name.Contains("place") || name.Contains("centroid") ||
                name.Contains("-pos") || name.Contains("_pos") || name.EndsWith("pos") ||
                name.Contains("xy") || name.Contains("cpl"))
                return true;

            // Fall back to the header row: a designator column plus an X column.
            try
            {
                foreach (string line in ReadHead(file, 12))
                {
                    string l = line.ToLowerInvariant();
                    bool hasRef = l.Contains("designator") || l.Contains("ref");
                    bool hasX   = l.Contains("midx") || l.Contains("mid x") || l.Contains("posx") ||
                                  l.Contains(",x,") || l.Contains("\tx\t");
                    if (hasRef && hasX) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool LooksLikeBom(string file)
        {
            string ext  = Path.GetExtension(file).ToLowerInvariant();
            string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (ext is not (".csv" or ".txt" or ".tsv")) return false;
            return name.Contains("bom") || name.Contains("billofmaterial") ||
                   name.Contains("bill_of_material") || name.Contains("parts");
        }

        /// <summary>Render order within the stack — lower is drawn nearer the top of
        /// the volume (remember -Z is up, so the renderer negates this).</summary>
        private static int StackOrder(PcbLayerKind k) => k switch
        {
            PcbLayerKind.Silkscreen   => 0,
            PcbLayerKind.Paste        => 1,
            PcbLayerKind.SolderMask   => 2,
            PcbLayerKind.CopperTop    => 3,
            PcbLayerKind.CopperInner  => 4,
            PcbLayerKind.CopperBottom => 5,
            PcbLayerKind.Outline      => 6,
            PcbLayerKind.Drill        => 7,
            _                         => 8,
        };

        // ── Classification ────────────────────────────────────────────────────

        private static PcbLayerKind ClassifyLayer(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            string ext  = Path.GetExtension(file).ToLowerInvariant();

            bool Has(params string[] keys)
            {
                foreach (var k in keys) if (name.Contains(k)) return true;
                return false;
            }

            // Outline first: "edge_cuts" also contains no copper hint but must not
            // fall through to Unknown.
            if (Has("edge_cuts", "edge.cuts", "outline", "boardoutline", "profile", "contour") ||
                ext is ".gm1" or ".gm2" or ".gko")
                return PcbLayerKind.Outline;

            if (Has("f_silks", "f.silks", "silkscreen_top", "topsilk", "silktop") || ext is ".gto" or ".plc")
                return PcbLayerKind.Silkscreen;
            if (Has("b_silks", "b.silks", "bottomsilk", "silkbottom") || ext is ".gbo" or ".pls")
                return PcbLayerKind.Silkscreen;

            if (Has("f_mask", "f.mask", "topmask", "soldermask_top") || ext is ".gts" or ".stc")
                return PcbLayerKind.SolderMask;
            if (Has("b_mask", "b.mask", "bottommask", "soldermask_bottom") || ext is ".gbs" or ".sts")
                return PcbLayerKind.SolderMask;

            if (Has("paste", "f_paste", "b_paste") || ext is ".gtp" or ".gbp")
                return PcbLayerKind.Paste;

            if (Has("f_cu", "f.cu", "topcopper", "top_copper", "toplayer", "gtl") ||
                ext is ".gtl" or ".cmp")
                return PcbLayerKind.CopperTop;
            if (Has("b_cu", "b.cu", "bottomcopper", "bottom_copper", "bottomlayer", "gbl") ||
                ext is ".gbl" or ".sol")
                return PcbLayerKind.CopperBottom;
            if (Has("in1_cu", "in2_cu", "in3_cu", "in4_cu", "internalplane", "inner") ||
                ext is ".g1" or ".g2" or ".g3" or ".g4")
                return PcbLayerKind.CopperInner;

            return PcbLayerKind.Unknown;
        }

        /// <summary>Sniff the first few lines — .txt drill files and extension-less
        /// Gerber are both common enough to be worth checking rather than guessing.</summary>
        private static bool LooksLikeDrill(string file)
        {
            try
            {
                foreach (string line in ReadHead(file, 12))
                    if (line.StartsWith("M48") || line.StartsWith("METRIC") || line.StartsWith("INCH"))
                        return true;
            }
            catch { }
            return false;
        }

        private static bool LooksLikeGerber(string file)
        {
            try
            {
                foreach (string line in ReadHead(file, 12))
                    if (line.StartsWith("%FS") || line.StartsWith("%MO") || line.StartsWith("G04"))
                        return true;
            }
            catch { }
            return false;
        }

        private static IEnumerable<string> ReadHead(string file, int count)
        {
            using var sr = new StreamReader(file);
            for (int i = 0; i < count; i++)
            {
                string? line = sr.ReadLine();
                if (line == null) break;
                yield return line.Trim();
            }
        }
    }
}
