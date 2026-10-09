using System;
using System.Collections.Generic;
using System.IO;

namespace SaveZ8.Core;

/// <summary>
/// Extracts TIFF IFD trees (EXIF, GPS, Makernote, custom tags) from original NEF files
/// for byte-level preservation into the output DNG.
/// </summary>
public class TiffIfdExtractor
{
    public class TiffIfd
    {
        public uint Offset { get; init; }
        public List<TiffEntry> Entries { get; } = new();
        public uint NextIfdOffset { get; set; }
        public List<TiffIfd> SubIfds { get; } = new();
    }

    public class TiffEntry
    {
        public ushort Tag { get; init; }
        public ushort Type { get; init; }
        public uint Count { get; init; }
        public uint ValueOrOffset { get; init; }
        public byte[]? Data { get; set; } // if data is > 4 bytes, stored here
    }

    public class ExtractedMetadata
    {
        public byte[] OriginalFile { get; init; } = Array.Empty<byte>();
        public bool IsLittleEndian { get; init; }
        public List<TiffIfd> AllIfds { get; } = new();
        public Dictionary<string, string> CommonExif { get; } = new();
    }

    // TIFF type sizes
    private static readonly int[] TypeSizes = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8 };

    /// <summary>
    /// Extract all IFD structures from an NEF file for later re-embedding.
    /// </summary>
    public static ExtractedMetadata Extract(string nefPath)
    {
        var data = File.ReadAllBytes(nefPath);
        return Extract(data);
    }

    public static ExtractedMetadata Extract(byte[] data)
    {
        if (data.Length < 8)
            throw new InvalidDataException("File too small for TIFF");

        bool le = data[0] == 0x49 && data[1] == 0x49; // II = little endian
        if (!le && !(data[0] == 0x4D && data[1] == 0x4D))
            throw new InvalidDataException("Not a TIFF file");

        var result = new ExtractedMetadata { OriginalFile = data, IsLittleEndian = le };

        // Walk IFD chain
        uint ifd0Offset = le ? ReadUInt32(data, 4) : ReadUInt32BE(data, 4);
        var visited = new HashSet<uint>();

        var ifd0 = ParseIfd(data, ifd0Offset, le, visited, result.AllIfds, depth: 0);

        // Extract common EXIF values for display
        ExtractCommonValues(result);

        return result;
    }

    private static TiffIfd ParseIfd(byte[] data, uint offset, bool le,
        HashSet<uint> visited, List<TiffIfd> allIfds, int depth)
    {
        if (visited.Contains(offset) || offset + 2 > data.Length || depth > 5)
            return new TiffIfd { Offset = offset };

        visited.Add(offset);
        var ifd = new TiffIfd { Offset = offset };
        allIfds.Add(ifd);

        int entryCount = (int)ReadUInt16(data, offset);
        if (entryCount <= 0 || entryCount > 1024) return ifd;

        for (int i = 0; i < entryCount; i++)
        {
            uint entryOffset = offset + 2 + (uint)(i * 12);
            if (entryOffset + 12 > data.Length) break;

            ushort tag = ReadUInt16(data, entryOffset);
            ushort type = ReadUInt16(data, entryOffset + 2);
            uint count = le ? ReadUInt32(data, entryOffset + 4) : ReadUInt32BE(data, entryOffset + 4);
            uint valueOrOffset = le ? ReadUInt32(data, entryOffset + 8) : ReadUInt32BE(data, entryOffset + 8);

            var entry = new TiffEntry { Tag = tag, Type = type, Count = count, ValueOrOffset = valueOrOffset };

            // Calculate data size
            int typeSize = type < TypeSizes.Length ? TypeSizes[type] : 1;
            long dataSize = (long)typeSize * count;

            if (dataSize > 4 && valueOrOffset + dataSize <= data.Length)
            {
                entry.Data = new byte[dataSize];
                Array.Copy(data, valueOrOffset, entry.Data, 0, (int)dataSize);
            }

            ifd.Entries.Add(entry);

            // Follow SubIFD pointers
            if (tag == 0x8769 || tag == 0x8825 || tag == 0xA005 || tag == 0x014A) // EXIF, GPS, Interop, SubIFDs
            {
                if (valueOrOffset > 0 && valueOrOffset < data.Length)
                {
                    var sub = ParseIfd(data, valueOrOffset, le, visited, allIfds, depth + 1);
                    ifd.SubIfds.Add(sub);
                }
            }
        }

        // Next IFD pointer
        uint nextOffset = offset + 2 + (uint)(entryCount * 12);
        if (nextOffset + 4 <= data.Length)
        {
            ifd.NextIfdOffset = ReadUInt32(data, nextOffset);
        }

        return ifd;
    }

    private static void ExtractCommonValues(ExtractedMetadata meta)
    {
        // Walk all IFDs looking for common EXIF tags
        foreach (var ifd in meta.AllIfds)
        {
            foreach (var entry in ifd.Entries)
            {
                string? value = entry.Tag switch
                {
                    0x0110 => ReadAscii(entry, meta), // Model
                    0x010F => ReadAscii(entry, meta), // Make
                    0x0112 => entry.ValueOrOffset.ToString(), // Orientation
                    0x829A => $"f/{ExposureTimeFromRational(entry, meta)}", // ExposureTime
                    0x829D => ReadRational(entry, meta, "F"), // FNumber
                    0x8827 => entry.ValueOrOffset.ToString(), // ISO
                    0x920A => ReadRational(entry, meta, "mm"), // FocalLength
                    0x9003 => ReadAscii(entry, meta), // DateTimeOriginal
                    0xA002 => entry.ValueOrOffset.ToString(), // PixelXDimension
                    0xA003 => entry.ValueOrOffset.ToString(), // PixelYDimension
                    _ => null
                };
                if (value != null)
                {
                    string key = entry.Tag switch
                    {
                        0x0110 => "Model", 0x010F => "Make", 0x0112 => "Orientation",
                        0x829A => "ExposureTime", 0x829D => "FNumber", 0x8827 => "ISO",
                        0x920A => "FocalLength", 0x9003 => "DateTimeOriginal",
                        0xA002 => "Width", 0xA003 => "Height", _ => $"Tag_{entry.Tag:X4}"
                    };
                    meta.CommonExif[key] = value;
                }
            }
        }
    }

    private static string? ReadAscii(TiffEntry entry, ExtractedMetadata meta)
    {
        if (entry.Type != 2) return null;
        if (entry.Data != null)
            return System.Text.Encoding.ASCII.GetString(entry.Data).TrimEnd('\0');
        if (entry.Count <= 4)
        {
            var bytes = new byte[entry.Count];
            for (int i = 0; i < entry.Count && i < 4; i++)
                bytes[i] = (byte)(entry.ValueOrOffset >> (8 * i));
            return System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }
        return null;
    }

    private static string? ReadRational(TiffEntry entry, ExtractedMetadata meta, string unit)
    {
        if (entry.Data != null && entry.Data.Length >= 8)
        {
            uint num = BitConverter.ToUInt32(entry.Data, 0);
            uint den = BitConverter.ToUInt32(entry.Data, 4);
            if (!meta.IsLittleEndian)
            {
                num = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(num);
                den = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(den);
            }
            if (den > 0) return $"{(double)num / den:0.##} {unit}";
        }
        return null;
    }

    private static string? ExposureTimeFromRational(TiffEntry entry, ExtractedMetadata meta)
    {
        if (entry.Data != null && entry.Data.Length >= 8)
        {
            uint num = BitConverter.ToUInt32(entry.Data, 0);
            uint den = BitConverter.ToUInt32(entry.Data, 4);
            if (!meta.IsLittleEndian)
            {
                num = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(num);
                den = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(den);
            }
            if (den > 0 && num > 0)
            {
                double d = (double)num / den;
                if (d < 1) return $"1/{Math.Round(1 / d)}";
                return d.ToString("0.#");
            }
        }
        return null;
    }

    // Utility
    private static ushort ReadUInt16(byte[] d, uint off) =>
        (ushort)(d[off] | (d[off + 1] << 8));

    private static uint ReadUInt32(byte[] d, uint off) =>
        (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));

    private static uint ReadUInt32BE(byte[] d, uint off) =>
        (uint)(d[off] << 24 | d[off + 1] << 16 | d[off + 2] << 8 | d[off + 3]);
}
