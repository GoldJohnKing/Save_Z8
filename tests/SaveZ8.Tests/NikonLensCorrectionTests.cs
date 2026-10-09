using SaveZ8.Core;
using Xunit;

namespace SaveZ8.Tests;

/// <summary>
/// Nikon NEFInfo (0xC7D5) → DNG WarpRectilinear conversion, calibrated against
/// Adobe DNG Converter 18.6 output (DSC_7592.NEF, NIKKOR Z 28-400mm @ 28mm).
/// </summary>
public class NikonLensCorrectionTests
{
    /// <summary>Builds a NEFInfo blob with the given block payloads (block6/8 optional).</summary>
    private static byte[] BuildNefInfo(int[] block5, int[] block7, int[]? block6 = null, int[]? block8 = null)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { (byte)'N', (byte)'i', (byte)'k', (byte)'o', (byte)'n', 0, 1, 3, 0, 0 });
        bw.Write(new byte[] { 0x49, 0x49, 42, 0 }); // II*\0
        bw.Write(8u);                                // IFD offset (rel. to TIFF base)

        int nEntries = 2 + (block6 != null ? 1 : 0) + (block8 != null ? 1 : 0);
        int ifdSize = 2 + nEntries * 12 + 4;
        // entry values are TIFF-relative (header at position 10); data starts after the IFD
        int b5 = 8 + ifdSize;
        int b7 = b5 + block5.Length * 4;
        int b6 = block6 != null ? b7 + block7.Length * 4 : 0;
        int b8 = block8 != null ? b6 + block6.Length * 4 : 0;

        bw.Write((ushort)nEntries);
        void Entry(ushort tag, uint count, uint val)
        {
            bw.Write(tag); bw.Write((ushort)7); bw.Write(count); bw.Write(val);
        }
        // entries must be sorted by tag: 5, 6, 7, 8
        Entry(5, (uint)(block5.Length * 4), (uint)b5);
        if (block6 != null) Entry(6, (uint)(block6.Length * 4), (uint)b6);
        Entry(7, (uint)(block7.Length * 4), (uint)b7);
        if (block8 != null) Entry(8, (uint)(block8.Length * 4), (uint)b8);
        bw.Write(0u);
        foreach (var v in block5) bw.Write(v);
        foreach (var v in block7) bw.Write(v);
        if (block6 != null) foreach (var v in block6) bw.Write(v);
        if (block8 != null) foreach (var v in block8) bw.Write(v);
        return ms.ToArray();
    }

    // DSC_7592.NEF block payloads (i32) — tail verbatim from the file
    // (0x2c..0x50: (0,1),(0,1),0, t4=-28655@0x40, 2^20, 0, 0, last-word@0x50)
    private static readonly int[] B5_7592 =
    {
        808464688, 259, 88474, 4096, 4, 27361, 1048576, -12087, 1048576, -94746, 1048576,
        0, 1, 0, 1, 0, -28655, 1048576, 0, 0, 275775488,
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
        var w = NikonLensCorrection.Decode(BuildNefInfo(B5_7592, B7_7592))!.Warp;
        Assert.NotNull(w);
        Assert.Equal(3, w!.Planes.Length);

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
        var w = NikonLensCorrection.Decode(BuildNefInfo(B5_7592, b7))!.Warp;
        Assert.NotNull(w);
        var w2 = NikonLensCorrection.Decode(BuildNefInfo(B5_7592, new int[B7_7592.Length]))!.Warp;
        Assert.NotNull(w2);
        Assert.Equal(w2.Planes[0], w2.Planes[1]);
        Assert.Equal(w2.Planes[2], w2.Planes[1]);
    }

    // DSC_7576.NEF vignette payload (block6 i32, 116B) + block8 (24B) with active flag
    private static readonly int[] B6_7576 =
    {
        808464688, 257, 88474, 4096, 8, 0, 1048576, 0, 1, 4918180, 1048576, 0, 1,
        -1136314, 1048576, 0, 1, 414090, 1048576, 0, 1, 1, 1, 0, 0, 1024, 0, 0, 0,
    };
    private static readonly int[] B8_7576 = { 808464688, -55, 256, 88, 256, 0 };

    // ADC 18.6 actual FixVignetteRadial output for DSC_7576
    private static readonly double[] AdcVignette = { 0.06478833, -0.17777813, 0.76928908, 0, 0 };

    [Fact]
    public void Vignette_MatchesAdobeDngConverter()
    {
        var v = NikonLensCorrection.Decode(BuildNefInfo(B5_7576(), B7_7592, B6_7576, B8_7576))!.Vignette;
        Assert.NotNull(v);
        for (int i = 0; i < 5; i++)
            Assert.True(Math.Abs(v!.P[i] - AdcVignette[i]) < 5e-4,
        $"P[{i}]: {v.P[i]} vs ADC {AdcVignette[i]}");

        var blob = NikonLensCorrection.SerializeOpcodeList2(v);
        // 4 + 16 + 56
        Assert.Equal(76, blob.Length);
        Assert.Equal(3, blob[7]);                                            // id=3 FixVignetteRadial
        Assert.Equal(0x38, blob[19]);                                        // paramLen=56
        Assert.Equal(0x3F, blob[20]);                                        // P0 ≈ 0.065 → BE f64 0x3F0...
    }

    [Fact]
    public void Vignette_SkippedWhenFlagZero()
    {
        // block8 flag (+0x04) = 0 → Vignette Control off → no opcode (DSC_7592 behavior)
        var b8 = (int[])B8_7576.Clone(); b8[1] = 0;
        var r = NikonLensCorrection.Decode(BuildNefInfo(B5_7576(), B7_7592, B6_7576, b8));
        Assert.NotNull(r);
        Assert.Null(r!.Vignette);
        Assert.NotNull(r.Warp);
    }

    private static int[] B5_7576()
    {
        // 7576: k = (28216, -15312, -94191)/2^20, t4 = -28953/2^20
        var b = (int[])B5_7592.Clone();
        b[5] = 28216; b[7] = -15312; b[9] = -94191; b[16] = -28953;
        return b;
    }

    // ADC 18.6 actual WarpRectilinear G-plane output for DSC_7576 (t4 scale applied)
    private static readonly double[] AdcG_7576 = { 1.02855819, -0.09811991, 0.01487094, 0.0030287 };

    [Fact]
    public void Warp_T4Scale_MatchesAdobeAcrossFocusStates()
    {
        // Same lens, different focus: the +0x40 scale term must track ADC exactly
        var w = NikonLensCorrection.Decode(BuildNefInfo(B5_7576(), B7_7576()))!.Warp;
        Assert.NotNull(w);
        for (int c = 0; c < 4; c++)
            Assert.True(Math.Abs(w!.Planes[1][c] - AdcG_7576[c]) < 1e-5,
        $"G7576[{c}]: {w.Planes[1][c]} vs ADC {AdcG_7576[c]}");
    }

    private static int[] B7_7576()
    {
        var b = (int[])B7_7592.Clone();
        b[5] = -254; b[7] = 77; b[11] = 379; b[14] = 334; b[16] = -102; b[20] = 293;
        return b;
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
        var w = NikonLensCorrection.Decode(BuildNefInfo(B5_7592, B7_7592))!.Warp;
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
