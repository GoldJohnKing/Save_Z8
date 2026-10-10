using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenOutput))]
    private string _outputPath = "";

    public bool CanOpenOutput => !string.IsNullOrEmpty(OutputPath);

    // ---- UI 状态派生（Status 是唯一事实来源；仅供视图分层着色，不参与处理逻辑）----
    public bool IsPending => Status == "待处理";
    public bool IsProcessing => Status.StartsWith("处理中", StringComparison.Ordinal);
    public bool IsFixed => Status.StartsWith("已修复", StringComparison.Ordinal);
    public bool IsNotNeeded => Status == "无需修复";
    public bool IsError => Status.StartsWith("错误", StringComparison.Ordinal);

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsFixed));
        OnPropertyChanged(nameof(IsNotNeeded));
        OnPropertyChanged(nameof(IsError));
    }

    /// <summary>Open Explorer with the output file pre-selected.</summary>
    [RelayCommand]
    private void OpenOutput()
    {
        if (string.IsNullOrEmpty(OutputPath) || !File.Exists(OutputPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{OutputPath}\"")
        {
            UseShellExecute = true
        });
    }
}

public partial class MainViewModel : ObservableObject
{
    /// <summary>Subfolder created next to each NEF; fixed DNGs are written here.</summary>
    public const string OutputFolderName = "Save_Z8";

    [ObservableProperty] private ObservableCollection<FileItemViewModel> _files = new();
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "拖放 NEF 文件到此处，或点击「添加 NEF 文件」";
    [ObservableProperty] private string _libRawVersion = "";

    // ---- UI 状态 ----
    [ObservableProperty] private FileItemViewModel? _selectedFile;
    [ObservableProperty] private bool _isDragOver;

    /// <summary>本软件版本号（如 v0.1.0），启动时读程序集版本并缓存。</summary>
    [ObservableProperty] private string _appVersion = ReadAppVersion();

    private static string ReadAppVersion()
    {
        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "";
        }
    }

    private CancellationTokenSource? _cts;
    private readonly NefProcessor _processor = new();

    public MainViewModel()
    {
        // 列表增删 → 刷新空态/计数总览，并维护行级 PropertyChanged 订阅
        Files.CollectionChanged += OnFilesCollectionChanged;
    }

    // ---- 总览计数（UI 扫读用）----
    public bool HasFiles => Files.Count > 0;
    public int PendingCount => Files.Count(f => f.IsPending);
    public int FixedCount => Files.Count(f => f.IsFixed);
    public int NotNeededCount => Files.Count(f => f.IsNotNeeded);
    public int ErrorCount => Files.Count(f => f.IsError);

    private void RefreshCounters()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(FixedCount));
        OnPropertyChanged(nameof(NotNeededCount));
        OnPropertyChanged(nameof(ErrorCount));
    }

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (FileItemViewModel item in e.NewItems)
                item.PropertyChanged += OnFileItemPropertyChanged;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (FileItemViewModel item in e.OldItems)
                item.PropertyChanged -= OnFileItemPropertyChanged;
        }

        OnPropertyChanged(nameof(HasFiles));
        RefreshCounters();
    }

    private void OnFileItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileItemViewModel.Status))
            RefreshCounters();
    }

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

        IsProcessing = true;
        Progress = 0;
        _cts = new CancellationTokenSource();

        var items = Files.Where(f => f.Status == "待处理" || f.Status.StartsWith("错误")).ToList();
        int total = items.Count;
        int done = 0;

        try
        {
            foreach (var item in items)
            {
                _cts.Token.ThrowIfCancellationRequested();
                StatusText = $"处理中 ({done + 1}/{total}): {item.FileName}";
                item.Status = "处理中…";

                await Task.Run(() =>
                {
                    // Output: <NEF folder>\Save_Z8\<name>_fixed.dng
                    var outputDir = Path.Combine(
                        Path.GetDirectoryName(item.FilePath) ?? ".", OutputFolderName);
                    var outputPath = Path.Combine(outputDir,
                        Path.GetFileNameWithoutExtension(item.FileName) + "_fixed.dng");
                    try
                    {
                        Directory.CreateDirectory(outputDir);

                        var result = _processor.Process(item.FilePath, outputPath);
                        item.FixApplied = result.FixApplied;
                        item.PixelsFixed = result.PixelsFixed;
                        item.OutputPath = result.Error == null ? result.OutputPath : "";
                        item.Error = result.Error ?? "";
                        item.Status = result.Error != null ? $"错误: {TruncateError(result.Error)}" :
                            (result.FixApplied ? "已修复" : "无需修复");
                    }
                    catch (Exception ex)
                    {
                        item.OutputPath = "";
                        item.Error = ex.Message;
                        item.Status = $"错误: {TruncateError(ex.Message)}";
                    }
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

    private static string TruncateError(string msg) =>
        string.IsNullOrEmpty(msg) ? "" : (msg.Length > 150 ? msg[..150] + "…" : msg);
}
