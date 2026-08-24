// ═══════════════════════════════════════════════════════════════════════════
//  FusionChecks.cs — the Fusion bridge, verified without Fusion
//
//  This is the whole reason the client half was built first. The format lives in
//  FusionWire, which takes a byte array, so every framing rule can be checked
//  against hand-built frames; and the socket layer is exercised by a fake server
//  in this file. Neither needs Autodesk installed anywhere.
//
//  What is NOT covered here, and cannot be: that Fusion's occurrence body
//  proxies really return assembly-space coordinates, and that its custom events
//  behave as documented under a real UI. Those are the assertions the trip to the
//  Fusion machine exists for.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using EDes.Cad;
using EDes.Pcb;
using EDes.Sim;
using Voxon;

namespace PcbParserTests
{
    internal static class FusionChecks
    {
        private static int _failures;

        private static void Ok(string what, bool pass)
        {
            if (!pass) _failures++;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        // ── Frame building, mirroring what the add-in will emit ───────────────

        private sealed class BodySpec
        {
            public string Path = "", Name = "";
            public bool   Visible = true;
            /// <summary>Still specified as a flat triangle soup (9 floats per triangle) —
            /// simplest thing for a test fixture to write out by hand. BuildFrame dedupes
            /// this into the real indexed wire format (shared vertices + index triples)
            /// before sending, the same way protocol.py's build_frame does; nothing about
            /// how a test SPECIFIES a shape needed to change for the wire format to.</summary>
            public float[] Tris = Array.Empty<float>();   // 9 per triangle
        }

        private static byte[] BuildFrame(IEnumerable<BodySpec> bodies, string doc = "Test",
                                         string rev = "r1", int dropped = 0,
                                         string unit = "mm", bool ok = true)
        {
            var list = bodies.ToList();
            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(ok ? "true" : "false")
              .Append(",\"unit\":\"").Append(unit).Append('"')
              .Append(",\"revision\":\"").Append(rev).Append('"')
              .Append(",\"document\":\"").Append(doc).Append('"')
              .Append(",\"dropped\":").Append(dropped)
              .Append(",\"bodies\":[");

            var allVerts = new List<float>();
            var allIndices = new List<int>();
            int voffset = 0, toffset = 0;

            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                DedupeToIndexed(b.Tris, out List<float> verts, out List<int> indices);
                int vcount = verts.Count / 3, tcount = indices.Count / 3;

                if (i > 0) sb.Append(',');
                sb.Append("{\"path\":\"").Append(b.Path).Append('"')
                  .Append(",\"name\":\"").Append(b.Name).Append('"')
                  .Append(",\"visible\":").Append(b.Visible ? "true" : "false")
                  .Append(",\"vertices\":").Append(vcount)
                  .Append(",\"triangles\":").Append(tcount)
                  .Append(",\"vertexOffset\":").Append(voffset)
                  .Append(",\"triangleOffset\":").Append(toffset).Append('}');

                allVerts.AddRange(verts);
                // GLOBAL indices, exactly like protocol.py's build_frame -- a reader never
                // has to re-base an individual body's indices.
                foreach (int idx in indices) allIndices.Add(idx + voffset);
                voffset += vcount;
                toffset += tcount;
            }
            sb.Append("]}");

            byte[] header = Encoding.UTF8.GetBytes(sb.ToString());
            var frame = new byte[8 + header.Length + allVerts.Count * 4 + allIndices.Count * 4];
            Buffer.BlockCopy(FusionWire.Magic, 0, frame, 0, 4);
            BitConverter.GetBytes((uint)header.Length).CopyTo(frame, 4);
            Buffer.BlockCopy(header, 0, frame, 8, header.Length);

            int p = 8 + header.Length;
            Buffer.BlockCopy(allVerts.ToArray(), 0, frame, p, allVerts.Count * 4);
            p += allVerts.Count * 4;
            Buffer.BlockCopy(allIndices.ToArray(), 0, frame, p, allIndices.Count * 4);
            return frame;
        }

        /// <summary>Flat triangle soup (9 floats/triangle) to a real indexed mesh: every
        /// DISTINCT vertex position gets one entry, and every triangle becomes 3 indices
        /// into that list — mirroring what a real tessellator (or protocol.py's own
        /// build_frame, for multiple bodies in one frame) produces, so a test fixture built
        /// this way exercises the same "shared vertex" property real geometry has.</summary>
        private static void DedupeToIndexed(float[] tris, out List<float> verts, out List<int> indices)
        {
            verts = new List<float>();
            indices = new List<int>();
            var seen = new Dictionary<(float, float, float), int>();

            for (int i = 0; i < tris.Length - 2; i += 3)
            {
                var key = (tris[i], tris[i + 1], tris[i + 2]);
                if (!seen.TryGetValue(key, out int idx))
                {
                    idx = verts.Count / 3;
                    verts.Add(tris[i]); verts.Add(tris[i + 1]); verts.Add(tris[i + 2]);
                    seen[key] = idx;
                }
                indices.Add(idx);
            }
        }

        /// <summary>A 10 mm axis-aligned cube at the origin: 12 triangles, 6 directions.
        /// The fixture the whole bridge is checked against, because its right answer is
        /// obvious at a glance.</summary>
        private static float[] Cube(float s = 10f)
        {
            var v = new (float x, float y, float z)[]
            {
                (0,0,0), (s,0,0), (s,s,0), (0,s,0),
                (0,0,s), (s,0,s), (s,s,s), (0,s,s),
            };
            int[][] quads =
            {
                new[]{0,3,2,1}, new[]{4,5,6,7},      // bottom, top
                new[]{0,1,5,4}, new[]{2,3,7,6},      // front, back
                new[]{1,2,6,5}, new[]{3,0,4,7},      // right, left
            };

            var f = new List<float>();
            foreach (var q in quads)
            {
                void Tri(int a, int b, int c)
                {
                    f.Add(v[a].x); f.Add(v[a].y); f.Add(v[a].z);
                    f.Add(v[b].x); f.Add(v[b].y); f.Add(v[b].z);
                    f.Add(v[c].x); f.Add(v[c].y); f.Add(v[c].z);
                }
                Tri(q[0], q[1], q[2]);
                Tri(q[0], q[2], q[3]);
            }
            return f.ToArray();
        }

        /// <summary>`count` cheap, valid (non-degenerate, non-collinear), distinct
        /// triangles — a fast way to generate a large payload for size/throughput tests
        /// without caring what the shape actually looks like.</summary>
        private static float[] Strip(int count, float offset)
        {
            var f = new float[count * 9];
            for (int k = 0; k < count; k++)
            {
                float x = offset + k;
                int i = k * 9;
                f[i]     = x;     f[i + 1] = 0f; f[i + 2] = 0f;
                f[i + 3] = x + 1; f[i + 4] = 0f; f[i + 5] = 0f;
                f[i + 6] = x;     f[i + 7] = 1f; f[i + 8] = 0f;
            }
            return f;
        }

        /// <summary>One single-body frame per spec, back to back, plus a terminator — the
        /// streamed shape FusionBridge.py actually sends, built here without a live server.</summary>
        private static byte[] ConcatFrames(IEnumerable<BodySpec> bodies)
        {
            var parts = new List<byte[]>();
            foreach (var b in bodies) parts.Add(BuildFrame(new[] { b }));
            parts.Add(BuildFrame(Array.Empty<BodySpec>()));

            int total = 0;
            foreach (var p in parts) total += p.Length;
            var outBuf = new byte[total];
            int off = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, outBuf, off, p.Length); off += p.Length; }
            return outBuf;
        }

        // ── A fake add-in ────────────────────────────────────────────────────

        /// <summary>Serves one canned frame and closes, which is what the real add-in does.
        /// Thirty lines, and it is what makes the socket layer testable at all.</summary>
        private sealed class FakeAddIn : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            private volatile bool _stop;

            public int Port { get; }
            public string LastRequest = "";

            public FakeAddIn(Func<string, byte[]?> respond)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                _thread = new Thread(() =>
                {
                    while (!_stop)
                    {
                        try
                        {
                            using var c = _listener.AcceptTcpClient();
                            using var st = c.GetStream();

                            var line = new StringBuilder();
                            int ch;
                            while ((ch = st.ReadByte()) >= 0 && ch != '\n')
                                line.Append((char)ch);
                            LastRequest = line.ToString();

                            byte[]? reply = respond(LastRequest);
                            if (reply != null) st.Write(reply, 0, reply.Length);
                            st.Flush();
                        }
                        catch { if (!_stop) continue; }
                    }
                }) { IsBackground = true };
                _thread.Start();
            }

            public void Dispose()
            {
                _stop = true;
                try { _listener.Stop(); } catch { }
            }
        }

        public static int Run()
        {
            var notes = new List<string>();

            // ── The cube, end to end through the parser ──────────────────────
            {
                byte[] frame = BuildFrame(new[]
                {
                    new BodySpec { Path = "Cube:1", Name = "Cube", Tris = Cube() },
                });

                var f = FusionWire.Parse(frame, frame.Length);
                Ok($"the frame parses ({(f.Failed ? f.Error : "ok")})", !f.Failed);
                Ok($"12 triangles arrived ({f.TriangleCount})", f.TriangleCount == 12);
                Ok($"the document name came through ('{f.Document}')", f.Document == "Test");

                var solids = FusionWire.ToSolids(f, notes);
                Ok($"one solid was built ({solids.Count})", solids.Count == 1);

                int tris = solids[0].Faces.Sum(x => x.TriCount);
                Ok($"no triangles were lost in grouping ({tris})", tris == 12);

                // A cube has 6 distinct face directions. 12 groups would mean the direction
                // quantisation is not collapsing coplanar triangles -- the cost this exists
                // to avoid.
                Ok($"the cube collapses to SIX face groups ({solids[0].Faces.Count})",
                   solids[0].Faces.Count == 6);

                Ok($"bounds are exactly 0..10 mm on every axis "
                 + $"({solids[0].MinX}..{solids[0].MaxX})",
                   Math.Abs(solids[0].MinX) < 1e-4 && Math.Abs(solids[0].MaxX - 10) < 1e-4 &&
                   Math.Abs(solids[0].MinY) < 1e-4 && Math.Abs(solids[0].MaxY - 10) < 1e-4 &&
                   Math.Abs(solids[0].MinZ) < 1e-4 && Math.Abs(solids[0].MaxZ - 10) < 1e-4);

                bool allNormals = solids[0].Faces.All(x => x.HasNormalSet &&
                    Math.Abs(Math.Sqrt(x.NX * x.NX + x.NY * x.NY + x.NZ * x.NZ) - 1) < 1e-3);
                Ok("every face group got a unit normal, recomputed from winding", allNormals);
            }

            // ── Identity, and Fusion's visibility ────────────────────────────
            {
                byte[] frame = BuildFrame(new[]
                {
                    new BodySpec { Path = "A:1/Bolt:1", Name = "Bolt", Tris = Cube(2) },
                    new BodySpec { Path = "B:1/Bolt:1", Name = "Bolt", Visible = false,
                                   Tris = Cube(3) },
                });

                var f = FusionWire.Parse(frame, frame.Length);
                Ok($"duplicate names are kept apart by path "
                 + $"('{f.Bodies[0].Path}' vs '{f.Bodies[1].Path}')",
                   f.Bodies.Count == 2 && f.Bodies[0].Path != f.Bodies[1].Path);

                var solids = FusionWire.ToSolids(f, notes);
                Ok("a hidden body is still LOADED, not dropped", solids.Count == 2);
                Ok("and carries Fusion's visibility",
                   solids[0].Visible && !solids[1].Visible);

                // Each body must read its own slice: swapping the offsets would give both
                // the same geometry and nothing would look wrong on screen.
                Ok($"each body got its own triangles "
                 + $"({solids[0].MaxX} vs {solids[1].MaxX})",
                   Math.Abs(solids[0].MaxX - 2) < 1e-4 &&
                   Math.Abs(solids[1].MaxX - 3) < 1e-4);
            }

            // ── Malformed frames report, never throw ─────────────────────────
            {
                byte[] good = BuildFrame(new[]
                    { new BodySpec { Path = "C", Name = "C", Tris = Cube() } });

                Ok("a truncated frame reports",
                   FusionWire.Parse(good, good.Length / 2).Failed);
                Ok("an empty buffer reports",
                   FusionWire.Parse(Array.Empty<byte>(), 0).Failed);

                var bad = (byte[])good.Clone();
                bad[1] = (byte)'X';
                var f = FusionWire.Parse(bad, bad.Length);
                Ok($"bad magic reports and says what to check ('{Trim(f.Error)}')",
                   f.Failed && f.Error.Contains("port"));

                // A body claiming more triangles than arrived must be refused, not read
                // past the end of the buffer.
                byte[] lying = BuildFrame(new[]
                    { new BodySpec { Path = "L", Name = "L", Tris = Cube() } });
                string hdr = Encoding.UTF8.GetString(lying, 8,
                                 BitConverter.ToInt32(lying, 4));
                hdr = hdr.Replace("\"triangles\":12", "\"triangles\":9999");
                byte[] hb = Encoding.UTF8.GetBytes(hdr);
                var forged = new byte[8 + hb.Length];
                Buffer.BlockCopy(FusionWire.Magic, 0, forged, 0, 4);
                BitConverter.GetBytes((uint)hb.Length).CopyTo(forged, 4);
                Buffer.BlockCopy(hb, 0, forged, 8, hb.Length);
                var lf = FusionWire.Parse(forged, forged.Length);
                Ok($"a body claiming more triangles than arrived is refused "
                 + $"('{Trim(lf.Error)}')", lf.Failed);

                // Wrong units must be refused rather than silently drawn 10x small. This is
                // the failure mode the whole cm/mm care exists to prevent.
                byte[] cm = BuildFrame(new[]
                    { new BodySpec { Path = "M", Name = "M", Tris = Cube() } }, unit: "cm");
                var cf = FusionWire.Parse(cm, cm.Length);
                Ok($"a frame in centimetres is REFUSED, not scaled by guesswork "
                 + $"('{Trim(cf.Error)}')", cf.Failed && cf.Error.Contains("mm"));

                byte[] notOk = BuildFrame(Array.Empty<BodySpec>(), ok: false);
                Ok("ok:false is treated as a failure",
                   FusionWire.Parse(notOk, notOk.Length).Failed);
            }

            // ── The socket layer, against the fake add-in ────────────────────
            {
                byte[] frame = BuildFrame(new[]
                    { new BodySpec { Path = "Cube:1", Name = "Cube", Tris = Cube() } },
                    doc: "IRSensor", rev: "abc123");

                using var server = new FakeAddIn(_ => frame);
                var client = new FusionClient();

                var r = client.Fetch("127.0.0.1", server.Port, 0.4f, 300_000);
                Ok($"a real socket round trip works ({r.Message})", r.Ok);
                Ok($"the document arrived ('{r.Document}')", r.Document == "IRSensor");
                Ok($"the revision arrived ('{r.Revision}')", r.Revision == "abc123");
                Ok($"one solid, 12 triangles ({r.Solids.Count}, {r.Triangles})",
                   r.Solids.Count == 1 && r.Triangles == 12);
                Ok($"the request was well formed ('{Trim(server.LastRequest)}')",
                   server.LastRequest.Contains("\"cmd\":\"geometry\"") &&
                   server.LastRequest.Contains("0.4"));

                string rev = client.FetchRevision("127.0.0.1", server.Port, out string err);
                Ok($"the revision can be fetched on its own ('{rev}')",
                   rev == "abc123" && err.Length == 0);
            }

            // ── The STREAMED reply: several frames back to back, one per body plus a
            // terminator, exactly as FusionBridge.py's _build_geometry now sends — not one
            // frame holding every body. This is what makes onBody fire per body rather than
            // once at the end, and it is the one thing single-frame tests cannot exercise:
            // finding where one frame ends and the next begins in the same buffer.
            {
                byte[] bodyA = BuildFrame(new[]
                    { new BodySpec { Path = "A:1", Name = "A", Tris = Cube() } },
                    doc: "Streamed", rev: "s1");
                byte[] bodyB = BuildFrame(new[]
                    { new BodySpec { Path = "B:1", Name = "B", Tris = Cube() } },
                    doc: "Streamed", rev: "s1");
                byte[] terminator = BuildFrame(Array.Empty<BodySpec>(),
                    doc: "Streamed", rev: "s1", dropped: 3, ok: true);

                var multi = new byte[bodyA.Length + bodyB.Length + terminator.Length];
                Buffer.BlockCopy(bodyA, 0, multi, 0, bodyA.Length);
                Buffer.BlockCopy(bodyB, 0, multi, bodyA.Length, bodyB.Length);
                Buffer.BlockCopy(terminator, 0, multi, bodyA.Length + bodyB.Length,
                                 terminator.Length);

                using var streamed = new FakeAddIn(_ => multi);
                var client2 = new FusionClient();

                var seenInOrder = new List<string>();
                var progressCalls = new List<float>();
                var r2 = client2.Fetch("127.0.0.1", streamed.Port, 0.4f, 300_000,
                                       p => progressCalls.Add(p),
                                       solid => seenInOrder.Add(solid.Name));

                Ok($"a streamed reply still succeeds ({r2.Message})", r2.Ok);
                Ok($"both bodies arrived ({r2.Solids.Count})", r2.Solids.Count == 2);
                Ok($"onBody fired once per body, IN ORDER ({string.Join(",", seenInOrder)})",
                   seenInOrder.Count == 2 && seenInOrder[0] == "A" && seenInOrder[1] == "B");
                Ok($"onProgress reached 1.0 by the end ({(progressCalls.Count > 0 ? progressCalls[^1] : -1)})",
                   progressCalls.Count > 0 && Math.Abs(progressCalls[^1] - 1f) < 1e-4f);
                Ok($"the terminator's dropped count survived the merge ({r2.Notes.Count})",
                   r2.Notes.Any(n => n.Contains("3")));
            }

            // ── A transfer big enough to force the receive buffer to compact more than
            // once must still come through byte-for-byte correct — this is the "large
            // assembly" path: several MB of triangles, several frames, no corruption from
            // shifting already-parsed bytes out of the buffer mid-stream. ──────────────
            {
                // ~1.2 MB of triangle payload per body (36 bytes/triangle), five bodies:
                // comfortably past FusionClient's 4 MB compaction threshold more than once
                // over the whole transfer, without making the test slow to generate or run.
                const int trisPerBody = 33_000;
                var bigBodies = new BodySpec[5];
                for (int b = 0; b < bigBodies.Length; b++)
                    bigBodies[b] = new BodySpec
                        { Path = $"Big:{b}", Name = $"Big{b}", Tris = Strip(trisPerBody, offset: b * 1000f) };

                byte[] bigFrames = ConcatFrames(bigBodies);
                using var bigServer = new FakeAddIn(_ => bigFrames);
                var bigClient = new FusionClient();

                int bodiesSeen = 0;
                long trisSeen = 0;
                var bigResult = bigClient.Fetch("127.0.0.1", bigServer.Port, 0.4f, 10_000_000,
                                                onBody: solid =>
                                                {
                                                    bodiesSeen++;
                                                    foreach (var face in solid.Faces) trisSeen += face.TriCount;
                                                });

                Ok($"a multi-megabyte transfer still succeeds ({bigResult.Message})", bigResult.Ok);
                Ok($"all {bigBodies.Length} large bodies arrived ({bodiesSeen})",
                   bodiesSeen == bigBodies.Length);
                Ok($"every triangle survived compaction ({trisSeen:N0} of "
                 + $"{(long)trisPerBody * bigBodies.Length:N0})",
                   trisSeen == (long)trisPerBody * bigBodies.Length);
            }

            // ── Failure paths must be diagnosable, not just "failed" ─────────
            {
                var client = new FusionClient { TimeoutMs = 1500 };

                // Nothing listening: the port is almost certainly free.
                var dead = client.Fetch("127.0.0.1", 47999, 0.4f, 1000);
                Ok($"a closed port names what to check ('{Trim(dead.Message)}')",
                   !dead.Ok && dead.Message.Contains("listening"));

                // Accepts and says nothing — which is what a request landing while Fusion is
                // busy looks like from this end.
                using var silent = new FakeAddIn(_ => null);
                var quiet = client.Fetch("127.0.0.1", silent.Port, 0.4f, 1000);
                Ok($"a silent add-in is explained, not just 'failed' "
                 + $"('{Trim(quiet.Message)}')",
                   !quiet.Ok && quiet.Message.Contains("busy"));

                // Junk on the wire.
                using var junk = new FakeAddIn(_ => Encoding.ASCII.GetBytes("hello there"));
                var jr = client.Fetch("127.0.0.1", junk.Port, 0.4f, 1000);
                Ok($"junk is reported ('{Trim(jr.Message)}')", !jr.Ok);
            }

            // ── Placement: the flip, and the floor ───────────────────────────
            {
                const float zHalf = 2f;
                var p = CadPlacement.Default(zHalf);

                Ok($"the default origin is the FLOOR, +zHalf ({p.OriginZ})",
                   Math.Abs(p.OriginZ - zHalf) < 1e-6);

                // Fusion Z up -> display -Z up. A part 10 mm above the origin must be
                // HIGHER in the volume, which on this display means a SMALLER z.
                var atOrigin = p.Map(0, 0, 0);
                var above    = p.Map(0, 0, 10);
                Ok($"the Fusion origin lands on the floor (z {atOrigin.z:0.###})",
                   Math.Abs(atOrigin.z - zHalf) < 1e-5);
                Ok($"10 mm up in Fusion is UP in the volume, i.e. smaller z "
                 + $"({above.z:0.###} < {atOrigin.z:0.###})", above.z < atOrigin.z);
                Ok($"and by exactly scale x 10 ({atOrigin.z - above.z:0.###})",
                   Math.Abs((atOrigin.z - above.z) - 10f * p.Scale) < 1e-5);

                // X and Y pass straight through: Fusion owns them.
                var side = p.Map(25, -5, 0);
                Ok($"X and Y are not flipped or re-centred ({side.x:0.###}, {side.y:0.###})",
                   Math.Abs(side.x - 25f * p.Scale) < 1e-5 &&
                   Math.Abs(side.y + 5f * p.Scale) < 1e-5);

                // The ceiling anchor, which is what the first draft of the plan specified.
                var ceiling = new CadPlacement
                    { Scale = 0.04f, OriginX = 0, OriginY = 0, OriginZ = -zHalf };
                var up = ceiling.Map(0, 0, 10);
                Ok($"anchored to the CEILING, upward geometry leaves the volume "
                 + $"(z {up.z:0.###} < {-zHalf})",
                   up.z < -zHalf && !CadPlacement.Inside(up, 4f, zHalf));
                Ok("...which is why the default is the floor",
                   CadPlacement.Inside(p.Map(0, 0, 10), 4f, zHalf));

                // The render-path split: MapLinear (rotates with the camera) + Anchor
                // (added back afterwards) must put the assembly's floor spot somewhere a
                // rotation cannot move -- the whole reason the split exists instead of
                // just calling Map() before cam.Transform.
                var cam = new SceneCamera();
                var before = p.Anchor(cam.Transform(p.MapLinear(0, 0, 0)));
                cam.RotateLocal(0.4f, -0.3f, 0.6f);
                var after = p.Anchor(cam.Transform(p.MapLinear(0, 0, 0)));
                Ok($"the assembly's floor anchor does not move when the scene rotates "
                 + $"(({before.x:0.###},{before.y:0.###},{before.z:0.###}) -> "
                 + $"({after.x:0.###},{after.y:0.###},{after.z:0.###}))",
                   MathF.Abs(before.x - after.x) < 1e-4f && MathF.Abs(before.y - after.y) < 1e-4f
                   && MathF.Abs(before.z - after.z) < 1e-4f);
            }

            // ── The renderer: it draws, it clips, it reports ─────────────────
            {
                const float radius = 4f, zHalf = 2f;

                byte[] frame = BuildFrame(new[]
                    { new BodySpec { Path = "Cube:1", Name = "Cube", Tris = Cube(20f) } });
                var solids = FusionWire.ToSolids(
                                 FusionWire.Parse(frame, frame.Length), notes);

                var batch = new VoxelBatch();
                batch.BeginFrame(400_000, radius, zHalf, 0.03f);
                var scene = new CadSceneRenderer();

                scene.Draw(batch, new SceneCamera(), solids,
                           CadPlacement.Default(zHalf), CadLight.Off(0.35f),
                           radius, zHalf, 0.6f, 1f);

                Ok($"the cube draws ({batch.Count:N0} voxels, {scene.TrianglesDrawn} tri)",
                   batch.Count > 500 && scene.TrianglesDrawn == 12);
                Ok($"nothing is clipped when it fits ({scene.ClippedFraction:0.###})",
                   scene.ClippedFraction < 1e-6);

                // Hidden bodies are not drawn -- Fusion's visibility, honoured.
                solids[0].Visible = false;
                var b2 = new VoxelBatch();
                b2.BeginFrame(400_000, radius, zHalf, 0.03f);
                var s2 = new CadSceneRenderer();
                s2.Draw(b2, new SceneCamera(), solids, CadPlacement.Default(zHalf),
                        CadLight.Off(0.35f), radius, zHalf, 0.6f, 1f);
                Ok($"a body Fusion hides is not drawn ({b2.Count} voxels)", b2.Count == 0);
                solids[0].Visible = true;

                // Ceiling anchor: most of an upward cube leaves the volume, and the
                // fraction must SAY so rather than the model just being absent.
                var b3 = new VoxelBatch();
                b3.BeginFrame(400_000, radius, zHalf, 0.03f);
                var s3 = new CadSceneRenderer();
                s3.Draw(b3, new SceneCamera(), solids,
                        new CadPlacement { Scale = 0.04f, OriginZ = -zHalf },
                        CadLight.Off(0.35f), radius, zHalf, 0.6f, 1f);
                Ok($"a ceiling-anchored assembly reports its clipping "
                 + $"({s3.ClippedFraction * 100f:0}% outside)", s3.ClippedFraction > 0.4f);

                // Ghost must cost less than solid, since sparser is what faint means here.
                var b4 = new VoxelBatch();
                b4.BeginFrame(400_000, radius, zHalf, 0.03f);
                new CadSceneRenderer().Draw(b4, new SceneCamera(), solids,
                        CadPlacement.Default(zHalf), CadLight.Off(0.35f),
                        radius, zHalf, 0.6f, 1f, _ => CadDrawMode.Ghost);
                Ok($"ghost draws fewer voxels than solid ({b4.Count:N0} vs {batch.Count:N0})",
                   b4.Count < batch.Count / 2);

                // Flat must draw (it is a fill, like Solid) but must NOT vary with the
                // light -- that is its whole point, so it needs a light that WOULD vary
                // shading if it were being consulted at all.
                var flatLight = CadLight.AtPoint(0f, 0f, 100f, 0f, 0.1f);
                var bFlat = new VoxelBatch();
                bFlat.BeginFrame(400_000, radius, zHalf, 0.03f);
                new CadSceneRenderer().Draw(bFlat, new SceneCamera(), solids,
                        CadPlacement.Default(zHalf), flatLight,
                        radius, zHalf, 0.6f, 1f, _ => CadDrawMode.Flat);
                var bLit = new VoxelBatch();
                bLit.BeginFrame(400_000, radius, zHalf, 0.03f);
                new CadSceneRenderer().Draw(bLit, new SceneCamera(), solids,
                        CadPlacement.Default(zHalf), flatLight,
                        radius, zHalf, 0.6f, 1f, _ => CadDrawMode.Solid);
                Ok($"flat draws too ({bFlat.Count:N0} voxels)", bFlat.Count > 500);
                Ok($"flat ignores the light that makes lit uneven ({bFlat.Count:N0} vs lit "
                 + $"{bLit.Count:N0}, lit itself unbalanced under a strong point light)",
                   bFlat.Count != bLit.Count);

                // Wireframe: far fewer voxels than a fill of the same cube (edges, not
                // area), but still something -- 12 triangles x 3 edges, not zero.
                var bWire = new VoxelBatch();
                bWire.BeginFrame(400_000, radius, zHalf, 0.03f);
                var sWire = new CadSceneRenderer();
                sWire.Draw(bWire, new SceneCamera(), solids,
                          CadPlacement.Default(zHalf), CadLight.Off(0.35f),
                          radius, zHalf, 0.6f, 1f, _ => CadDrawMode.Wireframe);
                Ok($"wireframe draws something, and fewer voxels than a filled cube "
                 + $"({bWire.Count:N0} vs {batch.Count:N0})",
                   bWire.Count > 0 && bWire.Count < batch.Count);

                // colorOf overrides the solid's own colour -- the per-body colour picker's
                // whole mechanism. VoxelBatch does not expose colours publicly (there is no
                // per-body Cs span the way there is Xs/Ys/Zs), so what is checkable from
                // here is that the callback really is consulted, once per solid drawn, and
                // that supplying one does not change how much gets drawn.
                int colorOfCalls = 0;
                var bColour = new VoxelBatch();
                bColour.BeginFrame(400_000, radius, zHalf, 0.03f);
                new CadSceneRenderer().Draw(bColour, new SceneCamera(), solids,
                        CadPlacement.Default(zHalf), CadLight.Off(0.35f),
                        radius, zHalf, 0.6f, 1f, null, s => { colorOfCalls++; return 0x00FF00; });
                Ok($"colorOf is consulted once per solid drawn ({colorOfCalls})",
                   colorOfCalls == solids.Count);
                Ok($"and drawing the same geometry with a different colour draws the same "
                 + $"count ({bColour.Count:N0} vs {batch.Count:N0})",
                   bColour.Count == batch.Count);

                // And the budget is respected: a tiny limit must not be exceeded.
                var b5 = new VoxelBatch();
                b5.BeginFrame(2_000, radius, zHalf, 0.03f);
                new CadSceneRenderer().Draw(b5, new SceneCamera(), solids,
                        CadPlacement.Default(zHalf), CadLight.Off(0.35f),
                        radius, zHalf, 2f, 1f);
                Ok($"a tight budget is not exceeded ({b5.Count} <= 2000)", b5.Count <= 2000);
            }

            // ── Multi-light: brightest UNOCCLUDED light wins, never darker than either
            // light alone ──────────────────────────────────────────────────────────
            {
                const float radius = 4f, zHalf = 2f;
                byte[] frame = BuildFrame(new[]
                    { new BodySpec { Path = "Cube:1", Name = "Cube", Tris = Cube(20f) } });
                var solids = FusionWire.ToSolids(
                                 FusionWire.Parse(frame, frame.Length), notes);

                var dim    = CadLight.AtPoint(-200f, 10f, 10f, 400f, 0.1f);
                var strong = CadLight.AtPoint(-30f,  10f, 10f, 0f,   0.1f);

                VoxelBatch DrawWith(params CadLight[] lights)
                {
                    var b = new VoxelBatch();
                    b.BeginFrame(400_000, radius, zHalf, 0.03f);
                    new CadSceneRenderer().Draw(b, new SceneCamera(), solids,
                            CadPlacement.Default(zHalf), lights, radius, zHalf, 0.6f, 1f);
                    return b;
                }

                var onlyDim    = DrawWith(dim);
                var onlyStrong = DrawWith(strong);
                var both       = DrawWith(dim, strong);

                // shade -> triStep = step/shade, so a HIGHER shade means MORE samples: the
                // combined max can only match or beat each single-light count, never fall
                // between or below them.
                Ok($"two lights together are at least as dense as either alone "
                 + $"(both {both.Count:N0} >= dim {onlyDim.Count:N0}, strong {onlyStrong.Count:N0})",
                   both.Count >= onlyDim.Count && both.Count >= onlyStrong.Count);
            }

            // ── Shadows: a body between the light and another body dims the far one,
            // only when castShadowsOnOthers is on ───────────────────────────────────
            {
                // Generous bounds so the two stacked bodies (100 mm apart) both land well
                // inside the volume -- this test is about shadowing, not clipping.
                const float radius = 50f, zHalf = 50f;

                static float[] OffsetZ(float[] tris, float dz)
                {
                    var o = (float[])tris.Clone();
                    for (int k = 2; k < o.Length; k += 3) o[k] += dz;
                    return o;
                }

                var blocker = new BodySpec { Path = "A:1", Name = "Blocker", Tris = Cube(10f) };
                var target  = new BodySpec
                    { Path = "B:1", Name = "Target", Tris = OffsetZ(Cube(10f), 100f) };

                byte[] pair = BuildFrame(new[] { blocker, target });
                var stacked = FusionWire.ToSolids(
                                  FusionWire.Parse(pair, pair.Length), notes);

                // Straight below both, along Z: the blocker's underside sees it directly,
                // and the target sits almost exactly behind the blocker from here (a ~3
                // degree angular spread at this distance, well inside one shadow bucket).
                var overhead = CadLight.AtPoint(5f, 5f, -200f, 0f, 0.1f);

                VoxelBatch DrawStacked(bool castShadows)
                {
                    var b = new VoxelBatch();
                    b.BeginFrame(400_000, radius, zHalf, 0.03f);
                    new CadSceneRenderer().Draw(b, new SceneCamera(), stacked,
                            CadPlacement.Default(zHalf), new[] { overhead },
                            radius, zHalf, 0.6f, 1f, null, null,
                            selfShadow: false, castShadowsOnOthers: castShadows);
                    return b;
                }

                var unshadowed = DrawStacked(castShadows: false);
                var shadowed   = DrawStacked(castShadows: true);

                Ok($"casting shadows on other bodies reduces density where one blocks "
                 + $"another ({shadowed.Count:N0} < {unshadowed.Count:N0})",
                   shadowed.Count < unshadowed.Count);
            }

            // ── Cutting plane: keeps only one side, and stays FIXED IN MODEL SPACE — a
            // camera rotation must not change which half a print-slice cut keeps ─────
            {
                const float radius = 4f, zHalf = 2f;
                byte[] frame = BuildFrame(new[]
                    { new BodySpec { Path = "Cube:1", Name = "Cube", Tris = Cube(10f) } });
                var solids = FusionWire.ToSolids(FusionWire.Parse(frame, frame.Length), notes);
                var off = CadLight.Off(0.35f);

                var full = new VoxelBatch();
                full.BeginFrame(400_000, radius, zHalf, 0.03f);
                new CadSceneRenderer().Draw(full, new SceneCamera(), solids,
                        CadPlacement.Default(zHalf), off, radius, zHalf, 0.6f, 1f);

                // z=5 bisects a 0..10 mm cube. Positive side keeps z>=5 (the top half);
                // the other keeps z<=5 -- the complementary bottom half.
                var topHalf    = new CutPlane(0f, 0f, 1f, 5f, true,  0xFF0000, 0f);
                var bottomHalf = new CutPlane(0f, 0f, 1f, 5f, false, 0xFF0000, 0f);

                (VoxelBatch batch, CadSceneRenderer scene) DrawCut(SceneCamera cam, CutPlane plane)
                {
                    var b = new VoxelBatch();
                    b.BeginFrame(400_000, radius, zHalf, 0.03f);
                    var s = new CadSceneRenderer();
                    s.Draw(b, cam, solids, CadPlacement.Default(zHalf),
                          new[] { off }, radius, zHalf, 0.6f, 1f, null, null, false, false, plane);
                    return (b, s);
                }

                var (top, topScene)       = DrawCut(new SceneCamera(), topHalf);
                var (bottom, bottomScene) = DrawCut(new SceneCamera(), bottomHalf);

                Ok($"a cutting plane draws less than the whole model on either side "
                 + $"(top {top.Count:N0}, bottom {bottom.Count:N0} < full {full.Count:N0})",
                   top.Count > 0 && top.Count < full.Count
                   && bottom.Count > 0 && bottom.Count < full.Count);
                Ok($"the two halves together roughly account for the whole cube "
                 + $"(top {top.Count:N0} + bottom {bottom.Count:N0} ~ full {full.Count:N0})",
                   Math.Abs((top.Count + bottom.Count) - full.Count) < full.Count * 0.15f);

                var rotatedCam = new SceneCamera();
                rotatedCam.RotateLocal(0.7f, 0.4f, -0.3f);
                var (topRotated, topRotatedScene) = DrawCut(rotatedCam, topHalf);
                Ok($"the cut stays fixed to the model through a camera rotation "
                 + $"({topRotatedScene.TrianglesDrawn} vs {topScene.TrianglesDrawn} triangles kept)",
                   topRotatedScene.TrianglesDrawn == topScene.TrianglesDrawn);
            }

            // ── Cursor probe: nearest body by bounding-box distance ──────────────────
            {
                var near = new CadSolid { Name = "Near", AssemblyPath = "Near:1", Visible = true,
                                          MinX = 0, MinY = 0, MinZ = 0, MaxX = 10, MaxY = 10, MaxZ = 10 };
                var far = new CadSolid { Name = "Far", AssemblyPath = "Far:1", Visible = true,
                                         MinX = 100, MinY = 0, MinZ = 0, MaxX = 110, MaxY = 10, MaxZ = 10 };
                var hiddenClose = new CadSolid { Name = "HiddenClose", AssemblyPath = "Hidden:1",
                                                 Visible = false,
                                                 MinX = 1, MinY = 1, MinZ = 1, MaxX = 2, MaxY = 2, MaxZ = 2 };
                var probeSolids = new List<CadSolid> { near, far, hiddenClose };

                int atOrigin = CadProbe.NearestBody(probeSolids, -5f, 0f, 0f);
                Ok($"a point closest to 'Near' picks it, not the further body ({atOrigin})",
                   atOrigin == probeSolids.IndexOf(near));

                int inside = CadProbe.NearestBody(probeSolids, 5f, 5f, 5f);
                Ok($"a point INSIDE a box picks that box, distance zero ({inside})",
                   inside == probeSolids.IndexOf(near));

                int nearFar = CadProbe.NearestBody(probeSolids, 105f, 5f, 5f);
                Ok($"moving the point near the far body picks IT instead ({nearFar})",
                   nearFar == probeSolids.IndexOf(far));

                int ignoresHidden = CadProbe.NearestBody(probeSolids, 1.5f, 1.5f, 1.5f);
                Ok($"a Fusion-hidden body is never picked, even from inside its own box ({ignoresHidden})",
                   ignoresHidden != probeSolids.IndexOf(hiddenClose));

                int empty = CadProbe.NearestBody(Array.Empty<CadSolid>(), 0f, 0f, 0f);
                Ok($"no bodies at all reports -1, not a crash ({empty})", empty == -1);
            }

            // ── Explode: a real per-body displacement, not just a plumbed-through no-op ──
            //
            // The "a body at the assembly's own centre does not move" rule lives in
            // EDesApp.BodyExplode (which picks the direction from the assembly's bounding
            // box), not in CadSceneRenderer itself -- EDesApp.cs is UI-layer and not part of
            // this test project, so that rule is not checkable from here. What IS this
            // renderer's own job, and what belongs here, is applying whatever offset it is
            // given, exactly, every time.
            {
                const float radius = 4f, zHalf = 2f;
                var solid = new BodySpec { Path = "Off:1", Name = "Offcentre", Tris = Cube(10f) };
                byte[] frame = BuildFrame(new[] { solid });
                var solids = FusionWire.ToSolids(FusionWire.Parse(frame, frame.Length), notes);
                var off = new[] { CadLight.Off(0.35f) };

                VoxelBatch DrawExploded(Func<CadSolid, (float, float, float)>? explodeOf)
                {
                    var b = new VoxelBatch();
                    b.BeginFrame(400_000, radius, zHalf, 0.03f);
                    new CadSceneRenderer().Draw(b, new SceneCamera(), solids,
                            CadPlacement.Default(zHalf), off, radius, zHalf, 0.6f, 1f,
                            null, null, false, false, CutPlane.Off, explodeOf);
                    return b;
                }

                var noCallback  = DrawExploded(null);
                var zeroOffset  = DrawExploded(_ => (0f, 0f, 0f));
                Ok($"no explodeOf and a zero offset draw identically ({noCallback.Count:N0} "
                 + $"vs {zeroOffset.Count:N0})", noCallback.Count == zeroOffset.Count);

                // Push it far enough off-centre (in DISPLAY terms) that it leaves the volume
                // entirely -- proof the offset really reaches the geometry, not just that
                // the callback gets called.
                var pushedOut = DrawExploded(_ => (5000f, 0f, 0f));   // 5000mm * 0.04 units/mm = 200 units
                Ok($"a large enough push moves the body out of the volume entirely "
                 + $"({pushedOut.Count} voxels)", pushedOut.Count == 0);
            }

            // ── Cross-language conformance: Python writes, C# reads ──────────
            //
            // The add-in that BUILDS this format is Python; the client that READS it is
            // this code. Two implementations of one format is exactly where they drift,
            // and the drift would show up as subtly wrong geometry on hardware rather
            // than as an error. So a frame produced by the real add-in code path is
            // checked in as a fixture and asserted here.
            //
            // Regenerate it only when the format changes ON PURPOSE:
            //     python fusion/tests/make_golden.py
            {
                string golden = FindGolden();
                if (golden == null)
                {
                    Console.WriteLine("SKIP  golden frame not found "
                                    + "(fusion/tests/golden_frame.bin)");
                }
                else
                {
                    byte[] bytes = File.ReadAllBytes(golden);
                    var f = FusionWire.Parse(bytes, bytes.Length);

                    Ok($"the add-in's own frame parses here ({(f.Failed ? f.Error : "ok")})",
                       !f.Failed);

                    if (!f.Failed)
                    {
                        Ok($"document survives the language boundary ('{f.Document}')",
                           f.Document == "GoldenCube");
                        Ok($"revision survives ('{f.Revision}')", f.Revision == "gold01");
                        Ok($"the dropped count survives ({f.Dropped})", f.Dropped == 7);
                        Ok($"two bodies, 24 triangles ({f.Bodies.Count}, {f.TriangleCount})",
                           f.Bodies.Count == 2 && f.TriangleCount == 24);

                        // Offsets are in VERTICES/TRIANGLES, not bytes. If the two sides
                        // disagreed about that, the second body would read garbage that
                        // still drew.
                        Ok($"the second body's vertex offset is in vertices "
                         + $"({f.Bodies[1].VertexOffset})", f.Bodies[1].VertexOffset == 8);
                        Ok($"the second body's triangle offset is in triangles "
                         + $"({f.Bodies[1].TriangleOffset})", f.Bodies[1].TriangleOffset == 12);

                        Ok("Fusion's visibility crosses intact",
                           f.Bodies[0].Visible && !f.Bodies[1].Visible);
                        Ok($"paths keep duplicate names apart "
                         + $"('{f.Bodies[1].Path}')",
                           f.Bodies[0].Path != f.Bodies[1].Path
                           && f.Bodies[1].Path.Contains("Hidden"));

                        // THE units assertion. The add-in was handed a 1 cm cube and must
                        // have converted it to 10 mm. A missing x10 here is the single
                        // most plausible-looking bug in the whole bridge.
                        var solids = FusionWire.ToSolids(f, notes);
                        Ok($"a 1 cm cube arrived as 10 mm — the cm->mm conversion holds "
                         + $"({solids[0].MaxX:0.###})",
                           solids.Count == 2 && Math.Abs(solids[0].MaxX - 10f) < 1e-4
                                             && Math.Abs(solids[0].MinX) < 1e-4);

                        Ok($"and it still collapses to six faces "
                         + $"({solids[0].Faces.Count})", solids[0].Faces.Count == 6);
                    }
                }
            }

            return _failures;
        }

        /// <summary>Find the checked-in fixture from wherever the test binary runs.</summary>
        private static string? FindGolden()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int up = 0; up < 8 && dir != null; up++, dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "fusion", "tests", "golden_frame.bin");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static string Trim(string s)
            => s.Length <= 46 ? s : s.Substring(0, 46) + "...";
    }
}
