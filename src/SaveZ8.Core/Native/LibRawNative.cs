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
    private static extern int lr_get_filters(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_cfa_pattern(IntPtr handle, byte[] outPattern);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void lr_close(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_version();

    public static string GetVersion() => "LibRaw (需要 libraw_wrapper.dll)";

    /// <summary>
    /// Decode a NEF file into raw mosaic pixels + metadata.
    /// Returns (pixels, width, height, blackLevel, whiteLevel, cfaPattern).
    /// </summary>
    public static (ushort[] pixels, int width, int height, int blackLevel, int whiteLevel, byte[] cfaPattern)
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

            return (pixels, width, height, black, white, cfa);
        }
        finally
        {
            lr_close(handle);
        }
    }

    private static string LibRawErrorMessage(int code) => code switch
    {
        -10000 => "文件不存在",
        -10001 => "文件读取失败",
        -10002 => "格式不支持",
        -10003 => "文件损坏",
        -100009 => "RAW格式不支持(-100009)",
        -100010 => "请求不存在的图像",
        -100011 => "调用顺序错误",
        _ => $"LibRaw错误({code})"
    };
}
