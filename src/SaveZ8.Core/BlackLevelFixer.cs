using System.Collections.Concurrent;

namespace SaveZ8.Core;

/// <summary>
/// Implements the Z8 black-level −1 LSB fix using the notch criterion.
/// The notch criterion (c1008 &lt; c1007/3) does not false-trigger on healthy frames
/// whose analog black center is naturally below 1007.5 (e.g., ISO 100).
///
/// Both passes are parallelized over disjoint row bands (Partitioner over [0, height))
/// with band-local integer accumulation; the quadrant decision runs once, sequentially,
/// on the exact sums. All arithmetic is integral, so results are bit-identical to the
/// previous single-threaded scalar implementation.
/// </summary>
public class BlackLevelFixer
{
    public const int BlackLevelDefault = 1008;
    public const int NotchRatio = 3;

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
    /// Apply the black-level fix in-place on the mosaic pixel array using the
    /// notch criterion (c1008·3 &lt; c1007 per CFA quadrant).
    /// </summary>
    /// <param name="pixels">u16 mosaic array (will be modified)</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="blackLevel">Nominal black level (default 1008)</param>
    public static unsafe FixResult Fix(ushort[] pixels, int width, int height,
        int blackLevel = BlackLevelDefault)
    {
        int bl = blackLevel;
        int b0 = bl - 2, b1 = bl - 1, b2 = bl; // {1006, 1007, 1008}

        // Pass 1: per-CFA-quadrant histogram (row-band parallel, band-local counts
        // merged by exact integer addition).
        var counts = new long[4, 3]; // [quadrant, {b0,b1,b2}]
        var gate = new object();

        if (height > 0)
        {
            Parallel.ForEach(Partitioner.Create(0, height), range =>
            {
                long[] local = CountBand(pixels, width, range.Item1, range.Item2, b0, b1, b2);
                lock (gate)
                    for (int i = 0; i < 12; i++)
                        counts[i / 3, i % 3] += local[i];
            });
        }

        // Decision: notch criterion per quadrant
        var thresholds = new int[4];
        bool anyFix = false;
        for (int q = 0; q < 4; q++)
        {
            long c0 = counts[q, 0]; // count at BL-2 (1006)
            long c1 = counts[q, 1]; // count at BL-1 (1007)
            long c2 = counts[q, 2]; // count at BL   (1008)

            thresholds[q] = (c2 * NotchRatio < c1) ? b1
                : (c2 > c1 && c1 * NotchRatio < c0) ? b0
                : 0;

            if (thresholds[q] > 0) anyFix = true;
        }

        long totalFixed = 0;
        if (anyFix && height > 0)
        {
            // Pass 2: apply fix. A quadrant with threshold 0 must never be touched —
            // int.MinValue is the "no fix" sentinel: `v <= sentinel` is false for every
            // possible pixel value (including 0), replacing the old `t > 0 && v <= t`
            // guard branchlessly.
            var sentinels = new int[4];
            for (int q = 0; q < 4; q++)
                sentinels[q] = thresholds[q] > 0 ? thresholds[q] : int.MinValue;

            Parallel.ForEach(Partitioner.Create(0, height), range =>
            {
                long bandFixed = FixBand(pixels, width, range.Item1, range.Item2, sentinels);
                Interlocked.Add(ref totalFixed, bandFixed);
            });
        }

        return new FixResult
        {
            Applied = anyFix,
            PixelsFixed = totalFixed,
            Thresholds = thresholds,
            Counts1006 = new[] { counts[0,0], counts[1,0], counts[2,0], counts[3,0] },
            Counts1007 = new[] { counts[0,1], counts[1,1], counts[2,1], counts[3,1] },
            Counts1008 = new[] { counts[0,2], counts[1,2], counts[2,2], counts[3,2] }
        };
    }

    /// <summary>
    /// Pass 1 over one row band: branchless per-pixel histogram into a flat
    /// local[quadrant*3 + {0,1,2}] (the equality tests lower to setcc/cmov, removing
    /// the data-dependent branch the range check used to take).
    /// </summary>
    private static unsafe long[] CountBand(ushort[] pixels, int width, int fromRow, int toRow,
        int b0, int b1, int b2)
    {
        var local = new long[12];
        fixed (ushort* p = pixels)
        {
            for (int row = fromRow; row < toRow; row++)
            {
                ushort* rowPtr = p + (long)row * width;
                int qRow = (row & 1) << 1;
                for (int col = 0; col < width; col++)
                {
                    int v = rowPtr[col];
                    int qb = (qRow | (col & 1)) * 3;
                    local[qb] += v == b0 ? 1 : 0;
                    local[qb + 1] += v == b1 ? 1 : 0;
                    local[qb + 2] += v == b2 ? 1 : 0;
                }
            }
        }
        return local;
    }

    /// <summary>
    /// Pass 2 over one row band (bands are disjoint, so in-place writes are safe):
    /// branchless conditional increment — add is 1 exactly when v ≤ sentinel.
    /// </summary>
    private static unsafe long FixBand(ushort[] pixels, int width, int fromRow, int toRow,
        int[] sentinels)
    {
        long bandFixed = 0;
        fixed (ushort* p = pixels)
        {
            for (int row = fromRow; row < toRow; row++)
            {
                ushort* rowPtr = p + (long)row * width;
                int qRow = (row & 1) << 1;
                int tvEven = sentinels[qRow];
                int tvOdd = sentinels[qRow | 1];
                for (int col = 0; col < width; col++)
                {
                    int tv = (col & 1) == 0 ? tvEven : tvOdd;
                    int add = rowPtr[col] <= tv ? 1 : 0;
                    rowPtr[col] += (ushort)add;
                    bandFixed += add;
                }
            }
        }
        return bandFixed;
    }
}
