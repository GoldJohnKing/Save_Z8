using SaveZ8.Core;
using Xunit;
using static SaveZ8.Tests.TestTiff;

namespace SaveZ8.Tests;

/// <summary>
/// Preview embedding (SubIFD previews + IFD1 thumbnail), DefaultCrop derivation,
/// and safe-IFD0-tag whitelist (Orientation) validation.
/// </summary>
public class DngPreviewTests : IDisposable
{
    private readonly string _tmpDir;

    public DngPreviewTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "savez8-preview-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    /// <summary>Minimal JPEG with SOF0 (w×h) plus an optional COM segment for padding.</summary>
    private static byte[] MiniJpeg(int w, int h, int pad)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xD8 });
        ms.Write(new byte[] { 0xFF, 0xC0, 0x00, 0x0B, 0x08,
            (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x01, 0x01, 0x22, 0x00 });
        if (pad > 0)
        {
            ms.Write(new byte[] { 0xFF, 0xFE, (byte)((pad + 2) >> 8), (byte)(pad + 2) });
            ms.Write(new byte[pad]);
        }
        ms.Write(new byte[] { 0xFF, 0xD9 });
        return ms.ToArray();
    }

    [Fact]
    public void Write_EmbedsPreviewsCropAndOrientation()
    {
        // source: IFD0 thumbnail (8×6, 17B) + SubIFD preview (12×10, 45B), Orientation=6
        var thumb = MiniJpeg(8, 6, pad: 0);
        var big = MiniJpeg(12, 10, pad: 24);
        Assert.Equal(17, thumb.Length);
        Assert.True(big.Length > thumb.Length);

        var src = BuildSyntheticNef(thumb, big, orientation: 6);
        var metadata = TiffIfdExtractor.Extract(src);
        Assert.Equal(2, metadata.Previews.Count);
        Assert.Equal(8, metadata.Previews[0].Width);    // smallest first
        Assert.Equal(12, metadata.Previews[^1].Width);

        var path = Path.Combine(_tmpDir, "preview.dng");
        var pixels = new ushort[16 * 12];
        pixels[0] = 1000; pixels[1] = 1001;   // spot-checkable values
        DngWriter.Write(path, pixels, 16, 12, new byte[] { 0, 1, 3, 2 }, metadata);

        var file = File.ReadAllBytes(path);
        var ifd0 = ParseIfd(file, U32(file, 4));

        // NEW topology (ADC parity): IFD0 = thumbnail, raw + previews in SubIFDs
        Assert.Equal(1u, U32(ifd0.Tags[254].Value, 0));                   // IFD0 NST=1 (thumbnail)
        Assert.Equal(8u, U32(ifd0.Tags[256].Value, 0));                    // thumb dims
        Assert.Equal(6u, U32(ifd0.Tags[257].Value, 0));
        Assert.Equal(6, U16(ifd0.Tags[259].Value, 0));                    // JPEG compression
        uint tOff = U32(ifd0.Tags[513].Value, 0), tLen = U32(ifd0.Tags[514].Value, 0);
        Assert.Equal((uint)thumb.Length, tLen);
        Assert.Equal(thumb, file[(int)tOff..(int)(tOff + tLen)]);        // thumbnail bytes intact
        Assert.Equal(0u, ifd0.NextIfd);                                   // no IFD1 chain

        // Orientation preserved from source IFD0
        Assert.True(ifd0.Tags.ContainsKey(274), "Orientation not preserved");
        Assert.Equal(6, U16(ifd0.Tags[274].Value, 0));

        // SubIFDs pointer array → [raw, preview]
        Assert.True(ifd0.Tags.ContainsKey(330), "SubIFDs tag missing in IFD0");
        var (t330, c330, v330) = ifd0.Tags[330];
        Assert.Equal(2u, c330);
        uint rawOff = U32(v330, 0);                        // array[0] = raw IFD
        uint prevOff = U32(v330, 4);                       // array[1] = preview IFD

        // raw SubIFD
        var rawIfd = ParseIfd(file, rawOff);
        Assert.Equal(0u, U32(rawIfd.Tags[254].Value, 0));                 // NST=0 (raw)
        Assert.Equal(16u, U32(rawIfd.Tags[256].Value, 0));
        Assert.Equal(12u, U32(rawIfd.Tags[257].Value, 0));
        Assert.Equal(32803, U16(rawIfd.Tags[262].Value, 0));              // CFA photometric
        Assert.Equal(new byte[] { 0, 1, 1, 2 }, rawIfd.Tags[33422].Value);
        Assert.Equal((uint)(16 * 12 * 2), U32(rawIfd.Tags[279].Value, 0));
        uint px = U32(rawIfd.Tags[273].Value, 0);
        Assert.Equal(4000, U16(file, (int)px));                           // pixel[0]<<2

        // ActiveArea: full stored area (warp normalization base) + DefaultCrop pair
        // (displayed framing matches NEF) — both in the raw IFD, replicating ADC.
        Assert.True(rawIfd.Tags.ContainsKey(50829), "ActiveArea missing");
        Assert.Equal(0u, U32(rawIfd.Tags[50829].Value, 0));
        Assert.Equal(12u, U32(rawIfd.Tags[50829].Value, 8));              // bottom = height
        Assert.Equal(16u, U32(rawIfd.Tags[50829].Value, 12));             // right = width

        Assert.True(rawIfd.Tags.ContainsKey(50720), "DefaultCropSize missing");
        // RATIONAL×2: (12,1),(10,1) — crop = largest preview dims
        Assert.Equal(12u, U32(rawIfd.Tags[50720].Value, 0));
        Assert.Equal(10u, U32(rawIfd.Tags[50720].Value, 8));
        Assert.True(rawIfd.Tags.ContainsKey(50719), "DefaultCropOrigin missing");
        Assert.Equal(2u, U32(rawIfd.Tags[50719].Value, 0));               // (16-12)/2 = 2
        Assert.Equal(0u, U32(rawIfd.Tags[50719].Value, 8));               // (12-10)/2=1 → &~1 = 0

        // preview SubIFD with the large JPEG
        var prevIfd = ParseIfd(file, prevOff);
        Assert.Equal(1u, U32(prevIfd.Tags[254].Value, 0));
        Assert.Equal(6, U16(prevIfd.Tags[259].Value, 0));
        Assert.Equal(2, U16(prevIfd.Tags[262].Value, 0));
        Assert.Equal(3, U16(prevIfd.Tags[277].Value, 0));
        Assert.Equal(12u, U32(prevIfd.Tags[256].Value, 0));
        Assert.Equal(10u, U32(prevIfd.Tags[257].Value, 0));
        uint jOff = U32(prevIfd.Tags[513].Value, 0), jLen = U32(prevIfd.Tags[514].Value, 0);
        Assert.Equal((uint)big.Length, jLen);
        Assert.Equal(big, file[(int)jOff..(int)(jOff + jLen)]);
    }

    private static byte[] BuildSyntheticNef(byte[] thumb, byte[] big, ushort orientation)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x49, 0x49 }); bw.Write((ushort)42); bw.Write((uint)8);

        const int ifd0Entries = 8; // Make, Orientation, DateTime, SubIFDs, 513, 514, ExifIFD, GPS
        int cursor = 8 + 2 + ifd0Entries * 12 + 4;
        int makeOff = cursor; cursor += 8;
        int dtOff = cursor; cursor += 20;
        int thumbOff = cursor; cursor += thumb.Length;
        int prevIfdOff = cursor; cursor += 2 + 2 * 12 + 4;   // preview IFD: 513/514 only
        int bigOff = cursor; cursor += big.Length;
        int exifOff = cursor; cursor += 2 + 4 * 12 + 4;
        int expTimeOff = cursor; cursor += 8;
        int dtoOff = cursor; cursor += 20;
        int interopOff = cursor; cursor += 2 + 1 * 12 + 4;
        int gpsOff = cursor; cursor += 2 + 2 * 12 + 4;

        // IFD0
        ms.Position = 8; bw.Write((ushort)ifd0Entries);
        Entry(bw, 0x010F, 2, 8, (uint)makeOff);
        Entry(bw, 274, 3, 1, orientation);                 // Orientation inline SHORT
        Entry(bw, 0x0132, 2, 20, (uint)dtOff);
        Entry(bw, 330, 4, 1, (uint)prevIfdOff);            // SubIFDs → preview IFD
        Entry(bw, 513, 4, 1, (uint)thumbOff);              // IFD0 thumbnail
        Entry(bw, 514, 4, 1, (uint)thumb.Length);
        Entry(bw, 0x8769, 4, 1, (uint)exifOff);
        Entry(bw, 0x8825, 4, 1, (uint)gpsOff);
        bw.Write((uint)0);

        WriteMakeDateTime(ms, bw, makeOff, dtOff);
        ms.Position = thumbOff; bw.Write(thumb);

        // preview SubIFD
        ms.Position = prevIfdOff; bw.Write((ushort)2);
        Entry(bw, 513, 4, 1, (uint)bigOff);
        Entry(bw, 514, 4, 1, (uint)big.Length);
        bw.Write((uint)0);
        ms.Position = bigOff; bw.Write(big);

        WriteExifInteropGps(ms, bw, exifOff, expTimeOff, dtoOff, interopOff, gpsOff);

        var buf = new byte[gpsOff + 2 + 2 * 12 + 4];
        Array.Copy(ms.ToArray(), buf, buf.Length);
        return buf;
    }
}
