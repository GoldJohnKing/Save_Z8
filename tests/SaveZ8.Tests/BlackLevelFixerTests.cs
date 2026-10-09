using Xunit;
using SaveZ8.Core;

namespace SaveZ8.Tests;

public class BlackLevelFixerTests
{
    private static ushort[] MakeGaussian(int width, int height, double center, double sigma, int seed = 42)
    {
        var rng = new Random(seed);
        var pixels = new ushort[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            // Box-Muller
            double u1 = rng.NextDouble();
            double u2 = rng.NextDouble();
            double z = Math.Sqrt(-2 * Math.Log(u1 + 1e-10)) * Math.Cos(2 * Math.PI * u2);
            pixels[i] = (ushort)Math.Max(0, Math.Min(65535, Math.Round(center + sigma * z)));
        }
        return pixels;
    }

    private static void ApplyDefect(ushort[] pixels, int width, int height, int blackLevel, int[] quadrants)
    {
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int q = ((row & 1) << 1) | (col & 1);
                if (quadrants.Contains(q))
                {
                    int idx = row * width + col;
                    if (pixels[idx] <= blackLevel)
                        pixels[idx]--;
                }
            }
        }
    }

    [Fact]
    public void Notch_DetectsAndFixes_DefectedFrame()
    {
        // Simulate a healthy ISO 64 frame with center 1008.3
        var pixels = MakeGaussian(200, 200, 1008.3, 2.0);
        // Apply the defect (−1 for x ≤ 1008) in quadrants 0-2
        ApplyDefect(pixels, 200, 200, 1008, new[] { 0, 1, 2 });

        var result = BlackLevelFixer.Fix(pixels, 200, 200, 1008);

        Assert.True(result.Applied);
        Assert.True(result.PixelsFixed > 0);
        // After fix, value 1008 should no longer be completely empty
        int c1008 = pixels.Count(p => p == 1008);
        Assert.True(c1008 > 1000, $"1008 count after fix: {c1008} (should be >1000)");
    }

    [Fact]
    public void Notch_DoesNotTrigger_OnHealthyISO100()
    {
        // Simulate a healthy ISO 100 frame with center 1007.2 (naturally below 1007.5)
        var pixels = MakeGaussian(200, 200, 1007.2, 2.5, seed: 99);

        var result = BlackLevelFixer.Fix(pixels, 200, 200, 1008);

        Assert.False(result.Applied);
        Assert.Equal(0, result.PixelsFixed);
    }

    [Fact]
    public void Fix_InvertsDefect_Perfectly()
    {
        // Create a defect frame, fix it, verify histogram becomes smooth
        var pixels = MakeGaussian(400, 400, 1008.3, 2.0, seed: 7);
        ApplyDefect(pixels, 400, 400, 1008, new[] { 0, 1, 2, 3 });

        // After defect, 1008 should be depleted
        int before1008 = pixels.Count(p => p == 1008);
        Assert.True(before1008 < 5000, $"1008 after defect: {before1008} (should be low)");

        var result = BlackLevelFixer.Fix(pixels, 400, 400, 1008);
        Assert.True(result.Applied);

        // After fix, 1008 should be restored as the peak
        int after1008 = pixels.Count(p => p == 1008);
        int after1007 = pixels.Count(p => p == 1007);
        int after1009 = pixels.Count(p => p == 1009);
        Assert.True(after1008 > after1007, $"1008({after1008}) should > 1007({after1007})");
        Assert.True(after1008 > after1009, $"1008({after1008}) should > 1009({after1009})");
    }
}
