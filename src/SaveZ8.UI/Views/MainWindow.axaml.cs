using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Semi.Avalonia;
using SaveZ8.UI.ViewModels;

namespace SaveZ8.UI.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private bool _isDark = true;

    // 拖动经过子元素时 DragLeave/DragOver 的触发次序不稳定，
    // 用「DragOver 持续续期 + 计时器到期收敛」保证悬停遮罩可靠消隐。
    private readonly DispatcherTimer _dragHideTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };

    public MainWindow()
    {
        InitializeComponent();
        // Drop target opt-in (Avalonia defaults to false → OLE reports no
        // effects → forbidden cursor). Required for Explorer file drops.
        DragDrop.SetAllowDrop(this, true);
        // Drag-drop handlers attached in code-behind (Avalonia 11 compatible)
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _dragHideTimer.Tick += (_, _) =>
        {
            _dragHideTimer.Stop();
            ViewModel.IsDragOver = false;
        };
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

        // 深色为默认主题（Aquatic 为 Semi 官方深色方案）
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = SemiTheme.Aquatic;
        ApplyThemeState(dark: true);
    }

    // ---------- 拖放 ----------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        ViewModel.IsDragOver = true;
        _dragHideTimer.Stop();
        _dragHideTimer.Start();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragHideTimer.Stop();
        ViewModel.IsDragOver = false;
        AddDroppedFiles(e);
    }

    private void AddDroppedFiles(DragEventArgs e)
    {
        var paths = e.Data.GetFiles()?
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p) && p.EndsWith(".nef", StringComparison.OrdinalIgnoreCase))
            .Select(p => p!)
            .ToArray();

        if (paths.Length > 0)
            ViewModel.AddFilesFromPaths(paths);
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

    // ---------- 列表选中 / 详情面板 ----------

    private void OnFileRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: FileItemViewModel item })
            ViewModel.SelectedFile = item;
    }

    private void OnCloseDetail(object? sender, RoutedEventArgs e)
    {
        ViewModel.SelectedFile = null;
    }

    private void OnRemoveDetailFile(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFile is { } item)
            ViewModel.RemoveSelectedCommand.Execute(item);
    }

    // ---------- 主题切换 ----------

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is null) return;
        _isDark = !_isDark;
        Application.Current.RequestedThemeVariant = _isDark ? SemiTheme.Aquatic : SemiTheme.Desert;
        ApplyThemeState(_isDark);
    }

    private void ApplyThemeState(bool dark)
    {
        ThemeMoonIcon.IsVisible = dark;
        ThemeSunIcon.IsVisible = !dark;
        ToolTip.SetTip(ThemeButton, dark ? "切换到浅色主题" : "切换到深色主题");
    }
}
