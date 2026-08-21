// ═══════════════════════════════════════════════════════════════════════════
//  StepConverter.cs — STEP to a triangle mesh, via an external tessellator
//
//  StepParser reads STEP without a geometry kernel by reading only what needs no
//  kernel: edges, and the fill of PLANAR faces. That covers a PCB and most
//  brackets, and it deliberately leaves curved faces alone rather than guessing
//  at them. But a part that is mostly round — a connector barrel, a can
//  capacitor, a lens — is mostly curved faces, so it comes out as a sparse cage
//  with chord-approximated edges and nothing to light.
//
//  Tessellating trimmed NURBS IS kernel work, and the right move is to use one
//  rather than write one badly. So a STEP file is converted ONCE to STL by
//  whatever tessellator is on the machine, cached, and read back through
//  StlMesh into the same flat-shaded path the planar faces already use.
//
//  DISCOVERY, in order:
//    1. an explicit command from the settings — so an unusual toolchain, or a
//       commercial converter, can be pointed at without a code change
//    2. gmsh, which is one `pip install gmsh` away, headless, and has
//       OpenCASCADE built in
//    3. FreeCAD's freecadcmd, if it happens to be installed
//
//  Nothing is installed automatically and nothing is downloaded. When no
//  converter is found the note says exactly what to install, and the wireframe
//  still draws — a missing optional tool degrades the view, it does not break it.
//
//  CACHING is keyed on the source's path, size and write time plus the
//  tolerance. Conversion of a real assembly takes seconds, and this runs on the
//  game thread, so doing it on every import would stall rendering every time a
//  board was reloaded — including the automatic reload at every launch.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace EDes.Pcb
{
    public static class StepConverter
    {
        /// <summary>Where converted meshes live. Beside the cache rather than beside the
        /// source: a fabrication output folder is someone else's deliverable and should not
        /// acquire files we generated.</summary>
        public static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "EDes", "stepcache");

        /// <summary>The converter that will be used, for the settings readout, or an empty
        /// string with the reason in <paramref name="how"/>.</summary>
        public static string Discover(string explicitCommand, out string how)
        {
            if (!string.IsNullOrWhiteSpace(explicitCommand))
            {
                string cmd = explicitCommand.Trim();
                how = "from the configured command";
                return cmd;
            }

            string? gmsh = OnPath("gmsh");
            if (gmsh != null) { how = "gmsh found on PATH"; return gmsh; }

            string? freecad = OnPath("freecadcmd") ?? OnPath("FreeCADCmd");
            if (freecad != null) { how = "FreeCAD found on PATH"; return freecad; }

            how = "no tessellator found — run:  pip install gmsh   " +
                  "(headless, OpenCASCADE inside, reads STEP directly). " +
                  "FreeCAD's freecadcmd also works if it is already installed. " +
                  "Until then STEP still draws as a wireframe.";
            return "";
        }

        /// <summary>Convert if needed and return the STL path, or null.
        ///
        /// Returning null is a normal outcome, not a failure: no converter installed is the
        /// default state of a fresh machine, and the caller falls back to the wireframe.</summary>
        public static string? EnsureStl(string stepPath, float tolMm, string explicitCommand,
                                       List<string> notes, Action<string>? progress = null)
        {
            string tool = Discover(explicitCommand, out string how);
            if (tool.Length == 0)
            {
                notes.Add($"{Path.GetFileName(stepPath)}: surfaces need a tessellator — {how}");
                return null;
            }

            string cached;
            try
            {
                cached = Path.Combine(CacheDir, CacheName(stepPath, tolMm));
                if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;
                Directory.CreateDirectory(CacheDir);
            }
            catch (Exception ex)
            {
                notes.Add($"{Path.GetFileName(stepPath)}: cannot use the mesh cache — {ex.Message}");
                return null;
            }

            progress?.Invoke($"tessellating {Path.GetFileName(stepPath)} (first time only)");

            bool ok = tool.IndexOf("freecad", StringComparison.OrdinalIgnoreCase) >= 0
                      ? RunFreeCad(tool, stepPath, cached, tolMm, notes)
                      : RunGmsh(tool, stepPath, cached, tolMm, notes);

            if (!ok) return null;
            if (!File.Exists(cached) || new FileInfo(cached).Length == 0)
            {
                notes.Add($"{Path.GetFileName(stepPath)}: the tessellator produced no mesh");
                return null;
            }

            notes.Add($"{Path.GetFileName(stepPath)}: tessellated to STL ({how})");
            return cached;
        }

        /// <summary>Cache file name. Includes the source's SIZE and WRITE TIME as well as
        /// its name, so editing the STEP invalidates the mesh — a cache keyed on the name
        /// alone would keep serving the old geometry after a revision, which is worse than
        /// no cache at all.</summary>
        private static string CacheName(string stepPath, float tolMm)
        {
            long len = 0, stamp = 0;
            try
            {
                var fi = new FileInfo(stepPath);
                len = fi.Length;
                stamp = fi.LastWriteTimeUtc.Ticks;
            }
            catch { }

            string raw = stepPath.ToLowerInvariant() + "|" + len + "|" + stamp + "|" +
                         tolMm.ToString("0.####", CultureInfo.InvariantCulture);

            // FNV-1a: short, stable across runs, and does not need a crypto dependency for
            // what is only a cache key.
            ulong h = 14695981039346656037UL;
            foreach (char c in raw) { h ^= c; h *= 1099511628211UL; }

            string stem = Path.GetFileNameWithoutExtension(stepPath);
            foreach (char bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '_');
            if (stem.Length > 40) stem = stem.Substring(0, 40);

            return $"{stem}_{h:x16}.stl";
        }

        private static bool RunGmsh(string tool, string stepPath, string outPath,
                                    float tolMm, List<string> notes)
        {
            // -2 surface mesh only (a 3D volume mesh would be wasted work for a shell),
            // -clmax caps element size so the tolerance knob has an effect, -v 0 keeps the
            // banner out of the captured output.
            string args = $"\"{stepPath}\" -2 -format stl -o \"{outPath}\" " +
                          $"-clmax {tolMm.ToString("0.####", CultureInfo.InvariantCulture)} -v 0";
            return Run(tool, args, stepPath, notes);
        }

        private static bool RunFreeCad(string tool, string stepPath, string outPath,
                                       float tolMm, List<string> notes)
        {
            // freecadcmd needs a script; write one next to the output so a failure leaves
            // something inspectable rather than vanishing with the temp directory.
            string script = Path.ChangeExtension(outPath, ".py");
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("import Import, Mesh, FreeCAD");
                sb.AppendLine($"Import.open(r\"{stepPath}\")");
                sb.AppendLine("doc = FreeCAD.ActiveDocument");
                sb.AppendLine("shapes = [o.Shape for o in doc.Objects if hasattr(o, 'Shape')]");
                sb.AppendLine("m = Mesh.Mesh()");
                sb.AppendLine("for s in shapes:");
                sb.AppendLine($"    m.addFacets(s.tessellate({tolMm.ToString("0.####", CultureInfo.InvariantCulture)}))");
                sb.AppendLine($"m.write(r\"{outPath}\")");
                File.WriteAllText(script, sb.ToString());
            }
            catch (Exception ex)
            {
                notes.Add($"{Path.GetFileName(stepPath)}: cannot write the conversion script — {ex.Message}");
                return false;
            }

            return Run(tool, $"\"{script}\"", stepPath, notes);
        }

        private static bool Run(string tool, string args, string stepPath, List<string> notes)
        {
            try
            {
                var psi = new ProcessStartInfo(tool, args)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    notes.Add($"{Path.GetFileName(stepPath)}: could not start {tool}");
                    return false;
                }

                // Drained BEFORE waiting: a child that fills its stderr pipe blocks on the
                // write, and a parent waiting for exit before reading would deadlock with
                // it. Chatty tessellators make that a real risk, not a theoretical one.
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit(TIMEOUT_MS))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    notes.Add($"{Path.GetFileName(stepPath)}: tessellation timed out after " +
                              $"{TIMEOUT_MS / 1000}s — try a coarser tolerance");
                    return false;
                }

                if (proc.ExitCode != 0)
                {
                    string why = (stderr.Length > 0 ? stderr : stdout).Trim();
                    if (why.Length > 200) why = why.Substring(0, 200) + "...";
                    notes.Add($"{Path.GetFileName(stepPath)}: tessellator exited {proc.ExitCode}" +
                              (why.Length > 0 ? $" — {why}" : ""));
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                notes.Add($"{Path.GetFileName(stepPath)}: {ex.GetType().Name} running the " +
                          $"tessellator — {ex.Message}");
                return false;
            }
        }

        /// <summary>Two minutes. Long enough for a real assembly, short enough that a
        /// tessellator waiting on a prompt cannot hang the game thread indefinitely.</summary>
        private const int TIMEOUT_MS = 120_000;

        private static string? OnPath(string exe)
        {
            try
            {
                string? pathVar = Environment.GetEnvironmentVariable("PATH");
                if (pathVar == null) return null;

                string[] exts = OperatingSystem.IsWindows()
                                ? new[] { ".exe", ".cmd", ".bat", "" }
                                : new[] { "" };

                foreach (string dir in pathVar.Split(Path.PathSeparator))
                {
                    if (dir.Length == 0) continue;
                    foreach (string ext in exts)
                    {
                        string candidate = Path.Combine(dir, exe + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
