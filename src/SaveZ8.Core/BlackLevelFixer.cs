namespace SaveZ8.Core;

/// <summary>
/// Implements the Z8 black-level −1 LSB fix using the notch criterion.
/// The notch criterion (c1008 < c1007/3) does not false-trigger on healthy frames
/// whose analog black center is naturally below 1007.5 (e.g., ISO 100).
/// </summary>
public class BlackLevelFixer
{
    public const int BlackLevelDefault = 1008;
    public const int NotchRatio = 3;

    public enum FixMode
    {
        /// <summary>Notch criterion: c1008 &lt; c1007/3 — safe for all ISOs (recommended)</summary>
        Notch,
        /// <summary>Original Save Z8 criterion: c1008 &lt; c1007 — may false-trigger on ISO 100</summary>
        SaveZ8,
        /// <summary>No fixing, pass-through</summary>
        Disabled
    }

    public class FixResult
    {
        public bool Applied { get; init; }
        public long PixelsFixed { get; init; }
        public int[] Thresholds { get; init; } = new int[4];
        public long[] Counts1006 { get; init; } = new long[4];
        public long[] Counts1007 { get; init; } = new long[4];
        public long[] Counts1008 { get; init; } = new long[4];
    }

    /// <summary>
    /// Apply the black-level fix in-place on the mosaic pixel array.
    /// </summary>
    /// <param name="pixels">u16 mosaic array (will be modified)</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="mode">Fix criterion mode</param>
    /// <param name="blackLevel">Nominal black level (default 1008)</param>
    /// <returns>Fix result with statistics</returns>
    public static unsafe FixResult Fix(ushort[] pixels, int width, int height,
        FixMode mode = FixMode.Notch, int blackLevel = BlackLevelDefault)
    {
        if (mode == FixMode.Disabled)
            return new FixResult { Applied = false };

        int bl = blackLevel;
        int b0 = bl - 2, b1 = bl - 1, b2 = bl; // {1006, 1007, 1008}

        // Pass 1: per-CFA-quadrant histogram
        var counts = new long[4, 3]; // [quadrant, {b0,b1,b2}]
        fixed (ushort* p = pixels)
        {
            for (int row = 0; row < height; row++)
            {
                ushort* rowPtr = p + (long)row * width;
                int qRow = (row & 1) << 1;
                for (int col = 0; col < width; col++)
                {
                    ushort v = rowPtr[col];
                    if (v >= b0 && v <= b2)
                    {
                        int q = qRow | (col & 1);
                        counts[q, v - b0]++;
                    }
                }
            }
        }

        // Decision
        var thresholds = new int[4];
        bool anyFix = false;
        for (int q = 0; q < 4; q++)
        {
            long c0 = counts[q, 0]; // count at BL-2 (1006)
            long c1 = counts[q, 1]; // count at BL-1 (1007)
            long c2 = counts[q, 2]; // count at BL   (1008)

            thresholds[q] = mode switch
            {
                FixMode.SaveZ8 when c2 < c1 => b1,
                FixMode.SaveZ8 when c2 > c1 && c1 < c0 => b0,
                FixMode.Notch when c2 * NotchRatio < c1 => b1,
                FixMode.Notch when c2 > c1 && c1 * NotchRatio < c0 => b0,
                _ => 0
            };

            if (thresholds[q] > 0) anyFix = true;
        }

        if (!anyFix)
        {
            return new FixResult
            {
                Applied = false,
                Thresholds = thresholds,
                Counts1006 = new[] { counts[0,0], counts[1,0], counts[2,0], counts[3,0] },
                Counts1007 = new[] { counts[0,1], counts[1,1], counts[2,1], counts[3,1] },
                Counts1008 = new[] { counts[0,2], counts[1,2], counts[2,2], counts[3,2] }
            };
        }

        // Pass 2: apply fix
        long totalFixed = 0;
        fixed (ushort* p = pixels)
        {
            for (int row = 0; row < height; row++)
            {
                ushort* rowPtr = p + (long)row * width;
                int qRow = (row & 1) << 1;
                for (int col = 0; col < width; col++)
                {
                    int q = qRow | (col & 1);
                    int t = thresholds[q];
                    if (t > 0 && rowPtr[col] <= t)
                    {
                        rowPtr[col]++;
                        totalFixed++;
                    }
                }
            }
        }

        return new FixResult
        {
            Applied = true,
            PixelsFixed = totalFixed,
            Thresholds = thresholds,
            Counts1006 = new[] { counts[0,0], counts[1,0], counts[2,0], counts[3,0] },
            Counts1007 = new[] { counts[0,1], counts[1,1], counts[2,1], counts[3,1] },
            Counts1008 = new[] { counts[0,2], counts[1,2], counts[2,2], counts[3,2] }
        };
    }
}
