using System;
using System.Runtime.InteropServices;

namespace SaveZ8.Core.Native;

/// <summary>
/// P/Invoke wrapper for libraw_wrapper.dll (native LibRaw C wrapper).
/// Place libraw_wrapper.dll and libraw.dll in the application output directory.
/// </summary>
public static class LibRawNative
{
    public static string GetVersion() => "LibRaw (需要 libraw_wrapper.dll)";
    private const string DllName = "libraw_wrapper";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_open(string filepath, out int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_width(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_height(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_get_raw_image(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_black_level(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_white_level(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_filters(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_cfa_pattern(IntPtr handle, byte[] outPattern);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void lr_close(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_version();

    public static unsafe (ushort[] pixels, int width, int height, int blackLevel, int whiteLevel, byte[] cfaPattern) Decode(string filepath)
    {
        int error;
        IntPtr handle = lr_open(filepath, out error);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"LibRaw failed to open/decode '{filepath}': error {error}");

        try
        {
            int width = lr_get_width(handle);
            int height = lr_get_height(handle);
            int black = lr_get_black_level(handle);
            int white = lr_get_white_level(handle);

            var cfa = new byte[4];
            lr_get_cfa_pattern(handle, cfa);

            IntPtr rawPtr = lr_get_raw_image(handle);
            int count = width * height;
            var pixels = new ushort[count];
            Marshal.Copy(rawPtr, (short[])(object)Array.ConvertAll(pixels, x => (short)x), 0, count);
            // The above is wrong for ushort. Use unsafe:
            fixed (ushort* dst = pixels)
            {
                Buffer.MemoryCopy((void*)rawPtr, dst, count * 2, count * 2);
            }

            return (pixels, width, height, black, white, cfa);
        }
        finally
        {
            lr_close(handle);
        }
    }
}
