using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace BpmMeasurer;

/// <summary>
/// 调试日志工具：正常运行时不产生任何文件。
/// 所有日志先写入内存缓冲区；仅当崩溃（LogCrash）时才将缓冲区 + 异常详情
/// 一次性写入 exe 所在目录的 crash_*.txt。
/// </summary>
public static class DebugLog
{
    private static readonly string LogDir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly object Lock = new();
    private static readonly List<string> Buffer = new();
    private const int MaxBufferLines = 2000;

    /// <summary>
    /// 写入一条带时间戳的日志行到内存缓冲区（不写盘）。线程安全。
    /// </summary>
    public static void Log(string message,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var now = DateTime.Now;
        string shortFile = Path.GetFileName(file);
        string lineText = $"{now:HH:mm:ss.fff} [{shortFile}:{line}] {message}";

        lock (Lock)
        {
            // 容量保护：超出上限时丢弃最旧条目
            if (Buffer.Count >= MaxBufferLines)
                Buffer.RemoveAt(0);

            Buffer.Add(lineText);
        }
    }

    /// <summary>
    /// 写入崩溃日志（含内存缓冲区历史 + 异常详情和堆栈）。
    /// 写入失败时回退到 Debug.WriteLine。
    /// </summary>
    public static void LogCrash(string source, Exception ex)
    {
        // 先将崩溃摘要追加到缓冲区
        Log($"[CRASH] source={source} type={ex.GetType().FullName} msg={ex.Message}");

        var now = DateTime.Now;
        string fileName = $"crash_{now:yyyyMMdd_HHmmss}.txt";
        string fullPath = Path.Combine(LogDir, fileName);

        var lines = new List<string>();

        // ── 1. 内存中的日志历史 ──
        lock (Lock)
        {
            if (Buffer.Count > 0)
            {
                lines.Add("=== Log History ===");
                lines.AddRange(Buffer);
                lines.Add("");
            }
        }

        // ── 2. 异常详情 ──
        lines.Add("=== Crash Report ===");
        lines.Add($"Crash Time: {now:yyyy-MM-dd HH:mm:ss.fff}");
        lines.Add($"Source: {source}");
        lines.Add($"Exception Type: {ex.GetType().FullName}");
        lines.Add($"Message: {ex.Message}");
        lines.Add($"Stack Trace:");
        lines.Add(ex.StackTrace ?? "(null)");

        // 递归打印 InnerException
        var inner = ex.InnerException;
        int level = 1;
        while (inner != null)
        {
            lines.Add($"--- InnerException L{level} ---");
            lines.Add($"Type: {inner.GetType().FullName}");
            lines.Add($"Message: {inner.Message}");
            lines.Add($"Stack Trace:");
            lines.Add(inner.StackTrace ?? "(null)");
            inner = inner.InnerException;
            level++;
        }

        // 如果是 AggregateException，展开所有内部异常
        if (ex is AggregateException agg)
        {
            int i = 0;
            foreach (var ie in agg.InnerExceptions)
            {
                lines.Add($"--- Aggregate[{i}] ---");
                lines.Add($"Type: {ie.GetType().FullName}");
                lines.Add($"Message: {ie.Message}");
                lines.Add($"Stack Trace:");
                lines.Add(ie.StackTrace ?? "(null)");
                i++;
            }
        }

        lines.Add("");

        // ── 3. 落盘（写入失败时回退到 Debug.WriteLine） ──
        try
        {
            File.WriteAllText(fullPath, string.Join(Environment.NewLine, lines));
        }
        catch (Exception writeEx)
        {
            Debug.WriteLine($"DebugLog: failed to write {fullPath}: {writeEx.Message}");
            Debug.WriteLine(string.Join(Environment.NewLine, lines));
        }
    }
}
