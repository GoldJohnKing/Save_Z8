using System;
using System.Runtime.InteropServices;

namespace SaveZ8.Core.Native;

/// <summary>
/// P/Invoke wrapper for libraw_wrapper.dll (native LibRaw C wrapper).
/// </summary>
public static class LibRawNative
{
    private const string DllName = "libraw_wrapper";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
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
    private static extern int lr_get_cfa_pattern(IntPtr handle, byte[] outPattern);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_cam_mul(IntPtr handle, float[] out4);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void lr_close(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_version();

    /// <summary>Native LibRaw version string (e.g. "0.21.3-Release"), shown in the UI footer.</summary>
    public static string GetVersion()
        => Marshal.PtrToStringAnsi(lr_version()) ?? "LibRaw (版本未知)";

    /// <summary>
    /// Decode a NEF file into raw mosaic pixels + metadata.
    /// Returns (pixels, width, height, blackLevel, whiteLevel, cfaPattern, cameraWb).
    /// cameraWb = as-shot white balance multipliers (R,G1,B,G2) from LibRaw cam_mul.
    /// </summary>
    public static (ushort[] pixels, int width, int height, int blackLevel, int whiteLevel, byte[] cfaPattern, float[] cameraWb)
        Decode(string filepath)
    {
        int error;
        IntPtr handle = lr_open(filepath, out error);
        if (handle == IntPtr.Zero)
        {
            string msg = error switch
            {
                -1 => "内存分配失败",
                -2 => "raw_image 为空（不支持的格式）",
                _ => LibRawErrorMessage(error)
            };
            throw new InvalidOperationException($"LibRaw 打开/解码失败: {msg} (code={error}, file={filepath})");
        }

        try
        {
            int width = lr_get_width(handle);
            int height = lr_get_height(handle);
            int black = lr_get_black_level(handle);
            int white = lr_get_white_level(handle);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"无效尺寸: {width}×{height}");

            var cfa = new byte[4];
            lr_get_cfa_pattern(handle, cfa);

            var camMul = new float[4];
            lr_get_cam_mul(handle, camMul);

            IntPtr rawPtr = lr_get_raw_image(handle);
            if (rawPtr == IntPtr.Zero)
                throw new InvalidOperationException("raw_image 指针为空");

            long count = (long)width * height;
            var pixels = new ushort[count];

            // Copy native u16 data to managed array
            unsafe
            {
                fixed (ushort* dst = pixels)
                {
                    Buffer.MemoryCopy((void*)rawPtr, dst, count * 2, count * 2);
                }
            }

            return (pixels, width, height, black, white, cfa, camMul);
        }
        finally
        {
            lr_close(handle);
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_strerror(int code);

    /// <summary>Surfaces LibRaw's own (verbatim) error text for a return code.</summary>
    private static string LibRawErrorMessage(int code)
    {
        string? native = Marshal.PtrToStringAnsi(lr_strerror(code));
        return string.IsNullOrEmpty(native) ? $"LibRaw错误({code})" : $"LibRaw {native} ({code})";
    }
}
