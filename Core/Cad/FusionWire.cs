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
//      payload     float32[] little-endian, 9 per triangle (3 verts × xyz)
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
//  socket. It also makes the payload a third smaller.
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
        public int    Triangles;
        /// <summary>Triangle index into the shared payload, not a byte offset.</summary>
        public int    Offset;
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

        /// <summary>Triangle vertices, 9 floats each, millimetres, Fusion axes.</summary>
        public float[] Tris = Array.Empty<float>();
        public int     TriangleCount => Tris.Length / 9;

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

            // Trailing bytes that do not complete a triangle mean a truncated transfer. Read
            // whole triangles only -- a partial one would draw a wedge to the origin.
            int floats = payloadBytes / 4;
            int tris   = floats / 9;

            // Every body must lie inside what actually arrived. Without this a body claiming
            // more triangles than were sent would read past the end of the buffer.
            int needed = 0;
            foreach (var b in f.Bodies)
            {
                if (b.Offset < 0 || b.Triangles < 0)
                    return Fail(f, $"body '{b.Name}' has a negative offset or count");
                needed = Math.Max(needed, b.Offset + b.Triangles);
            }
            if (needed > tris)
                return Fail(f, $"the header describes {needed:N0} triangles but only "
                             + $"{tris:N0} arrived — truncated transfer");

            f.Tris = new float[tris * 9];
            Buffer.BlockCopy(data, payloadStart, f.Tris, 0, tris * 9 * 4);

            if (!BitConverter.IsLittleEndian) ReverseFloats(f.Tris);

            return f;
        }

        private static FusionFrame Fail(FusionFrame f, string why)
        {
            f.Error = why;
            return f;
        }

        private static void ReadHeader(FusionFrame f, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            f.Ok = !root.TryGetProperty("ok", out var ok) || ok.GetBoolean();
            if (root.TryGetProperty("document", out var d)) f.Document = d.GetString() ?? "";
            if (root.TryGetProperty("revision", out var r)) f.Revision = r.GetString() ?? "";
            if (root.TryGetProperty("note", out var n))     f.Note     = n.GetString() ?? "";
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
                if (b.TryGetProperty("triangles", out var t) && t.TryGetInt32(out int tc))
                    body.Triangles = tc;
                if (b.TryGetProperty("offset", out var o) && o.TryGetInt32(out int off))
                    body.Offset = off;

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

        /// <summary>Turn a parsed frame into solids, one per body, grouped by face
        /// direction through the same code the STL reader uses.
        ///
        /// Coordinates stay in MILLIMETRES with Fusion's Z-up axes, which is exactly the
        /// convention CadSolid already carries from the STEP importer — so the existing
        /// renderer, point light, legend and inspector need no changes. Placement to the
        /// display happens later, in CadPlacement.</summary>
        public static List<CadSolid> ToSolids(FusionFrame f, List<string> notes)
        {
            var solids = new List<CadSolid>(f.Bodies.Count);
            if (f.Failed) return solids;

            var soup = new List<Tri>();

            foreach (var b in f.Bodies)
            {
                soup.Clear();
                for (int t = 0; t < b.Triangles; t++)
                {
                    int i = (b.Offset + t) * 9;
                    soup.Add(new Tri
                    {
                        AX = f.Tris[i],     AY = f.Tris[i + 1], AZ = f.Tris[i + 2],
                        BX = f.Tris[i + 3], BY = f.Tris[i + 4], BZ = f.Tris[i + 5],
                        CX = f.Tris[i + 6], CY = f.Tris[i + 7], CZ = f.Tris[i + 8],
                        // No normal on the wire: winding decides, see the header note.
                    });
                }

                var solid = new CadSolid { Name = b.Name, Visible = b.Visible };
                solid.Faces.AddRange(TriangleGrouping.Group(soup));

                foreach (var face in solid.Faces)
                    for (int k = 0; k < face.TriCount * 3; k++)
                    {
                        float x = face.X[k], y = face.Y[k], z = face.Z[k];
                        if (x < solid.MinX) solid.MinX = x; if (x > solid.MaxX) solid.MaxX = x;
                        if (y < solid.MinY) solid.MinY = y; if (y > solid.MaxY) solid.MaxY = y;
                        if (z < solid.MinZ) solid.MinZ = z; if (z > solid.MaxZ) solid.MaxZ = z;
                    }

                if (solid.Faces.Count == 0 && b.Triangles > 0)
                    notes.Add($"{b.Name}: {b.Triangles} triangle(s) were all degenerate");

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
