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

        // UI smoke: open the window and auto-import files after layout — exercises
        // row instantiation (DataTemplate build) without manual interaction:
        // SaveZ8.UI.exe --ui-smoke <file.nef> [...]
        if (args.Length >= 2 && args[0] == "--ui-smoke")
        {
            var files = args[1..];
            App.OnMainWindowOpened += w =>
            {
                if (w.DataContext is ViewModels.MainViewModel vm)
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        vm.AddFilesFromPaths(files);
                        Console.WriteLine($"UI-SMOKE imported {files.Length} file(s), rows={vm.Files.Count}");
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
            };
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
