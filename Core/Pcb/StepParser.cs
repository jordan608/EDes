// ═══════════════════════════════════════════════════════════════════════════
//  StepParser.cs — STEP (ISO 10303-21) to an edge wireframe, no CAD kernel
//
//  WHY THIS EXISTS AT ALL, given "STEP needs a geometry kernel" is true:
//
//  It needs one to evaluate SURFACES. Trimmed NURBS patches, cylinders clipped
//  by edge loops — that is genuine kernel work and this file does not attempt
//  it. But a transparent volumetric display does not want surfaces: with no
//  occlusion, a filled or densely-sampled surface shows its own back faces
//  through its front and the part reads as fog. What reads as CAD is the EDGE
//  set, and edges are cheap. Measured on a real Altium AP214 export:
//
//      LINE                       1083   (84.8%)
//      CIRCLE                      138   (10.8%)
//      B_SPLINE_CURVE_WITH_KNOTS    76    (5.9%)
//
//  95.6% of edges are a straight line or a circular arc. Those need arithmetic,
//  not a kernel. So the expensive half of STEP is also the half we least want
//  to look at, which is what makes this subset worth having — the same bargain
//  GerberParser strikes with the RS-274X spec.
//
//  WHAT IS HANDLED
//    • The ISO-10303-21 exchange structure: #id = TYPE(args), strings with ''
//      escapes, $ unset, * derived, .ENUM., nested lists, and COMPLEX instances
//      (#39 = ( A(..) B(..) C(..) );) which AP214 uses for units and for the
//      assembly transform records.
//    • Units: SI_UNIT prefix (.MILLI./.CENTI./…) and CONVERSION_BASED_UNIT, so
//      an inch or metre file lands at the right size instead of 25.4x out.
//    • The assembly: SHAPE_DEFINITION_REPRESENTATION and
//      CONTEXT_DEPENDENT_SHAPE_REPRESENTATION → REPRESENTATION_RELATIONSHIP_-
//      WITH_TRANSFORMATION → ITEM_DEFINED_TRANSFORMATION, walked recursively
//      with accumulated placement. Without this every component collapses onto
//      the origin in a single heap, which is the classic wrong-looking STEP
//      import.
//    • Geometry: MANIFOLD_SOLID_BREP / SHELL_BASED_SURFACE_MODEL → faces →
//      EDGE_LOOP → EDGE_CURVE, de-duplicated (every edge is referenced by two
//      ORIENTED_EDGEs, so drawing them as found doubles the voxel cost for no
//      visible gain).
//    • LINE and CIRCLE tessellated properly, including arc sweep direction from
//      the edge sense — get that wrong and arcs appear as their own complement.
//    • Colour from STYLED_ITEM → …→ COLOUR_RGB, and the PRODUCT name per solid.
//    • A shading normal per edge, averaged from the PLANE faces meeting there. This
//      is the one thing that lets a wireframe be lit at all: N·L needs a normal and
//      an edge only has a tangent, but the face walk is already happening and a
//      PLANE states its normal exactly. Curved faces contribute nothing, so their
//      edges render unlit rather than mis-lit — see CadEdge.HasNormal.
//
//  WHAT IS NOT, each counted as a note rather than dropped in silence
//    • B_SPLINE_CURVE_WITH_KNOTS and the other analytic curves (ELLIPSE,
//      HYPERBOLA, PARABOLA, curve-on-surface) are drawn as the straight chord
//      between their end vertices. On mechanical parts these are fillet blends
//      and the chord is visually close; the note reports how many were
//      approximated so it is never a silent lie.
//    • Surfaces. Deliberate — see above.
//
//  Output is millimetres in the board frame, Z up, matching MeshLoader so
//  PcbRenderer can treat a CAD solid and a mesh cloud the same way.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EDes.Pcb
{
    public static class StepParser
    {
        public static readonly string[] StepExtensions = { ".step", ".stp" };

        public static bool IsStep(string path)
            => Array.IndexOf(StepExtensions,
                             Path.GetExtension(path).ToLowerInvariant()) >= 0;

        /// <summary>Chord tolerance for tessellating arcs, in millimetres. 0.05 mm is
        /// finer than the display can resolve on any board that fits the volume, so the
        /// limit on smoothness is the voxel spacing, not this.</summary>
        private const double CHORD_TOL_MM = 0.05;

        // ─────────────────────────────────────────────────────────────────────
        //  Value model
        // ─────────────────────────────────────────────────────────────────────

        private enum VKind { Unset, Ref, Num, Str, Enum, List }

        private sealed class Val
        {
            public VKind Kind;
            public int    Ref;
            public double Num;
            public string Str = "";
            public List<Val>? Items;

            public static readonly Val Unset = new Val { Kind = VKind.Unset };

            public int    AsRef  => Kind == VKind.Ref ? Ref : 0;
            public double AsNum  => Kind == VKind.Num ? Num : 0.0;
            public List<Val> AsList => Items ?? EmptyList;
            private static readonly List<Val> EmptyList = new();
        }

        /// <summary>One simple record. A complex instance turns into several of these
        /// sharing an id, which is why lookups go by (id, type) not id alone.</summary>
        private sealed class Rec
        {
            public string    Type = "";
            public List<Val> Args = new();

            public Val Arg(int i) => i >= 0 && i < Args.Count ? Args[i] : Val.Unset;
        }

        private sealed class StepFile
        {
            // An id maps to one record normally, or several for a complex instance.
            public readonly Dictionary<int, List<Rec>> ById = new();

            public Rec? Get(int id, string type)
            {
                if (id == 0 || !ById.TryGetValue(id, out var list)) return null;
                foreach (var r in list) if (r.Type == type) return r;
                return null;
            }

            /// <summary>First record at this id whose type is any of <paramref name="types"/>.
            /// Subtype-tolerant lookup: a face may be ADVANCED_FACE or FACE_SURFACE and
            /// the caller does not care which.</summary>
            public Rec? GetAny(int id, params string[] types)
            {
                if (id == 0 || !ById.TryGetValue(id, out var list)) return null;
                foreach (var t in types)
                    foreach (var r in list) if (r.Type == t) return r;
                return null;
            }

            public List<Rec> All(string type)
            {
                var hits = new List<Rec>();
                foreach (var kv in ById)
                    foreach (var r in kv.Value) if (r.Type == type) hits.Add(r);
                return hits;
            }

            public IEnumerable<KeyValuePair<int, List<Rec>>> Entries => ById;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Affine transform (3x4). Enough for assembly placement.
        // ─────────────────────────────────────────────────────────────────────

        private struct Xf
        {
            public double XX, XY, XZ, TX;
            public double YX, YY, YZ, TY;
            public double ZX, ZY, ZZ, TZ;

            public static Xf Identity => new Xf
            {
                XX = 1, YY = 1, ZZ = 1,
            };

            public void Apply(double x, double y, double z,
                              out double ox, out double oy, out double oz)
            {
                ox = XX * x + XY * y + XZ * z + TX;
                oy = YX * x + YY * y + YZ * z + TY;
                oz = ZX * x + ZY * y + ZZ * z + TZ;
            }

            /// <summary>this ∘ inner — apply inner first, then this.</summary>
            public Xf Compose(in Xf inner)
            {
                Xf r = default;
                r.XX = XX * inner.XX + XY * inner.YX + XZ * inner.ZX;
                r.XY = XX * inner.XY + XY * inner.YY + XZ * inner.ZY;
                r.XZ = XX * inner.XZ + XY * inner.YZ + XZ * inner.ZZ;
                r.YX = YX * inner.XX + YY * inner.YX + YZ * inner.ZX;
                r.YY = YX * inner.XY + YY * inner.YY + YZ * inner.ZY;
                r.YZ = YX * inner.XZ + YY * inner.YZ + YZ * inner.ZZ;
                r.ZX = ZX * inner.XX + ZY * inner.YX + ZZ * inner.ZX;
                r.ZY = ZX * inner.XY + ZY * inner.YY + ZZ * inner.ZY;
                r.ZZ = ZX * inner.XZ + ZY * inner.YZ + ZZ * inner.ZZ;
                Apply(inner.TX, inner.TY, inner.TZ, out r.TX, out r.TY, out r.TZ);
                return r;
            }

            /// <summary>Inverse of a rigid placement — transpose the rotation, then undo
            /// the translation. Valid because AXIS2_PLACEMENT_3D bases are orthonormal.</summary>
            public Xf InverseRigid()
            {
                Xf r = default;
                r.XX = XX; r.XY = YX; r.XZ = ZX;
                r.YX = XY; r.YY = YY; r.YZ = ZY;
                r.ZX = XZ; r.ZY = YZ; r.ZZ = ZZ;
                r.TX = -(r.XX * TX + r.XY * TY + r.XZ * TZ);
                r.TY = -(r.YX * TX + r.YY * TY + r.YZ * TZ);
                r.TZ = -(r.ZX * TX + r.ZY * TY + r.ZZ * TZ);
                return r;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public entry point
        // ─────────────────────────────────────────────────────────────────────

        public static CadModel? TryLoad(string path, List<string> notes,
                                        bool wantSurfaces = true)
        {
            var model = new CadModel { SourceName = Path.GetFileName(path) };
            try
            {
                string text = File.ReadAllText(path);
                var file = Tokenize(text, model);
                if (file.ById.Count == 0)
                {
                    notes.Add($"{model.SourceName}: no STEP entities found (not a STEP file?)");
                    return null;
                }

                double unitMm = FindLengthUnitMm(file, model);
                var ctx = new BuildContext(file, model, unitMm, wantSurfaces);
                ctx.Build();

                model.RecomputeBounds();

                if (!model.HasGeometry)
                {
                    notes.Add($"{model.SourceName}: parsed {file.ById.Count} entities but " +
                              "produced no edges");
                    return null;
                }

                foreach (var n in model.Notes) notes.Add($"{model.SourceName}: {n}");
                return model;
            }
            catch (Exception ex)
            {
                notes.Add($"{model.SourceName}: {ex.GetType().Name} — {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tokenizer
        // ─────────────────────────────────────────────────────────────────────
        //  Hand-rolled rather than regex: STEP strings can contain ; # ( ) and
        //  comments, so any line- or character-based shortcut mis-splits real
        //  files. This walks the text once, string- and comment-aware.

        private static StepFile Tokenize(string text, CadModel model)
        {
            var file = new StepFile();

            int start = text.IndexOf("DATA;", StringComparison.OrdinalIgnoreCase);
            int i     = start < 0 ? 0 : start + 5;
            int end   = text.IndexOf("ENDSEC;", i, StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = text.Length;

            while (i < end)
            {
                // Find the next '#id ='
                while (i < end && text[i] != '#')
                {
                    if (IsCommentStart(text, i)) { i = SkipComment(text, i, end); continue; }
                    i++;
                }
                if (i >= end) break;

                int idStart = ++i;
                while (i < end && char.IsDigit(text[i])) i++;
                if (i == idStart) continue;
                if (!int.TryParse(text.AsSpan(idStart, i - idStart), out int id)) continue;

                // Expect '='
                while (i < end && (text[i] == ' ' || text[i] == '\t' ||
                                   text[i] == '\r' || text[i] == '\n')) i++;
                if (i >= end || text[i] != '=') continue;
                i++;

                // Read the body up to the terminating ';' at depth 0, outside strings.
                int bodyStart = i;
                int depth = 0;
                bool inStr = false;
                while (i < end)
                {
                    char c = text[i];
                    if (inStr)
                    {
                        if (c == '\'')
                        {
                            // '' is an escaped quote, not a terminator.
                            if (i + 1 < end && text[i + 1] == '\'') i++;
                            else inStr = false;
                        }
                        i++;
                        continue;
                    }
                    if (c == '\'') { inStr = true; i++; continue; }
                    if (IsCommentStart(text, i)) { i = SkipComment(text, i, end); continue; }
                    if (c == '(') { depth++; i++; continue; }
                    if (c == ')') { depth--; i++; continue; }
                    if (c == ';' && depth <= 0) break;
                    i++;
                }

                string body = text.Substring(bodyStart, Math.Min(i, end) - bodyStart).Trim();
                i++;   // step past ';'

                ParseInstance(id, body, file, model);
            }

            return file;
        }

        private static bool IsCommentStart(string s, int i)
            => i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*';

        private static int SkipComment(string s, int i, int end)
        {
            int close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return close < 0 || close > end ? end : close + 2;
        }

        /// <summary>One '#id = ...' body. Either a single TYPE(args), or a complex
        /// instance '( TYPE(args) TYPE(args) … )' which AP214 uses for units and for
        /// the assembly transform records.</summary>
        private static void ParseInstance(int id, string body, StepFile file, CadModel model)
        {
            var recs = new List<Rec>();
            int p = 0;
            SkipWs(body, ref p);

            if (p < body.Length && body[p] == '(')
            {
                // Complex instance: a run of TYPE(args) inside one pair of parens.
                int close = MatchParen(body, p);
                string inner = body.Substring(p + 1, Math.Max(0, close - p - 1));
                int q = 0;
                while (true)
                {
                    SkipWs(inner, ref q);
                    if (q >= inner.Length) break;
                    var r = ReadRecord(inner, ref q);
                    if (r == null) break;
                    recs.Add(r);
                }
            }
            else
            {
                var r = ReadRecord(body, ref p);
                if (r != null) recs.Add(r);
            }

            if (recs.Count > 0) file.ById[id] = recs;
        }

        private static Rec? ReadRecord(string s, ref int p)
        {
            SkipWs(s, ref p);
            int nameStart = p;
            while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
            if (p == nameStart) return null;

            var rec = new Rec { Type = s.Substring(nameStart, p - nameStart) };

            SkipWs(s, ref p);
            if (p >= s.Length || s[p] != '(') return rec;

            int close = MatchParen(s, p);
            string args = s.Substring(p + 1, Math.Max(0, close - p - 1));
            p = close + 1;

            rec.Args = ParseArgList(args);
            return rec;
        }

        private static int MatchParen(string s, int open)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'') i++;
                        else inStr = false;
                    }
                    continue;
                }
                if (c == '\'') { inStr = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return i; }
            }
            return s.Length - 1;
        }

        private static List<Val> ParseArgList(string s)
        {
            var list = new List<Val>();
            int p = 0;
            while (true)
            {
                SkipWs(s, ref p);
                if (p >= s.Length) break;

                var v = ParseValue(s, ref p);
                list.Add(v);

                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p >= s.Length) break;
                // Unexpected character — skip it rather than spin.
                if (s[p] != ',') p++;
            }
            return list;
        }

        private static Val ParseValue(string s, ref int p)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) return Val.Unset;

            char c = s[p];

            if (c == '$') { p++; return Val.Unset; }
            if (c == '*') { p++; return Val.Unset; }

            if (c == '#')
            {
                int st = ++p;
                while (p < s.Length && char.IsDigit(s[p])) p++;
                int.TryParse(s.AsSpan(st, p - st), out int id);
                return new Val { Kind = VKind.Ref, Ref = id };
            }

            if (c == '\'')
            {
                var sb = new StringBuilder();
                p++;
                while (p < s.Length)
                {
                    if (s[p] == '\'')
                    {
                        if (p + 1 < s.Length && s[p + 1] == '\'') { sb.Append('\''); p += 2; continue; }
                        p++; break;
                    }
                    sb.Append(s[p]); p++;
                }
                return new Val { Kind = VKind.Str, Str = sb.ToString() };
            }

            if (c == '.')
            {
                int st = ++p;
                while (p < s.Length && s[p] != '.') p++;
                string e = s.Substring(st, Math.Max(0, p - st));
                if (p < s.Length) p++;
                return new Val { Kind = VKind.Enum, Str = e };
            }

            if (c == '(')
            {
                int close = MatchParen(s, p);
                string inner = s.Substring(p + 1, Math.Max(0, close - p - 1));
                p = close + 1;
                return new Val { Kind = VKind.List, Items = ParseArgList(inner) };
            }

            // A number, or a bare keyword we do not care about.
            int ns = p;
            while (p < s.Length && s[p] != ',' ) p++;
            string tok = s.Substring(ns, p - ns).Trim();
            if (TryParseStepNumber(tok, out double d))
                return new Val { Kind = VKind.Num, Num = d };
            return new Val { Kind = VKind.Str, Str = tok };
        }

        /// <summary>STEP reals include forms .NET rejects outright: '1.' is fine but
        /// '1.E-05' and '.5' are not, and both appear in real exports.</summary>
        private static bool TryParseStepNumber(string tok, out double d)
        {
            d = 0;
            if (tok.Length == 0) return false;

            // Normalise '1.E-05' → '1.0E-05' and '.5' → '0.5'.
            int e = tok.IndexOfAny(new[] { 'E', 'e' });
            if (e > 0 && tok[e - 1] == '.') tok = tok.Insert(e, "0");
            if (tok[0] == '.') tok = "0" + tok;
            else if (tok.Length > 1 && (tok[0] == '-' || tok[0] == '+') && tok[1] == '.')
                tok = tok[0] + "0" + tok.Substring(1);

            return double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        private static void SkipWs(string s, ref int p)
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { p++; continue; }
                if (IsCommentStart(s, p)) { p = SkipComment(s, p, s.Length); continue; }
                break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Units
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Millimetres per file unit. Wrong by 25.4x is the classic STEP import
        /// bug, so the assumption is always reported when it has to be guessed.</summary>
        private static double FindLengthUnitMm(StepFile file, CadModel model)
        {
            // CONVERSION_BASED_UNIT names the unit outright (INCH, FOOT, …).
            foreach (var r in file.All("CONVERSION_BASED_UNIT"))
            {
                string name = r.Arg(0).Kind == VKind.Str ? r.Arg(0).Str.ToUpperInvariant() : "";
                if (name.Contains("INCH")) return 25.4;
                if (name.Contains("FOOT")) return 304.8;
                if (name.Contains("MIL") && !name.Contains("MILLI")) return 0.0254;
            }

            foreach (var kv in file.Entries)
            foreach (var r in kv.Value)
            {
                if (r.Type != "SI_UNIT") continue;
                // SI_UNIT(prefix, name) — only the LENGTH one matters, and a length
                // SI_UNIT is the one sharing an id with a LENGTH_UNIT record.
                bool isLength = false;
                foreach (var sib in kv.Value) if (sib.Type == "LENGTH_UNIT") isLength = true;
                if (!isLength) continue;

                string name   = r.Arg(1).Kind == VKind.Enum ? r.Arg(1).Str : "";
                string prefix = r.Arg(0).Kind == VKind.Enum ? r.Arg(0).Str : "";
                if (name != "METRE") continue;

                switch (prefix)
                {
                    case "MILLI": return 1.0;
                    case "CENTI": return 10.0;
                    case "DECI":  return 100.0;
                    case "":      return 1000.0;      // bare metre
                    case "MICRO": return 0.001;
                    case "KILO":  return 1_000_000.0;
                    default:      return 1.0;
                }
            }

            model.Notes.Add("no length unit found, assuming millimetres");
            return 1.0;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build: assembly walk + edge extraction
        // ─────────────────────────────────────────────────────────────────────

        private sealed class BuildContext
        {
            private readonly StepFile _f;
            private readonly CadModel _m;
            private readonly double   _unit;

            // Colour per styled item target id.
            private readonly Dictionary<int, int> _colourByItem = new();

            // shape-representation id → product name.
            private readonly Dictionary<int, string> _nameByRep = new();

            // parent rep id → list of (child rep id, transform)
            private readonly Dictionary<int, List<(int child, Xf xf)>> _children = new();

            private int _splineApprox, _otherApprox, _skippedSolids;
            private int _facesWithHoles, _facesTooBig, _facesUntriangulated;
            private readonly bool _wantFaces;
            private readonly HashSet<int> _seenEdgeCurves = new();

            // Edge id → summed adjacent-face normals, and the order edges were first
            // seen so output stays deterministic. Both are per-shell scratch.
            private readonly Dictionary<int, (double x, double y, double z)> _edgeNormal = new();
            private readonly List<int> _edgeOrder = new();

            public BuildContext(StepFile f, CadModel m, double unitMm, bool wantFaces)
            {
                _f = f; _m = m; _unit = unitMm; _wantFaces = wantFaces;
            }

            public void Build()
            {
                CollectColours();
                CollectRepNames();
                CollectAssembly();

                // Roots: any shape representation that is nobody's child.
                var isChild = new HashSet<int>();
                foreach (var kv in _children)
                    foreach (var c in kv.Value) isChild.Add(c.child);

                var roots = new List<int>();
                foreach (var kv in _f.Entries)
                foreach (var r in kv.Value)
                {
                    if (r.Type != "SHAPE_REPRESENTATION" &&
                        r.Type != "ADVANCED_BREP_SHAPE_REPRESENTATION" &&
                        r.Type != "MANIFOLD_SURFACE_SHAPE_REPRESENTATION" &&
                        r.Type != "GEOMETRICALLY_BOUNDED_SURFACE_SHAPE_REPRESENTATION")
                        continue;
                    if (!isChild.Contains(kv.Key)) roots.Add(kv.Key);
                }

                if (roots.Count == 0)
                {
                    // No assembly structure at all — take every solid where it lies.
                    _m.Notes.Add("no assembly structure found, solids taken untransformed");
                    foreach (var kv in _f.Entries)
                    foreach (var r in kv.Value)
                        if (r.Type == "MANIFOLD_SOLID_BREP" || r.Type == "BREP_WITH_VOIDS")
                            EmitSolid(kv.Key, r, Xf.Identity, "", "");
                }
                else
                {
                    foreach (int root in roots)
                        WalkRep(root, Xf.Identity, 0, new List<string>());
                }

                if (_splineApprox > 0)
                    _m.Notes.Add($"{_splineApprox} spline edge(s) drawn as straight chords");
                if (_otherApprox > 0)
                    _m.Notes.Add($"{_otherApprox} edge(s) of unsupported curve type drawn as chords");
                if (_skippedSolids > 0)
                    _m.Notes.Add($"{_skippedSolids} solid(s) had no usable edges");
                if (_facesWithHoles > 0)
                    _m.Notes.Add($"{_facesWithHoles} face(s) with holes left unfilled " +
                                 "(filling them would cover the openings)");
                if (_facesTooBig > 0)
                    _m.Notes.Add($"{_facesTooBig} face(s) had too complex a boundary to fill");
                if (_facesUntriangulated > 0)
                    _m.Notes.Add($"{_facesUntriangulated} face(s) could not be triangulated");
            }

            // ── Assembly ──────────────────────────────────────────────────────

            private void CollectRepNames()
            {
                // SHAPE_DEFINITION_REPRESENTATION(definition, used_representation)
                //   definition        → PRODUCT_DEFINITION_SHAPE → PRODUCT_DEFINITION
                //   → PRODUCT_DEFINITION_FORMATION → PRODUCT(name)
                foreach (var sdr in _f.All("SHAPE_DEFINITION_REPRESENTATION"))
                {
                    int defId = sdr.Arg(0).AsRef;
                    int repId = sdr.Arg(1).AsRef;
                    if (repId == 0) continue;

                    string name = ProductNameFromDefinitionShape(defId);
                    if (name.Length > 0) _nameByRep[repId] = name;
                }
            }

            private string ProductNameFromDefinitionShape(int pdsId)
            {
                var pds = _f.GetAny(pdsId, "PRODUCT_DEFINITION_SHAPE");
                if (pds == null) return "";
                return ProductNameFromDefinition(pds.Arg(2).AsRef);
            }

            private string ProductNameFromDefinition(int pdId)
            {
                var pd = _f.GetAny(pdId, "PRODUCT_DEFINITION");
                if (pd == null) return "";
                var form = _f.GetAny(pd.Arg(2).AsRef,
                                     "PRODUCT_DEFINITION_FORMATION",
                                     "PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE");
                if (form == null) return "";
                var prod = _f.GetAny(form.Arg(2).AsRef, "PRODUCT");
                if (prod == null) return "";
                return prod.Arg(0).Kind == VKind.Str ? prod.Arg(0).Str : "";
            }

            private void CollectAssembly()
            {
                // CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(rep_relation, represented_shape)
                //   rep_relation is a COMPLEX instance carrying both
                //   REPRESENTATION_RELATIONSHIP(name, desc, rep_1, rep_2) and
                //   REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(item_defined_xf).
                //
                // rep_1 is the CHILD and rep_2 the PARENT in every export seen; the
                // transform maps the child's own origin onto its seat in the parent.
                foreach (var cdsr in _f.All("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
                {
                    int relId = cdsr.Arg(0).AsRef;
                    var rel   = _f.Get(relId, "REPRESENTATION_RELATIONSHIP");
                    if (rel == null) continue;

                    int child  = rel.Arg(2).AsRef;
                    int parent = rel.Arg(3).AsRef;
                    if (child == 0 || parent == 0) continue;

                    Xf xf = Xf.Identity;
                    var wt = _f.Get(relId, "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION");
                    if (wt != null)
                    {
                        var idt = _f.GetAny(wt.Arg(0).AsRef, "ITEM_DEFINED_TRANSFORMATION");
                        if (idt != null)
                        {
                            // Maps placement A onto placement B: B ∘ A⁻¹.
                            Xf a = PlacementXf(idt.Arg(2).AsRef);
                            Xf b = PlacementXf(idt.Arg(3).AsRef);
                            xf = b.Compose(a.InverseRigid());
                        }
                    }

                    if (!_children.TryGetValue(parent, out var list))
                        _children[parent] = list = new List<(int, Xf)>();
                    list.Add((child, xf));
                }
            }

            /// <summary>Does this look like a component designator — one to three letters
            /// then digits (R1, C14, U3, TP7)? Used to pick the useful name out of an
            /// assembly chain that also contains part numbers and exporter boilerplate.</summary>
            private static bool LooksLikeDesignator(string s)
            {
                if (s.Length < 2 || s.Length > 8) return false;
                int i = 0;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                if (i == 0 || i > 3 || i == s.Length) return false;
                for (int j = i; j < s.Length; j++) if (!char.IsDigit(s[j])) return false;
                return true;
            }

            /// <summary>OpenCASCADE stamps its own translator string in as a PRODUCT name
            /// for nodes it synthesises. Carrying that through would label a third of the
            /// solids "Open CASCADE STEP translator 7.5 1.4.1.1".</summary>
            private static bool IsExporterNoise(string s)
                => s.Length == 0
                   || s.Contains("STEP translator", StringComparison.OrdinalIgnoreCase)
                   || s.Contains("Open CASCADE", StringComparison.OrdinalIgnoreCase);

            /// <summary>The most useful label for a solid, given its whole assembly chain.
            /// A designator wins outright; failing that the deepest real name; failing
            /// that nothing, so the caller can say so rather than invent one.</summary>
            private static string BestName(List<string> path)
            {
                for (int i = path.Count - 1; i >= 0; i--)
                    if (LooksLikeDesignator(path[i])) return path[i];
                for (int i = path.Count - 1; i >= 0; i--)
                    if (!IsExporterNoise(path[i])) return path[i];
                return "";
            }

            private void WalkRep(int repId, in Xf xf, int depth, List<string> path)
            {
                if (depth > 32) { _m.Notes.Add("assembly nesting deeper than 32, truncated"); return; }

                var rep = _f.GetAny(repId, "SHAPE_REPRESENTATION",
                                           "ADVANCED_BREP_SHAPE_REPRESENTATION",
                                           "MANIFOLD_SURFACE_SHAPE_REPRESENTATION",
                                           "GEOMETRICALLY_BOUNDED_SURFACE_SHAPE_REPRESENTATION");
                _nameByRep.TryGetValue(repId, out string? nm);
                if (!string.IsNullOrEmpty(nm)) path.Add(nm!);

                if (rep != null)
                {
                    string name = BestName(path);
                    string chain = string.Join(" / ", path);

                    foreach (var item in rep.Arg(1).AsList)
                    {
                        int itemId = item.AsRef;
                        if (itemId == 0) continue;
                        var solid = _f.GetAny(itemId, "MANIFOLD_SOLID_BREP", "BREP_WITH_VOIDS");
                        if (solid != null) { EmitSolid(itemId, solid, xf, name, chain); continue; }

                        var ssm = _f.GetAny(itemId, "SHELL_BASED_SURFACE_MODEL");
                        if (ssm != null) { EmitShellModel(itemId, ssm, xf, name, chain); }
                    }
                }

                if (_children.TryGetValue(repId, out var kids))
                    foreach (var (child, cxf) in kids)
                        WalkRep(child, xf.Compose(cxf), depth + 1, path);

                if (!string.IsNullOrEmpty(nm)) path.RemoveAt(path.Count - 1);
            }

            // ── Colours ───────────────────────────────────────────────────────

            private void CollectColours()
            {
                foreach (var si in _f.All("STYLED_ITEM"))
                {
                    int target = si.Arg(2).AsRef;
                    if (target == 0) continue;
                    foreach (var st in si.Arg(1).AsList)
                    {
                        int col = ColourFromStyleAssignment(st.AsRef);
                        if (col >= 0) { _colourByItem[target] = col; break; }
                    }
                }
            }

            private int ColourFromStyleAssignment(int id)
            {
                var psa = _f.GetAny(id, "PRESENTATION_STYLE_ASSIGNMENT",
                                        "PRESENTATION_STYLE_BY_CONTEXT");
                if (psa == null) return -1;

                foreach (var s in psa.Arg(0).AsList)
                {
                    var ssu = _f.GetAny(s.AsRef, "SURFACE_STYLE_USAGE");
                    if (ssu == null) continue;
                    var side = _f.GetAny(ssu.Arg(1).AsRef, "SURFACE_SIDE_STYLE");
                    if (side == null) continue;
                    foreach (var e in side.Arg(1).AsList)
                    {
                        var fill = _f.GetAny(e.AsRef, "SURFACE_STYLE_FILL_AREA");
                        if (fill == null) continue;
                        var fas = _f.GetAny(fill.Arg(0).AsRef, "FILL_AREA_STYLE");
                        if (fas == null) continue;
                        foreach (var g in fas.Arg(1).AsList)
                        {
                            var fc = _f.GetAny(g.AsRef, "FILL_AREA_STYLE_COLOUR");
                            if (fc == null) continue;
                            var rgb = _f.GetAny(fc.Arg(1).AsRef, "COLOUR_RGB");
                            if (rgb == null) continue;
                            int r = Chan(rgb.Arg(1).AsNum);
                            int gg = Chan(rgb.Arg(2).AsNum);
                            int b = Chan(rgb.Arg(3).AsNum);
                            return (r << 16) | (gg << 8) | b;
                        }
                    }
                }
                return -1;
            }

            private static int Chan(double v) => Math.Clamp((int)Math.Round(v * 255.0), 0, 255);

            // ── Solids ────────────────────────────────────────────────────────

            private void EmitSolid(int id, Rec solid, in Xf xf, string name, string chain)
            {
                var cad = NewSolid(id, name, chain);
                int shellId = solid.Arg(1).AsRef;
                _seenEdgeCurves.Clear();
                AddShell(shellId, xf, cad);
                Finish(cad);
            }

            private void EmitShellModel(int id, Rec ssm, in Xf xf, string name, string chain)
            {
                var cad = NewSolid(id, name, chain);
                _seenEdgeCurves.Clear();
                foreach (var sh in ssm.Arg(1).AsList) AddShell(sh.AsRef, xf, cad);
                Finish(cad);
            }

            private CadSolid NewSolid(int id, string name, string chain)
            {
                var cad = new CadSolid
                {
                    Name  = name,
                    AssemblyPath = chain,
                    MinX  = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue,
                    MaxX  = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue,
                };
                if (_colourByItem.TryGetValue(id, out int c)) cad.Colour = c;
                return cad;
            }

            private void Finish(CadSolid cad)
            {
                if (cad.Edges.Count == 0) { _skippedSolids++; return; }
                _m.Solids.Add(cad);
            }

            private void AddShell(int shellId, in Xf xf, CadSolid cad)
            {
                var shell = _f.GetAny(shellId, "CLOSED_SHELL", "OPEN_SHELL");
                if (shell == null) return;

                // Two passes, and they cannot be merged: an edge's normal is the average
                // of BOTH faces meeting at it, so nothing can be emitted until every face
                // has been visited. Emitting on first sight (which is what the de-dup used
                // to do) would shade every edge from only one of its two neighbours.
                _edgeNormal.Clear();
                _edgeOrder.Clear();

                foreach (var f in shell.Arg(1).AsList)
                {
                    var face = _f.GetAny(f.AsRef, "ADVANCED_FACE", "FACE_SURFACE", "FACE");
                    if (face == null) continue;

                    bool haveN = PlaneNormal(face.Arg(2).AsRef,
                                             out double nx, out double ny, out double nz);

                    if (_wantFaces && haveN) BuildPlanarFace(face, nx, ny, nz, xf, cad);

                    // ADVANCED_FACE's same_sense flag flips the outward direction. Ignoring
                    // it lights half the faces from inside the solid.
                    if (haveN && face.Arg(3).Kind == VKind.Enum && face.Arg(3).Str == "F")
                    { nx = -nx; ny = -ny; nz = -nz; }

                    foreach (var b in face.Arg(1).AsList)
                    {
                        var bound = _f.GetAny(b.AsRef, "FACE_BOUND", "FACE_OUTER_BOUND");
                        if (bound == null) continue;
                        var loop = _f.GetAny(bound.Arg(1).AsRef, "EDGE_LOOP");
                        if (loop == null) continue;

                        foreach (var oe in loop.Arg(1).AsList)
                        {
                            var orient = _f.GetAny(oe.AsRef, "ORIENTED_EDGE");
                            if (orient == null) continue;
                            int ecId = orient.Arg(3).AsRef;

                            if (!_edgeNormal.TryGetValue(ecId, out var acc))
                            {
                                acc = (0, 0, 0);
                                _edgeOrder.Add(ecId);
                            }
                            if (haveN) acc = (acc.x + nx, acc.y + ny, acc.z + nz);
                            _edgeNormal[ecId] = acc;
                        }
                    }
                }

                foreach (int ecId in _edgeOrder)
                {
                    // Still de-duplicated across shells within one solid.
                    if (!_seenEdgeCurves.Add(ecId)) continue;

                    var n = _edgeNormal[ecId];
                    double len = Math.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                    bool has = len > 1e-9;
                    if (has) { n = (n.x / len, n.y / len, n.z / len); }

                    AddEdgeCurve(ecId, xf, cad, has, n.x, n.y, n.z);
                }
            }

            /// <summary>Triangulate one planar face for flat shading.
            ///
            /// The boundary is already available as tessellated edges, so this is polygon
            /// triangulation rather than surface evaluation — which is why it needs no
            /// geometry kernel. Steps: chain the loop in order (honouring each oriented
            /// edge's direction, or the polygon comes out as a star), project onto the
            /// plane's own 2D basis, then ear-clip.
            ///
            /// Faces with more than one bound are SKIPPED, not filled. A second bound is a
            /// hole, and filling the outer boundary while ignoring it would paste solid
            /// material over every clearance and pad opening on the board — confidently
            /// wrong beats absent here.</summary>
            private void BuildPlanarFace(Rec face, double nx, double ny, double nz,
                                         in Xf xf, CadSolid cad)
            {
                var bounds = face.Arg(1).AsList;
                if (bounds.Count == 0) return;
                if (bounds.Count > 1) { _facesWithHoles++; return; }

                var bound = _f.GetAny(bounds[0].AsRef, "FACE_BOUND", "FACE_OUTER_BOUND");
                if (bound == null) return;
                var loop = _f.GetAny(bound.Arg(1).AsRef, "EDGE_LOOP");
                if (loop == null) return;

                // ── Chain the boundary ────────────────────────────────────────
                var px = new List<double>();
                var py = new List<double>();
                var pz = new List<double>();

                foreach (var oe in loop.Arg(1).AsList)
                {
                    var orient = _f.GetAny(oe.AsRef, "ORIENTED_EDGE");
                    if (orient == null) return;
                    if (!EdgePoints(orient.Arg(3).AsRef, out var ex, out var ey, out var ez))
                        return;

                    bool forward = orient.Arg(4).Kind != VKind.Enum || orient.Arg(4).Str == "T";
                    int n = ex.Length;
                    for (int i = 0; i < n; i++)
                    {
                        int k = forward ? i : n - 1 - i;
                        // Skip the vertex shared with the previous edge, or every joint
                        // becomes a zero-length span and the ear clipper stalls on it.
                        if (px.Count > 0 &&
                            Math.Abs(px[px.Count - 1] - ex[k]) < 1e-9 &&
                            Math.Abs(py[py.Count - 1] - ey[k]) < 1e-9 &&
                            Math.Abs(pz[pz.Count - 1] - ez[k]) < 1e-9) continue;
                        px.Add(ex[k]); py.Add(ey[k]); pz.Add(ez[k]);
                    }
                }

                // Drop a closing duplicate of the very first point.
                if (px.Count > 2 &&
                    Math.Abs(px[0] - px[px.Count - 1]) < 1e-9 &&
                    Math.Abs(py[0] - py[py.Count - 1]) < 1e-9 &&
                    Math.Abs(pz[0] - pz[pz.Count - 1]) < 1e-9)
                {
                    px.RemoveAt(px.Count - 1); py.RemoveAt(py.Count - 1); pz.RemoveAt(pz.Count - 1);
                }

                int cnt = px.Count;
                if (cnt < 3) return;
                if (cnt > 4096) { _facesTooBig++; return; }

                // ── Project onto the plane ────────────────────────────────────
                // Any two perpendicular axes in the plane will do; only the triangle
                // topology comes out of the 2D step, and that is basis-independent.
                double ux, uy, uz;
                if (Math.Abs(nz) < 0.9) { ux = -ny; uy = nx; uz = 0; }
                else                    { ux = 0;   uy = -nz; uz = ny; }
                double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
                if (ul < 1e-12) return;
                ux /= ul; uy /= ul; uz /= ul;
                double vx = ny * uz - nz * uy;
                double vy = nz * ux - nx * uz;
                double vz = nx * uy - ny * ux;

                var u2 = new double[cnt];
                var v2 = new double[cnt];
                for (int i = 0; i < cnt; i++)
                {
                    u2[i] = px[i] * ux + py[i] * uy + pz[i] * uz;
                    v2[i] = px[i] * vx + py[i] * vy + pz[i] * vz;
                }

                // Ear clipping assumes CCW; a CW loop would report every ear as invalid.
                double area2 = 0;
                for (int i = 0; i < cnt; i++)
                {
                    int j = (i + 1) % cnt;
                    area2 += u2[i] * v2[j] - u2[j] * v2[i];
                }
                var order = new int[cnt];
                for (int i = 0; i < cnt; i++) order[i] = area2 < 0 ? cnt - 1 - i : i;

                var tris = EarClip(u2, v2, order);
                if (tris.Count == 0) { _facesUntriangulated++; return; }

                // ── Emit ──────────────────────────────────────────────────────
                var fx = new float[tris.Count];
                var fy = new float[tris.Count];
                var fz = new float[tris.Count];
                for (int i = 0; i < tris.Count; i++)
                {
                    int vi = tris[i];
                    xf.Apply(px[vi], py[vi], pz[vi], out double ox, out double oy, out double oz);
                    fx[i] = (float)ox; fy[i] = (float)oy; fz[i] = (float)oz;

                    if (fx[i] < cad.MinX) cad.MinX = fx[i]; if (fx[i] > cad.MaxX) cad.MaxX = fx[i];
                    if (fy[i] < cad.MinY) cad.MinY = fy[i]; if (fy[i] > cad.MaxY) cad.MaxY = fy[i];
                    if (fz[i] < cad.MinZ) cad.MinZ = fz[i]; if (fz[i] > cad.MaxZ) cad.MaxZ = fz[i];
                }

                cad.Faces.Add(new CadFace
                {
                    X = fx, Y = fy, Z = fz,
                    TriCount = tris.Count / 3,
                    HasNormalSet = true,
                    NX = (float)(xf.XX * nx + xf.XY * ny + xf.XZ * nz),
                    NY = (float)(xf.YX * nx + xf.YY * ny + xf.YZ * nz),
                    NZ = (float)(xf.ZX * nx + xf.ZY * ny + xf.ZZ * nz),
                });
            }

            /// <summary>Ear clipping. Returns vertex indices, 3 per triangle.
            ///
            /// Chosen over a fan because CAD faces are routinely non-convex — an L-shaped
            /// pour or a bracket outline fans into triangles that lie outside the shape.
            /// The bail-out counter matters: a self-intersecting or degenerate loop (STEP
            /// exports do contain them) otherwise spins here forever.</summary>
            private static List<int> EarClip(double[] u, double[] v, int[] order)
            {
                var result = new List<int>();
                var poly = new List<int>(order);
                int guard = poly.Count * poly.Count + 16;

                while (poly.Count > 3 && guard-- > 0)
                {
                    bool clipped = false;
                    for (int i = 0; i < poly.Count; i++)
                    {
                        int i0 = poly[(i + poly.Count - 1) % poly.Count];
                        int i1 = poly[i];
                        int i2 = poly[(i + 1) % poly.Count];

                        double cross = (u[i1] - u[i0]) * (v[i2] - v[i0])
                                     - (v[i1] - v[i0]) * (u[i2] - u[i0]);
                        if (cross <= 0) continue;                 // reflex, not an ear

                        bool contains = false;
                        for (int k = 0; k < poly.Count && !contains; k++)
                        {
                            int p = poly[k];
                            if (p == i0 || p == i1 || p == i2) continue;
                            if (PointInTri(u[p], v[p], u[i0], v[i0], u[i1], v[i1], u[i2], v[i2]))
                                contains = true;
                        }
                        if (contains) continue;

                        result.Add(i0); result.Add(i1); result.Add(i2);
                        poly.RemoveAt(i);
                        clipped = true;
                        break;
                    }
                    if (!clipped) break;      // no ear found: degenerate loop, take what we have
                }

                if (poly.Count == 3) { result.Add(poly[0]); result.Add(poly[1]); result.Add(poly[2]); }
                return result;
            }

            private static bool PointInTri(double px, double py,
                                           double ax, double ay,
                                           double bx, double by,
                                           double cx, double cy)
            {
                double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
                double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
                double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0;
                bool pos = d1 > 0 || d2 > 0 || d3 > 0;
                return !(neg && pos);
            }

            /// <summary>Outward normal of a PLANE surface — its placement's own Z axis.
            /// Returns false for anything else, including cylinders and NURBS, whose
            /// normal varies across the face and so cannot be reduced to one value.</summary>
            private bool PlaneNormal(int surfId, out double nx, out double ny, out double nz)
            {
                nx = ny = nz = 0;
                var plane = _f.GetAny(surfId, "PLANE");
                if (plane == null) return false;
                var a = _f.GetAny(plane.Arg(1).AsRef, "AXIS2_PLACEMENT_3D");
                if (a == null) return false;
                return Direction(a.Arg(2).AsRef, out nx, out ny, out nz);
            }

            // ── One edge ──────────────────────────────────────────────────────

            private void AddEdgeCurve(int ecId, in Xf xf, CadSolid cad,
                                      bool hasNormal, double nx, double ny, double nz)
            {
                _pendingHasNormal = hasNormal;
                _pendingNx = nx; _pendingNy = ny; _pendingNz = nz;

                var ec = _f.GetAny(ecId, "EDGE_CURVE");
                if (ec == null) return;

                if (!VertexPoint(ec.Arg(1).AsRef, out double sx, out double sy, out double sz)) return;
                if (!VertexPoint(ec.Arg(2).AsRef, out double ex, out double ey, out double ez)) return;

                int  geomId    = ec.Arg(3).AsRef;
                bool sameSense = ec.Arg(4).Kind == VKind.Enum
                                 ? ec.Arg(4).Str == "T"
                                 : true;

                var circle = _f.GetAny(geomId, "CIRCLE");
                if (circle != null)
                {
                    ArcPoints(circle, sx, sy, sz, ex, ey, ez, sameSense,
                              out var axs, out var ays, out var azs);
                    AddPolyline(axs, ays, azs, xf, cad);
                    return;
                }

                if (_f.GetAny(geomId, "LINE") != null)
                {
                    AddPolyline(new[] { sx, ex }, new[] { sy, ey }, new[] { sz, ez }, xf, cad);
                    return;
                }

                // Everything else: chord between the end vertices, and counted.
                if (_f.GetAny(geomId, "B_SPLINE_CURVE_WITH_KNOTS",
                                      "RATIONAL_B_SPLINE_CURVE",
                                      "B_SPLINE_CURVE") != null) _splineApprox++;
                else _otherApprox++;

                AddPolyline(new[] { sx, ex }, new[] { sy, ey }, new[] { sz, ez }, xf, cad);
            }

            /// <summary>An edge's tessellated points in FILE space, with no side effects.
            /// Shared by the edge emitter and the face builder — a face's boundary is
            /// exactly its edges, so tessellating them twice would risk the fill and the
            /// wireframe disagreeing along the same edge.</summary>
            private bool EdgePoints(int ecId, out double[] xs, out double[] ys, out double[] zs)
            {
                xs = ys = zs = Array.Empty<double>();

                var ec = _f.GetAny(ecId, "EDGE_CURVE");
                if (ec == null) return false;
                if (!VertexPoint(ec.Arg(1).AsRef, out double sx, out double sy, out double sz))
                    return false;
                if (!VertexPoint(ec.Arg(2).AsRef, out double ex, out double ey, out double ez))
                    return false;

                int  geomId    = ec.Arg(3).AsRef;
                bool sameSense = ec.Arg(4).Kind != VKind.Enum || ec.Arg(4).Str == "T";

                var circle = _f.GetAny(geomId, "CIRCLE");
                if (circle != null)
                {
                    ArcPoints(circle, sx, sy, sz, ex, ey, ez, sameSense, out xs, out ys, out zs);
                    return true;
                }

                xs = new[] { sx, ex }; ys = new[] { sy, ey }; zs = new[] { sz, ez };
                return true;
            }

            private bool VertexPoint(int vId, out double x, out double y, out double z)
            {
                x = y = z = 0;
                var v = _f.GetAny(vId, "VERTEX_POINT");
                if (v == null) return false;
                return CartesianPoint(v.Arg(1).AsRef, out x, out y, out z);
            }

            private bool CartesianPoint(int id, out double x, out double y, out double z)
            {
                x = y = z = 0;
                var p = _f.GetAny(id, "CARTESIAN_POINT");
                if (p == null) return false;
                var c = p.Arg(1).AsList;
                if (c.Count < 3) return false;
                x = c[0].AsNum * _unit;
                y = c[1].AsNum * _unit;
                z = c[2].AsNum * _unit;
                return true;
            }

            private bool Direction(int id, out double x, out double y, out double z)
            {
                x = y = z = 0;
                var d = _f.GetAny(id, "DIRECTION");
                if (d == null) return false;
                var c = d.Arg(1).AsList;
                if (c.Count < 3) return false;
                x = c[0].AsNum; y = c[1].AsNum; z = c[2].AsNum;
                double n = Math.Sqrt(x * x + y * y + z * z);
                if (n < 1e-12) return false;
                x /= n; y /= n; z /= n;
                return true;
            }

            /// <summary>AXIS2_PLACEMENT_3D as an affine transform. Note the placement's
            /// own location is in file units, so it is scaled like any other point.</summary>
            private Xf PlacementXf(int id)
            {
                var a = _f.GetAny(id, "AXIS2_PLACEMENT_3D");
                if (a == null) return Xf.Identity;

                CartesianPoint(a.Arg(1).AsRef, out double px, out double py, out double pz);

                if (!Direction(a.Arg(2).AsRef, out double zx, out double zy, out double zz))
                { zx = 0; zy = 0; zz = 1; }
                if (!Direction(a.Arg(3).AsRef, out double xx, out double xy, out double xz))
                { xx = 1; xy = 0; xz = 0; }

                // Gram-Schmidt the ref direction against the axis, then Y = Z × X.
                double d = xx * zx + xy * zy + xz * zz;
                xx -= d * zx; xy -= d * zy; xz -= d * zz;
                double n = Math.Sqrt(xx * xx + xy * xy + xz * xz);
                if (n < 1e-12)
                {
                    // Ref direction was parallel to the axis — pick any perpendicular.
                    if (Math.Abs(zx) < 0.9) { xx = 1; xy = 0; xz = 0; }
                    else                    { xx = 0; xy = 1; xz = 0; }
                    d = xx * zx + xy * zy + xz * zz;
                    xx -= d * zx; xy -= d * zy; xz -= d * zz;
                    n = Math.Sqrt(xx * xx + xy * xy + xz * xz);
                }
                xx /= n; xy /= n; xz /= n;

                double yx = zy * xz - zz * xy;
                double yy = zz * xx - zx * xz;
                double yz = zx * xy - zy * xx;

                return new Xf
                {
                    XX = xx, XY = yx, XZ = zx, TX = px,
                    YX = xy, YY = yy, YZ = zy, TY = py,
                    ZX = xz, ZY = yz, ZZ = zz, TZ = pz,
                };
            }

            /// <summary>Tessellate a trimmed circular arc.
            ///
            /// The sweep direction is the part that bites: an arc from A to B about an
            /// axis has two candidates that differ by 2*pi minus each other, and taking
            /// the wrong one draws the complement — a fillet becomes a three-quarter
            /// hoop sticking out of the part. The edge sense picks which.</summary>
            private void ArcPoints(Rec circle,
                                   double sx, double sy, double sz,
                                   double ex, double ey, double ez,
                                   bool sameSense,
                                   out double[] outX, out double[] outY, out double[] outZ)
            {
                double r = circle.Arg(2).AsNum * _unit;
                Xf pl = PlacementXf(circle.Arg(1).AsRef);

                // Centre and the circle's own X/Y axes, in file space.
                double cx = pl.TX, cy = pl.TY, cz = pl.TZ;
                double ax = pl.XX, ay = pl.YX, az = pl.ZX;   // local X
                double bx = pl.XY, by = pl.YY, bz = pl.ZY;   // local Y

                if (r <= 1e-9)
                {
                    outX = new[] { sx, ex }; outY = new[] { sy, ey }; outZ = new[] { sz, ez };
                    return;
                }

                double a0 = Math.Atan2((sx - cx) * bx + (sy - cy) * by + (sz - cz) * bz,
                                       (sx - cx) * ax + (sy - cy) * ay + (sz - cz) * az);
                double a1 = Math.Atan2((ex - cx) * bx + (ey - cy) * by + (ez - cz) * bz,
                                       (ex - cx) * ax + (ey - cy) * ay + (ez - cz) * az);

                double sweep;
                bool closed = Math.Abs(sx - ex) < 1e-9 &&
                              Math.Abs(sy - ey) < 1e-9 &&
                              Math.Abs(sz - ez) < 1e-9;
                if (closed)
                {
                    sweep = sameSense ? 2 * Math.PI : -2 * Math.PI;
                }
                else if (sameSense)
                {
                    sweep = a1 - a0;
                    while (sweep <= 0) sweep += 2 * Math.PI;
                }
                else
                {
                    sweep = a1 - a0;
                    while (sweep >= 0) sweep -= 2 * Math.PI;
                }

                // Segment count from the chord tolerance: the sagitta of a segment
                // spanning angle t on radius r is r(1 - cos(t/2)).
                double maxAngle = 2.0 * Math.Acos(Math.Clamp(1.0 - CHORD_TOL_MM / r, -1.0, 1.0));
                if (double.IsNaN(maxAngle) || maxAngle < 1e-4) maxAngle = 1e-4;
                int segs = (int)Math.Ceiling(Math.Abs(sweep) / maxAngle);
                segs = Math.Clamp(segs, 2, 512);

                var xs = new double[segs + 1];
                var ys = new double[segs + 1];
                var zs = new double[segs + 1];
                for (int i = 0; i <= segs; i++)
                {
                    double t = a0 + sweep * i / segs;
                    double ct = Math.Cos(t) * r, st = Math.Sin(t) * r;
                    xs[i] = cx + ax * ct + bx * st;
                    ys[i] = cy + ay * ct + by * st;
                    zs[i] = cz + az * ct + bz * st;
                }
                outX = xs; outY = ys; outZ = zs;
            }

            // Carried from AddEdgeCurve to AddPolyline rather than threaded through every
            // tessellation path, which would mean the same three doubles on five signatures.
            private bool   _pendingHasNormal;
            private double _pendingNx, _pendingNy, _pendingNz;

            private void AddPolyline(double[] xs, double[] ys, double[] zs,
                                     in Xf xf, CadSolid cad)
            {
                int n = xs.Length;
                if (n < 2) return;

                var edge = new CadEdge
                {
                    X = new float[n], Y = new float[n], Z = new float[n], Count = n,
                };

                for (int i = 0; i < n; i++)
                {
                    xf.Apply(xs[i], ys[i], zs[i], out double ox, out double oy, out double oz);
                    float fx = (float)ox, fy = (float)oy, fz = (float)oz;
                    edge.X[i] = fx; edge.Y[i] = fy; edge.Z[i] = fz;

                    if (fx < cad.MinX) cad.MinX = fx; if (fx > cad.MaxX) cad.MaxX = fx;
                    if (fy < cad.MinY) cad.MinY = fy; if (fy > cad.MaxY) cad.MaxY = fy;
                    if (fz < cad.MinZ) cad.MinZ = fz; if (fz > cad.MaxZ) cad.MaxZ = fz;
                }

                if (_pendingHasNormal)
                {
                    // Rotate the normal by the assembly placement. Translation must NOT
                    // apply — a direction has no position, and adding TX/TY/TZ would turn
                    // every off-origin component's lighting into nonsense.
                    edge.NX = (float)(xf.XX * _pendingNx + xf.XY * _pendingNy + xf.XZ * _pendingNz);
                    edge.NY = (float)(xf.YX * _pendingNx + xf.YY * _pendingNy + xf.YZ * _pendingNz);
                    edge.NZ = (float)(xf.ZX * _pendingNx + xf.ZY * _pendingNy + xf.ZZ * _pendingNz);
                    edge.HasNormal = true;
                    cad.NormalCount++;
                }

                cad.Edges.Add(edge);
                cad.PointCount += n;
            }
        }
    }
}
