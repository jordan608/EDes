// ═══════════════════════════════════════════════════════════════════════════
//  PcbNets.cs — connectivity from geometry, because Gerber has no nets
//
//  "Light up the whole trace" needs to know which copper belongs together, and
//  plain Gerber does not say. It is a photoplotter format: apertures and
//  coordinates, no names, no topology. Gerber X2 CAN carry net names as object
//  attributes (%TO.N,<net>*%) and GerberParser records them when present — but
//  they are optional and Altium does not emit them unless asked, so the exports
//  people actually have usually contain none. Measured on a real Altium export:
//  zero TO.N, TO.P and TO.C attributes across both copper layers.
//
//  So connectivity is DERIVED: a union-find over copper objects, joined where
//  they physically touch.
//
//    • two segment endpoints within tolerance of each other  → same net
//    • a segment endpoint inside a pad                        → same net
//    • anything reaching into the same PLATED hole            → same net,
//      across layers (this is what makes a net follow its vias)
//
//  What this deliberately does NOT do:
//
//    • Mid-span crossings. Two traces that cross without sharing an endpoint are
//      NOT joined. On a real board that is a different net on a different layer,
//      and joining them would merge half the board into one net — a wrong answer
//      that looks confident. Endpoint and pad contact is how routers actually
//      connect copper.
//    • Copper pours. A pour is a region, and treating its outline as a trace
//      would swallow every net that touches the plane.
//
//  The result is connectivity, not netlist truth. Where a real name exists it is
//  used; otherwise nets are numbered, and the numbering is deterministic (driven
//  by layer and object order, never by hash iteration) so the same board gives
//  the same net ids every import.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;

namespace EDes.Pcb
{
    public sealed class PcbNets
    {
        /// <summary>How close two pieces of copper must be to count as touching, in mm.
        /// Gerber coordinates here are 4-decimal mm, so 0.02 is well above quantisation
        /// noise while staying far below any real clearance.</summary>
        public const float TOL_MM = 0.02f;

        /// <summary>Net id per segment, indexed [layerIndex][segIndex]. -1 = not copper.</summary>
        private readonly Dictionary<int, int[]> _segNet = new();
        private readonly Dictionary<int, int[]> _padNet = new();

        private string[] _names = Array.Empty<string>();
        private int[]    _sizes = Array.Empty<int>();

        public int NetCount { get; private set; }

        public int SegNet(int layerIndex, int segIndex)
            => _segNet.TryGetValue(layerIndex, out var a) &&
               segIndex >= 0 && segIndex < a.Length ? a[segIndex] : -1;

        public int PadNet(int layerIndex, int padIndex)
            => _padNet.TryGetValue(layerIndex, out var a) &&
               padIndex >= 0 && padIndex < a.Length ? a[padIndex] : -1;

        /// <summary>Name for a net — the real one if the Gerber carried it, else a
        /// generated label. Generated labels say so, rather than pretending.</summary>
        public string Name(int net)
            => net >= 0 && net < _names.Length && _names[net].Length > 0
               ? _names[net]
               : (net >= 0 ? $"NET {net + 1} (derived)" : "");

        /// <summary>Copper objects on this net — the honest measure of how much will
        /// light up.</summary>
        public int Size(int net) => net >= 0 && net < _sizes.Length ? _sizes[net] : 0;

        // ─────────────────────────────────────────────────────────────────────
        //  Build
        // ─────────────────────────────────────────────────────────────────────

        public static PcbNets Build(PcbBoard board)
        {
            var nets = new PcbNets();
            nets.BuildInternal(board);
            return nets;
        }

        private int[] _parent = Array.Empty<int>();

        private int Find(int a)
        {
            while (_parent[a] != a)
            {
                _parent[a] = _parent[_parent[a]];   // halving, so deep chains flatten
                a = _parent[a];
            }
            return a;
        }

        private void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) _parent[b] = a;
        }

        /// <summary>One copper object: which layer, whether it is a pad, and its index.</summary>
        private struct Node
        {
            public int  Layer;
            public bool IsPad;
            public int  Item;
        }

        private void BuildInternal(PcbBoard board)
        {
            var nodes   = new List<Node>();
            var segBase = new Dictionary<int, int>();
            var padBase = new Dictionary<int, int>();

            // ── Enumerate copper objects, in a fixed order ────────────────────
            for (int li = 0; li < board.Layers.Count; li++)
            {
                var layer = board.Layers[li];
                if (layer.Kind is not (PcbLayerKind.CopperTop or PcbLayerKind.CopperInner
                                       or PcbLayerKind.CopperBottom)) continue;

                segBase[li] = nodes.Count;
                for (int i = 0; i < layer.Segs.Count; i++)
                    nodes.Add(new Node { Layer = li, IsPad = false, Item = i });

                padBase[li] = nodes.Count;
                for (int i = 0; i < layer.Pads.Count; i++)
                    nodes.Add(new Node { Layer = li, IsPad = true, Item = i });
            }

            if (nodes.Count == 0) { NetCount = 0; return; }

            _parent = new int[nodes.Count];
            for (int i = 0; i < _parent.Length; i++) _parent[i] = i;

            // ── Within a layer: endpoints that meet ───────────────────────────
            // Hashed on a grid of the tolerance, checking the 3x3 neighbourhood, so two
            // endpoints that land either side of a cell boundary still meet. Without the
            // neighbourhood sweep, connectivity would depend on where the grid happened
            // to fall — nets would split at arbitrary coordinates.
            var endpoints = new Dictionary<long, List<int>>();

            void AddEndpoint(int layer, float x, float y, int node)
            {
                long key = CellKey(layer, x, y, TOL_MM);
                if (!endpoints.TryGetValue(key, out var list))
                    endpoints[key] = list = new List<int>();
                list.Add(node);
            }

            for (int li = 0; li < board.Layers.Count; li++)
            {
                if (!segBase.TryGetValue(li, out int sb)) continue;
                var layer = board.Layers[li];
                for (int i = 0; i < layer.Segs.Count; i++)
                {
                    var sg = layer.Segs[i];
                    AddEndpoint(li, sg.X0, sg.Y0, sb + i);
                    AddEndpoint(li, sg.X1, sg.Y1, sb + i);
                }
            }

            foreach (var kv in endpoints)
            {
                DecodeKey(kv.Key, out int layer, out int gx, out int gy);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    long nk = MakeKey(layer, gx + dx, gy + dy);
                    if (!endpoints.TryGetValue(nk, out var other)) continue;
                    // Union everything in the neighbourhood; the exact distance test is
                    // skipped because the cell size IS the tolerance.
                    foreach (int a in kv.Value)
                        foreach (int b in other) Union(a, b);
                }
            }

            // ── Segment endpoint inside a pad ─────────────────────────────────
            // Coarser grid: pads are up to a couple of mm, so a tolerance-sized grid would
            // mean sweeping a hundred cells per pad.
            const float PAD_CELL = 1.0f;
            var padGrid = new Dictionary<long, List<int>>();
            for (int li = 0; li < board.Layers.Count; li++)
            {
                if (!padBase.TryGetValue(li, out int pb)) continue;
                var layer = board.Layers[li];
                for (int i = 0; i < layer.Pads.Count; i++)
                {
                    var pd = layer.Pads[i];
                    long key = CellKey(li, pd.X, pd.Y, PAD_CELL);
                    if (!padGrid.TryGetValue(key, out var list))
                        padGrid[key] = list = new List<int>();
                    list.Add(pb + i);
                }
            }

            for (int li = 0; li < board.Layers.Count; li++)
            {
                if (!segBase.TryGetValue(li, out int sb)) continue;
                var layer = board.Layers[li];
                if (!padBase.TryGetValue(li, out int pb)) continue;

                for (int i = 0; i < layer.Segs.Count; i++)
                {
                    var sg = layer.Segs[i];
                    JoinEndpointToPads(layer, li, sg.X0, sg.Y0, sb + i, pb, padGrid, PAD_CELL);
                    JoinEndpointToPads(layer, li, sg.X1, sg.Y1, sb + i, pb, padGrid, PAD_CELL);
                }
            }

            // ── Plated holes: the same net across layers ──────────────────────
            // This is what makes a net follow its vias. Unplated holes are mechanical and
            // conduct nothing, so they must not join anything.
            foreach (var h in board.Holes)
            {
                if (!h.Plated) continue;
                float reach = h.Dia * 0.5f + TOL_MM * 4f;

                int anchor = -1;
                for (int li = 0; li < board.Layers.Count; li++)
                {
                    var layer = board.Layers[li];
                    if (segBase.TryGetValue(li, out int sb))
                        for (int i = 0; i < layer.Segs.Count; i++)
                        {
                            var sg = layer.Segs[i];
                            if (!Near(sg.X0, sg.Y0, h.X, h.Y, reach) &&
                                !Near(sg.X1, sg.Y1, h.X, h.Y, reach)) continue;
                            if (anchor < 0) anchor = sb + i; else Union(anchor, sb + i);
                        }

                    if (padBase.TryGetValue(li, out int pb))
                        for (int i = 0; i < layer.Pads.Count; i++)
                        {
                            var pd = layer.Pads[i];
                            if (!Near(pd.X, pd.Y, h.X, h.Y, reach)) continue;
                            if (anchor < 0) anchor = pb + i; else Union(anchor, pb + i);
                        }
                }
            }

            // ── Number the nets, deterministically ────────────────────────────
            // Driven by node order, never by dictionary iteration, so the same board
            // always yields the same ids — otherwise a net's colour and label would
            // change between runs of the same import.
            var netOf = new Dictionary<int, int>();
            var sizes = new List<int>();
            var names = new List<string>();
            var assigned = new int[nodes.Count];

            for (int n = 0; n < nodes.Count; n++)
            {
                int root = Find(n);
                if (!netOf.TryGetValue(root, out int id))
                {
                    id = sizes.Count;
                    netOf[root] = id;
                    sizes.Add(0);
                    names.Add("");
                }
                assigned[n] = id;
                sizes[id]++;
            }

            NetCount = sizes.Count;
            _sizes   = sizes.ToArray();

            // A real name from the Gerber wins, if any object on the net carried one.
            for (int n = 0; n < nodes.Count; n++)
            {
                var nd = nodes[n];
                var layer = board.Layers[nd.Layer];
                string nm = nd.IsPad ? layer.PadNetName(nd.Item) : layer.SegNetName(nd.Item);
                if (nm.Length > 0 && names[assigned[n]].Length == 0) names[assigned[n]] = nm;
            }
            _names = names.ToArray();

            // ── Scatter back into per-layer arrays ────────────────────────────
            foreach (var kv in segBase)
            {
                var layer = board.Layers[kv.Key];
                var arr = new int[layer.Segs.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = assigned[kv.Value + i];
                _segNet[kv.Key] = arr;
            }
            foreach (var kv in padBase)
            {
                var layer = board.Layers[kv.Key];
                var arr = new int[layer.Pads.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = assigned[kv.Value + i];
                _padNet[kv.Key] = arr;
            }
        }

        private void JoinEndpointToPads(PcbLayer layer, int li, float x, float y, int node,
                                        int padBase, Dictionary<long, List<int>> padGrid,
                                        float cell)
        {
            int gx = (int)MathF.Floor(x / cell), gy = (int)MathF.Floor(y / cell);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!padGrid.TryGetValue(MakeKey(li, gx + dx, gy + dy), out var list)) continue;
                foreach (int pn in list)
                {
                    int pi = pn - padBase;
                    if (pi < 0 || pi >= layer.Pads.Count) continue;
                    if (InPad(layer.Pads[pi], x, y)) Union(node, pn);
                }
            }
        }

        private static bool InPad(in PcbPad pad, float x, float y)
        {
            float hw = pad.W * 0.5f + TOL_MM, hh = pad.H * 0.5f + TOL_MM;
            if (hh <= TOL_MM) hh = hw;      // circles record only W

            return pad.Shape == PadShape.Circle
                ? (x - pad.X) * (x - pad.X) + (y - pad.Y) * (y - pad.Y) <= hw * hw
                : MathF.Abs(x - pad.X) <= hw && MathF.Abs(y - pad.Y) <= hh;
        }

        private static bool Near(float ax, float ay, float bx, float by, float r)
            => (ax - bx) * (ax - bx) + (ay - by) * (ay - by) <= r * r;

        // Layer index in the high bits, grid coords below. Packed rather than a tuple key
        // so the dictionary does not allocate on every lookup in the neighbourhood sweep.
        private static long CellKey(int layer, float x, float y, float cell)
            => MakeKey(layer, (int)MathF.Floor(x / cell), (int)MathF.Floor(y / cell));

        private static long MakeKey(int layer, int gx, int gy)
            => ((long)(uint)layer << 48) ^ ((long)(gx & 0xFFFFFF) << 24) ^ (uint)(gy & 0xFFFFFF);

        private static void DecodeKey(long key, out int layer, out int gx, out int gy)
        {
            layer = (int)((key >> 48) & 0xFFFF);
            gx = (int)((key >> 24) & 0xFFFFFF);
            if ((gx & 0x800000) != 0) gx |= unchecked((int)0xFF000000);
            gy = (int)(key & 0xFFFFFF);
            if ((gy & 0x800000) != 0) gy |= unchecked((int)0xFF000000);
        }
    }
}
