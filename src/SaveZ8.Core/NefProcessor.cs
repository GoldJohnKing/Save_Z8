using SaveZ8.Core.Models;

namespace SaveZ8.Core;

/// <summary>
/// High-level pipeline: decode NEF → fix → write DNG.
/// </summary>
public class NefProcessor
{
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
                BlackLevel = black > 0 ? black : 1008,
                CfaPattern = cfa,
                OriginalMetadata = metadata
            };

            // Fix (notch criterion — always on)
            var fixResult = BlackLevelFixer.Fix(image.Pixels, image.Width, image.Height,
                image.BlackLevel);

            // Export
            DngWriter.Write(outputPath, image.Pixels, image.Width, image.Height,
                image.CfaPattern, image.OriginalMetadata, cameraWb: camMul);

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
