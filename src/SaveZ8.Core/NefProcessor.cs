using SaveZ8.Core.Models;

namespace SaveZ8.Core;

/// <summary>
/// High-level pipeline: decode NEF → fix → write DNG.
/// </summary>
public class NefProcessor
{
    /// <summary>
    /// Write the DNG with lossless-JPEG compressed raw data (Compression=7,
    /// ~45% smaller). Default on; the uncompressed path stays available as the
    /// byte-exact regression baseline.
    /// </summary>
    public bool CompressOutput { get; set; } = true;

    /// <summary>
    /// Process a single NEF file: decode, fix, and export as DNG.
    /// </summary>
    public ProcessingResult Process(string nefPath, string outputPath)
    {
        try
        {
            // Decode (LibRaw's white level is unused: the DNG carries Adobe's fixed 63568)
            var (pixels, width, height, black, _, cfa, camMul) = Native.LibRawNative.Decode(nefPath);
            var metadata = TiffIfdExtractor.Extract(nefPath);
            var image = new NefImage
            {
                Pixels = pixels,
                Width = width,
                Height = height,
                BlackLevel = black > 0 ? black : BlackLevelFixer.BlackLevelDefault,
                CfaPattern = cfa,
                OriginalMetadata = metadata
            };

            // Fix (notch criterion — always on)
            var fixResult = BlackLevelFixer.Fix(image.Pixels, image.Width, image.Height,
                image.BlackLevel);

            // Export
            DngWriter.Write(outputPath, image.Pixels, image.Width, image.Height,
                image.CfaPattern, image.OriginalMetadata, cameraWb: camMul,
                compress: CompressOutput);

            return new ProcessingResult
            {
                OutputPath = outputPath,
                FixApplied = fixResult.Applied,
                PixelsFixed = fixResult.PixelsFixed
            };
        }
        catch (Exception ex)
        {
            return new ProcessingResult
            {
                OutputPath = outputPath,
                FixApplied = false,
                PixelsFixed = 0,
                Error = ex.Message
            };
        }
    }
}
