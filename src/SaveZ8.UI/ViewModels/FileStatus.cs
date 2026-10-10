using System;

namespace SaveZ8.UI.ViewModels;

/// <summary>
/// FileItemViewModel.Status 的取值与判据（Status 字符串是行状态的唯一事实来源，
/// 派生 IsXxx 属性与总览计数都经此处判据，勿在别处散写字面量）。
/// 注意：带后缀的完整句子（进度、错误详情）用 *Format 模板拼接，
/// 判据用前缀/整串比较，与既有语义一致。
/// </summary>
internal static class FileStatus
{
    public const string Pending = "待处理";

    /// <summary>「处理中」前缀：判据按前缀匹配（兼容任何处理中文案变体）。</summary>
    private const string ProcessingPrefix = "处理中";

    /// <summary>行内处理中状态（省略号为 U+2026，与总览进度句区分）。</summary>
    public const string Processing = ProcessingPrefix + "…";

    public const string Fixed = "已修复";
    public const string NotNeeded = "无需修复";
    public const string Error = "错误";
    public const string Cancelled = "已取消";

    /// <summary>总览进度句模板（StatusText 专用）：arg0=当前序号, arg1=总数, arg2=文件名。</summary>
    public const string ProcessingProgressFormat = "处理中 ({0}/{1}): {2}";

    /// <summary>错误句模板：arg0=错误消息（调用方自行截断）。</summary>
    public const string ErrorFormat = "错误: {0}";

    public static bool IsPending(string status) => status == Pending;
    public static bool IsProcessing(string status) => status.StartsWith(ProcessingPrefix, StringComparison.Ordinal);
    public static bool IsFixed(string status) => status.StartsWith(Fixed, StringComparison.Ordinal);
    public static bool IsNotNeeded(string status) => status == NotNeeded;
    public static bool IsError(string status) => status.StartsWith(Error, StringComparison.Ordinal);
}
