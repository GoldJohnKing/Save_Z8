using SaveZ8.Core.Models;

namespace SaveZ8.Core;

/// <summary>
/// High-level pipeline: decode NEF → fix → write DNG.
/// </summary>
public class NefProcessor
{
    public BlackLevelFixer.FixMode FixMode { get; set; } = BlackLevelFixer.FixMode.Notch;
    public DngWriter.DngOptions DngOptions { get; set; } = new();

    /// <summary>
    /// Process a single NEF file: decode, fix, and export as DNG.
    /// </summary>
    public ProcessingResult Process(string nefPath, string outputPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Decode
            var (pixels, width, height, black, white, cfa) = Native.LibRawNative.Decode(nefPath);
            var metadata = TiffIfdExtractor.Extract(nefPath);
            var image = new Models.NefImage
            {
                SourcePath = nefPath,
                Pixels = pixels,
                Width = width,
                Height = height,
                BlackLevel = black > 0 ? black : 1008,
                WhiteLevel = white > 0 ? white : 65019,
                CfaPattern = cfa,
                OriginalMetadata = metadata
            };

            // Fix
            var fixResult = BlackLevelFixer.Fix(image.Pixels, image.Width, image.Height,
                FixMode, image.BlackLevel);

            // Export
            DngWriter.Write(outputPath, image.Pixels, image.Width, image.Height,
                image.CfaPattern, image.OriginalMetadata, DngOptions);

            sw.Stop();
            return new ProcessingResult
            {
                SourcePath = nefPath,
                OutputPath = outputPath,
                FixApplied = fixResult.Applied,
                PixelsFixed = fixResult.PixelsFixed,
                ProcessingTime = sw.Elapsed,
                FixDetail = fixResult
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProcessingResult
            {
                SourcePath = nefPath,
                OutputPath = outputPath,
                FixApplied = false,
                PixelsFixed = 0,
                ProcessingTime = sw.Elapsed,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Process multiple files with progress reporting.
    /// </summary>
    public IEnumerable<ProcessingResult> ProcessBatch(
        IEnumerable<string> nefFiles, string outputDirectory,
        IProgress<(int current, int total, string file)>? progress = null,
        CancellationToken cancellation = default)
    {
        var files = nefFiles.ToList();
        for (int i = 0; i < files.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report((i + 1, files.Count, files[i]));

            var name = Path.GetFileNameWithoutExtension(files[i]);
            var outputPath = Path.Combine(outputDirectory, name + "_fixed.dng");
            yield return Process(files[i], outputPath);
        }
    }
}
