// ═══════════════════════════════════════════════════════════════════════════
//  Layout.cs — vertical space allocation, so nothing can overlap
//
//  Text in this app used to be positioned with hand-picked fractions of the
//  display height (-0.92 * zHalf and friends). That works right up until a block
//  grows a line — an extra channel on the scope, a fourth layer in the stack —
//  and then two blocks quietly write into the same voxels and the display becomes
//  unreadable.
//
//  So vertical position is allocated instead of guessed. A TextStack hands out
//  one row at a time, top to bottom, in the display's own units, and a band is
//  reserved by asking for the rows it needs BEFORE anything is drawn. If the
//  content does not fit, the shortfall is visible in the numbers rather than as
//  overlapping glyphs.
//
//  Remember -Z is up: rows advance by ADDING to z, and the volume runs from
//  z = -zHalf (top) to z = +zHalf (bottom), with the origin at the centre.
//
//  ALL text lives at the TOP. The readout band used to sit at the bottom, which
//  split the reading between two ends of the volume — and on a display you walk
//  around, having to look in two places to read one machine is worse than having
//  slightly less room for geometry. So the header and the readouts are one
//  contiguous block at the top and the geometry gets everything below it.
// ═══════════════════════════════════════════════════════════════════════════

using System;

namespace EDes.Sim
{
    /// <summary>Allocates successive rows of text downward from a starting Z.</summary>
    public struct TextStack
    {
        public float Z;
        public readonly float Step;

        public TextStack(float z, float step)
        {
            Z    = z;
            Step = step;
        }

        /// <summary>Take the next row, advancing the cursor.</summary>
        public float Row()
        {
            float z = Z;
            Z += Step;
            return z;
        }

        /// <summary>Take n rows at once, returning the first.</summary>
        public float Rows(int n)
        {
            float z = Z;
            Z += Step * Math.Max(0, n);
            return z;
        }

        /// <summary>Leave a gap of `lines` rows.</summary>
        public void Gap(float lines = 0.5f) => Z += Step * lines;

        public float Remaining(float bottomZ) => bottomZ - Z;
    }

    /// <summary>The vertical plan for one frame: computed from the live display
    /// bounds and the number of rows each block actually needs this frame.</summary>
    public readonly struct FrameLayout
    {
        public readonly float TopZ, BottomZ;      // usable extremes (-Z is up)
        public readonly float Step;               // one text row
        public readonly float HeaderZ;            // title row
        public readonly float SubHeaderZ;         // voxel/status row
        public readonly float ContentTopZ;        // first free row under ALL the text
        public readonly float ContentBottomZ;     // last free row (the volume floor)
        public readonly float ReadoutTopZ;        // first readout row, under the header

        public FrameLayout(float zHalf, float step, int headerRows, int readoutRows)
        {
            // Use the FULL height of the volume: z runs -zHalf .. +zHalf.
            TopZ    = -zHalf;
            BottomZ =  zHalf;
            Step    = step;

            HeaderZ    = TopZ;
            SubHeaderZ = TopZ + step;

            // Header, then readouts, then geometry — all text contiguous at the top.
            ReadoutTopZ = TopZ + step * (headerRows + 0.25f);

            // The text band is CAPPED to the top half. Without this it can swallow the
            // whole volume: inspection mode alone asks for nine rows, and on a shallow
            // display that pushed ContentTopZ past the floor, so the board disappeared
            // to make room for text about the board. When the two cannot both fit, the
            // geometry wins and the surplus text runs over it — overlapping text is
            // ugly, no geometry is useless.
            float textFloor = TopZ + (BottomZ - TopZ) * 0.5f;
            float wanted    = ReadoutTopZ + step * (Math.Max(0, readoutRows) + 0.5f);

            ContentTopZ    = MathF.Min(wanted, textFloor);
            ContentBottomZ = BottomZ;
        }

        public float ContentHeight => MathF.Max(0f, ContentBottomZ - ContentTopZ);

        /// <summary>How many readout rows actually fit above the content band. Fewer than
        /// asked for means the surplus is drawing over the geometry.</summary>
        public int ReadoutRowsThatFit
            => Step > 1e-6f ? Math.Max(0, (int)((ContentTopZ - ReadoutTopZ) / Step)) : 0;

        /// <summary>Split the content band: the first `fraction` goes to the upper
        /// block (e.g. the circuit), the rest to the lower one (e.g. the scope).</summary>
        public void SplitContent(float fraction, float gapRows,
                                 out float upperTop, out float upperBottom,
                                 out float lowerTop, out float lowerBottom)
        {
            fraction = Math.Clamp(fraction, 0.1f, 0.9f);
            float gap = Step * gapRows;
            float usable = MathF.Max(Step, ContentHeight - gap);

            upperTop    = ContentTopZ;
            upperBottom = ContentTopZ + usable * fraction;
            lowerTop    = upperBottom + gap;
            lowerBottom = ContentBottomZ;
        }

        /// <summary>A cursor over the readout band at the top. Named for what it is
        /// rather than where it used to be — calling a block at the top of the display a
        /// "footer" would be actively misleading to the next reader.</summary>
        public TextStack Readout() => new TextStack(ReadoutTopZ, Step);
    }
}
