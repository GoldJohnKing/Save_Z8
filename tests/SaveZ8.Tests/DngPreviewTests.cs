using System.Text;
using SaveZ8.Core;
using Xunit;

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

    private sealed class ParsedIfd
    {
        public Dictionary<ushort, (ushort Type, uint Count, byte[] Value)> Tags = new();
        public uint NextIfd;
    }

    private static ushort U16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));
    private static uint U32(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

    private static ParsedIfd ParseIfd(byte[] file, uint offset)
    {
        var ifd = new ParsedIfd();
        int count = U16(file, (int)offset);
        for (int i = 0; i < count; i++)
        {
            long p = offset + 2 + i * 12L;
            ushort tag = U16(file, (int)p);
            ushort type = U16(file, (int)p + 2);
            uint cnt = U32(file, (int)p + 4);
            uint val = U32(file, (int)p + 8);
            int typeSize = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 1 };
            int size = typeSize * (int)cnt;
            var data = new byte[Math.Max(size, 0)];
            if (size <= 4)
                for (int j = 0; j < size; j++) data[j] = (byte)(val >> (8 * j));
            else
                Array.Copy(file, val, data, 0, size);
            ifd.Tags[tag] = (type, cnt, data);
        }
        ifd.NextIfd = U32(file, (int)(offset + 2 + count * 12L));
        return ifd;
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
        DngWriter.Write(path, pixels, 16, 12, new byte[] { 0, 1, 3, 2 }, metadata);

        var file = File.ReadAllBytes(path);
        var ifd0 = ParseIfd(file, U32(file, 4));

        // Orientation preserved from source IFD0
        Assert.True(ifd0.Tags.ContainsKey(274), "Orientation not preserved");
        Assert.Equal(6, U16(ifd0.Tags[274].Value, 0));

        // SubIFDs pointer array → preview IFD with the large JPEG
        Assert.True(ifd0.Tags.ContainsKey(330), "SubIFDs tag missing in IFD0");
        var (t330, c330, v330) = ifd0.Tags[330];
        Assert.Equal(1u, c330);
        uint subIfdOff = U32(v330, 0);
        Assert.True(subIfdOff > 0 && subIfdOff < file.Length, "SubIFD offset out of range");
        var prevIfd = ParseIfd(file, subIfdOff);
        Assert.Equal(1u, U32(prevIfd.Tags[254].Value, 0));              // NewSubfileType=1
        Assert.Equal(6, U16(prevIfd.Tags[259].Value, 0));               // Compression=JPEG
        Assert.Equal(2, U16(prevIfd.Tags[262].Value, 0));               // Photometric=RGB
        Assert.Equal(3, U16(prevIfd.Tags[277].Value, 0));              // SamplesPerPixel=3
        Assert.Equal(12u, U32(prevIfd.Tags[256].Value, 0));              // width = preview's own dims
        Assert.Equal(10u, U32(prevIfd.Tags[257].Value, 0));
        uint jOff = U32(prevIfd.Tags[513].Value, 0), jLen = U32(prevIfd.Tags[514].Value, 0);
        Assert.Equal((uint)big.Length, jLen);
        Assert.Equal(big, file[(int)jOff..(int)(jOff + jLen)]);         // JPEG bytes intact

        // IFD1 chain → thumbnail JPEG (WIC/Explorer path)
        Assert.True(ifd0.NextIfd != 0, "IFD1 (thumbnail chain) missing");
        var ifd1 = ParseIfd(file, ifd0.NextIfd);
        Assert.Equal(1u, U32(ifd1.Tags[254].Value, 0));
        Assert.Equal(6, U16(ifd1.Tags[259].Value, 0));
        uint tOff = U32(ifd1.Tags[513].Value, 0), tLen = U32(ifd1.Tags[514].Value, 0);
        Assert.Equal((uint)thumb.Length, tLen);
        Assert.Equal(thumb, file[(int)tOff..(int)(tOff + tLen)]);
        Assert.Equal(0u, ifd1.NextIfd);                                 // chain terminates

        // ActiveArea: full stored area, replicating Adobe DNG Converter geometry
        // (warp/vignette radius normalization base). No DefaultCrop.
        Assert.True(ifd0.Tags.ContainsKey(50829), "ActiveArea missing");
        Assert.Equal(0u, U32(ifd0.Tags[50829].Value, 0));
        Assert.Equal(0u, U32(ifd0.Tags[50829].Value, 4));
        Assert.Equal(12u, U32(ifd0.Tags[50829].Value, 8));   // bottom = height
        Assert.Equal(16u, U32(ifd0.Tags[50829].Value, 12));  // right = width
        Assert.False(ifd0.Tags.ContainsKey(50820), "DefaultCropSize should be absent");

        // Raw strip untouched by previews
        Assert.Equal((uint)(16 * 12 * 2), U32(ifd0.Tags[279].Value, 0));
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

        void Entry(ushort tag, ushort type, uint count, uint val)
        {
            bw.Write(tag); bw.Write(type); bw.Write(count); bw.Write(val);
        }

        // IFD0
        ms.Position = 8; bw.Write((ushort)ifd0Entries);
        Entry(0x010F, 2, 8, (uint)makeOff);
        Entry(274, 3, 1, orientation);                 // Orientation inline SHORT
        Entry(0x0132, 2, 20, (uint)dtOff);
        Entry(330, 4, 1, (uint)prevIfdOff);            // SubIFDs → preview IFD
        Entry(513, 4, 1, (uint)thumbOff);              // IFD0 thumbnail
        Entry(514, 4, 1, (uint)thumb.Length);
        Entry(0x8769, 4, 1, (uint)exifOff);
        Entry(0x8825, 4, 1, (uint)gpsOff);
        bw.Write((uint)0);

        ms.Position = makeOff; bw.Write(Encoding.ASCII.GetBytes("TestCam\0"));
        ms.Position = dtOff; bw.Write(Encoding.ASCII.GetBytes("2026:01:01 00:00:00\0"));
        ms.Position = thumbOff; bw.Write(thumb);

        // preview SubIFD
        ms.Position = prevIfdOff; bw.Write((ushort)2);
        Entry(513, 4, 1, (uint)bigOff);
        Entry(514, 4, 1, (uint)big.Length);
        bw.Write((uint)0);
        ms.Position = bigOff; bw.Write(big);

        // EXIF IFD
        ms.Position = exifOff; bw.Write((ushort)4);
        Entry(0x829A, 5, 1, (uint)expTimeOff);
        Entry(0x8827, 3, 1, 64u);
        Entry(0x9003, 2, 20, (uint)dtoOff);
        Entry(0xA005, 4, 1, (uint)interopOff);
        bw.Write((uint)0);
        ms.Position = expTimeOff; bw.Write(1u); bw.Write(1000u);
        ms.Position = dtoOff; bw.Write(Encoding.ASCII.GetBytes("2026:10:09 10:00:00\0"));

        // Interop IFD
        ms.Position = interopOff; bw.Write((ushort)1);
        Entry(0x0001, 2, 4, 0x00383952u); // "R98\0"
        bw.Write((uint)0);

        // GPS IFD
        ms.Position = gpsOff; bw.Write((ushort)2);
        Entry(0x0000, 1, 4, 0x00000302u);
        Entry(0x0001, 2, 2, 0x004Eu);
        bw.Write((uint)0);

        var buf = new byte[gpsOff + 2 + 2 * 12 + 4];
        Array.Copy(ms.ToArray(), buf, buf.Length);
        return buf;
    }
}
