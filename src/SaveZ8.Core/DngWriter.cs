using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SaveZ8.Core;

/// <summary>
/// Writes uncompressed 16-bit DNG files with original metadata preserved.
///
/// Topology (replicates Adobe DNG Converter so ACR renders identically):
///   IFD0  = thumbnail (NewSubfileType=1) + baseline/color/exposure tags + EXIF/GPS
///   SubIFDs(0x014A) → raw IFD (NewSubfileType=0, CFA, opcodes, ActiveArea) + previews
///   IFD1  = absent (IFD0 itself is the thumbnail for generic TIFF/WIC readers)
///
/// Rendering-parity values calibrated against ADC 18.6 output for the Z8:
///   WhiteLevel 63568 (= 15892×4), BaselineExposure +0.20 EV, BaselineNoise 0.8,
///   BaselineSharpness 1.0, AsShotNeutral = 1/cam_mul (per shot, from LibRaw).
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
    private const ushort TagDefaultScale = 50718;
    private const ushort TagDefaultCropOrigin = 50719;
    private const ushort TagDefaultCropSize = 50720;
    private const ushort TagActiveArea = 50829;
    private const ushort TagBaselineExposure = 50730;
    private const ushort TagBaselineNoise = 50731;
    private const ushort TagBaselineSharpness = 50732;
    private const ushort TagAsShotNeutral = 50728;
    private const ushort TagExifIFD = 0x8769;
    private const ushort TagGpsIFD = 0x8825;

    // TIFF field types
    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private const ushort TypeSRational = 10;

    // Rendering-parity constants (formerly the DngOptions class — no caller ever
    // overrode any of them, so they are fixed private constants now).
    private const int BlackLevelValue = 4032;              // 1008 * 4 (×4 domain)
    private const int WhiteLevelValue = 63568;             // 15892 * 4 — Adobe's Z8 white level
    private const int PixelBitShift = 2;                   // ×4 to expand 14-bit to 16-bit
    private const string CameraModelName = "Nikon Z 8";
    private const int CalibrationIlluminant1Value = 21;    // D65
    private static readonly double[] ColorMatrix1Values =
        { 1.1423, -0.4564, -0.1123, -0.4816, 1.2895, 0.2119, -0.021, 0.1061, 0.7282 };

    // IFD0 tags safe to copy verbatim from the source file (scalar/string/blobs only).
    private static readonly HashSet<ushort> CopyableIfd0Tags = new()
        { 270, 274, 282, 283, 296, 305, 306, 315, 700, 33432 };

    // Zero bytes for explicitly filling TIFF alignment gaps during serialization.
    private static readonly byte[] AlignmentZeros = new byte[16];

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
    }

    public static void Write(string outputPath, ushort[] pixels, int width, int height,
        byte[] cfaPattern, TiffIfdExtractor.ExtractedMetadata? originalMetadata,
        float[]? cameraWb = null)
    {
        // LibRaw CFA indices (0=R,1=G1,2=B,3=G2) → DNG CFAPattern colors (0=Red,1=Green,2=Blue)
        var dngCfa = new byte[4];
        for (int i = 0; i < 4; i++)
            dngCfa[i] = cfaPattern[i] switch { 0 => 0, 2 => 2, _ => 1 };

        // ================= raw IFD =================
        var rawBlock = new IfdBlock();

        void RawShort(ushort tag, ushort value) =>
            rawBlock.Entries.Add(new Entry { Tag = tag, Type = TypeShort, Count = 1, Inline = Pad(BitConverter.GetBytes(value)) });
        void RawLong(ushort tag, uint value) =>
            rawBlock.Entries.Add(new Entry { Tag = tag, Type = TypeLong, Count = 1, Inline = Pad(BitConverter.GetBytes(value)) });
        void RawShorts(ushort tag, ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 2);
            rawBlock.Entries.Add(bytes.Length <= 4
                ? new Entry { Tag = tag, Type = TypeShort, Count = (uint)values.Length, Inline = Pad(bytes) }
                : new Entry { Tag = tag, Type = TypeShort, Count = (uint)values.Length, Blob = bytes });
        }
        void RawLongs(ushort tag, uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 4);
            rawBlock.Entries.Add(new Entry { Tag = tag, Type = TypeLong, Count = (uint)values.Length, Blob = bytes });
        }
        void RawRationals(ushort tag, (int num, int den)[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
            {
                BitConverter.GetBytes(values[i].num).CopyTo(bytes, i * 8);
                BitConverter.GetBytes(values[i].den).CopyTo(bytes, i * 8 + 4);
            }
            rawBlock.Entries.Add(new Entry { Tag = tag, Type = TypeRational, Count = (uint)values.Length, Blob = bytes });
        }

        RawLong(TagNewSubfileType, 0);
        RawLong(TagImageWidth, (uint)width);
        RawLong(TagImageLength, (uint)height);
        RawShort(TagBitsPerSample, 16);
        RawShort(TagCompression, 1);                    // uncompressed
        RawShort(TagPhotometricInterpretation, 32803);  // CFA
        RawShort(TagSamplesPerPixel, 1);
        RawLong(TagRowsPerStrip, (uint)height);
        RawShort(TagPlanarConfiguration, 1);
        RawShorts(TagCFARepeatPatternDim, new ushort[] { 2, 2 });
        rawBlock.Entries.Add(new Entry { Tag = TagCFAPattern, Type = TypeByte, Count = 4, Inline = Pad(dngCfa) });
        RawShorts(TagBlackLevelRepeatDim, new ushort[] { 2, 2 });
        RawShorts(TagBlackLevel, new[] { (ushort)BlackLevelValue, (ushort)BlackLevelValue,
                                         (ushort)BlackLevelValue, (ushort)BlackLevelValue });
        RawLong(TagWhiteLevel, (uint)WhiteLevelValue);

        // ActiveArea: full stored area — replicates ADC's Z8 geometry. The warp/vignette
        // opcodes normalize radius over the active area, so this must match ADC's
        // convention (full 8280x5520) or the correction strength shifts.
        RawLongs(TagActiveArea, new[] { 0u, 0u, (uint)height, (uint)width });

        // DefaultCrop: ADC pairs the full ActiveArea with DefaultScale/Crop tags so the
        // displayed image matches the NEF's framing (Z8: origin 12,8, size 8256x5504).
        // Derived from the largest embedded preview (= camera's active crop), origin
        // centered and CFA-phase-even.
        var previews = originalMetadata?.Previews;
        if (previews is { Count: > 0 })
        {
            var active = previews[^1];
            if (active.Width <= width && active.Height <= height &&
                (active.Width < width || active.Height < height))
            {
                uint ox = (uint)(((width - active.Width) / 2) & ~1);
                uint oy = (uint)(((height - active.Height) / 2) & ~1);
                RawRationals(TagDefaultScale, new[] { (1, 1), (1, 1) });
                RawRationals(TagDefaultCropOrigin, new[] { ((int)ox, 1), ((int)oy, 1) });
                RawRationals(TagDefaultCropSize, new[] { (active.Width, 1), (active.Height, 1) });
            }
        }

        // Strip data location — real entries, values resolved by the layout pass
        rawBlock.Entries.Add(new Entry { Tag = TagStripOffsets, Type = TypeLong, Count = 1, IsPixelData = true });
        RawLong(TagStripByteCounts, (uint)(pixels.Length * 2));

        // ---- lens correction opcodes from Nikon NEFInfo (0xC7D5) ----
        var correction = NikonLensCorrection.Decode(originalMetadata?.NefInfoData);
        if (correction?.Warp != null)
        {
            var opcodes = NikonLensCorrection.SerializeOpcodeList3(correction.Warp);
            rawBlock.Entries.Add(new Entry { Tag = 51022, Type = 7, // OpcodeList3
                Count = (uint)opcodes.Length, Blob = opcodes });
        }
        if (correction?.Vignette != null)
        {
            var opcodes = NikonLensCorrection.SerializeOpcodeList2(correction.Vignette);
            rawBlock.Entries.Add(new Entry { Tag = 51009, Type = 7, // OpcodeList2
                Count = (uint)opcodes.Length, Blob = opcodes });
        }
        rawBlock.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // ================= preview IFDs =================
        static IfdBlock PreviewBlock(TiffIfdExtractor.PreviewInfo p)
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
            return block;
        }

        // ================= IFD0 =================
        // With previews: IFD0 = thumbnail (ADC topology), raw + larger previews in SubIFDs.
        // Without previews: IFD0 = raw directly (still valid DNG).
        var ifd0 = new IfdBlock();
        var subIfdTargets = new List<IfdBlock>();

        if (previews is { Count: > 0 })
        {
            // IFD0 becomes the smallest preview (thumbnail); prepend its image tags
            foreach (var e in PreviewBlock(previews[0]).Entries)
                ifd0.Entries.Add(e);

            // SubIFDs: raw first (like ADC), then the remaining previews largest→smallest
            subIfdTargets.Add(rawBlock);
            for (int i = previews.Count - 1; i >= 1; i--)
                subIfdTargets.Add(PreviewBlock(previews[i]));
        }
        else
        {
            // No previews: IFD0 = raw (move all entries)
            foreach (var e in rawBlock.Entries)
                ifd0.Entries.Add(e);
        }

        void AddAscii(ushort tag, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value + "\0");
            ifd0.Entries.Add(bytes.Length <= 4
                ? new Entry { Tag = tag, Type = TypeAscii, Count = (uint)bytes.Length, Inline = Pad(bytes) }
                : new Entry { Tag = tag, Type = TypeAscii, Count = (uint)bytes.Length, Blob = bytes });
        }
        void AddShort(ushort tag, ushort value) =>
            ifd0.Entries.Add(new Entry { Tag = tag, Type = TypeShort, Count = 1, Inline = Pad(BitConverter.GetBytes(value)) });
        void AddRationals(ushort tag, int type, (int num, int den)[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
            {
                BitConverter.GetBytes(values[i].num).CopyTo(bytes, i * 8);
                BitConverter.GetBytes(values[i].den).CopyTo(bytes, i * 8 + 4);
            }
            ifd0.Entries.Add(new Entry { Tag = tag, Type = (ushort)type, Count = (uint)values.Length, Blob = bytes });
        }

        AddAscii(TagMake, "Nikon");
        AddAscii(TagModel, CameraModelName);

        ifd0.Entries.Add(new Entry { Tag = TagDNGVersion, Type = TypeByte, Count = 4,
            Inline = Pad(new byte[] { 1, 6, 0, 0 }) });
        ifd0.Entries.Add(new Entry { Tag = TagDNGBackwardVersion, Type = TypeByte, Count = 4,
            Inline = Pad(new byte[] { 1, 4, 0, 0 }) });
        AddAscii(TagUniqueCameraModel, CameraModelName);

        var cm = new byte[ColorMatrix1Values.Length * 8];
        for (int i = 0; i < ColorMatrix1Values.Length; i++)
        {
            BitConverter.GetBytes((int)Math.Round(ColorMatrix1Values[i] * 10000)).CopyTo(cm, i * 8);
            BitConverter.GetBytes(10000).CopyTo(cm, i * 8 + 4);
        }
        ifd0.Entries.Add(new Entry { Tag = TagColorMatrix1, Type = TypeSRational,
            Count = (uint)ColorMatrix1Values.Length, Blob = cm });
        AddShort(TagCalibrationIlluminant1, (ushort)CalibrationIlluminant1Value);

        // Rendering parity with Adobe's Z8 defaults (measured from ADC 18.6 output)
        AddRationals(TagBaselineExposure, TypeSRational, new[] { (20, 100) });     // +0.20 EV
        AddRationals(TagBaselineNoise, TypeRational, new[] { (80, 100) });
        AddRationals(TagBaselineSharpness, TypeRational, new[] { (100, 100) });

        // AsShotNeutral: per-shot camera white balance (inverse multipliers, like ADC)
        if (cameraWb is { Length: >= 3 } && cameraWb[0] > 0 && cameraWb[1] > 0 && cameraWb[2] > 0)
        {
            AddRationals(TagAsShotNeutral, TypeRational, new[]
            {
                ((int)Math.Round(1e6 / cameraWb[0]), 1000000),
                ((int)Math.Round(1e6 / cameraWb[1]), 1000000),
                ((int)Math.Round(1e6 / cameraWb[2]), 1000000),
            });
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

        // SubIFDs pointer (raw + previews) — ADC topology
        if (subIfdTargets.Count == 1)
            ifd0.Entries.Add(new Entry { Tag = TagSubIFDs, Type = TypeLong, Count = 1, SubBlock = subIfdTargets[0] });
        else if (subIfdTargets.Count > 1)
            ifd0.Entries.Add(new Entry { Tag = TagSubIFDs, Type = TypeLong,
                Count = (uint)subIfdTargets.Count, SubBlockList = subIfdTargets });

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

        // ---- serialize: single pass directly to file (no full-image intermediate copies) ----
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);
        bw.Write((byte)0x49); bw.Write((byte)0x49); bw.Write((ushort)42); bw.Write((uint)8);

        foreach (var block in orderedBlocks)
        {
            fs.Position = block.Offset;
            bw.Write((ushort)block.Entries.Count);
            foreach (var e in block.Entries)
            {
                bw.Write(e.Tag); bw.Write(e.Type); bw.Write(e.Count);
                var inline = new byte[4]; // TIFF value field is fixed 4 bytes
                if (e.Inline != null) Array.Copy(e.Inline, inline, Math.Min(4, e.Inline.Length));
                bw.Write(inline);
            }
            bw.Write(0u); // next IFD: none (IFD chains are modeled as SubIFDs)
            foreach (var e in block.Entries)
                if (e.Blob != null && e.SubBlockList == null)
                    WriteBlob4Aligned(bw, fs, e.Blob);
        }
        foreach (var e in multiPointers)
            WriteBlob4Aligned(bw, fs, e.Blob!);

        // Pixel data: 14-bit → 16-bit expansion (×4, clamped to u16) streamed straight
        // to the file in bounded 128 KB chunks — replaces the previous three full-image
        // copies (shifted array + pixelBytes + MemoryStream snapshot).
        long pos = fs.Position;
        if (pos < pixelOffset) bw.Write(AlignmentZeros, 0, (int)(pixelOffset - pos));
        else fs.Position = pixelOffset;
        const int ChunkPixels = 1 << 16;
        var chunk = new byte[ChunkPixels * 2];
        for (int i = 0; i < pixels.Length; i += ChunkPixels)
        {
            int n = Math.Min(ChunkPixels, pixels.Length - i);
            var src = pixels.AsSpan(i, n);
            int off = 0;
            for (int k = 0; k < src.Length; k++)
            {
                ushort v = (ushort)Math.Min(src[k] << PixelBitShift, 65535);
                chunk[off++] = (byte)v;
                chunk[off++] = (byte)(v >> 8);
            }
            bw.Write(chunk, 0, n * 2);
        }
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
        int typeSize = TiffConstants.SizeOfType(e.Type);
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
        int es = TiffConstants.SizeOfType(type);
        if (es <= 1 || data.Length % es != 0) return data;
        var r = new byte[data.Length];
        for (int i = 0; i + es <= data.Length; i += es)
            for (int j = 0; j < es; j++)
                r[i + j] = data[i + es - 1 - j];
        return r;
    }

    /// <summary>Writes a blob at the next 4-byte boundary, explicitly zero-filling the
    /// ≤3-byte alignment gap so the emitted bytes are identical to the previous
    /// MemoryStream-based layout (which zero-filled seek gaps implicitly).</summary>
    private static void WriteBlob4Aligned(BinaryWriter bw, FileStream fs, byte[] blob)
    {
        long pos = fs.Position;
        int pad = (int)(((pos + 3) & ~3L) - pos);
        if (pad > 0) bw.Write(AlignmentZeros, 0, pad);
        bw.Write(blob);
    }

    private static byte[] Pad(byte[] b)
    {
        var p = new byte[4];
        Array.Copy(b, p, b.Length);
        return p;
    }

    private static uint Align(uint v, int alignment) => (uint)((v + alignment - 1) & ~(alignment - 1));
}
