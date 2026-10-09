using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SaveZ8.UI.ViewModels;

namespace SaveZ8.UI.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        // Attach drag-drop handlers in code-behind (Avalonia 11 compatible)
        AddHandler(Avalonia.Input.DragDrop.DropEvent, OnDropHandler);
        AddHandler(Avalonia.Input.DragDrop.DragOverEvent, OnDragOverHandler);
    }

    private void OnDragOverHandler(object? sender, Avalonia.Input.DragEventArgs e)
    {
        e.DragEffects = Avalonia.Input.DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDropHandler(object? sender, Avalonia.Input.DragEventArgs e)
    {
        e.Handled = true;
        // For now, open file picker as fallback (drag-drop file extraction to be refined)
        _ = OpenFilePickerAsync();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            ViewModel.LibRawVersion = Core.Native.LibRawNative.GetVersion();
        }
        catch
        {
            ViewModel.LibRawVersion = "未加载";
        }
    }

    private async void OnBrowseFiles(object? sender, RoutedEventArgs e)
    {
        await OpenFilePickerAsync();
    }

    private async System.Threading.Tasks.Task OpenFilePickerAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 NEF 文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Nikon RAW") { Patterns = new[] { "*.nef" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null && p.EndsWith(".nef", StringComparison.OrdinalIgnoreCase))
            .Select(p => p!)
            .ToArray();

        if (paths.Length > 0)
            ViewModel.AddFilesFromPaths(paths);
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择输出目录",
            AllowMultiple = false
        });
        var path = folder.FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
            ViewModel.OutputDirectory = path;
    }
}
