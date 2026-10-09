using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SaveZ8.Core;

/// <summary>
/// Writes uncompressed 16-bit DNG files with original metadata preserved.
/// Layout: TIFF header → IFD0 (raw, + external blobs) → preview SubIFDs → EXIF IFD
/// (+ Interop) → GPS IFD → IFD1 (thumbnail chain) → pointer arrays → pixel data.
/// Reference: JO.html convert_to_dng (core tags) + Adobe DNG spec 1.6 (previews, crops).
/// </summary>
public class DngWriter
{
    // TIFF/DNG tags
    private const ushort TagNewSubfileType = 254;
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometricInterpretation = 262;
    private const ushort TagMake = 271;
    private const ushort TagModel = 272;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagPlanarConfiguration = 284;
    private const ushort TagJpegInterchangeFormat = 513;
    private const ushort TagJpegInterchangeFormatLength = 514;
    private const ushort TagCFARepeatPatternDim = 33421;
    private const ushort TagCFAPattern = 33422;
    private const ushort TagSubIFDs = 0x014A;
    private const ushort TagDNGVersion = 50706;
    private const ushort TagDNGBackwardVersion = 50707;
    private const ushort TagUniqueCameraModel = 50708;
    private const ushort TagBlackLevelRepeatDim = 50713;
    private const ushort TagBlackLevel = 50714;
    private const ushort TagWhiteLevel = 50717;
    private const ushort TagColorMatrix1 = 50721;
    private const ushort TagCalibrationIlluminant1 = 50778;
    private const ushort TagDefaultCropOrigin = 50819;
    private const ushort TagDefaultCropSize = 50820;
    private const ushort TagExifIFD = 0x8769;
    private const ushort TagGpsIFD = 0x8825;

    // TIFF field types
    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private const ushort TypeSRational = 10;

    private static readonly int[] TypeSizes = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8 };

    // IFD0 tags safe to copy verbatim from the source file (scalar/string/blobs only).
    // Strip/SubIFD/preview machinery is deliberately excluded (rebuilt from Previews).
    private static readonly HashSet<ushort> CopyableIfd0Tags = new()
        { 270, 274, 282, 283, 296, 305, 306, 315, 700, 33432 };

    public class DngOptions
    {
        public int BlackLevel { get; set; } = 4032; // 1008 * 4
        public int WhiteLevel { get; set; } = 65019;
        public int BitShift { get; set; } = 2; // ×4 to expand 14-bit to 16-bit
        public string UniqueCameraModel { get; set; } = "Nikon Z 8";
        public double[]? ColorMatrix1 { get; set; } = { 1.1423, -0.4564, -0.1123, -0.4816, 1.2895, 0.2119, -0.021, 0.1061, 0.7282 };
        public int CalibrationIlluminant1 { get; set; } = 21; // D65
        /// <summary>Write DefaultCropOrigin/Size derived from the largest embedded
        /// preview (active area), origin centered and CFA-phase-even. Default true.</summary>
        public bool AutoDefaultCrop { get; set; } = true;
    }

    // ---------- output model ----------

    private sealed class Entry
    {
        public ushort Tag, Type;
        public uint Count;
        public byte[]? Inline;      // ≤4 bytes, little-endian, final
        public byte[]? Blob;        // >4 bytes, little-endian, final (Inline gets blob offset)
        public IfdBlock? SubBlock;  // tag value = offset of this block
        public List<IfdBlock>? SubBlockList; // tag value = offset of uint[] array (SubIFDs)
        public bool IsPixelData;    // tag value = pixel data offset
    }

    private sealed class IfdBlock
    {
        public List<Entry> Entries { get; } = new();
        public uint Offset;             // assigned during layout
        public IfdBlock? NextBlock;     // TIFF next-IFD chain (IFD1 thumbnail)
    }

    public static void Write(string outputPath, ushort[] pixels, int width, int height,
        byte[] cfaPattern, TiffIfdExtractor.ExtractedMetadata? originalMetadata, DngOptions? options = null)
    {
        options ??= new DngOptions();

        // 14-bit → 16-bit expansion
        var shifted = new ushort[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            int v = pixels[i] << options.BitShift;
            shifted[i] = (ushort)Math.Min(v, 65535);
        }

        // LibRaw CFA indices (0=R,1=G1,2=B,3=G2) → DNG CFAPattern colors (0=Red,1=Green,2=Blue)
        var dngCfa = new byte[4];
        for (int i = 0; i < 4; i++)
            dngCfa[i] = cfaPattern[i] switch { 0 => 0, 2 => 2, _ => 1 };

        // ---- build IFD0 (raw) ----
        var ifd0 = new IfdBlock();

        void AddShort(ushort tag, ushort value) =>
            ifd0.Entries.Add(new Entry { Tag = tag, Type = TypeShort, Count = 1, Inline = Pad(BitConverter.GetBytes(value)) });
        void AddLong(ushort tag, uint value) =>
            ifd0.Entries.Add(new Entry { Tag = tag, Type = TypeLong, Count = 1, Inline = Pad(BitConverter.GetBytes(value)) });
        void AddAscii(ushort tag, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value + "\0");
            ifd0.Entries.Add(bytes.Length <= 4
                ? new Entry { Tag = tag, Type = TypeAscii, Count = (uint)bytes.Length, Inline = Pad(bytes) }
                : new Entry { Tag = tag, Type = TypeAscii, Count = (uint)bytes.Length, Blob = bytes });
        }
        void AddBytes(ushort tag, byte[] value)
        {
            ifd0.Entries.Add(value.Length <= 4
                ? new Entry { Tag = tag, Type = TypeByte, Count = (uint)value.Length, Inline = Pad(value) }
                : new Entry { Tag = tag, Type = TypeByte, Count = (uint)value.Length, Blob = value });
        }
        void AddShorts(ushort tag, ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 2);
            ifd0.Entries.Add(bytes.Length <= 4
                ? new Entry { Tag = tag, Type = TypeShort, Count = (uint)values.Length, Inline = Pad(bytes) }
                : new Entry { Tag = tag, Type = TypeShort, Count = (uint)values.Length, Blob = bytes });
        }
        void AddLongs(ushort tag, uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 4);
            ifd0.Entries.Add(new Entry { Tag = tag, Type = TypeLong, Count = (uint)values.Length, Blob = bytes });
        }

        AddLong(TagNewSubfileType, 0);
        AddLong(TagImageWidth, (uint)width);
        AddLong(TagImageLength, (uint)height);
        AddShort(TagBitsPerSample, 16);
        AddShort(TagCompression, 1);                    // uncompressed
        AddShort(TagPhotometricInterpretation, 32803);  // CFA
        AddAscii(TagMake, "Nikon");
        AddAscii(TagModel, options.UniqueCameraModel);
        AddShort(TagSamplesPerPixel, 1);
        AddLong(TagRowsPerStrip, (uint)height);
        AddShort(TagPlanarConfiguration, 1);

        AddShorts(TagCFARepeatPatternDim, new ushort[] { 2, 2 });
        AddBytes(TagCFAPattern, dngCfa);

        AddBytes(TagDNGVersion, new byte[] { 1, 6, 0, 0 });
        AddBytes(TagDNGBackwardVersion, new byte[] { 1, 4, 0, 0 });
        AddAscii(TagUniqueCameraModel, options.UniqueCameraModel);

        AddShorts(TagBlackLevelRepeatDim, new ushort[] { 2, 2 });
        AddShorts(TagBlackLevel, new[] { (ushort)options.BlackLevel, (ushort)options.BlackLevel,
                                         (ushort)options.BlackLevel, (ushort)options.BlackLevel });
        AddLong(TagWhiteLevel, (uint)options.WhiteLevel);

        if (options.ColorMatrix1 != null)
        {
            var cm = new byte[options.ColorMatrix1.Length * 8];
            for (int i = 0; i < options.ColorMatrix1.Length; i++)
            {
                BitConverter.GetBytes((int)Math.Round(options.ColorMatrix1[i] * 10000)).CopyTo(cm, i * 8);
                BitConverter.GetBytes(10000).CopyTo(cm, i * 8 + 4);
            }
            ifd0.Entries.Add(new Entry { Tag = TagColorMatrix1, Type = TypeSRational,
                Count = (uint)options.ColorMatrix1.Length, Blob = cm });
        }
        AddShort(TagCalibrationIlluminant1, (ushort)options.CalibrationIlluminant1);

        // Strip data location — real entries, values resolved by the layout pass
        ifd0.Entries.Add(new Entry { Tag = TagStripOffsets, Type = TypeLong, Count = 1, IsPixelData = true });
        AddLong(TagStripByteCounts, (uint)(shifted.Length * 2));

        // Default crop: active area derived from the largest embedded preview (Z8: 8256×5504
        // vs raw 8280×5520 including masked borders). Origin centered, rounded to even (CFA-safe).
        var previews = originalMetadata?.Previews;
        if (options.AutoDefaultCrop && previews is { Count: > 0 })
        {
            var active = previews[^1];
            if (active.Width <= width && active.Height <= height && (active.Width < width || active.Height < height))
            {
                uint ox = (uint)(((width - active.Width) / 2) & ~1);
                uint oy = (uint)(((height - active.Height) / 2) & ~1);
                AddLongs(TagDefaultCropOrigin, new[] { ox, oy });
                AddLongs(TagDefaultCropSize, new[] { (uint)active.Width, (uint)active.Height });
            }
        }

        // ---- preview IFDs (SubIFDs belt) + IFD1 thumbnail (WIC/Explorer braces) ----
        IfdBlock? thumbBlock = null;
        var previewBlocks = new List<IfdBlock>();
        if (previews is { Count: > 0 })
        {
            // SubIFD previews: all but the smallest; IFD1 chain: the smallest (thumbnail)
            foreach (var p in previews)
            {
                var block = new IfdBlock();
                block.Entries.Add(new Entry { Tag = TagNewSubfileType, Type = TypeLong, Count = 1,
                    Inline = Pad(BitConverter.GetBytes(1u)) });
                block.Entries.Add(new Entry { Tag = TagImageWidth, Type = TypeLong, Count = 1,
                    Inline = Pad(BitConverter.GetBytes((uint)p.Width)) });
                block.Entries.Add(new Entry { Tag = TagImageLength, Type = TypeLong, Count = 1,
                    Inline = Pad(BitConverter.GetBytes((uint)p.Height)) });
                block.Entries.Add(new Entry { Tag = TagBitsPerSample, Type = TypeShort, Count = 3,
                    Blob = new byte[] { 8, 0, 8, 0, 8, 0 } });
                block.Entries.Add(new Entry { Tag = TagCompression, Type = TypeShort, Count = 1,
                    Inline = Pad(BitConverter.GetBytes((ushort)6)) });          // old-style JPEG
                block.Entries.Add(new Entry { Tag = TagPhotometricInterpretation, Type = TypeShort, Count = 1,
                    Inline = Pad(BitConverter.GetBytes((ushort)2)) });          // RGB
                block.Entries.Add(new Entry { Tag = TagJpegInterchangeFormat, Type = TypeLong, Count = 1,
                    Blob = p.Jpeg });                                            // value = jpeg offset
                block.Entries.Add(new Entry { Tag = TagSamplesPerPixel, Type = TypeShort, Count = 1,
                    Inline = Pad(BitConverter.GetBytes((ushort)3)) });
                block.Entries.Add(new Entry { Tag = TagRowsPerStrip, Type = TypeLong, Count = 1,
                    Inline = Pad(BitConverter.GetBytes((uint)p.Height)) });
                block.Entries.Add(new Entry { Tag = TagJpegInterchangeFormatLength, Type = TypeLong, Count = 1,
                    Inline = Pad(BitConverter.GetBytes((uint)p.Jpeg.Length)) });
                previewBlocks.Add(block);
            }
            thumbBlock = previewBlocks[0];                       // smallest → IFD1 chain
            var subPreviews = previewBlocks.Skip(1).ToList();    // rest → SubIFDs
            if (subPreviews.Count == 1)
            {
                // TIFF rule: count=1 LONG (4 bytes) must be inline — use single-pointer entry
                ifd0.Entries.Add(new Entry { Tag = TagSubIFDs, Type = TypeLong, Count = 1,
                    SubBlock = subPreviews[0] });
            }
            else if (subPreviews.Count > 1)
            {
                ifd0.Entries.Add(new Entry { Tag = TagSubIFDs, Type = TypeLong,
                    Count = (uint)subPreviews.Count, SubBlockList = subPreviews });
            }
            ifd0.NextBlock = thumbBlock;
        }

        // ---- preserve original metadata (EXIF → Interop, GPS, safe IFD0 tags) ----
        IfdBlock? exifBlock = null, gpsBlock = null;
        if (originalMetadata != null && originalMetadata.AllIfds.Count > 0)
        {
            var srcIfd0 = originalMetadata.AllIfds[0];

            foreach (var tag in CopyableIfd0Tags)
            {
                var e = srcIfd0.Entries.Find(x => x.Tag == tag);
                if (e != null) CopyEntry(ifd0, e, originalMetadata.IsLittleEndian);
            }

            exifBlock = FindSubIfd(originalMetadata, 0x8769, out var srcExif) ? ReEmitIfd(srcExif, originalMetadata) : null;
            gpsBlock = FindSubIfd(originalMetadata, 0x8825, out var srcGps) ? ReEmitIfd(srcGps, originalMetadata) : null;

            if (exifBlock != null)
                ifd0.Entries.Add(new Entry { Tag = TagExifIFD, Type = TypeLong, Count = 1, SubBlock = exifBlock });
            if (gpsBlock != null)
                ifd0.Entries.Add(new Entry { Tag = TagGpsIFD, Type = TypeLong, Count = 1, SubBlock = gpsBlock });
        }

        ifd0.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // ---- layout phase 1: BFS over blocks, assign IFD offsets and plain blobs ----
        uint cursor = 8;
        var pending = new List<IfdBlock> { ifd0 };
        var orderedBlocks = new List<IfdBlock>();
        var multiPointers = new List<Entry>();
        while (pending.Count > 0)
        {
            var block = pending[0]; pending.RemoveAt(0);
            if (orderedBlocks.Contains(block)) continue;
            orderedBlocks.Add(block);
            block.Offset = cursor;
            cursor += (uint)(2 + block.Entries.Count * 12 + 4);
            foreach (var e in block.Entries)
            {
                if (e.Blob != null && e.SubBlockList == null)
                {
                    cursor = Align(cursor, 4);
                    e.Inline = BitConverter.GetBytes(cursor);
                    cursor += (uint)e.Blob.Length;
                }
                if (e.SubBlock != null) pending.Add(e.SubBlock);
                if (e.SubBlockList != null)
                {
                    multiPointers.Add(e);
                    foreach (var sub in e.SubBlockList) pending.Add(sub);
                }
            }
            if (block.NextBlock != null) pending.Add(block.NextBlock); // IFD1 chain (thumbnail)
        }

        // ---- layout phase 2: SubIFD pointer arrays appended after all blocks ----
        foreach (var e in multiPointers)
        {
            var arr = new byte[e.SubBlockList!.Count * 4];
            for (int i = 0; i < e.SubBlockList.Count; i++)
                BitConverter.GetBytes(e.SubBlockList[i].Offset).CopyTo(arr, i * 4);
            e.Blob = arr;
            cursor = Align(cursor, 4);
            e.Inline = BitConverter.GetBytes(cursor);
            cursor += (uint)arr.Length;
        }

        uint pixelOffset = Align(cursor, 16);

        // ---- layout phase 3: resolve pointer-valued entries ----
        foreach (var block in orderedBlocks)
            foreach (var e in block.Entries)
                if (e.IsPixelData) e.Inline = BitConverter.GetBytes(pixelOffset);
                else if (e.SubBlock != null) e.Inline = BitConverter.GetBytes(e.SubBlock.Offset);

        // ---- serialize ----
        using var ms = new MemoryStream((int)(pixelOffset + shifted.Length * 2L));
        var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write((byte)0x49); bw.Write((byte)0x49); bw.Write((ushort)42); bw.Write((uint)8);

        foreach (var block in orderedBlocks)
        {
            ms.Position = block.Offset;
            bw.Write((ushort)block.Entries.Count);
            foreach (var e in block.Entries)
            {
                bw.Write(e.Tag); bw.Write(e.Type); bw.Write(e.Count);
                var inline = new byte[4]; // TIFF value field is fixed 4 bytes
                if (e.Inline != null) Array.Copy(e.Inline, inline, Math.Min(4, e.Inline.Length));
                bw.Write(inline);
            }
            bw.Write(block.NextBlock != null ? block.NextBlock.Offset : 0u); // next IFD
            foreach (var e in block.Entries)
                if (e.Blob != null && e.SubBlockList == null)
                { ms.Position = Align((uint)ms.Position, 4); bw.Write(e.Blob); }
        }
        foreach (var e in multiPointers)
        { ms.Position = Align((uint)ms.Position, 4); bw.Write(e.Blob!); }

        var pixelBytes = new byte[shifted.Length * 2];
        Buffer.BlockCopy(shifted, 0, pixelBytes, 0, pixelBytes.Length);
        ms.Position = pixelOffset;
        bw.Write(pixelBytes);

        File.WriteAllBytes(outputPath, ms.ToArray());
    }

    // ---------- metadata relocation helpers ----------

    /// <summary>Finds the sub-IFD referenced from IFD0 by pointer tag (0x8769 EXIF / 0x8825 GPS).</summary>
    private static bool FindSubIfd(TiffIfdExtractor.ExtractedMetadata meta, ushort pointerTag,
        out TiffIfdExtractor.TiffIfd sub)
    {
        sub = new TiffIfdExtractor.TiffIfd();
        if (meta.AllIfds.Count == 0) return false;
        var srcIfd0 = meta.AllIfds[0];
        var ptr = srcIfd0.Entries.Find(x => x.Tag == pointerTag);
        if (ptr == null) return false;
        var match = srcIfd0.SubIfds.FirstOrDefault(s => s.Offset == ptr.ValueOrOffset);
        if (match == null || match.Entries.Count == 0) return false;
        sub = match;
        return true;
    }

    /// <summary>
    /// Re-emits a source IFD (EXIF or GPS) into the output: every entry is copied with its
    /// data relocated; nested IFD pointers (Interop 0xA005) become relocated sub-blocks.
    /// MakerNote is copied verbatim as an opaque blob (Nikon MakerNotes are self-relative).
    /// </summary>
    private static IfdBlock ReEmitIfd(TiffIfdExtractor.TiffIfd src, TiffIfdExtractor.ExtractedMetadata meta)
    {
        var block = new IfdBlock();
        bool srcLE = meta.IsLittleEndian;

        foreach (var e in src.Entries)
        {
            if (e.Tag == 0xA005) // InteropIFD pointer → relocated sub-block
            {
                var interop = src.SubIfds.FirstOrDefault(s => s.Offset == e.ValueOrOffset);
                if (interop != null && interop.Entries.Count > 0)
                    block.Entries.Add(new Entry { Tag = e.Tag, Type = TypeLong, Count = 1, SubBlock = ReEmitIfd(interop, meta) });
                continue;
            }
            CopyEntry(block, e, srcLE);
        }

        block.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        return block;
    }

    /// <summary>Copies one source entry, converting byte order when the source is big-endian.</summary>
    private static void CopyEntry(IfdBlock target, TiffIfdExtractor.TiffEntry e, bool srcLE)
    {
        int typeSize = e.Type < TypeSizes.Length ? TypeSizes[e.Type] : 1;
        long size = (long)typeSize * e.Count;

        if (size <= 4)
        {
            var raw = new byte[size];
            if (srcLE)
                for (int i = 0; i < size; i++) raw[i] = (byte)(e.ValueOrOffset >> (8 * i));
            else
                for (int i = 0; i < size; i++) raw[i] = (byte)(e.ValueOrOffset >> (8 * (3 - i)));
            target.Entries.Add(new Entry { Tag = e.Tag, Type = e.Type, Count = e.Count,
                Inline = Pad(ConvertByteOrder(raw, e.Type, srcLE)) });
        }
        else if (e.Data != null)
        {
            target.Entries.Add(new Entry { Tag = e.Tag, Type = e.Type, Count = e.Count,
                Blob = ConvertByteOrder(e.Data, e.Type, srcLE) });
        }
        // entries with out-of-range data offsets are skipped (malformed in source)
    }

    /// <summary>Swaps element byte order for multi-byte numeric types when source ≠ little-endian.</summary>
    private static byte[] ConvertByteOrder(byte[] data, ushort type, bool srcLE)
    {
        if (srcLE) return data;
        int es = type < TypeSizes.Length ? TypeSizes[type] : 1;
        if (es <= 1 || data.Length % es != 0) return data;
        var r = new byte[data.Length];
        for (int i = 0; i + es <= data.Length; i += es)
            for (int j = 0; j < es; j++)
                r[i + j] = data[i + es - 1 - j];
        return r;
    }

    private static byte[] Pad(byte[] b)
    {
        var p = new byte[4];
        Array.Copy(b, p, b.Length);
        return p;
    }

    private static uint Align(uint v, int alignment) => (uint)((v + alignment - 1) & ~(alignment - 1));
}
