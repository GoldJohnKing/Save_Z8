using SaveZ8.Core;
using Xunit;

namespace SaveZ8.Tests;

/// <summary>
/// Nikon NEFInfo (0xC7D5) → DNG WarpRectilinear conversion, calibrated against
/// Adobe DNG Converter 18.6 output (DSC_7592.NEF, NIKKOR Z 28-400mm @ 28mm).
/// </summary>
public class NikonLensCorrectionTests
{
    /// <summary>Builds a NEFInfo blob with the given block5/block7 int32 payloads.</summary>
    private static byte[] BuildNefInfo(int[] block5, int[] block7)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { (byte)'N', (byte)'i', (byte)'k', (byte)'o', (byte)'n', 0, 1, 3, 0, 0 });
        bw.Write(new byte[] { 0x49, 0x49, 42, 0 }); // II*\0
        bw.Write(8u);                                // IFD offset (rel. to TIFF base)

        int ifdSize = 2 + 2 * 12 + 4;
        // entry values are TIFF-relative (header at position 10); data starts after the IFD
        int b5 = 8 + ifdSize;   // absolute 18+ifdSize minus TIFF base 10
        int b7 = b5 + block5.Length * 4;

        bw.Write((ushort)2);
        void Entry(ushort tag, uint count, uint val)
        {
            bw.Write(tag); bw.Write((ushort)7); bw.Write(count); bw.Write(val);
        }
        Entry(5, (uint)(block5.Length * 4), (uint)b5);
        Entry(7, (uint)(block7.Length * 4), (uint)b7);
        bw.Write(0u);
        foreach (var v in block5) bw.Write(v);
        foreach (var v in block7) bw.Write(v);
        return ms.ToArray();
    }

    // DSC_7592.NEF block payloads (i32)
    private static readonly int[] B5_7592 =
    {
        808464688, 259, 88474, 4096, 4, 27361, 1048576, -12087, 1048576, -94746, 1048576,
        0, 1, 0, 1, -28655, 1048576, 0, 0, 0, 0,
    };
    private static readonly int[] B7_7592 =
    {
        808464688, 513, 88474, 4096, 3, -372, 1048576, 250, 1048576, 0, 1, 360, 1048576, 3,
        942, 1048576, -792, 1048576, 0, 1, 345, 1048576, 65536, 0, 0, 0,
    };

    // ADC 18.6 actual output for the same file
    private static readonly double[][] AdcPlanes =
    {
        new[] {  1.02858204, -0.09747418,  0.01649753,  0.00248783 }, // R
        new[] {  1.02822347, -0.09754287,  0.01678212,  0.00239010 }, // G
        new[] {  1.02854768, -0.09799926,  0.01756935,  0.00213392 }, // B
    };

    [Fact]
    public void Decode_MatchesAdobeDngConverter()
    {
        var w = NikonLensCorrection.Decode(BuildNefInfo(B5_7592, B7_7592));
        Assert.NotNull(w);
        Assert.Equal(3, w.Planes.Length);

        // distortion (G) exact to ~1e-6
        for (int c = 0; c < 4; c++)
            Assert.True(Math.Abs(w.Planes[1][c] - AdcPlanes[1][c]) < 2e-6,
        $"G[{c}]: {w.Planes[1][c]} vs ADC {AdcPlanes[1][c]}");

        // CA planes (R/B) first-order: within 5e-6 of ADC on the calibration file
        for (int p = 0; p < 3; p += 2)
            for (int c = 0; c < 4; c++)
                Assert.True(Math.Abs(w.Planes[p][c] - AdcPlanes[p][c]) < 5e-6,
        $"plane{p}[{c}]: {w.Planes[p][c]} vs ADC {AdcPlanes[p][c]}");
    }

    [Fact]
    public void Decode_HandlesMissingBlock7()
    {
        // no block7 → R==B==G (distortion only)
        var b7 = (int[])B7_7592.Clone();
        var w = NikonLensCorrection.Decode(BuildNefInfo(B5_7592, b7));
        Assert.NotNull(w);
        var w2 = NikonLensCorrection.Decode(BuildNefInfo(B5_7592, new int[B7_7592.Length]));
        Assert.NotNull(w2);
        Assert.Equal(w2.Planes[0], w2.Planes[1]);
        Assert.Equal(w2.Planes[2], w2.Planes[1]);
    }

    [Fact]
    public void Decode_RejectsGarbage()
    {
        Assert.Null(NikonLensCorrection.Decode(null));
        Assert.Null(NikonLensCorrection.Decode(new byte[10]));
        Assert.Null(NikonLensCorrection.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
    }

    [Fact]
    public void Serialize_BigEndianOpcodeLayout()
    {
        var w = NikonLensCorrection.Decode(BuildNefInfo(B5_7592, B7_7592));
        Assert.NotNull(w);
        var blob = NikonLensCorrection.SerializeOpcodeList3(w!);

        // 4 (count) + 16 (header) + 4 + 3*48 + 16 (params)
        Assert.Equal(184, blob.Length);
        Assert.Equal(0, blob[0]); Assert.Equal(0, blob[1]); Assert.Equal(0, blob[2]); Assert.Equal(1, blob[3]);   // 1 opcode
        Assert.Equal(0, blob[4]); Assert.Equal(0, blob[5]); Assert.Equal(0, blob[6]); Assert.Equal(1, blob[7]);   // id=1 WarpRectilinear
        Assert.Equal(1, blob[8]); Assert.Equal(3, blob[9]);                                                        // minVersion 1.3.0.0
        Assert.Equal(0, blob[12]); Assert.Equal(0, blob[13]); Assert.Equal(0, blob[14]); Assert.Equal(0, blob[15]); // flags=0
        Assert.Equal(0, blob[16]); Assert.Equal(0xA4, blob[19]);                                                   // paramLen=164
        Assert.Equal(0, blob[20]); Assert.Equal(0, blob[21]); Assert.Equal(0, blob[22]); Assert.Equal(3, blob[23]); // planes=3 (BE)

        // first plane offset double (BE): exponent bytes for ~1.0285 → 0x3FF0...
        Assert.Equal(0x3F, blob[24]);
        Assert.Equal(0xF0, blob[25]);

        // center (0.5, 0.5) at tail: 0x3FE0000000000000 twice
        int tail = 184 - 16;
        Assert.Equal(0x3F, blob[tail]); Assert.Equal(0xE0, blob[tail + 1]);
        Assert.Equal(0x3F, blob[tail + 8]); Assert.Equal(0xE0, blob[tail + 9]);
        for (int i = 2; i < 8; i++) { Assert.Equal(0, blob[tail + i]); Assert.Equal(0, blob[tail + 8 + i]); }
    }
}
