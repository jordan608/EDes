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

            int offset = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                int tris = b.Tris.Length / 9;
                if (i > 0) sb.Append(',');
                sb.Append("{\"path\":\"").Append(b.Path).Append('"')
                  .Append(",\"name\":\"").Append(b.Name).Append('"')
                  .Append(",\"visible\":").Append(b.Visible ? "true" : "false")
                  .Append(",\"triangles\":").Append(tris)
                  .Append(",\"offset\":").Append(offset).Append('}');
                offset += tris;
            }
            sb.Append("]}");

            byte[] header = Encoding.UTF8.GetBytes(sb.ToString());
            var payload = new List<float>();
            foreach (var b in list) payload.AddRange(b.Tris);

            var frame = new byte[8 + header.Length + payload.Count * 4];
            Buffer.BlockCopy(FusionWire.Magic, 0, frame, 0, 4);
            BitConverter.GetBytes((uint)header.Length).CopyTo(frame, 4);
            Buffer.BlockCopy(header, 0, frame, 8, header.Length);
            Buffer.BlockCopy(payload.ToArray(), 0, frame, 8 + header.Length,
                             payload.Count * 4);
            return frame;
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

                // And the budget is respected: a tiny limit must not be exceeded.
                var b5 = new VoxelBatch();
                b5.BeginFrame(2_000, radius, zHalf, 0.03f);
                new CadSceneRenderer().Draw(b5, new SceneCamera(), solids,
                        CadPlacement.Default(zHalf), CadLight.Off(0.35f),
                        radius, zHalf, 2f, 1f);
                Ok($"a tight budget is not exceeded ({b5.Count} <= 2000)", b5.Count <= 2000);
            }

            return _failures;
        }

        private static string Trim(string s)
            => s.Length <= 46 ? s : s.Substring(0, 46) + "...";
    }
}
