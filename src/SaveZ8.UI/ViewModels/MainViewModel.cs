using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveZ8.Core;

namespace SaveZ8.UI.ViewModels;

public partial class FileItemViewModel : ObservableObject
{
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _status = "待处理";
    [ObservableProperty] private bool _fixApplied;
    [ObservableProperty] private long _pixelsFixed;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _outputPath = "";
}

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<FileItemViewModel> _files = new();
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "拖放 NEF 文件到此处或点击添加";
    [ObservableProperty] private int _fixModeIndex; // 0=Notch, 1=SaveZ8, 2=Disabled
    [ObservableProperty] private string _libRawVersion = "";

    private CancellationTokenSource? _cts;
    private readonly NefProcessor _processor = new();

    public string[] FixModeNames { get; } = { "Notch（推荐，不误伤）", "Save Z8 原版", "禁用" };

    // File picking is handled by the View (Avalonia StorageProvider)

    [RelayCommand]
    private void RemoveSelected(FileItemViewModel? item)
    {
        if (item != null)
            Files.Remove(item);
    }

    [RelayCommand]
    private void ClearAll()
    {
        Files.Clear();
        StatusText = "已清空文件列表";
    }

    [RelayCommand]
    private async Task ProcessAllAsync()
    {
        if (IsProcessing || Files.Count == 0) return;
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            StatusText = "请先设置输出目录";
            return;
        }

        IsProcessing = true;
        Progress = 0;
        _cts = new CancellationTokenSource();

        _processor.FixMode = FixModeIndex switch
        {
            0 => BlackLevelFixer.FixMode.Notch,
            1 => BlackLevelFixer.FixMode.SaveZ8,
            _ => BlackLevelFixer.FixMode.Disabled
        };

        var items = Files.Where(f => f.Status == "待处理" || f.Status == "错误").ToList();
        int total = items.Count;
        int done = 0;

        try
        {
            foreach (var item in items)
            {
                _cts.Token.ThrowIfCancellationRequested();
                StatusText = $"处理中 ({done + 1}/{total}): {item.FileName}";
                item.Status = "处理中...";

                await Task.Run(() =>
                {
                    var outputPath = Path.Combine(OutputDirectory,
                        Path.GetFileNameWithoutExtension(item.FileName) + "_fixed.dng");
                    var result = _processor.Process(item.FilePath, outputPath);

                    item.FixApplied = result.FixApplied;
                    item.PixelsFixed = result.PixelsFixed;
                    item.OutputPath = result.OutputPath;
                    item.Error = result.Error ?? "";
                    item.Status = result.Error != null ? "错误" :
                        (result.FixApplied ? $"已修复 ({result.PixelsFixed:N0} 像素)" : "无需修复");
                }, _cts.Token);

                done++;
                Progress = (double)done / total * 100;
            }

            StatusText = $"完成: {done}/{total} 个文件";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    public void AddFilesFromPaths(string[] paths)
    {
        foreach (var path in paths)
        {
            if (path.EndsWith(".nef", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                Files.Add(new FileItemViewModel
                {
                    FileName = Path.GetFileName(path),
                    FilePath = path,
                    Status = "待处理"
                });
            }
        }
        StatusText = $"已加载 {Files.Count} 个文件";
    }
}
