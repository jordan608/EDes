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
        public readonly float ContentTopZ;        // first free row under the header
        public readonly float ContentBottomZ;     // last free row above the footer
        public readonly float FooterTopZ;         // first footer row

        public FrameLayout(float zHalf, float step, int headerRows, int footerRows)
        {
            // Use the FULL height of the volume: z runs -zHalf .. +zHalf.
            TopZ    = -zHalf;
            BottomZ =  zHalf;
            Step    = step;

            HeaderZ    = TopZ;
            SubHeaderZ = TopZ + step;

            ContentTopZ    = TopZ + step * (headerRows + 0.5f);
            FooterTopZ     = BottomZ - step * Math.Max(0, footerRows);
            ContentBottomZ = FooterTopZ - step * 0.5f;
        }

        public float ContentHeight => MathF.Max(0f, ContentBottomZ - ContentTopZ);

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

        public TextStack Footer() => new TextStack(FooterTopZ, Step);
    }
}
