// ═══════════════════════════════════════════════════════════════════════════
//  CircuitRenderer.cs — draws a solved CircuitScene
//
//  Reads the flat WireSegment list and nothing else: no solving, no layout, no
//  allocation. Everything is emitted through VoxelBatch (bounds-clipped, budget
//  limited, one native call per frame) and through SceneCamera.Transform so the
//  whole scene pans/rotates/zooms as one.
//
//  What the visuals encode (the animation IS the data, not decoration):
//    - resistor colour  = fraction of this circuit's max power dissipation
//                         (blue > cyan > yellow > red)
//    - resistor height  = same fraction, raised toward -Z (it "heats up")
//    - wire brightness  = branch current relative to the total
//    - flow-dot speed   = that branch's actual current
//    - parallel branches sit in separate Y lanes, so you can walk around the
//      display and compare them instead of reading overlapping lines
//
//  Text policy (PROJECT_GUIDE section 7.5): facts about the circuit are drawn
//  IN the volume. Only operating chrome belongs on a flat overlay.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using Voxon;

namespace EDes.Sim
{
    public sealed class CircuitRenderer
    {
        private const int   ZIG_PEAKS = 6;
        private const float LEAD_FRAC = 0.18f;   // lead stub at each end of a resistor

        /// <summary>Draw the circuit. animClock advances the flow dots; pass
        /// showLabels=false to reclaim the voxels that text costs.</summary>
        public void Draw(VoxelBatch batch, Hud hud, SceneCamera cam, CircuitScene scene,
                         float animClock, bool showLabels, float textSize, float textStep)
        {
            double maxCurrent = Math.Max(1e-9, scene.TotalCurrent);
            // The bulge may only use the headroom CircuitScene reserved above the
            // top wire, so a hot resistor cannot climb into the header text.
            float  bulgeMax   = MathF.Max(textStep * 0.5f, (scene.BotZ - scene.TopZ) * 0.45f);

            foreach (var seg in scene.Segments)
            {
                if (batch.BudgetHit) break;

                if (seg.IsBattery)
                    DrawBattery(batch, hud, cam, seg, scene, textSize, showLabels);
                else if (seg.Body != null)
                    DrawResistor(batch, hud, cam, seg, scene, bulgeMax, textSize, textStep, showLabels);
                else if (seg.Led != null)
                    DrawDiode(batch, hud, cam, seg, textSize, showLabels);
                else
                    DrawWire(batch, cam, seg, maxCurrent);
            }

            DrawFlowDots(batch, cam, scene, animClock, maxCurrent);
        }

        // ── Plain wire ────────────────────────────────────────────────────────
        private static void DrawWire(VoxelBatch batch, SceneCamera cam, in WireSegment seg,
                                     double maxCurrent)
        {
            float f   = (float)Math.Clamp(seg.Current / maxCurrent, 0.0, 1.0);
            int   col = Palette.Mix(Palette.WireDim, Palette.WireBright, 0.35f + 0.65f * f);
            batch.Line(cam.Transform(seg.Start), cam.Transform(seg.End), col);
        }

        // ── Diode: the schematic triangle-and-bar, lit when it conducts ───────
        //
        // Whether it is CONDUCTING is the whole lesson, so that is what the drawing
        // encodes: passing current it is a solid bright wedge, below its forward drop it
        // is a dim outline. Not a colour change alone -- on a seven-colour display with no
        // brightness axis, "dimmer" has to mean "sparser", so the non-conducting state is
        // drawn as an outline while the conducting one is filled.
        private static void DrawDiode(VoxelBatch batch, Hud hud, SceneCamera cam,
                                      in WireSegment seg, float textSize, bool showLabels)
        {
            var d = seg.Led!;
            bool on = d.Current > 1e-9;

            // Leads in, symbol in the middle third: the standard schematic proportions,
            // so it reads as a diode rather than as a decorated wire.
            point3d a = seg.Start, b = seg.End;
            point3d p1 = CircuitScene.Lerp(a, b, 0.34f);
            point3d p2 = CircuitScene.Lerp(a, b, 0.66f);

            int col  = on ? Palette.Warning : Palette.TextDim;
            int lead = on ? Palette.WireBright : Palette.WireDim;

            batch.Line(cam.Transform(a),  cam.Transform(p1), lead);
            batch.Line(cam.Transform(p2), cam.Transform(b),  lead);

            // The triangle points along the current: anode at p1, cathode bar at p2.
            float h = Dist(p1, p2) * 0.55f;
            var upA = new point3d(p1.x, p1.y, p1.z - h);
            var dnA = new point3d(p1.x, p1.y, p1.z + h);
            var upB = new point3d(p2.x, p2.y, p2.z - h);
            var dnB = new point3d(p2.x, p2.y, p2.z + h);

            batch.Line(cam.Transform(upA), cam.Transform(dnA), col);   // back of the wedge
            batch.Line(cam.Transform(upA), cam.Transform(p2),  col);   // to the tip
            batch.Line(cam.Transform(dnA), cam.Transform(p2),  col);
            batch.Line(cam.Transform(upB), cam.Transform(dnB), col);   // the cathode bar

            // Filled only when conducting -- that fill IS the "it is on" signal.
            if (on)
                for (int i = 1; i < 5; i++)
                {
                    float t = i / 5f;
                    var s = CircuitScene.Lerp(upA, p2, t);
                    var e = CircuitScene.Lerp(dnA, p2, t);
                    batch.Line(cam.Transform(s), cam.Transform(e), col);
                }

            if (!showLabels) return;

            string label = d.Name + "  VF " + d.Vf.ToString("0.##") + "V";
            if (!on) label += "  OFF";
            var at = new point3d(p1.x, p1.y, p1.z - h - textSize * 0.8f);
            hud.Text(cam.Transform(at), textSize * 0.7f, col, label);
        }

        private static float Dist(point3d a, point3d b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // ── Resistor: schematic zigzag, heat colour, power bulge ──────────────
        private void DrawResistor(VoxelBatch batch, Hud hud, SceneCamera cam, in WireSegment seg,
                                  CircuitScene scene, float bulgeMax, float textSize,
                                  float textStep, bool showLabels)
        {
            var   r         = seg.Body!;
            float powerFrac = (float)Math.Clamp(r.Power / Math.Max(1e-12, scene.MaxPower), 0.0, 1.0);
            int   col       = Palette.Heat(powerFrac);
            bool  selected  = ReferenceEquals(r, scene.Active.Resistors[scene.Selected]);

            // -Z is up, so SUBTRACTING from z raises the body as it heats.
            float bulge = powerFrac * bulgeMax;

            float dx = seg.End.x - seg.Start.x;
            float dy = seg.End.y - seg.Start.y;
            float dz = seg.End.z - seg.Start.z;
            float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-5f) return;

            float ux = dx / len, uz = dz / len;
            // Perpendicular in the schematic plane (x/z) — the zigzag peak direction.
            float px = -uz, pz = ux;
            float amp = MathF.Min(len * 0.16f, bulgeMax * 0.9f) + 0.02f;

            var a  = seg.Start;
            var b  = seg.End;
            var la = Lerp(a, b, LEAD_FRAC);
            var lb = Lerp(a, b, 1f - LEAD_FRAC);

            // Leads slope up into the raised body so it reads as one component.
            batch.Line(cam.Transform(a), cam.Transform(la.x, la.y, la.z - bulge), col);
            batch.Line(cam.Transform(lb.x, lb.y, lb.z - bulge), cam.Transform(b), col);

            // Zigzag between the leads: alternating perpendicular offsets.
            var prev = new point3d(la.x, la.y, la.z - bulge);
            for (int i = 1; i <= ZIG_PEAKS + 1; i++)
            {
                float t    = i / (float)(ZIG_PEAKS + 1);
                var   mid  = Lerp(la, lb, t);
                float side = (i % 2 == 0) ? -amp : amp;
                if (i == ZIG_PEAKS + 1) side = 0f;      // land back on the lead

                var pt = new point3d(mid.x + px * side, mid.y, mid.z + pz * side - bulge);
                batch.Line(cam.Transform(prev), cam.Transform(pt), col);
                prev = pt;
            }

            var centre = Lerp(la, lb, 0.5f);
            centre.z -= bulge;

            if (selected) DrawSelectionRing(batch, cam, centre, len * 0.55f);
            if (!showLabels) return;

            // Labels go BELOW the component, inside the loop, where CircuitScene
            // reserved three rows for them — never above, where the header lives.
            float step = textStep;
            float lz   = seg.Start.z + step * 0.35f;
            int   lc   = selected ? Palette.TextHilite : Palette.Text;
            string l1  = r.Name + " " + Hud.Eng(r.R, "R");
            string l2  = Hud.Eng(r.Voltage, "V") + " " + Hud.Eng(r.Current, "A");
            string l3  = Hud.Eng(r.Power, "W");

            float x0 = centre.x - Hud.Width(l1, textSize) * 0.5f;
            hud.Text(new point3d(x0, centre.y, lz),            textSize, lc, l1, cam);
            hud.Text(new point3d(x0, centre.y, lz + step),     textSize, Palette.Scale(lc, 0.75f), l2, cam);
            hud.Text(new point3d(x0, centre.y, lz + step * 2f), textSize, Palette.Heat(powerFrac), l3, cam);
        }

        private static void DrawSelectionRing(VoxelBatch batch, SceneCamera cam, point3d centre,
                                              float radius)
        {
            // Ellipse in the schematic plane, drawn point by point so the camera
            // transform applies per voxel exactly like everything else.
            const int segs = 48;
            for (int i = 0; i < segs; i++)
            {
                float t = i * 2f * MathF.PI / segs;
                float x = centre.x + MathF.Cos(t) * radius;
                float z = centre.z + MathF.Sin(t) * radius * 0.6f;
                var   p = cam.Transform(x, centre.y, z);
                if (!batch.Add(p, Palette.TextHilite) && batch.BudgetHit) return;
            }
        }

        // ── Source symbol ─────────────────────────────────────────────────────
        private static void DrawBattery(VoxelBatch batch, Hud hud, SceneCamera cam, in WireSegment seg,
                                        CircuitScene scene, float textSize, bool showLabels)
        {
            var   a   = seg.Start;
            var   b   = seg.End;
            var   mid = Lerp(a, b, 0.5f);
            float len = MathF.Abs(b.z - a.z);

            float plateLong  = len * 0.22f;
            float plateShort = len * 0.11f;
            float gap        = len * 0.07f;

            batch.Line(cam.Transform(a), cam.Transform(mid.x, mid.y, mid.z + gap), Palette.Battery);
            batch.Line(cam.Transform(mid.x, mid.y, mid.z - gap), cam.Transform(b), Palette.Battery);

            // Two plates across X: long = +, short = -.
            batch.Line(cam.Transform(mid.x - plateLong,  mid.y, mid.z - gap),
                       cam.Transform(mid.x + plateLong,  mid.y, mid.z - gap), Palette.Battery);
            batch.Line(cam.Transform(mid.x - plateShort, mid.y, mid.z + gap),
                       cam.Transform(mid.x + plateShort, mid.y, mid.z + gap),
                       Palette.Scale(Palette.Battery, 0.7f));

            if (!showLabels) return;
            string s = Hud.Eng(scene.SourceVolts, "V");
            hud.Text(new point3d(mid.x - plateLong - Hud.Width(s, textSize) - textSize * 0.4f,
                                 mid.y, mid.z - textSize * 0.6f),
                     textSize, Palette.Battery, s, cam);
        }

        // ── Current-flow animation ────────────────────────────────────────────
        // Dots at fixed spacing per segment, sliding along it at a speed scaled by
        // that branch's real current, so a loaded branch visibly flows faster.
        private static void DrawFlowDots(VoxelBatch batch, SceneCamera cam, CircuitScene scene,
                                         float animClock, double maxCurrent)
        {
            float dotSpacing = batch.Radius * 0.10f;
            float dotRadius  = batch.Spacing * 1.6f;

            foreach (var seg in scene.Segments)
            {
                if (batch.BudgetHit) return;

                float dx = seg.End.x - seg.Start.x;
                float dy = seg.End.y - seg.Start.y;
                float dz = seg.End.z - seg.Start.z;
                float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-4f) continue;

                int   count    = Math.Max(1, (int)(len / dotSpacing));
                float speedMag = (float)Math.Clamp(seg.Current / maxCurrent, 0.05, 1.0);
                int   col      = Palette.Mix(Palette.WireBright, Palette.FlowDot, speedMag);

                for (int i = 0; i < count; i++)
                {
                    float frac = (i / (float)count + animClock * speedMag * 0.35f) % 1f;
                    batch.Blob(cam.Transform(Lerp(seg.Start, seg.End, frac)), dotRadius, col);
                    if (batch.BudgetHit) return;
                }
            }
        }

        private static point3d Lerp(point3d a, point3d b, float t) => CircuitScene.Lerp(a, b, t);
    }
}
