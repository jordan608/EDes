// ═══════════════════════════════════════════════════════════════════════════
//  PdfSchematic.cs — the schematic sheet, read out of the PDF print
//
//  WHY a PDF parser. A fabrication output set contains the schematic only as a
//  print: there is no netlist and no .SchDoc in the folder. The alternative was
//  to SYNTHESISE a schematic from the derived nets and the placement file, but
//  that would be a different drawing from the one the engineer drew -- different
//  layout, different grouping, no sheet symbols, no notes -- and showing it while
//  calling it "the schematic" would be a lie. So the real one is read.
//
//  This is NOT a PDF renderer, and deliberately much less than one:
//
//    • STROKES ONLY. Filled paths are skipped entirely. On a transparent display
//      a filled rectangle is a solid block of voxels that hides everything behind
//      it, and the first fill on the page is the sheet background -- the whole
//      A3 sheet -- which would bury the schematic under itself. Same lesson as
//      the CAD renderer: edges read, surfaces fog. (invariant 13)
//    • No xref table walk. Streams are found by scanning for `stream`/`endstream`
//      and inflating what is between them, which is robust against the broken and
//      linearised xrefs that CAD tools emit, and needs no object graph.
//    • No fonts. Text is positioned from the text matrix and drawn with the app's
//      own voxel font. A schematic's strings are ASCII in practice.
//    • No shading, no images, no transparency, no clipping.
//
//  Coordinates come out in PDF points with Y UP, which is the PDF convention and
//  also the schematic's own. The renderer flips to the display's -Z-is-up.
//
//  Tested against Altium's llPDFLib output; the operator subset is small enough
//  that anything writing plain stroked paths will work.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EDes.Pcb
{
    /// <summary>One straight run of the drawing, in PDF points.</summary>
    public struct SchLine
    {
        public float X1, Y1, X2, Y2;
    }

    /// <summary>One string, with the point it starts at and its height in points.</summary>
    public struct SchText
    {
        public float  X, Y, Size;
        public string Text;
    }

    /// <summary>One sheet of a schematic.</summary>
    public sealed class SchematicSheet
    {
        public string Name = "";
        public readonly List<SchLine> Lines = new();
        public readonly List<SchText> Texts = new();

        public float MinX = float.MaxValue, MinY = float.MaxValue;
        public float MaxX = float.MinValue, MaxY = float.MinValue;

        public bool  HasGeometry => MaxX > MinX && MaxY > MinY;
        public float WidthPt     => HasGeometry ? MaxX - MinX : 0f;
        public float HeightPt    => HasGeometry ? MaxY - MinY : 0f;

        public void Bound(float x, float y)
        {
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
        }
    }

    public static class PdfSchematic
    {
        /// <summary>Does this look like a schematic print rather than some other PDF?
        ///
        /// A fab output set is full of PDFs -- assembly drawings, the BOM, the DRC report --
        /// and importing the BOM as a drawing would fill the volume with a table. Decided
        /// by PATH first because that is what the exporter actually encodes: Altium writes
        /// the schematic into a "Schematic Prints" folder. Content is not sniffed: a BOM
        /// and a schematic are both strokes and text, so there is nothing to tell them
        /// apart at that level, and guessing would be worse than a clear rule.</summary>
        public static bool LooksLikeSchematic(string path)
        {
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return false;

            string full = path.Replace('\\', '/').ToLowerInvariant();
            return full.Contains("/schematic") || full.Contains("schematic print")
                || Path.GetFileNameWithoutExtension(full).Contains("schematic")
                || full.Contains("/sch/");
        }

        /// <summary>Read every sheet from a schematic PDF. Never throws: a PDF this cannot
        /// read adds a note and returns what it managed, because a partially-drawn
        /// schematic is still useful and a hard failure mid-import is not.</summary>
        public static List<SchematicSheet> Load(string path, List<string> notes)
        {
            var sheets = new List<SchematicSheet>();
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                notes.Add($"{Path.GetFileName(path)}: unreadable ({ex.Message})");
                return sheets;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            int page = 0;

            foreach (byte[] raw in InflateStreams(data))
            {
                var sheet = new SchematicSheet { Name = name };
                try { Interpret(Encoding.Latin1.GetString(raw), sheet); }
                catch (Exception ex)
                {
                    notes.Add($"{name}: a content stream failed ({ex.Message}) -- "
                            + "the rest of the sheet is still drawn");
                }

                // A stream with a handful of strokes is a logo or a title-block flourish,
                // not a sheet. The threshold keeps those out of the sheet list, where they
                // would show up as blank pages to page through.
                if (sheet.Lines.Count < 20) continue;

                page++;
                sheet.Name = page > 1 ? $"{name} (sheet {page})" : name;
                sheets.Add(sheet);
            }

            if (sheets.Count == 0)
                notes.Add($"{name}: no drawable content found. If this PDF is a scan "
                        + "rather than a vector print there is nothing to extract -- an "
                        + "image has no lines in it.");

            return sheets;
        }

        // ── Streams ───────────────────────────────────────────────────────────

        /// <summary>Every FlateDecode content stream in the file.
        ///
        /// Streams are located by scanning for the `stream` keyword rather than by walking
        /// the xref table, because the xref is the part CAD exporters most often get wrong
        /// and nothing here needs the object graph -- a content stream is self-describing.
        ///
        /// But the scan alone was not enough, and the two things it got wrong are worth
        /// naming because both were silent:
        ///
        ///   • "endstream" CONTAINS "stream". Matching the bare keyword found a phantom
        ///     stream three bytes into every terminator, and the span from there to the
        ///     NEXT terminator covers a whole object header plus its data -- which
        ///     occasionally inflates to megabytes of nonsense and swallows the rest of the
        ///     file. On the real schematic that produced exactly one 2.8 MB "stream" and
        ///     then nothing, so the parser reported an empty document rather than an error.
        ///
        ///   • The object's /Length and /Filter were ignored. A PDF holds fonts and images
        ///     in streams too; a 2.8 MB image inflates perfectly well and contains no
        ///     drawing operators at all. Requiring FlateDecode and honouring /Length skips
        ///     them by construction instead of by trying and failing.</summary>
        private static IEnumerable<byte[]> InflateStreams(byte[] d)
        {
            var marker = Encoding.ASCII.GetBytes("stream");
            var endTag = Encoding.ASCII.GetBytes("endstream");

            int i = 0;
            while (i + marker.Length < d.Length)
            {
                int at = IndexOf(d, marker, i);
                if (at < 0) break;

                // Not the tail of "endstream".
                if (at >= 3 && d[at - 3] == 'e' && d[at - 2] == 'n' && d[at - 1] == 'd')
                {
                    i = at + marker.Length;
                    continue;
                }

                int p = at + marker.Length;
                if (p < d.Length && d[p] == (byte)'\r') p++;
                if (p < d.Length && d[p] == (byte)'\n') p++;

                // The dictionary immediately before the keyword says how long the stream is
                // and how it is encoded. Only FlateDecode is wanted: a /DCTDecode image
                // would inflate to nothing useful, and skipping it here is cheaper and
                // clearer than discovering it has no operators in it.
                ReadStreamDict(d, at, out int declared, out bool flate);

                int scanEnd = IndexOf(d, endTag, p);
                int end = declared > 0 && p + declared <= d.Length ? p + declared
                                                                   : scanEnd;
                if (end < 0) break;

                if (flate)
                {
                    byte[]? raw = TryInflate(d, p, end - p);
                    if (raw != null && raw.Length > 0) yield return raw;
                }

                // Continue past whichever is FURTHER: the found terminator or the end of
                // the declared length. Taking scanEnd alone resumed the scan inside the
                // stream's own bytes whenever the compressed payload happened to contain
                // the literal "endstream" -- compressed data can contain any byte sequence
                // -- and from there the next object's header could be stepped over,
                // dropping a page with nothing to say why.
                int resume = scanEnd > 0 ? Math.Max(scanEnd, end) : end;
                i = resume + endTag.Length;
            }
        }

        /// <summary>Read /Length and /Filter out of the dictionary that precedes `stream`.
        ///
        /// A backwards window rather than a real object parser: everything needed sits in
        /// the few hundred bytes before the keyword, and a full parse would need the object
        /// graph this deliberately avoids. An indirect /Length ("12 0 R") yields 0, which
        /// falls back to scanning for the terminator.</summary>
        private static void ReadStreamDict(byte[] d, int streamAt, out int length, out bool flate)
        {
            length = 0;
            flate  = false;

            int from = Math.Max(0, streamAt - 512);
            string head = Encoding.Latin1.GetString(d, from, streamAt - from);

            // The ENCLOSING dictionary, found by balancing backwards -- not simply the
            // last "<<".
            //
            // LastIndexOf finds a NESTED dictionary when there is one, and truncating to it
            // cuts off everything before it. For
            //     << /Length 1234 /Filter /FlateDecode /DecodeParms << /Predictor 12 >> >>
            // that left "<< /Predictor 12 >> >>", so /FlateDecode was not found, flate came
            // back false, and the content stream was skipped entirely -- the whole document
            // then reported "no drawable content", which points at the wrong cause.
            int depth = 0, dict = -1;
            for (int b = head.Length - 2; b >= 0; b--)
            {
                if (head[b] == '>' && head[b + 1] == '>') { depth++; b--; continue; }
                if (head[b] == '<' && head[b + 1] == '<')
                {
                    depth--;
                    if (depth <= 0) { dict = b; break; }
                    b--;
                }
            }
            if (dict >= 0) head = head.Substring(dict);

            // Absent /Filter means an uncompressed stream, which is not what is wanted here
            // -- content streams from every real exporter are compressed, and treating raw
            // bytes as deflate input just fails.
            flate = head.Contains("/FlateDecode", StringComparison.Ordinal);

            int at = head.IndexOf("/Length", StringComparison.Ordinal);
            if (at < 0) return;

            int k = at + "/Length".Length;
            while (k < head.Length && head[k] == ' ') k++;

            int start = k;
            while (k < head.Length && char.IsDigit(head[k])) k++;
            if (k == start) return;

            // "/Length 12 0 R" is an indirect reference, not a length. Detected by what
            // follows the number: a second integer and an R.
            string after = head.Substring(k).TrimStart();
            if (after.Length > 0 && char.IsDigit(after[0])) return;

            int.TryParse(head.AsSpan(start, k - start), out length);
        }

        /// <summary>Inflate a zlib or raw-deflate span, or null.
        ///
        /// Both are tried because the two-byte zlib header is present in most PDFs and
        /// absent in some. Skipping it by hand and retrying is cheaper than deciding which
        /// it is from the bytes. Partial output is KEPT: a stream that hits its end marker
        /// with trailing checksum bytes still decoded everything that mattered, and
        /// discarding it would lose a whole page over four bytes.</summary>
        private static byte[]? TryInflate(byte[] d, int off, int len)
        {
            if (len <= 2 || off < 0 || off + len > d.Length) return null;

            byte[]? best = null;
            for (int skip = 2; skip >= 0; skip -= 2)
            {
                var outp = new MemoryStream();
                try
                {
                    using var src = new MemoryStream(d, off + skip, len - skip);
                    using var inf = new DeflateStream(src, CompressionMode.Decompress);
                    inf.CopyTo(outp, 16 * 1024);
                }
                catch { /* keep whatever was decoded before it gave up */ }

                if (outp.Length > 0 && (best == null || outp.Length > best.Length))
                    best = outp.ToArray();
            }
            return best;
        }

        private static bool Match(byte[] d, int at, byte[] pat)
        {
            if (at + pat.Length > d.Length) return false;
            for (int k = 0; k < pat.Length; k++) if (d[at + k] != pat[k]) return false;
            return true;
        }

        private static int IndexOf(byte[] d, byte[] pat, int from)
        {
            for (int i = from; i + pat.Length <= d.Length; i++)
                if (Match(d, i, pat)) return i;
            return -1;
        }

        // ── The content-stream interpreter ────────────────────────────────────

        private struct Mat
        {
            public float A, B, C, D, E, F;
            public static Mat Identity => new Mat { A = 1, B = 0, C = 0, D = 1, E = 0, F = 0 };

            public void Apply(float x, float y, out float ox, out float oy)
            {
                ox = A * x + C * y + E;
                oy = B * x + D * y + F;
            }

            public static Mat Mul(in Mat m, in Mat n) => new Mat
            {
                A = m.A * n.A + m.B * n.C,
                B = m.A * n.B + m.B * n.D,
                C = m.C * n.A + m.D * n.C,
                D = m.C * n.B + m.D * n.D,
                E = m.E * n.A + m.F * n.C + n.E,
                F = m.E * n.B + m.F * n.D + n.F,
            };

            /// <summary>Scale this matrix applies, for turning a text size in text space
            /// into one in page space.</summary>
            public float Scale => MathF.Sqrt(MathF.Abs(A * D - B * C));
        }

        private static void Interpret(string s, SchematicSheet sheet)
        {
            var stack   = new List<Mat> { Mat.Identity };
            var ctm     = Mat.Identity;
            var ops     = new List<string>(8);

            // The path being built, in PAGE space (transformed as it is appended, so a
            // `cm` mid-path cannot retroactively move points already added).
            var path    = new List<(float x, float y)>();
            var starts  = new List<int>();       // subpath start indices, for `h`
            float curX = 0, curY = 0, startX = 0, startY = 0;

            Mat textMat = Mat.Identity;
            float pendingSize = 0f;

            int i = 0;
            while (i < s.Length)
            {
                string tok = NextToken(s, ref i);
                if (tok.Length == 0) break;

                // Operands accumulate; an operator consumes them.
                if (IsNumber(tok) || tok[0] == '/' || tok[0] == '(' || tok[0] == '[')
                {
                    ops.Add(tok);
                    if (ops.Count > 64) ops.RemoveRange(0, 32);   // runaway guard
                    continue;
                }

                switch (tok)
                {
                    case "q":
                        stack.Add(ctm);
                        break;

                    case "Q":
                        if (stack.Count > 1)
                        {
                            ctm = stack[^1];
                            stack.RemoveAt(stack.Count - 1);
                        }
                        break;

                    case "cm":
                        if (ops.Count >= 6)
                        {
                            var m = new Mat
                            {
                                A = Num(ops[^6]), B = Num(ops[^5]), C = Num(ops[^4]),
                                D = Num(ops[^3]), E = Num(ops[^2]), F = Num(ops[^1]),
                            };
                            ctm = Mat.Mul(m, ctm);
                        }
                        break;

                    case "m":
                        if (ops.Count >= 2)
                        {
                            ctm.Apply(Num(ops[^2]), Num(ops[^1]), out curX, out curY);
                            startX = curX; startY = curY;
                            starts.Add(path.Count);
                            path.Add((curX, curY));
                        }
                        break;

                    case "l":
                        if (ops.Count >= 2)
                        {
                            ctm.Apply(Num(ops[^2]), Num(ops[^1]), out curX, out curY);
                            path.Add((curX, curY));
                        }
                        break;

                    case "c":
                    case "v":
                    case "y":
                        AppendCurve(tok, ops, ref ctm, path, ref curX, ref curY);
                        break;

                    case "re":
                        if (ops.Count >= 4)
                        {
                            float x = Num(ops[^4]), y = Num(ops[^3]);
                            float w = Num(ops[^2]), h = Num(ops[^1]);
                            starts.Add(path.Count);
                            AddPt(ctm, path, x,     y);
                            AddPt(ctm, path, x + w, y);
                            AddPt(ctm, path, x + w, y + h);
                            AddPt(ctm, path, x,     y + h);
                            AddPt(ctm, path, x,     y);
                            curX = x; curY = y;
                        }
                        break;

                    case "h":
                        if (path.Count > 0) path.Add((startX, startY));
                        break;

                    // ── Painting ──────────────────────────────────────────────
                    // Stroking ops keep the path. FILLS ARE DISCARDED -- see the header:
                    // the largest fill on the page is the sheet background, and a filled
                    // region on a transparent display hides everything behind it.
                    case "S":
                    case "s":
                        if (tok == "s" && path.Count > 0) path.Add((startX, startY));
                        Stroke(path, starts, sheet);
                        path.Clear(); starts.Clear();
                        break;

                    case "f":
                    case "F":
                    case "f*":
                    case "B":
                    case "B*":
                    case "b":
                    case "b*":
                    case "n":
                        path.Clear(); starts.Clear();
                        break;

                    // ── Text ──────────────────────────────────────────────────
                    case "BT":
                        textMat = Mat.Identity;
                        break;

                    case "Tf":
                        // "/TT1 <size> Tf" — the size is in text space and is usually 1
                        // here, with the real scale carried by Tm. Both are combined below.
                        if (ops.Count >= 1) pendingSize = Num(ops[^1]);
                        break;

                    case "Tm":
                        if (ops.Count >= 6)
                            textMat = new Mat
                            {
                                A = Num(ops[^6]), B = Num(ops[^5]), C = Num(ops[^4]),
                                D = Num(ops[^3]), E = Num(ops[^2]), F = Num(ops[^1]),
                            };
                        break;

                    case "Td":
                    case "TD":
                        if (ops.Count >= 2)
                        {
                            var t = new Mat
                            {
                                A = 1, B = 0, C = 0, D = 1,
                                E = Num(ops[^2]), F = Num(ops[^1]),
                            };
                            textMat = Mat.Mul(t, textMat);
                        }
                        break;

                    case "Tj":
                    case "'":
                        if (ops.Count >= 1)
                            AddText(sheet, Unescape(ops[^1]), textMat, ctm, pendingSize);
                        break;

                    case "TJ":
                        // An array of strings and kerning numbers. The kerning is ignored:
                        // it moves glyphs by fractions of a point, which is far below one
                        // voxel, so honouring it would cost parsing for no visible effect.
                        if (ops.Count >= 1)
                            AddText(sheet, JoinArray(ops[^1]), textMat, ctm, pendingSize);
                        break;
                }

                ops.Clear();
            }

            // A stream that ended mid-path still has geometry worth keeping.
            if (path.Count > 1) Stroke(path, starts, sheet);
        }

        private static void AddPt(in Mat ctm, List<(float, float)> path, float x, float y)
        {
            ctm.Apply(x, y, out float ox, out float oy);
            path.Add((ox, oy));
        }

        /// <summary>Flatten a Bezier into chords.
        ///
        /// Eight segments, fixed. Adaptive flattening by curvature would be the right answer
        /// for a printer; here the output is quantised to a ~0.03-unit voxel grid a moment
        /// later, so anything finer is discarded downstream. Schematics are mostly
        /// orthogonal lines anyway -- the curves are logos and the occasional arc.</summary>
        private static void AppendCurve(string op, List<string> ops, ref Mat ctm,
                                        List<(float x, float y)> path,
                                        ref float curX, ref float curY)
        {
            const int STEPS = 8;

            float x0 = curX, y0 = curY;
            float x1, y1, x2, y2, x3, y3;

            switch (op)
            {
                case "c" when ops.Count >= 6:
                    ctm.Apply(Num(ops[^6]), Num(ops[^5]), out x1, out y1);
                    ctm.Apply(Num(ops[^4]), Num(ops[^3]), out x2, out y2);
                    ctm.Apply(Num(ops[^2]), Num(ops[^1]), out x3, out y3);
                    break;
                case "v" when ops.Count >= 4:
                    x1 = x0; y1 = y0;
                    ctm.Apply(Num(ops[^4]), Num(ops[^3]), out x2, out y2);
                    ctm.Apply(Num(ops[^2]), Num(ops[^1]), out x3, out y3);
                    break;
                case "y" when ops.Count >= 4:
                    ctm.Apply(Num(ops[^4]), Num(ops[^3]), out x1, out y1);
                    ctm.Apply(Num(ops[^2]), Num(ops[^1]), out x3, out y3);
                    x2 = x3; y2 = y3;
                    break;
                default:
                    return;
            }

            for (int k = 1; k <= STEPS; k++)
            {
                float t = k / (float)STEPS, u = 1f - t;
                float bx = u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3;
                float by = u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
                path.Add((bx, by));
            }
            curX = x3; curY = y3;
        }

        /// <summary>Turn the accumulated path into line segments.
        ///
        /// Subpath starts are honoured, so a `m` in the middle of a path lifts the pen
        /// instead of drawing a line back to wherever the last one ended -- without that,
        /// a multi-part path grows a spurious diagonal across the sheet for every jump.</summary>
        private static void Stroke(List<(float x, float y)> path, List<int> starts,
                                   SchematicSheet sheet)
        {
            for (int i = 1; i < path.Count; i++)
            {
                if (starts.Contains(i)) continue;      // pen lift

                var a = path[i - 1];
                var b = path[i];

                // Zero-length segments are common (a `m` immediately followed by a close)
                // and cost budget for nothing.
                if (MathF.Abs(a.x - b.x) < 1e-4f && MathF.Abs(a.y - b.y) < 1e-4f) continue;

                sheet.Lines.Add(new SchLine { X1 = a.x, Y1 = a.y, X2 = b.x, Y2 = b.y });
                sheet.Bound(a.x, a.y);
                sheet.Bound(b.x, b.y);
            }
        }

        private static void AddText(SchematicSheet sheet, string text, in Mat textMat,
                                    in Mat ctm, float fontSize)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var full = Mat.Mul(textMat, ctm);
            full.Apply(0f, 0f, out float x, out float y);

            // The rendered height is the font size scaled by BOTH matrices. In this
            // exporter's output the Tf size is 1 and Tm carries the scale; other writers do
            // the reverse, so multiplying handles both without having to detect which.
            float size = MathF.Abs(fontSize <= 0f ? 1f : fontSize) * full.Scale;
            if (size <= 0.01f) return;

            sheet.Texts.Add(new SchText { X = x, Y = y, Size = size, Text = text });
            sheet.Bound(x, y);
        }

        // ── Tokenising ────────────────────────────────────────────────────────

        private static string NextToken(string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]))) i++;
            if (i >= s.Length) return "";

            char c = s[i];

            if (c == '%')                                   // comment to end of line
            {
                while (i < s.Length && s[i] != '\n') i++;
                return NextToken(s, ref i);
            }

            if (c == '(')  return ReadString(s, ref i);
            if (c == '[')  return ReadArray(s, ref i);
            if (c == '<')                                    // hex string or dict — skipped
            {
                int depth = 0;
                while (i < s.Length)
                {
                    if (s[i] == '<') depth++;
                    else if (s[i] == '>') { depth--; if (depth <= 0) { i++; break; } }
                    i++;
                }
                return "<>";
            }

            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])
                                && s[i] != '(' && s[i] != '[' && s[i] != '<'
                                && s[i] != '/') i++;
            if (i == start) i++;                             // never stall
            return s.Substring(start, i - start);
        }

        /// <summary>A PDF literal string, honouring balanced parens and backslash escapes --
        /// both of which appear in real designator text like "(1)" and "R1\(A\)".</summary>
        private static string ReadString(string s, ref int i)
        {
            // Seeded EMPTY. It used to start with "(" and then append the opening paren
            // again from the loop, so every string came back as "((R1)" -- and Unescape,
            // which strips one character from each end, handed back "(R1". Every piece of
            // text on the sheet carried a stray bracket.
            var sb = new StringBuilder();
            int depth = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { sb.Append(c).Append(s[++i]); continue; }
                if (c == '(') { depth++; sb.Append(c); continue; }
                if (c == ')')
                {
                    depth--;
                    sb.Append(c);
                    if (depth == 0) { i++; break; }
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string ReadArray(string s, ref int i)
        {
            var sb = new StringBuilder();
            int depth = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '[') depth++;
                else if (c == ']') { depth--; sb.Append(c); if (depth == 0) { i++; break; } continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Unescape(string tok)
        {
            if (tok.Length < 2 || tok[0] != '(') return "";
            string body = tok.Substring(1, tok.Length - 2);

            var sb = new StringBuilder(body.Length);
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] != '\\') { sb.Append(body[i]); continue; }
                if (++i >= body.Length) break;
                switch (body[i])
                {
                    case 'n': sb.Append(' '); break;      // a newline inside one string
                    case 'r': sb.Append(' '); break;      // is a space on one HUD row
                    case 't': sb.Append(' '); break;
                    case '(': sb.Append('('); break;
                    case ')': sb.Append(')'); break;
                    case '\\': sb.Append('\\'); break;
                    default:
                        // Octal escape: \053 and friends.
                        if (body[i] >= '0' && body[i] <= '7')
                        {
                            int v = 0, n = 0;
                            while (n < 3 && i < body.Length && body[i] >= '0' && body[i] <= '7')
                            { v = v * 8 + (body[i] - '0'); i++; n++; }
                            i--;
                            sb.Append((char)v);
                        }
                        else sb.Append(body[i]);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Pull the strings out of a TJ array, dropping the kerning numbers.</summary>
        private static string JoinArray(string arr)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != '(') continue;
                int j = i;
                string lit = ReadString(arr, ref j);
                sb.Append(Unescape(lit));
                i = j - 1;
            }
            return sb.ToString();
        }

        private static bool IsNumber(string t)
        {
            if (t.Length == 0) return false;
            char c = t[0];
            return char.IsDigit(c) || c == '-' || c == '+' || c == '.';
        }

        private static float Num(string t)
            => float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
               && float.IsFinite(v) ? v : 0f;
    }
}
