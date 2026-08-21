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
            ".gpt", ".gpb",                              // Altium pad master top/bottom
            ".gm", ".gm1", ".gm2", ".gm3", ".gm4", ".gm5", ".gm6",   // mechanical
            ".gko", ".gd1", ".gg1",                      // keep-out, drill drawing/guide
            ".g1", ".g2", ".g3", ".g4",
            ".cmp", ".sol", ".plc", ".pls", ".stc", ".sts",
        };

        /// <summary>Files that sit right next to the Gerbers and are NOT geometry.
        /// Altium's aperture library and its report files would otherwise be fed to the
        /// Gerber parser, which produces either nothing or nonsense.</summary>
        private static readonly string[] NeverGeometry =
        {
            ".apr", ".apr_lib", ".extrep", ".rep", ".drr", ".ldp", ".html", ".htm",
            ".xls", ".xlsx", ".xlsm", ".ods", ".pdf", ".doc", ".docx", ".rtf",
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
        /// The board is cleared first. Returns true if anything was loaded.
        ///
        /// <paramref name="progress"/> is called with the file about to be handled, before
        /// the work rather than after. That ordering is the point: this runs on the game
        /// thread, so a slow file freezes rendering, and a status set beforehand is what
        /// tells the difference between "still parsing this 80 MB STEP" and "hung". A
        /// status set afterwards would name the last file that FINISHED, which is exactly
        /// the wrong one to know about.</summary>
        /// <summary>Tessellation settings for STEP surfaces. Passed as a struct so adding
        /// another knob later does not change the signature every caller uses.</summary>
        public struct StepOptions
        {
            public bool   Tessellate;
            public float  ToleranceMm;
            public string Command;
        }

        public static bool Import(string path, PcbBoard board, int meshPointBudget,
                                  Action<string>? progress = null,
                                  StepOptions step = default)
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
            int solids = 0;
            int skippedDuplicates = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var totalWatch = System.Diagnostics.Stopwatch.StartNew();
            var fileWatch  = new System.Diagnostics.Stopwatch();
            string rootDir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? "");

            // Records one file's outcome. Called from every branch below, including the
            // ignore paths — a file missing from this list means the importer never even
            // enumerated it, which is a different bug from mis-classifying it.
            void Note(string f, string role, string detail, bool used)
            {
                long bytes = 0;
                try { bytes = new FileInfo(f).Length; } catch { }
                string folder = "";
                try
                {
                    string? d = Path.GetDirectoryName(f);
                    if (d != null && rootDir.Length > 0 &&
                        d.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
                        folder = d.Substring(rootDir.Length).Trim(Path.DirectorySeparatorChar,
                                                                  Path.AltDirectorySeparatorChar);
                }
                catch { }

                board.ImportLog.Add(new ImportedFile
                {
                    Name   = Path.GetFileName(f),
                    Folder = folder,
                    Role   = role,
                    Detail = detail,
                    Bytes  = bytes,
                    Ms     = (int)fileWatch.ElapsedMilliseconds,
                    Used   = used,
                });
            }

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
                if (!seen.Add(key))
                {
                    skippedDuplicates++;
                    fileWatch.Restart();
                    Note(file, "duplicate", "same name and size already loaded", false);
                    continue;
                }

                // Size goes in the status too: "parsing assembly.step (86.4 MB)" tells you
                // to wait, where a bare name looks identical to a hang.
                if (progress != null)
                {
                    long pb = 0;
                    try { pb = new FileInfo(file).Length; } catch { }
                    progress(pb >= 1024L * 1024L
                             ? $"{name} ({pb / 1024.0 / 1024.0:0.#} MB)"
                             : name);
                }
                fileWatch.Restart();

                if (StepParser.IsStep(file))
                {
                    // Parsed as an edge wireframe (see StepParser) AND inventoried, so the
                    // document readout still shows the design ships a 3D model.
                    var cad = StepParser.TryLoad(file, board.Notes);
                    if (cad != null)
                    {
                        // Curved faces need a real kernel, so if a tessellator is available
                        // its mesh REPLACES the analytically-filled faces. The exact edges
                        // from StepParser are kept either way: they are sharper than
                        // anything a tessellation gives back.
                        string extra = "";
                        if (step.Tessellate)
                        {
                            string? stl = StepConverter.EnsureStl(
                                file, step.ToleranceMm <= 0f ? 0.4f : step.ToleranceMm,
                                step.Command ?? "", board.Notes, progress);

                            if (stl != null)
                            {
                                var faces = StlMesh.TryLoad(stl, board.Notes);
                                if (faces != null && faces.Count > 0)
                                    extra = AttachTessellation(cad, faces);
                            }
                        }

                        board.Solids.AddRange(cad.Solids);
                        solids += cad.SolidCount;
                        Note(file, "STEP", $"{cad.SolidCount} solid(s), {cad.TotalEdges} edge(s)"
                                           + extra, true);
                    }
                    else
                    {
                        Note(file, "STEP", "FAILED to parse — see the notes above", false);
                    }
                    AddDocument(board, file, DocKind.Cad3D);
                    docs++;
                    continue;
                }

                if (MeshLoader.IsMesh(file))
                {
                    var cloud = MeshLoader.TryLoad(file, meshPointBudget, board.Notes);
                    if (cloud != null)
                    {
                        board.Meshes.Add(cloud); meshes++;
                        Note(file, "mesh", $"{cloud.Count} point(s)", true);
                    }
                    else Note(file, "mesh", "FAILED to load", false);
                    continue;
                }

                // Reports and workbooks are inventory, never geometry — check this
                // BEFORE the Gerber/drill sniffing, which would otherwise be handed
                // an aperture library or a DRC report.
                if (Array.IndexOf(NeverGeometry, ext) >= 0)
                {
                    if (LooksLikeBom(file))
                    {
                        bomFiles.Add(file);
                        Note(file, "BOM", "queued for the second pass", true);
                        continue;
                    }

                    if (ext == ".drc" || name.Contains("Design Rule Check",
                                                       StringComparison.OrdinalIgnoreCase))
                    {
                        ParseDrc(file, board);
                        AddDocument(board, file, DocKind.Other);
                        docs++;
                        Note(file, "DRC", board.Drc.Parsed
                             ? $"{board.Drc.Violations} violation(s) over {board.Drc.Rules} rule(s)"
                             : "not parsed", board.Drc.Parsed);
                        continue;
                    }

                    var k = ClassifyDocument(file);
                    if (k.HasValue)
                    {
                        AddDocument(board, file, k.Value);
                        docs++;
                        Note(file, "document", k.Value.ToString(), true);
                    }
                    else Note(file, "ignored", "not geometry and not a known document", false);
                    continue;
                }

                if (ext == ".drc")
                {
                    ParseDrc(file, board);
                    AddDocument(board, file, DocKind.Other);
                    docs++;
                    Note(file, "DRC", board.Drc.Parsed ? "parsed" : "not parsed", board.Drc.Parsed);
                    continue;
                }

                if (LooksLikePlacement(file))
                {
                    placementFiles.Add(file);
                    Note(file, "placement", "queued for the second pass", true);
                    continue;
                }
                if (LooksLikeBom(file))
                {
                    bomFiles.Add(file);
                    Note(file, "BOM", "queued for the second pass", true);
                    continue;
                }

                if (Array.IndexOf(DrillExtensions, ext) >= 0 || LooksLikeDrill(file))
                {
                    int n = ExcellonParser.Parse(file, board);
                    if (n > 0)
                    {
                        drills++; holes += n;
                        Note(file, "drill", $"{n} hole(s)", true);
                    }
                    else
                    {
                        AddDocument(board, file, DocKind.Other);
                        Note(file, "drill", "no holes parsed — filed as a document", false);
                    }
                    continue;
                }

                if (Array.IndexOf(GerberExtensions, ext) >= 0 || LooksLikeGerber(file))
                {
                    var kind  = ClassifyLayer(file, out bool onBottom);
                    var layer = board.GetOrAddLayer(name, kind);
                    layer.Bottom = onBottom;

                    // Mechanical/drawing layers are dimension art — often the largest
                    // file in the set. They load, but start hidden so they cannot eat
                    // the voxel budget before the copper is drawn.
                    if (kind == PcbLayerKind.Mechanical) layer.Visible = false;
                    // Pad masters duplicate the copper pads; hidden by default so pads
                    // are not drawn twice on the same plane.
                    if (kind == PcbLayerKind.PadMaster) layer.Visible = false;

                    if (GerberParser.Parse(file, layer, board) && layer.ObjectCount > 0)
                    {
                        gerbers++;
                        Note(file, "gerber", $"{kind}, {layer.ObjectCount} object(s)" +
                                             (layer.Visible ? "" : ", hidden by default"), true);
                    }
                    else
                    {
                        board.Layers.Remove(layer);      // unparseable, or drew nothing
                        Note(file, "gerber", "parsed to nothing — dropped", false);
                    }
                    continue;
                }

                // Everything else is inventory, not geometry.
                var docKind = ClassifyDocument(file);
                if (docKind.HasValue)
                {
                    AddDocument(board, file, docKind.Value);
                    docs++;
                    Note(file, "document", docKind.Value.ToString(), true);
                }
                else Note(file, "ignored", "unrecognised extension and content", false);
            }

            // Altium writes the SAME placement data as both .csv and .txt, so the second
            // file must not double the part list. Parse each, then drop designators that
            // are already present — cheaper and safer than trying to guess which file to
            // prefer, and it also handles a top/bottom pair split across two files.
            foreach (string f in placementFiles)
            {
                int before = board.Components.Count;
                int n = PlacementParser.Parse(f, board);
                if (n <= 0) { AddDocument(board, f, DocKind.Other); continue; }

                int removed = DedupeComponents(board, before);
                int kept    = board.Components.Count - before;
                parts += kept;
                AddDocument(board, f, DocKind.Placement);
                if (kept == 0)
                    board.Notes.Add($"{Path.GetFileName(f)}: duplicate placement data ignored");
                else if (removed > 0)
                    board.Notes.Add($"{Path.GetFileName(f)}: {removed} already-placed part(s) skipped");
            }
            foreach (string f in bomFiles)
            {
                int n = PlacementParser.ParseBom(f, board);
                if (n > 0) { bomRows += n; AddDocument(board, f, DocKind.Bom); }
                else AddDocument(board, f, DocKind.Other);
            }

            // Stack copper layers top-to-bottom, everything else around them.
            board.Layers.Sort((a, b) =>
            {
                int oa = StackOrder(a.Kind, a.Bottom), ob = StackOrder(b.Kind, b.Bottom);
                // Name as the tie-break so the order is stable rather than dependent on
                // the order the files happened to be enumerated in.
                return oa != ob ? oa.CompareTo(ob)
                                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            board.ComputeBounds();

            // Solids count as geometry. They did not, which meant a plain STEP file -- no
            // Gerbers, no drill, just a model -- was rejected here as "no geometry" and
            // returned BEFORE connectivity and designator linking ever ran. A STEP file on
            // its own is a perfectly reasonable thing to want to look at.
            if (gerbers + drills + meshes + parts + solids == 0)
            {
                board.Notes.Add("No geometry found — expected Gerber (.gbr/.gtl/...), " +
                                "drill (.drl), mesh (.stl/.glb), STEP (.step/.stp) or a " +
                                "placement file." +
                                (docs > 0 ? $" ({docs} document(s) were catalogued.)" : ""));
                return docs > 0;
            }

            int linked = LinkSolidsToComponents(board);

            // Copper connectivity, once every layer and hole is in. Built here rather than
            // on demand because it depends on the WHOLE board, so a lazy build would land
            // in the middle of a draw call.
            board.Nets = PcbNets.Build(board);
            board.ImportMs = (int)totalWatch.ElapsedMilliseconds;
            progress?.Invoke("");

            var summary = $"{gerbers} gerber layer(s), {holes} hole(s) from {drills} drill file(s), " +
                          $"{meshes} mesh(es), {solids} CAD solid(s), {parts} part(s), " +
                          $"{bomRows} BOM row(s), {docs} document(s)";
            if (solids > 0)
                summary += $", {linked} solid(s) matched to a designator";
            if (board.Drc.Parsed)
                summary += $", DRC {board.Drc.Violations} violation(s) over {board.Drc.Rules} rule(s)";
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
                    {
                        // Suffix matching ONLY for the entries that are written as suffixes.
                        // A blanket EndsWith silently swallowed any folder whose name merely
                        // ended in one of these words — "Fabrication-bin", "STEP and OBJ",
                        // "Rev2 backup" — and a skipped folder looks exactly like a missing
                        // file to whoever is staring at the viewer.
                        bool hit = s[0] == '-' ? leaf.EndsWith(s, StringComparison.Ordinal)
                                               : leaf == s;
                        if (hit) { skip = true; break; }
                    }
                    if (skip)
                    {
                        board.Notes.Add($"Skipped folder \"{Path.GetFileName(dir)}\" " +
                                        "(matches the backup/temp skip list)");
                        continue;
                    }

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

        // ── Design rule check ─────────────────────────────────────────────────

        /// <summary>Parse a Protel/Altium DRC report. The format is a run of
        ///     Processing Rule : &lt;name&gt;
        ///     Rule Violations :&lt;n&gt;
        /// pairs, which gives a rule count, a total violation count, and the names of
        /// any rules that actually failed.</summary>
        private static void ParseDrc(string file, PcbBoard board)
        {
            try
            {
                var drc = new DrcSummary { File = Path.GetFileName(file) };
                string? pendingRule = null;

                foreach (string raw in File.ReadLines(file))
                {
                    string line = raw.Trim();

                    if (line.StartsWith("Processing Rule", StringComparison.OrdinalIgnoreCase))
                    {
                        int colon = line.IndexOf(':');
                        pendingRule = colon >= 0 ? line[(colon + 1)..].Trim() : line;
                        drc.Rules++;
                        continue;
                    }

                    if (!line.StartsWith("Rule Violations", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int c = line.IndexOf(':');
                    if (c < 0 || !int.TryParse(line[(c + 1)..].Trim(), out int n)) continue;

                    drc.Violations += n;
                    if (n > 0 && pendingRule != null)
                        drc.Failing.Add($"{Shorten(pendingRule)}: {n}");
                    pendingRule = null;
                }

                drc.Parsed = drc.Rules > 0;
                if (drc.Parsed) board.Drc = drc;
            }
            catch (Exception ex)
            {
                board.Notes.Add($"{Path.GetFileName(file)}: DRC parse failed ({ex.GetType().Name})");
            }
        }

        /// <summary>Rule names carry their whole parameter list; keep the leading name.</summary>
        private static string Shorten(string rule)
        {
            int paren = rule.IndexOf('(');
            string name = paren > 0 ? rule[..paren] : rule;
            name = name.Trim();
            return name.Length > 40 ? name[..40] : name;
        }

        // ── Component de-duplication ──────────────────────────────────────────

        /// <summary>Remove components added at or after `from` whose designator was
        /// already present before that point. Returns how many were dropped.</summary>
        private static int DedupeComponents(PcbBoard board, int from)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < from && i < board.Components.Count; i++)
                seen.Add(board.Components[i].Designator);

            int removed = 0;
            for (int i = board.Components.Count - 1; i >= from; i--)
            {
                string d = board.Components[i].Designator;
                if (seen.Contains(d)) { board.Components.RemoveAt(i); removed++; }
                else seen.Add(d);
            }
            return removed;
        }

        // ── Document inventory ────────────────────────────────────────────────

        private static void AddDocument(PcbBoard board, string file, DocKind kind)
        {
            long bytes = 0;
            try { bytes = new FileInfo(file).Length; } catch { }

            string folder = "";
            try { folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "") ?? ""; } catch { }

            // The folder refines the kind: in a fixed export tree every PDF carries the
            // project name, so "Schematic Prints/" is the only thing that says schematic.
            kind = RefineByFolder(kind, folder);

            board.Documents.Add(new PcbDocument
            {
                Path   = file,
                Name   = Path.GetFileName(file),
                Folder = folder,
                Kind   = kind,
                Bytes  = bytes,
                Pages  = kind is DocKind.Schematic or DocKind.Drawing ? PdfPageCount(file) : 0,
            });
        }

        /// <summary>Fold the containing folder's meaning into a document's kind. Matches
        /// the Altium "Project Outputs" layout (Schematic Prints, Assembly Drawings, BOM,
        /// Pick Place, ExportSTEP, PDF3D, PCB Prints, Design Rules Check, Report Board
        /// Stack) and anything else that names itself as plainly.</summary>
        private static DocKind RefineByFolder(DocKind kind, string folder)
        {
            if (folder.Length == 0) return kind;
            string f = folder.ToLowerInvariant();

            if (f.Contains("schematic"))                       return DocKind.Schematic;
            if (f.Contains("bill of material") || f == "bom")  return DocKind.Bom;
            if (f.Contains("pick place") || f.Contains("pick and place")) return DocKind.Placement;
            if (f.Contains("step") || f.Contains("pdf3d") ||
                f.Contains("3d print") || f.Contains("3d"))    return DocKind.Cad3D;
            if (f.Contains("assembly") || f.Contains("drawing") ||
                f.Contains("pcb print") || f.Contains("print")) return DocKind.Drawing;
            if (f.Contains("netlist"))                         return DocKind.Netlist;

            return kind;
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
            if (ext is not (".csv" or ".txt" or ".tsv" or ".xlsx" or ".xlsm")) return false;
            return name.Contains("bom") || name.Contains("billofmaterial") ||
                   name.Contains("bill of material") || name.Contains("bill_of_material") ||
                   name.Contains("parts");
        }

        /// <summary>Render order within the stack — lower is drawn nearer the top of
        /// the volume (remember -Z is up, so the renderer negates this).</summary>
        /// <summary>Attach each CAD solid to the placed component it belongs to.
        ///
        /// Matching is by designator, against the names already on the assembly chain, and
        /// it is deliberately strict: a solid is only claimed when a chain element equals
        /// a designator the placement file actually lists. A fuzzy match here would be
        /// worse than none — mislabelling U1 as U11 puts the wrong part number next to
        /// the wrong body, and the whole point of the link is to trust that label.
        ///
        /// Returns how many solids were matched, so the caller can report it rather than
        /// leave the user guessing whether the link worked.</summary>
        /// <summary>Hang a tessellated mesh onto the parsed model.
        ///
        /// The mesh comes back as ONE body with no assembly structure — a tessellator
        /// flattens the tree — so it cannot be split back across the individual solids.
        /// It goes onto a single carrier solid instead, and the per-solid analytic faces
        /// are dropped so the two do not both draw the same surfaces at slightly different
        /// positions, which reads as z-fighting even on a display that has no z-buffer.
        /// The per-solid EDGES stay: they are exact, and they are what makes the model
        /// readable.</summary>
        private static string AttachTessellation(CadModel cad, List<CadFace> faces)
        {
            int tris = 0;
            foreach (var f in faces) tris += f.TriCount;

            foreach (var s in cad.Solids) s.Faces.Clear();

            var carrier = new CadSolid
            {
                Name    = "tessellated surfaces",
                Colour  = 0x9FC5E8,
                Visible = true,
                MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue,
                MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue,
            };

            foreach (var f in faces)
            {
                carrier.Faces.Add(f);
                for (int i = 0; i < f.TriCount * 3; i++)
                {
                    if (f.X[i] < carrier.MinX) carrier.MinX = f.X[i];
                    if (f.X[i] > carrier.MaxX) carrier.MaxX = f.X[i];
                    if (f.Y[i] < carrier.MinY) carrier.MinY = f.Y[i];
                    if (f.Y[i] > carrier.MaxY) carrier.MaxY = f.Y[i];
                    if (f.Z[i] < carrier.MinZ) carrier.MinZ = f.Z[i];
                    if (f.Z[i] > carrier.MaxZ) carrier.MaxZ = f.Z[i];
                }
            }

            cad.Solids.Add(carrier);
            return $", {tris} tessellated triangle(s) in {faces.Count} group(s)";
        }

        private static int LinkSolidsToComponents(PcbBoard board)
        {
            if (board.Solids.Count == 0 || board.Components.Count == 0) return 0;

            var byDesignator = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in board.Components)
                if (!string.IsNullOrEmpty(c.Designator))
                    byDesignator[c.Designator] = c.Designator;

            int linked = 0;
            foreach (var solid in board.Solids)
            {
                // The leaf name first — BestName already preferred a designator-shaped
                // element — then the rest of the chain, deepest first.
                if (byDesignator.TryGetValue(solid.Name, out string? exact))
                {
                    solid.Designator = exact;
                    linked++;
                    continue;
                }

                if (solid.AssemblyPath.Length == 0) continue;
                var parts = solid.AssemblyPath.Split(" / ", StringSplitOptions.RemoveEmptyEntries);
                for (int i = parts.Length - 1; i >= 0; i--)
                {
                    if (!byDesignator.TryGetValue(parts[i].Trim(), out string? hit)) continue;
                    solid.Designator = hit;
                    linked++;
                    break;
                }
            }
            return linked;
        }

        /// <summary>Position in the stack, top of the board first.
        ///
        /// The side matters and used to be ignored: silkscreen, mask and paste share one
        /// PcbLayerKind per type regardless of side, so every silkscreen layer sorted to
        /// slot 0 and the BOTTOM silkscreen ended up above the top copper — and above the
        /// 3D components. The physical order is what this now follows: outward layers on
        /// the top, then copper, then the mirror of those on the bottom.</summary>
        private static int StackOrder(PcbLayerKind k, bool bottom) => k switch
        {
            PcbLayerKind.Silkscreen   => bottom ? 10 : 0,
            PcbLayerKind.Paste        => bottom ?  9 : 1,
            PcbLayerKind.SolderMask   => bottom ?  8 : 2,
            PcbLayerKind.PadMaster    => bottom ?  7 : 3,
            PcbLayerKind.CopperTop    => 4,
            PcbLayerKind.CopperInner  => 5,
            PcbLayerKind.CopperBottom => 6,
            PcbLayerKind.Outline      => 11,
            PcbLayerKind.Mechanical   => 12,
            PcbLayerKind.Drill        => 13,
            _                         => 14,
        };

        // ── Classification ────────────────────────────────────────────────────

        private static PcbLayerKind ClassifyLayer(string file, out bool bottom)
        {
            string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            string ext  = Path.GetExtension(file).ToLowerInvariant();
            bottom = false;

            bool Has(params string[] keys)
            {
                foreach (var k in keys) if (name.Contains(k)) return true;
                return false;
            }

            // Outline first: "edge_cuts" also contains no copper hint but must not
            // fall through to Unknown.
            if (Has("edge_cuts", "edge.cuts", "outline", "boardoutline", "profile", "contour") ||
                ext is ".gm1" or ".gko")
                return PcbLayerKind.Outline;

            // GM2 and up are Altium mechanical layers: dimensions, notes, assembly art.
            if (ext is ".gm" or ".gm2" or ".gm3" or ".gm4" or ".gm5" or ".gm6"
                     or ".gd1" or ".gg1" ||
                Has("mechanical", "assembly", "drill_drawing", "drillguide"))
                return PcbLayerKind.Mechanical;

            if (Has("f_silks", "f.silks", "silkscreen_top", "topsilk", "silktop") || ext is ".gto" or ".plc")
                return PcbLayerKind.Silkscreen;
            if (Has("b_silks", "b.silks", "bottomsilk", "silkbottom") || ext is ".gbo" or ".pls")
            { bottom = true; return PcbLayerKind.Silkscreen; }

            if (Has("f_mask", "f.mask", "topmask", "soldermask_top") || ext is ".gts" or ".stc")
                return PcbLayerKind.SolderMask;
            if (Has("b_mask", "b.mask", "bottommask", "soldermask_bottom") || ext is ".gbs" or ".sts")
            { bottom = true; return PcbLayerKind.SolderMask; }

            if (Has("b_paste", "b.paste", "bottompaste", "pastebottom") || ext is ".gbp")
            { bottom = true; return PcbLayerKind.Paste; }
            if (Has("paste", "f_paste", "f.paste") || ext is ".gtp")
                return PcbLayerKind.Paste;

            // Pad master = a composite of the pads already present on the copper layer.
            if (ext is ".gpb") { bottom = true; return PcbLayerKind.PadMaster; }
            if (ext is ".gpt" || Has("padmaster", "pad_master"))
                return PcbLayerKind.PadMaster;

            if (Has("f_cu", "f.cu", "topcopper", "top_copper", "toplayer", "gtl") ||
                ext is ".gtl" or ".cmp")
                return PcbLayerKind.CopperTop;
            if (Has("b_cu", "b.cu", "bottomcopper", "bottom_copper", "bottomlayer", "gbl") ||
                ext is ".gbl" or ".sol")
            { bottom = true; return PcbLayerKind.CopperBottom; }
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
