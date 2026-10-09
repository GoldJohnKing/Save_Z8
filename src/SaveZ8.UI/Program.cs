using Avalonia;
using System;

namespace SaveZ8.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless pipeline self-test (also verifies native DLLs load from a
        // single-file bundle): SaveZ8.UI.exe --selftest <file.nef>
        if (args.Length == 2 && args[0] == "--selftest")
        {
            var result = new Core.NefProcessor().Process(args[1],
                System.IO.Path.ChangeExtension(args[1], ".selftest.dng"));
            Console.WriteLine(result.Error != null
                ? $"SELFTEST FAIL: {result.Error}"
                : $"SELFTEST OK: fixed={result.FixApplied} pixels={result.PixelsFixed:N0} -> {result.OutputPath}");
            Environment.Exit(result.Error != null ? 1 : 0);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
