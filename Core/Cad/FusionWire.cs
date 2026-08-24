// ═══════════════════════════════════════════════════════════════════════════
//  FusionWire.cs — the Fusion bridge frame format, and nothing else
//
//  Deliberately separate from the socket. Parsing is where the bugs live and
//  sockets are where the test friction lives, so the parser takes a byte array
//  and knows nothing about connections — which is what lets the whole format be
//  tested against hand-built frames with no server running at all.
//
//  Frame:
//      magic       4 bytes   "EDS1"
//      headerLen   uint32    little-endian
//      header      JSON, headerLen bytes
//      payload     vertices, THEN indices — see below
//
//  A real INDEXED mesh, not a flattened triangle soup: the payload is every
//  vertex ONCE (float32 x,y,z, little-endian) followed by every triangle as
//  three uint32 vertex indices. Two triangles sharing an edge share the same
//  two index values on the wire, which is what a cutting-plane slicer needs to
//  walk the intersection as a graph of shared edges instead of hoping two
//  independently-computed intersection points land on the same float — the
//  whole reason this replaced the one-frame-was-just-float[9*N] soup format.
//  Multiple bodies in one frame share ONE vertex payload and ONE index
//  payload; each body's indices are already GLOBAL (offset into the shared
//  vertex array), not body-local, so nothing here has to re-base them. See
//  protocol.py's build_frame for the writer side of that.
//
//  Coordinates arrive in MILLIMETRES and in ASSEMBLY space: the add-in
//  tessellates occurrence body proxies, which Fusion returns in root-component
//  context, so position and orientation are already baked in. There is no
//  transform on the wire, by design — Fusion owns placement.
//
//  Axes are Fusion's: Z up. The flip to the display's -Z-is-up happens once, in
//  CadPlacement, so this file stays a pure format reader.
//
//  Normals are NOT transmitted. TriangleGrouping recomputes them from winding
//  and treats a supplied normal as an orientation hint at most; STL files proved
//  stored normals are not worth trusting, and the same reasoning applies to a
//  socket.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using EDes.Pcb;

namespace EDes.Cad
{
    /// <summary>One component's triangles, as they arrived.</summary>
    public sealed class FusionBody
    {
        /// <summary>Occurrence full path — the STABLE identity. Fusion permits duplicate
        /// component names, so the name alone cannot key a legend or survive a refresh.</summary>
        public string Path = "";
        /// <summary>Display name, which may be ambiguous. Never used as a key.</summary>
        public string Name = "";
        /// <summary>Fusion's own visibility, accounting for parent state (isVisible, not
        /// isLightBulbOn). Hidden bodies are still sent so the legend stays stable.</summary>
        public bool   Visible;
        public int    VertexCount;
        public int    TriangleCount;
        /// <summary>Where this body's vertices start in the frame's shared vertex array
        /// (a VERTEX index, not a byte offset). Its triangle indices already point at
        /// VertexOffset..VertexOffset+VertexCount-1 directly — nothing needs adding.</summary>
        public int    VertexOffset;
        /// <summary>Where this body's triangles start in the frame's shared index array
        /// (a TRIANGLE index, not a byte offset).</summary>
        public int    TriangleOffset;
    }

    /// <summary>What one geometry response contained.</summary>
    public sealed class FusionFrame
    {
        public bool   Ok;
        public string Document = "";
        public string Revision = "";
        public string Note     = "";
        public int    Dropped;
        public readonly List<FusionBody> Bodies = new();

        /// <summary>Present only when this frame is one of a SEQUENCE of single-body frames
        /// making up one geometry response (see FusionBridge.py's streaming send) — -1 when
        /// absent, which an older add-in's single all-bodies-in-one-frame reply always is.
        /// A pure progress hint: every frame is fully self-describing without these.</summary>
        public int BodyIndex = -1, BodyCount = -1;

        /// <summary>Every vertex ONCE, 3 floats each, millimetres, Fusion axes.</summary>
        public float[] Vertices = Array.Empty<float>();
        /// <summary>Every triangle as 3 vertex indices into Vertices — GLOBAL across the
        /// whole frame (a multi-body frame's second body's indices already point past the
        /// first body's vertices; see the header note on why the writer does that).</summary>
        public int[]   Indices  = Array.Empty<int>();
        public int     TriangleCount => Indices.Length / 3;

        /// <summary>Set when the frame could not be read. Never thrown: a malformed frame
        /// is a message to show, not a crash on the game thread.</summary>
        public string Error = "";
        public bool   Failed => Error.Length > 0;
    }

    public static class FusionWire
    {
        public static readonly byte[] Magic = { (byte)'E', (byte)'D', (byte)'S', (byte)'1' };

        /// <summary>Cap on the declared header size. A corrupt or hostile length field would
        /// otherwise ask us to allocate whatever 32 bits can express before we have read a
        /// single byte of it.</summary>
        public const int MaxHeaderBytes = 4 * 1024 * 1024;

        /// <summary>Request line for a geometry fetch. Newline-terminated, because the
        /// add-in reads one line.</summary>
        public static string GeometryRequest(float toleranceMm, int maxTriangles)
            => "{\"cmd\":\"geometry\",\"tolerance_mm\":"
             + toleranceMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
             + ",\"max_triangles\":" + Math.Max(1, maxTriangles) + "}\n";

        public static string RevisionRequest() => "{\"cmd\":\"rev\"}\n";

        /// <summary>Parse a whole frame. Returns a frame with Error set rather than throwing:
        /// this runs on the game thread, where an exception costs the render loop.</summary>
        public static FusionFrame Parse(byte[] data, int length)
        {
            var f = new FusionFrame();

            if (data == null || length < 8)
                return Fail(f, "frame too short to contain a header");

            for (int i = 0; i < 4; i++)
                if (data[i] != Magic[i])
                    return Fail(f, "not a bridge frame (bad magic) — is something else "
                                 + "listening on that port?");

            uint headerLen = BinaryPrimitives.ReadUInt32LittleEndian(
                                 new ReadOnlySpan<byte>(data, 4, 4));

            if (headerLen == 0 || headerLen > MaxHeaderBytes)
                return Fail(f, $"header length {headerLen} is not plausible");
            if (8 + headerLen > (uint)length)
                return Fail(f, "frame ends inside its own header");

            string json = Encoding.UTF8.GetString(data, 8, (int)headerLen);
            try { ReadHeader(f, json); }
            catch (Exception ex) { return Fail(f, "header is not valid JSON: " + ex.Message); }

            if (!f.Ok) return Fail(f, f.Note.Length > 0 ? f.Note : "the add-in reported a failure");

            int payloadStart = 8 + (int)headerLen;
            int payloadBytes = length - payloadStart;
            if (payloadBytes < 0) return Fail(f, "negative payload length");

            // Every body must lie inside what its OWN declared counts imply. Without this a
            // body claiming more vertices/triangles than were sent would read past the end
            // of the buffer, or a rebased index could point at another body's vertices.
            int neededVerts = 0, neededTris = 0;
            foreach (var b in f.Bodies)
            {
                if (b.VertexOffset < 0 || b.VertexCount < 0
                    || b.TriangleOffset < 0 || b.TriangleCount < 0)
                    return Fail(f, $"body '{b.Name}' has a negative offset or count");
                neededVerts = Math.Max(neededVerts, b.VertexOffset + b.VertexCount);
                neededTris  = Math.Max(neededTris,  b.TriangleOffset + b.TriangleCount);
            }

            // Vertices first, then indices. Trailing bytes that do not complete a vertex or
            // an index triple mean a truncated transfer -- read whole ones only.
            int vertexBytes = neededVerts * 3 * 4;
            if (vertexBytes > payloadBytes)
                return Fail(f, $"the header describes {neededVerts:N0} vertices but only "
                             + $"{payloadBytes / 12:N0} could fit — truncated transfer");

            int indexBytesAvailable = payloadBytes - vertexBytes;
            int trisAvailable = indexBytesAvailable / 12;
            if (neededTris > trisAvailable)
                return Fail(f, $"the header describes {neededTris:N0} triangles but only "
                             + $"{trisAvailable:N0} arrived — truncated transfer");

            f.Vertices = new float[neededVerts * 3];
            Buffer.BlockCopy(data, payloadStart, f.Vertices, 0, neededVerts * 3 * 4);
            if (!BitConverter.IsLittleEndian) ReverseFloats(f.Vertices);

            f.Indices = new int[neededTris * 3];
            Buffer.BlockCopy(data, payloadStart + vertexBytes, f.Indices, 0, neededTris * 3 * 4);
            if (!BitConverter.IsLittleEndian) ReverseInts(f.Indices);

            // Every index must actually land inside the vertex array a plausible-looking
            // frame could otherwise smuggle in an out-of-range read deep inside ToSolids.
            foreach (int idx in f.Indices)
                if (idx < 0 || idx >= neededVerts)
                    return Fail(f, $"a triangle index ({idx}) points outside the "
                                 + $"{neededVerts:N0}-vertex payload");

            return f;
        }

        private static FusionFrame Fail(FusionFrame f, string why)
        {
            f.Error = why;
            return f;
        }

        /// <summary>Peek only the header of the frame STARTING at <paramref name="start"/>, to
        /// learn how many bytes that one frame will be — used both to report byte-progress on
        /// the very first frame (before any bodyIndex/bodyCount is known) and, generalised via
        /// <paramref name="start"/>, to find where one frame ends and the next begins in a
        /// stream of several (see FusionClient's incremental reader). Returns false until the
        /// header itself has fully arrived (the caller tries again after the next chunk); a
        /// header this small essentially always arrives in the first read that reaches it, so
        /// the window where a frame's length is unknown is brief.</summary>
        public static bool TryPeekExpectedBytes(byte[] data, int length, out long expectedTotalBytes,
                                                int start = 0)
        {
            expectedTotalBytes = 0;
            if (data == null || length - start < 8) return false;
            for (int i = 0; i < 4; i++)
                if (data[start + i] != Magic[i]) return false;

            uint headerLen = BinaryPrimitives.ReadUInt32LittleEndian(
                                 new ReadOnlySpan<byte>(data, start + 4, 4));
            if (headerLen == 0 || headerLen > MaxHeaderBytes) return false;
            if (start + 8 + headerLen > (uint)length) return false;   // header not fully in yet

            long totalVerts = 0, totalTriangles = 0;
            try
            {
                string json = Encoding.UTF8.GetString(data, start + 8, (int)headerLen);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("bodies", out var bodies)
                    && bodies.ValueKind == JsonValueKind.Array)
                    foreach (var b in bodies.EnumerateArray())
                    {
                        if (b.TryGetProperty("vertices", out var v) && v.TryGetInt32(out int vc))
                            totalVerts += vc;
                        if (b.TryGetProperty("triangles", out var t) && t.TryGetInt32(out int tc))
                            totalTriangles += tc;
                    }
            }
            catch { return false; }

            expectedTotalBytes = 8L + headerLen + totalVerts * 3L * 4L + totalTriangles * 3L * 4L;
            return true;
        }

        private static void ReadHeader(FusionFrame f, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            f.Ok = !root.TryGetProperty("ok", out var ok) || ok.GetBoolean();
            if (root.TryGetProperty("document", out var d)) f.Document = d.GetString() ?? "";
            if (root.TryGetProperty("revision", out var r)) f.Revision = r.GetString() ?? "";
            if (root.TryGetProperty("note", out var n))     f.Note     = n.GetString() ?? "";
            if (root.TryGetProperty("bodyIndex", out var bi) && bi.TryGetInt32(out int biv))
                f.BodyIndex = biv;
            if (root.TryGetProperty("bodyCount", out var bc) && bc.TryGetInt32(out int bcv))
                f.BodyCount = bcv;
            if (root.TryGetProperty("dropped", out var dr) && dr.TryGetInt32(out int drop))
                f.Dropped = drop;

            // A unit other than mm is refused rather than guessed at. The add-in converts
            // from Fusion's centimetres; if a future version stops doing so, silently
            // drawing a model ten times too small is the worst possible outcome.
            if (root.TryGetProperty("unit", out var u))
            {
                string unit = (u.GetString() ?? "mm").Trim().ToLowerInvariant();
                if (unit.Length > 0 && unit != "mm")
                {
                    f.Ok   = false;
                    f.Note = $"the add-in sent '{unit}' but this build only reads mm";
                    return;
                }
            }

            if (!root.TryGetProperty("bodies", out var bodies)
                || bodies.ValueKind != JsonValueKind.Array) return;

            foreach (var b in bodies.EnumerateArray())
            {
                var body = new FusionBody();
                if (b.TryGetProperty("path", out var p))  body.Path = p.GetString() ?? "";
                if (b.TryGetProperty("name", out var nm)) body.Name = nm.GetString() ?? "";
                if (b.TryGetProperty("visible", out var v)
                    && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    body.Visible = v.GetBoolean();
                else body.Visible = true;          // absent means shown
                if (b.TryGetProperty("vertices", out var vc) && vc.TryGetInt32(out int vcv))
                    body.VertexCount = vcv;
                if (b.TryGetProperty("triangles", out var t) && t.TryGetInt32(out int tc))
                    body.TriangleCount = tc;
                if (b.TryGetProperty("vertexOffset", out var vo) && vo.TryGetInt32(out int vov))
                    body.VertexOffset = vov;
                if (b.TryGetProperty("triangleOffset", out var to) && to.TryGetInt32(out int tov))
                    body.TriangleOffset = tov;

                // Path is the identity; fall back to the name only so a hand-written frame
                // is still usable.
                if (body.Path.Length == 0) body.Path = body.Name;
                if (body.Name.Length == 0) body.Name = body.Path;

                f.Bodies.Add(body);
            }
        }

        /// <summary>Byte-swap in place on a big-endian host. The wire is little-endian
        /// because every machine this runs on is, but stating it beats assuming it.</summary>
        private static void ReverseFloats(float[] v)
        {
            var bytes = new byte[4];
            for (int i = 0; i < v.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes, v[i]);
                Array.Reverse(bytes);
                v[i] = BitConverter.ToSingle(bytes, 0);
            }
        }

        /// <summary>ReverseFloats' counterpart for the index array.</summary>
        private static void ReverseInts(int[] v)
        {
            for (int i = 0; i < v.Length; i++)
                v[i] = BinaryPrimitives.ReverseEndianness(v[i]);
        }

        /// <summary>Turn a parsed frame into solids, one per body, grouped by face
        /// direction through the same code the STL reader uses.
        ///
        /// Coordinates stay in MILLIMETRES with Fusion's Z-up axes, which is exactly the
        /// convention CadSolid already carries from the STEP importer — so the existing
        /// renderer, point light, legend and inspector need no changes. Placement to the
        /// display happens later, in CadPlacement.
        ///
        /// Each body's real vertex/index data is ALSO copied onto its CadSolid (rebased to
        /// LOCAL, 0-based indices — a solid should not need to know it once shared a
        /// frame's payload with other bodies), alongside the flattened triangle soup
        /// TriangleGrouping already wants. Rendering keeps using the soup, unchanged; the
        /// indexed copy is there for a future consumer (a cutting-plane contour walk, say)
        /// that needs real shared-edge connectivity rather than independent triangles.</summary>
        public static List<CadSolid> ToSolids(FusionFrame f, List<string> notes)
        {
            var solids = new List<CadSolid>(f.Bodies.Count);
            if (f.Failed) return solids;

            var soup = new List<Tri>();

            foreach (var b in f.Bodies)
            {
                soup.Clear();

                var vertices = new float[b.VertexCount * 3];
                Buffer.BlockCopy(f.Vertices, b.VertexOffset * 3 * sizeof(float),
                                 vertices, 0, b.VertexCount * 3 * sizeof(float));

                var indices = new int[b.TriangleCount * 3];
                for (int k = 0; k < indices.Length; k++)
                    indices[k] = f.Indices[b.TriangleOffset * 3 + k] - b.VertexOffset;

                for (int t = 0; t < b.TriangleCount; t++)
                {
                    int ia = indices[t * 3] * 3, ib = indices[t * 3 + 1] * 3, ic = indices[t * 3 + 2] * 3;
                    soup.Add(new Tri
                    {
                        AX = vertices[ia],     AY = vertices[ia + 1], AZ = vertices[ia + 2],
                        BX = vertices[ib],     BY = vertices[ib + 1], BZ = vertices[ib + 2],
                        CX = vertices[ic],     CY = vertices[ic + 1], CZ = vertices[ic + 2],
                        // No normal on the wire: winding decides, see the header note.
                    });
                }

                // AssemblyPath carries Fusion's occurrence path (the wire's real identity —
                // see FusionBody.Path) so per-body UI state (colour, render mode) can key off
                // something stable across a re-fetch even when two bodies share a Name.
                var solid = new CadSolid
                {
                    Name = b.Name, AssemblyPath = b.Path, Visible = b.Visible,
                    Vertices = vertices, Indices = indices,
                };
                solid.Faces.AddRange(TriangleGrouping.Group(soup));

                foreach (var face in solid.Faces)
                    for (int k = 0; k < face.TriCount * 3; k++)
                    {
                        float x = face.X[k], y = face.Y[k], z = face.Z[k];
                        if (x < solid.MinX) solid.MinX = x; if (x > solid.MaxX) solid.MaxX = x;
                        if (y < solid.MinY) solid.MinY = y; if (y > solid.MaxY) solid.MaxY = y;
                        if (z < solid.MinZ) solid.MinZ = z; if (z > solid.MaxZ) solid.MaxZ = z;
                    }

                if (solid.Faces.Count == 0 && b.TriangleCount > 0)
                    notes.Add($"{b.Name}: {b.TriangleCount} triangle(s) were all degenerate");

                solids.Add(solid);
            }

            if (f.Dropped > 0)
                notes.Add($"{f.Dropped:N0} triangle(s) dropped by the add-in to stay under "
                        + "the requested cap — coarsen the tolerance for the full model");
            if (f.Note.Length > 0) notes.Add(f.Note);

            return solids;
        }
    }
}
