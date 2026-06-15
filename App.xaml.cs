using System.Globalization;
using System.Windows;
using WPFLocalizeExtension.Engine;

namespace BpmMeasurer;

public partial class App : Application
{
    /// <summary>
    /// 启动时指定的音频文件路径(由 --audio= 显式参数或位置参数提供)。
    /// 为 null 表示未指定;非空时由 MainWindow.Window_Loaded 触发自动加载。
    /// </summary>
    public static string? StartupAudioPath { get; private set; }

    public App()
    {
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;

        var args = Environment.GetCommandLineArgs();
        var lang = "zh-CN"; // default

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--language=", StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i].Substring("--language=".Length);
                lang = value switch
                {
                    "en_us" or "en-us" or "en" => "en-US",
                    "zh_cn" or "zh-cn" or "zh" => "zh-CN",
                    _ => "zh-CN"
                };
            }
            else if (args[i].StartsWith("--audio=", StringComparison.OrdinalIgnoreCase))
            {
                StartupAudioPath = args[i].Substring("--audio=".Length);
            }
        }

        // 兼容位置参数(拖入 exe 图标 / `Bpm Measurer.exe "song.mp3"`):
        // --audio= 优先,仅在其未提供时取第一个位置参数。
        if (string.IsNullOrEmpty(StartupAudioPath))
        {
            for (int i = 1; i < args.Length; i++) // 跳过 args[0] 自身路径
            {
                var arg = args[i];
                if (arg.StartsWith("--"))
                {
                    // 形如 `--language en` 这种空格分隔的选项会消费下一个 token,
                    // 简单起见跳过单个 `--` 开头 token 即可(本工具仅用 `--key=` 形式)。
                    continue;
                }
                StartupAudioPath = arg;
                break;
            }
        }

        // 空字符串视为未指定
        StartupAudioPath = string.IsNullOrWhiteSpace(StartupAudioPath) ? null : StartupAudioPath;

        LocalizeDictionary.Instance.Culture = new CultureInfo(lang);
    }
}

