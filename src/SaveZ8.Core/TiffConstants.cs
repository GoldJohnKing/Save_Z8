namespace SaveZ8.Core;

/// <summary>
/// TIFF constants shared by <see cref="DngWriter"/> and <see cref="TiffIfdExtractor"/>.
/// </summary>
internal static class TiffConstants
{
    /// <summary>Byte size per TIFF field type (index = type code; 0 = invalid).</summary>
    internal static readonly int[] TypeSizes = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8 };

    /// <summary>Byte size of a TIFF field type, falling back to 1 for unknown codes.</summary>
    internal static int SizeOfType(ushort type) => type < TypeSizes.Length ? TypeSizes[type] : 1;
}
