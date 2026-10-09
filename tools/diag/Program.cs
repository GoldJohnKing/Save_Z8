using System;
using System.IO;
using System.Runtime.InteropServices;
using SaveZ8.Core;

class Diag
{
    [DllImport("kernel32", SetLastError=true)]
    static extern IntPtr LoadLibrary(string name);

    [DllImport("libraw_wrapper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr lr_version();

    [DllImport("libraw_wrapper", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    static extern IntPtr lr_open(string path, out int err);

    [DllImport("libraw_wrapper", CallingConvention = CallingConvention.Cdecl)]
    static extern int lr_get_width(IntPtr h);

    [DllImport("libraw_wrapper", CallingConvention = CallingConvention.Cdecl)]
    static extern int lr_get_height(IntPtr h);

    [DllImport("libraw_wrapper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr lr_get_raw_image(IntPtr h);

    [DllImport("libraw_wrapper", CallingConvention = CallingConvention.Cdecl)]
    static extern void lr_close(IntPtr h);

    static int Main(string[] args)
    {
        Console.WriteLine("=== Save Z8 Diagnostic ===");
        Console.WriteLine($"DLL dir: {AppContext.BaseDirectory}");

        // Step 0: Check DLLs
        foreach (var dll in new[] { "libraw.dll", "libraw_wrapper.dll" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, dll);
            Console.WriteLine($"  {dll}: {(File.Exists(path) ? $"OK ({new FileInfo(path).Length} bytes)" : "MISSING!")}");
        }
        var h1 = LoadLibrary("libraw.dll");
        var h2 = LoadLibrary("libraw_wrapper.dll");
        Console.WriteLine($"  LoadLibrary: libraw={h1 != IntPtr.Zero}, wrapper={h2 != IntPtr.Zero}");
        if (h2 == IntPtr.Zero) { Console.WriteLine("  FATAL: Cannot load wrapper."); return 1; }

        try { Console.WriteLine($"  LibRaw: {Marshal.PtrToStringAnsi(lr_version())}"); }
        catch (Exception ex) { Console.WriteLine($"  lr_version: {ex.Message}"); }

        if (args.Length < 1) { Console.WriteLine("\nUsage: diag <nef-file>"); return 0; }

        var nefPath = args[0];
        var outDir = args.Length > 1 ? args[1] : Path.GetDirectoryName(nefPath)!;
        var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(nefPath) + "_diag.dng");
        Console.WriteLine($"\nFile: {nefPath} ({new FileInfo(nefPath).Length} bytes)\n");

        // Step 1: Decode via LibRawNative (same code path as main app)
        Console.Write("[1/4] LibRawNative.Decode... ");
        ushort[] pixels; int width, height, black, white; byte[] cfa; float[] camMul;
        try
        {
            (pixels, width, height, black, white, cfa, camMul) = SaveZ8.Core.Native.LibRawNative.Decode(nefPath);
            Console.WriteLine($"OK ({width}x{height}, BL={black}, WL={white}, CFA=[{cfa[0]},{cfa[1]},{cfa[2]},{cfa[3]}])");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED:\n  {ex.Message}\n  {ex.StackTrace}");
            return 1;
        }

        // Step 2: TIFF metadata
        Console.Write("[2/4] TiffIfdExtractor.Extract... ");
        TiffIfdExtractor.ExtractedMetadata metadata;
        try
        {
            metadata = TiffIfdExtractor.Extract(nefPath);
            Console.WriteLine($"OK ({metadata.AllIfds.Count} IFDs)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED:\n  {ex.Message}\n  {ex.StackTrace}");
            return 1;
        }

        // Step 3: Fix
        Console.Write("[3/4] BlackLevelFixer.Fix... ");
        try
        {
            var r = BlackLevelFixer.Fix(pixels, width, height,
                black > 0 ? black : 1008);
            Console.WriteLine(r.Applied ? $"APPLIED ({r.PixelsFixed:N0} px)" : "SKIP (no notch)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED:\n  {ex.Message}\n  {ex.StackTrace}");
            return 1;
        }

        // Step 4: DNG write
        Console.Write("[4/4] DngWriter.Write... ");
        try
        {
            DngWriter.Write(outPath, pixels, width, height, cfa, metadata, cameraWb: camMul);
            Console.WriteLine($"OK ({new FileInfo(outPath).Length / 1024 / 1024:F1} MB)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED:\n  {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            Console.WriteLine($"  Stack: {ex.StackTrace?.Split('\n')[0]}");
            return 1;
        }

        Console.WriteLine("\nALL 4 STEPS PASSED");
        return 0;
    }
}
