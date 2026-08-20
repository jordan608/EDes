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
                    files.AddRange(Directory.GetFiles(path));
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

            files.Sort(StringComparer.OrdinalIgnoreCase);

            int gerbers = 0, drills = 0, meshes = 0, holes = 0;

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();

                if (MeshLoader.IsStep(file))
                {
                    MeshLoader.TryLoad(file, meshPointBudget, board.Notes);   // records the note
                    continue;
                }

                if (MeshLoader.IsMesh(file))
                {
                    var cloud = MeshLoader.TryLoad(file, meshPointBudget, board.Notes);
                    if (cloud != null) { board.Meshes.Add(cloud); meshes++; }
                    continue;
                }

                if (Array.IndexOf(DrillExtensions, ext) >= 0 || LooksLikeDrill(file))
                {
                    int n = ExcellonParser.Parse(file, board);
                    if (n > 0) { drills++; holes += n; }
                    continue;
                }

                if (Array.IndexOf(GerberExtensions, ext) >= 0 || LooksLikeGerber(file))
                {
                    var kind  = ClassifyLayer(file);
                    var layer = board.GetOrAddLayer(Path.GetFileName(file), kind);
                    if (GerberParser.Parse(file, layer, board)) gerbers++;
                    else board.Layers.Remove(layer);
                    continue;
                }
            }

            // Stack copper layers top-to-bottom, everything else around them.
            board.Layers.Sort((a, b) => StackOrder(a.Kind).CompareTo(StackOrder(b.Kind)));
            board.ComputeBounds();

            if (gerbers + drills + meshes == 0)
            {
                board.Notes.Add("Nothing recognised — expected Gerber (.gbr/.gtl/…), " +
                                "drill (.drl) or mesh (.stl/.glb) files.");
                return false;
            }

            board.Notes.Insert(0, $"{gerbers} gerber layer(s), {holes} hole(s) from {drills} drill " +
                                  $"file(s), {meshes} mesh(es)");
            return true;
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
