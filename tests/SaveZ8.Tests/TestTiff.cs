using System.Text;

namespace SaveZ8.Tests;

/// <summary>
/// Shared helpers for the DNG structure/preview tests: a minimal little-endian TIFF
/// IFD reader and the common synthetic-NEF segments (Make/DateTime strings and the
/// EXIF/Interop/GPS IFDs) used by both test suites.
/// </summary>
internal static class TestTiff
{
    internal sealed class ParsedIfd
    {
        public Dictionary<ushort, (ushort Type, uint Count, byte[] Value)> Tags = new();
        public uint NextIfd;
    }

    internal static ushort U16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));

    internal static uint U32(byte[] b, int i) =>
        (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

    internal static string Ascii(byte[] b) => Encoding.ASCII.GetString(b).TrimEnd('\0');

    /// <summary>Writes one 12-byte TIFF IFD entry.</summary>
    internal static void Entry(BinaryWriter bw, ushort tag, ushort type, uint count, uint val)
    {
        bw.Write(tag); bw.Write(type); bw.Write(count); bw.Write(val);
    }

    /// <summary>Parses one little-endian IFD at the given offset (values resolved inline or by offset).</summary>
    internal static ParsedIfd ParseIfd(byte[] file, uint offset)
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

    /// <summary>Writes the shared "TestCam" Make and IFD0 DateTime strings.</summary>
    internal static void WriteMakeDateTime(MemoryStream ms, BinaryWriter bw, int makeOff, int dtOff)
    {
        ms.Position = makeOff; bw.Write(Encoding.ASCII.GetBytes("TestCam\0"));
        ms.Position = dtOff; bw.Write(Encoding.ASCII.GetBytes("2026:01:01 00:00:00\0"));
    }

    /// <summary>
    /// Writes the shared EXIF IFD (ExposureTime 1/1000, ISO 64, DateTimeOriginal,
    /// InteropIFD pointer), the Interop IFD ("R98") and the GPS IFD at the
    /// pre-computed offsets.
    /// </summary>
    internal static void WriteExifInteropGps(MemoryStream ms, BinaryWriter bw,
        int exifOff, int expTimeOff, int dtoOff, int interopOff, int gpsOff)
    {
        ms.Position = exifOff; bw.Write((ushort)4);
        Entry(bw, 0x829A, 5, 1, (uint)expTimeOff);      // ExposureTime RATIONAL
        Entry(bw, 0x8827, 3, 1, 64u);                   // ISO inline SHORT
        Entry(bw, 0x9003, 2, 20, (uint)dtoOff);         // DateTimeOriginal
        Entry(bw, 0xA005, 4, 1, (uint)interopOff);      // InteropIFD pointer
        bw.Write((uint)0);

        ms.Position = expTimeOff; bw.Write(1u); bw.Write(1000u);
        ms.Position = dtoOff; bw.Write(Encoding.ASCII.GetBytes("2026:10:09 10:00:00\0"));

        ms.Position = interopOff; bw.Write((ushort)1);
        Entry(bw, 0x0001, 2, 4, 0x00383952u);           // "R98\0" inline
        bw.Write((uint)0);

        ms.Position = gpsOff; bw.Write((ushort)2);
        Entry(bw, 0x0000, 1, 4, 0x00000302u);           // GPSVersionID [2,3,0,0] inline
        Entry(bw, 0x0001, 2, 2, 0x004Eu);               // "N\0" inline
        bw.Write((uint)0);
    }
}
