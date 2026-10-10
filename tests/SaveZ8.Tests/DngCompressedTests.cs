using System.IO;
using System.Linq;
using SaveZ8.Core;
using Xunit;
using static SaveZ8.Tests.TestTiff;

namespace SaveZ8.Tests;

/// <summary>
/// Compressed-output path (Compression=7 lossless-JPEG tiles) — independent of the
/// uncompressed structure tests. Verifies both the container layout (tile tags,
/// per-tile SOF3 streams) and pixel round-trip via LibRaw's own DNG decoder.
/// Requires the native DLLs (libs/*.dll) copied next to the test assembly.
/// </summary>
public class DngCompressedTests : TempDirTestBase
{
    public DngCompressedTests() : base("dngcomp") { }
    private static (ushort[] pixels, byte[] cfa) SyntheticFrame(int width, int height, int seed = 7)
    {
        var rng = new Random(seed);
        var pixels = new ushort[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                // Smooth gradient + noise: compressible, like real black-region data.
                int v = 990 + (x * 3 + y * 5) % 40 + rng.Next(0, 8);
                pixels[y * width + x] = (ushort)v;
            }
        return (pixels, new byte[] { 0, 1, 1, 2 }); // RGGB (LibRaw indices)
    }

    private string WriteDng(int width, int height, string name, bool compress, int seed = 7)
    {
        var (pixels, cfa) = SyntheticFrame(width, height, seed);
        string path = Path.Combine(_tmpDir, name);
        DngWriter.Write(path, pixels, width, height, cfa, originalMetadata: null,
            cameraWb: new float[] { 1.8f, 1f, 1.8f, 1f }, compress: compress);
        return path;
    }

    [Fact]
    public void TileTags_PresentAndConsistent()
    {
        // 300x200 → 2 tiles across, 1 tile row (edge tile padded)
        string path = WriteDng(300, 200, "t.dng", compress: true);
        var data = File.ReadAllBytes(path);
        var ifd0 = ParseIfd(data, 8);

        // Without previews IFD0 IS the raw IFD (see DngWriter: no SubIFDs then).
        ParsedIfd raw = ifd0.Tags.TryGetValue(0x014A, out var subifds)
            ? ParseIfd(data, U32(subifds.Value, 0))
            : ifd0;

        Assert.True(raw.Tags.TryGetValue(0x0103, out var comp), "Compression tag missing");
        Assert.Equal(7u, U16(comp.Value, 0));
        Assert.True(raw.Tags.TryGetValue(322, out var tw), "TileWidth missing");
        Assert.Equal(256u, U16(tw.Value, 0));
        Assert.True(raw.Tags.TryGetValue(323, out var tl), "TileLength missing");
        Assert.Equal(256u, U16(tl.Value, 0));
        Assert.False(raw.Tags.ContainsKey(273), "strip offsets must be absent in tiled mode");
        Assert.False(raw.Tags.ContainsKey(278), "rows-per-strip must be absent in tiled mode");
        Assert.False(raw.Tags.ContainsKey(279), "strip byte counts must be absent in tiled mode");

        int nTiles = 2 * 1;
        var offs = raw.Tags[324];
        var cnts = raw.Tags[325];
        Assert.Equal((uint)nTiles, offs.Count);
        Assert.Equal((uint)nTiles, cnts.Count);

        // Every tile: even start, independent SOI..SOF3..EOI JPEG stream
        for (int i = 0; i < nTiles; i++)
        {
            uint off = U32(offs.Value, 4 * i);
            uint len = U32(cnts.Value, 4 * i);
            Assert.Equal(0u, off % 2);
            Assert.Equal(0xFF, data[off]);
            Assert.Equal(0xD8, data[off + 1]);
            Assert.Equal(0xFF, data[off + 2]);
            Assert.Equal(0xC3, data[off + 3]); // SOF3 = lossless
            Assert.Equal(0xFF, data[off + len - 2]);
            Assert.Equal(0xD9, data[off + len - 1]);
        }

        // Sequential packing, 2-byte alignment
        for (int i = 0; i + 1 < nTiles; i++)
        {
            uint next = U32(offs.Value, 4 * i) + U32(cnts.Value, 4 * i);
            if ((next & 1) == 1) next++;
            Assert.Equal(next, U32(offs.Value, 4 * (i + 1)));
        }
    }

    [Fact]
    public void PixelRoundTrip_ThroughLibRaw_IsExact()
    {
        // Full-tile geometry (256x256) plus an odd edge frame — both must decode
        // back to the exact source samples (x<<2 domain) via LibRaw's DNG reader.
        // NOTE: widths equal to 256/512 hit LibRaw's deinterleave heuristic (clrs*jwide ==
        // raw_width) and misdecode in LibRaw itself — real sensor widths never do.
        foreach ((int w, int h) in new[] { (257, 260), (300, 130) })
        {
            var (pixels, cfa) = SyntheticFrame(w, h);
            string path = Path.Combine(_tmpDir, $"rt_{w}x{h}.dng");
            DngWriter.Write(path, pixels, w, h, cfa, originalMetadata: null,
                cameraWb: new float[] { 1.8f, 1f, 1.8f, 1f }, compress: true);

            var (decoded, dw, dh, _, _, _, _) = Core.Native.LibRawNative.Decode(path);
            Assert.Equal(w, dw);
            Assert.Equal(h, dh);
            for (int i = 0; i < pixels.Length; i++)
                Assert.Equal((ushort)System.Math.Min(pixels[i] << 2, 65535), decoded[i]);
        }
    }

    [Fact]
    public void UncompressedAndCompressed_DecodeIdentically()
    {
        string unc = WriteDng(300, 130, "u.dng", compress: false, seed: 42);
        string comp = WriteDng(300, 130, "c.dng", compress: true, seed: 42);

        var (a, _, _, _, _, _, _) = Core.Native.LibRawNative.Decode(unc);
        var (b, _, _, _, _, _, _) = Core.Native.LibRawNative.Decode(comp);
        Assert.True(a.SequenceEqual(b));
        Assert.True(new FileInfo(comp).Length < new FileInfo(unc).Length);
    }
}
