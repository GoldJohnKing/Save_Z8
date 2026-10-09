namespace SaveZ8.Core.Models;

/// <summary>
/// Represents a decoded NEF image with all metadata needed for fix + export.
/// </summary>
public class NefImage
{
    public required ushort[] Pixels { get; set; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int BlackLevel { get; init; }
    public required byte[] CfaPattern { get; init; }
    public required TiffIfdExtractor.ExtractedMetadata OriginalMetadata { get; init; }
}

/// <summary>
/// Result of processing a single NEF file.
/// </summary>
public class ProcessingResult
{
    public required string OutputPath { get; init; }
    public required bool FixApplied { get; init; }
    public required long PixelsFixed { get; init; }
    public string? Error { get; init; }
}
