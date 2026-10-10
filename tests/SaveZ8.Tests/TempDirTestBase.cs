using System;

namespace SaveZ8.Tests;

/// <summary>
/// 共享样板：为每个测试实例分配唯一临时目录，Dispose 时递归删除。
/// 子类通过 base(prefix) 指定目录名前缀（用于区分测试套件）。
/// </summary>
public abstract class TempDirTestBase : IDisposable
{
    protected readonly string _tmpDir;

    protected TempDirTestBase(string prefix)
    {
        _tmpDir = Path.Combine(Path.GetTempPath(),
            prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }
}
