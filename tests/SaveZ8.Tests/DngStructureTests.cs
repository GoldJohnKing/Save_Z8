using SaveZ8.Core;
using Xunit;
using static SaveZ8.Tests.TestTiff;

namespace SaveZ8.Tests;

/// <summary>
/// Structural validation of DNG output: TIFF/DNG spec compliance and EXIF preservation.
/// Reference: JO.html convert_to_dng (verified working in Photoshop/ACR).
/// </summary>
public class DngStructureTests : TempDirTestBase
{
    public DngStructureTests() : base("savez8-dng-tests")
    {
    }

    // ---------- Test 1: core DNG structure (the Photoshop-fatal defects) ----------

    [Fact]
    public void Write_CoreStructure_IsValidDng()
    {
        var path = Path.Combine(_tmpDir, "core.dng");
        const int W = 8, H = 8;
        var pixels = new ushort[W * H];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(1000 + i);

        // LibRaw RGGB indices: 0=R, 1=G1, 3=G2, 2=B
        DngWriter.Write(path, pixels, W, H, new byte[] { 0, 1, 3, 2 }, null);

        var file = File.ReadAllBytes(path);
        Assert.Equal(0x49, file[0]); Assert.Equal(0x49, file[1]);       // II
        Assert.Equal(42, U16(file, 2));                                  // TIFF magic
        var ifd0 = ParseIfd(file, U32(file, 4));

        // FATAL fix: StripOffsets must exist and point at pixel data
        Assert.True(ifd0.Tags.ContainsKey(273), "StripOffsets (273) missing — image data unreachable");
        var (t273, _, v273) = ifd0.Tags[273];
        Assert.Equal(4u, t273);
        uint stripOff = U32(v273, 0);

        // FATAL fix: StripByteCounts must exist and match pixel byte count
        Assert.True(ifd0.Tags.ContainsKey(279), "StripByteCounts (279) missing");
        Assert.Equal((uint)(W * H * 2), U32(ifd0.Tags[279].Value, 0));

        // Pixel data actually at StripOffsets: pixel[0]=1000 << 2 = 4000 = 0x0FA0 → A0 0F (LE)
        Assert.Equal(4000, U16(file, (int)stripOff));
        Assert.Equal(4004, U16(file, (int)stripOff + 2)); // pixel[1]=1001<<2

        // CFA pattern: LibRaw [0,1,3,2] → DNG [0,1,1,2] (G2→Green)
        Assert.True(ifd0.Tags.ContainsKey(33422), "CFAPattern missing");
        Assert.Equal(new byte[] { 0, 1, 1, 2 }, ifd0.Tags[33422].Value);

        // CFARepeatPatternDim [2,2]
        Assert.Equal(new ushort[] { 2, 2 },
            new ushort[] { U16(ifd0.Tags[33421].Value, 0), U16(ifd0.Tags[33421].Value, 2) });

        // BlackLevel: count must be 4 (matches 2x2 BlackLevelRepeatDim), all = 4032
        var (t514, c514, v514) = ifd0.Tags[50714];
        Assert.Equal(4u, c514);
        for (int i = 0; i < 4; i++) Assert.Equal(4032, U16(v514, i * 2));

        // DNGVersion stays 1.6 per user decision (fallback to 1.4 only if real-world fails)
        Assert.Equal(new byte[] { 1, 6, 0, 0 }, ifd0.Tags[50706].Value);

        // WhiteLevel
        Assert.Equal(63568u, U32(ifd0.Tags[50717].Value, 0));

        // Essential baseline tags
        Assert.Equal(32803, U16(ifd0.Tags[262].Value, 0)); // PhotometricInterpretation = CFA
        Assert.Equal(16, U16(ifd0.Tags[258].Value, 0));    // BitsPerSample
        Assert.Equal(1, U16(ifd0.Tags[259].Value, 0));     // Compression = none
        Assert.Equal(1, U16(ifd0.Tags[277].Value, 0));     // SamplesPerPixel
        Assert.Equal((uint)H, U32(ifd0.Tags[278].Value, 0)); // RowsPerStrip
    }

    // ---------- Test 2: EXIF / GPS / Interop preservation ----------

    [Fact]
    public void Write_PreservesExifGpsInterop()
    {
        var src = BuildSyntheticNef();
        var metadata = TiffIfdExtractor.Extract(src);

        var path = Path.Combine(_tmpDir, "exif.dng");
        var pixels = new ushort[64];
        DngWriter.Write(path, pixels, 8, 8, new byte[] { 0, 1, 3, 2 }, metadata);

        var file = File.ReadAllBytes(path);
        var ifd0 = ParseIfd(file, U32(file, 4));

        // IFD0 → ExifIFD
        Assert.True(ifd0.Tags.ContainsKey(0x8769), "ExifIFD pointer missing in IFD0");
        var exif = ParseIfd(file, U32(ifd0.Tags[0x8769].Value, 0));

        // EXIF values survive relocation with byte-exact content
        Assert.Equal(1u, exif.Tags[0x829A].Count);                        // ExposureTime RATIONAL
        Assert.Equal(1u, U32(exif.Tags[0x829A].Value, 0));                // numerator
        Assert.Equal(1000u, U32(exif.Tags[0x829A].Value, 4));             // denominator
        Assert.Equal(64, U16(exif.Tags[0x8827].Value, 0));                // ISO
        Assert.Equal("2026:10:09 10:00:00", Ascii(exif.Tags[0x9003].Value)); // DateTimeOriginal

        // EXIF → InteropIFD (nested pointer relocation)
        Assert.True(exif.Tags.ContainsKey(0xA005), "InteropIFD pointer missing in EXIF");
        var interop = ParseIfd(file, U32(exif.Tags[0xA005].Value, 0));
        Assert.Equal("R98", Ascii(interop.Tags[0x0001].Value));

        // IFD0 → GPS IFD
        Assert.True(ifd0.Tags.ContainsKey(0x8825), "GPS IFD pointer missing in IFD0");
        var gps = ParseIfd(file, U32(ifd0.Tags[0x8825].Value, 0));
        Assert.Equal(new byte[] { 2, 3, 0, 0 }, gps.Tags[0x0000].Value);  // GPSVersionID
        Assert.Equal("N", Ascii(gps.Tags[0x0001].Value));                 // GPSLatitudeRef

        // Safe IFD0 scalar tags copied (DateTime)
        Assert.True(ifd0.Tags.ContainsKey(306), "IFD0 DateTime not preserved");
        Assert.Equal("2026:01:01 00:00:00", Ascii(ifd0.Tags[306].Value));
    }

    // ---------- synthetic source: minimal NEF-like TIFF with EXIF + Interop + GPS ----------

    private static byte[] BuildSyntheticNef()
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x49, 0x49 }); bw.Write((ushort)42); bw.Write((uint)8);

        // layout
        int ifd0Size = 2 + 4 * 12 + 4;
        int cursor = 8 + ifd0Size;
        int makeOff = cursor; cursor += 8;                       // "TestCam\0"
        int dtOff = cursor; cursor += 20;                        // "2026:01:01 00:00:00\0"
        int exifOff = cursor; cursor += 2 + 4 * 12 + 4;
        int expTimeOff = cursor; cursor += 8;                    // RATIONAL 1/1000
        int dtoOff = cursor; cursor += 20;                       // "2026:10:09 10:00:00\0"
        int interopOff = cursor; cursor += 2 + 1 * 12 + 4;
        int gpsOff = cursor; cursor += 2 + 2 * 12 + 4;

        // IFD0
        ms.Position = 8; bw.Write((ushort)4);
        Entry(bw, 0x010F, 2, 8, (uint)makeOff);
        Entry(bw, 0x0132, 2, 20, (uint)dtOff);
        Entry(bw, 0x8769, 4, 1, (uint)exifOff);
        Entry(bw, 0x8825, 4, 1, (uint)gpsOff);
        bw.Write((uint)0);

        WriteMakeDateTime(ms, bw, makeOff, dtOff);
        WriteExifInteropGps(ms, bw, exifOff, expTimeOff, dtoOff, interopOff, gpsOff);

        var buf = new byte[gpsOff + 2 + 2 * 12 + 4];
        Array.Copy(ms.ToArray(), buf, buf.Length);
        return buf;
    }
}
