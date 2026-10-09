using System;
using System.IO;

namespace SaveZ8.Core;

/// <summary>
/// Decodes Nikon Z lens correction data (NEFInfo, SubIFD tag 0xC7D5) and converts it to
/// a DNG WarpRectilinear OpcodeList3 (big-endian serialization per Adobe DNG SDK).
///
/// Mapping derivation (2026-10-09, empirical, calibrated against Adobe DNG Converter 18.6
/// output on Z8 firmware 3.11 — synthetic basis sweeps of the NEF rationals):
///   Green plane:  (off, a, b, c) = M_G · (1, k1, k2, k3)
///   Red plane:    G + A · p   (p = six block-7 rationals)
///   Blue plane:   G + C · p
/// Residuals: distortion 1e-6 (≈0.003 px), CA first-order (corner residual < 1 px).
/// Vignette (block 6) is not transferred (no calibration sample with Vignette Control ON).
/// </summary>
public static class NikonLensCorrection
{
    // M_G rows: images of (1, k1, k2, k3) → (off, r2, r4, r6)
    private static readonly double[][] MG =
    {
        new[] {  1.02809531,  0.0,         -0.0111178147, 0.0        },
        new[] {  0.0,         0.0,          0.356251074,   1.03407210 },
        new[] {  0.0,         1.04009288,   0.898552896,   0.0        },
        new[] {  0.0,         0.0,         -0.207344855,   0.0        },
    };

    // CA responses per unit block-7 parameter (R and B identical shapes, disjoint params)
    private static readonly double[][] AR =
    {
        new[] { -0.0156,  0.4462,  0.6126, -0.2220 },  // p5
        new[] {  0.0001,  1.0927, -0.3067,  0.0763 },  // p7
        new[] {  1.0286, -0.0976,  0.0168,  0.0024 },  // p11
    };
    private static readonly double[][] CB =
    {
        new[] { -0.0156,  0.4462,  0.6126, -0.2220 },  // p14
        new[] {  0.0001,  1.0927, -0.3067,  0.0763 },  // p16
        new[] {  1.0286, -0.0976,  0.0168,  0.0024 },  // p20
    };

    private const double Q = 1048576.0; // rational denominator 2^20

    public class WarpParams
    {
        /// <summary>Per plane [R,G,B] → (off, r2, r4, r6).</summary>
        public double[][] Planes { get; init; } = Array.Empty<double[]>();
        public double CenterX { get; init; } = 0.5;
        public double CenterY { get; init; } = 0.5;
        public double[] K { get; init; } = new double[3];
    }

    /// <summary>
    /// Parses the NEFInfo blob (verbatim from the raw SubIFD) and computes WarpRectilinear
    /// parameters. Returns null when the blob is absent/malformed or carries no data.
    /// </summary>
    public static WarpParams? Decode(byte[]? nefInfo)
    {
        if (nefInfo is not { Length: > 64 }) return null;
        if (nefInfo[0] != (byte)'N' || nefInfo[1] != (byte)'i' || nefInfo[2] != (byte)'k' ||
            nefInfo[3] != (byte)'o' || nefInfo[4] != (byte)'n' || nefInfo[5] != 0)
            return null;

        int tiffBase = 10; // "Nikon\0" + 4-byte version
        if (tiffBase + 4 > nefInfo.Length) return null;
        if (nefInfo[tiffBase] != 0x49 || nefInfo[tiffBase + 1] != 0x49) return null; // II

        uint ifdOff = U32(nefInfo, (uint)(tiffBase + 4));
        uint o = (uint)(tiffBase + ifdOff);
        if (o + 2 > nefInfo.Length) return null;
        int entries = U16(nefInfo, o);

        byte[]? block5 = null, block7 = null;
        for (int i = 0; i < entries; i++)
        {
            uint p = o + 2 + (uint)(i * 12);
            if (p + 12 > nefInfo.Length) break;
            ushort tag = U16(nefInfo, p);
            ushort type = U16(nefInfo, p + 2);
            uint count = U32(nefInfo, p + 4);
            uint val = U32(nefInfo, p + 8);
            if (type != 7) continue; // undef blocks

            int typeSize = 1;
            long size = (long)typeSize * count;
            if (size <= 4 || val + size > nefInfo.Length) continue;
            uint dataOff = (uint)(tiffBase + val);
            if (dataOff + size > nefInfo.Length) continue;

            if (tag == 5 && count == 84) block5 = Slice(nefInfo, dataOff, 84);
            if (tag == 7 && count == 104) block7 = Slice(nefInfo, dataOff, 104);
        }

        if (block5 == null) return null;

        // Distortion triple: rational pairs at +0x14, +0x1c, +0x24
        double k1 = I32(block5, 0x14) / Q, k2 = I32(block5, 0x1c) / Q, k3 = I32(block5, 0x24) / Q;

        // Green plane = pure distortion
        var g = new double[4];
        double[] basis = { 1, k1, k2, k3 };
        for (int r = 0; r < 4; r++)
            g[r] = MG[r][0] * 1 + MG[r][1] * k1 + MG[r][2] * k2 + MG[r][3] * k3;

        var r0 = (double[])g.Clone();
        var b0 = (double[])g.Clone();

        // Chromatic aberration: block-7 rationals; (p5,p7,p11) → R, (p14,p16,p20) → B
        if (block7 != null)
        {
            double[] pr = { I32(block7, 5 * 4) / Q, I32(block7, 7 * 4) / Q, I32(block7, 11 * 4) / Q };
            double[] pb = { I32(block7, 14 * 4) / Q, I32(block7, 16 * 4) / Q, I32(block7, 20 * 4) / Q };
            for (int j = 0; j < 3; j++)
                for (int c = 0; c < 4; c++)
                {
                    r0[c] += AR[j][c] * pr[j];
                    b0[c] += CB[j][c] * pb[j];
                }
        }

        return new WarpParams { Planes = new[] { r0, g, b0 }, K = new[] { k1, k2, k3 } };
    }

    /// <summary>
    /// Serializes OpcodeList3 (tag 51022) containing one WarpRectilinear opcode.
    /// Byte order: big-endian (opcode lists are always big-endian, DNG spec / SDK).
    /// Layout per Adobe DNG SDK dng_opcode_WarpRectilinear::PutData:
    ///   list: { u32 count; per opcode: u32 id, u32 minVersion, u32 flags, u32 paramLen, params }
    ///   params: i32 planes; per plane 6×f64 (off, r2, r4, r6, tan0, tan1); f64 cx; f64 cy
    /// </summary>
    public static byte[] SerializeOpcodeList3(WarpParams w)
    {
        int planes = w.Planes.Length;
        int paramLen = 4 + planes * 48 + 16;
        var ms = new MemoryStream(8 + 16 + paramLen);
        var bw = new BinaryWriter(ms);

        bw.Write(Be((uint)1));                 // one opcode
        bw.Write(Be(1u));                      // opcode id: WarpRectilinear
        bw.Write(Be(0x0103_0000));             // minVersion 1.3.0.0
        bw.Write(Be(0u));                      // flags
        bw.Write(Be((uint)paramLen));
        bw.Write(Be((uint)planes));
        for (int p = 0; p < planes; p++)
        {
            var v = w.Planes[p];
            bw.Write(Be(v[0]));                // offset
            bw.Write(Be(v[1]));                // r^2
            bw.Write(Be(v[2]));                // r^4
            bw.Write(Be(v[3]));                // r^6
            bw.Write(Be(0.0));                 // tan0
            bw.Write(Be(0.0));                 // tan1
        }
        bw.Write(Be(w.CenterX));
        bw.Write(Be(w.CenterY));
        return ms.ToArray();
    }

    // ---------- little helpers ----------

    private static ushort U16(byte[] d, uint o) => (ushort)(d[o] | (d[o + 1] << 8));
    private static uint U32(byte[] d, uint o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    private static int I32(byte[] d, int o) => unchecked((int)U32(d, (uint)o));

    private static byte[] Slice(byte[] d, uint off, int len)
    {
        var r = new byte[len];
        Array.Copy(d, off, r, 0, len);
        return r;
    }

    private static byte[] Be(uint v) => new[]
        { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] Be(double v)
    {
        // IEEE 754 big-endian
        long bits = BitConverter.DoubleToInt64Bits(v);
        return new[]
        {
            (byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32),
            (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits,
        };
    }
}
