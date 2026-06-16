using System.Diagnostics;
using System.IO;

namespace BpmMeasurer;

/// <summary>
/// 轻量级加载耗时日志。记录每个环节的开始/结束时间及耗时
/// 输出到 bpm_load_timing.log 文件。
/// 所有写操作是线程安全的。
/// </summary>
public static class LoadTimingLogger
{
    /// <summary>设为 false 关闭所有日志输出（文件写入均跳过）。</summary>
    public static bool Enabled = false;

    private static readonly Stopwatch Watch = new();
    private static readonly object Lock = new();
    private static string? _logPath;
    private static string? _fileName;

    /// <summary>
    /// 开始一次新的加载计时。如已有进行中的计时会被覆盖。
    /// </summary>
    public static void Begin(string filePath)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            _fileName = Path.GetFileName(filePath);
            _logPath = Path.Combine(
                AppContext.BaseDirectory, "bpm_load_timing.log");
            Watch.Restart();

            var header = $"━━━ {_fileName} ━━━";
            LogLine(header);
            LogLine($"Start: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            LogLine($"File : {filePath}");
        }
    }

    /// <summary>
    /// 记录一个环节的耗时。调用时以 Stopwatch 当前已过时间为该环节结束时间，
    /// 上一次 Phase 记录的时间为开始时间。
    /// </summary>
    public static void Phase(string phaseName)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            double ms = Watch.Elapsed.TotalMilliseconds;
            LogLine($"  [{ms,10:F3} ms]  {phaseName}");
        }
    }

    /// <summary>
    /// 结束计时并输出总耗时。
    /// </summary>
    public static void End(string? note = null)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            double ms = Watch.Elapsed.TotalMilliseconds;
            LogLine($"  [{ms,10:F3} ms]  Total");
            if (!string.IsNullOrEmpty(note))
                LogLine($"  → {note}");
            LogLine("");
        }
    }

    private static void LogLine(string text)
    {
        if (_logPath == null) return;
        try
        {
            File.AppendAllText(_logPath, text + "\n");
        }
        catch
        {
            // 静默丢弃文件写入异常，不影响主流程
        }
    }
}
