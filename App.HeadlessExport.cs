using System.IO;
using System.Text.Json;
using System.Windows;

namespace BpmMeasurer;

/// <summary>
/// 无头配置导出:当 --parse_config 与 --notify 同时出现时,
/// 解析配置文件并把 JSON 写入 --notify 指定的路径,随后立即退出,不启动 GUI。
/// 失败时弹窗显示原因后退出:退出码 1(读取/解析失败)用本地化文案,
/// 退出码 2(写 notify 失败)与 3(其他异常)用英文硬编码(不走 i18n)。
/// 退出码: 0 = 成功 / 1 = 读取或解析配置失败 / 2 = 写 notify 文件失败 / 3 = 其他未预期异常。
/// </summary>
public partial class App
{
    internal static void RunHeadlessConfigExport()
    {
        try
        {
            // 1. 读取配置文件(失败 → 1)。弹窗用本地化文案。
            string text;
            try
            {
                text = File.ReadAllText(StartupParseConfigPath!);
            }
            catch (Exception ex)
            {
                ShowError($"{BpmMeasurer.MainWindow.Loc("ConfigImport_Failed")}\n{ex.Message}",
                    BpmMeasurer.MainWindow.Loc("Error"));
                Environment.Exit(1);
                return; // 抚慰编译器:Environment.Exit 实际不返回。
            }

            // 2. 解析(失败 → 1)。TryParse 本身不抛异常,error 为已本地化的友好消息。
            if (!TimingConfigParser.TryParse(text, out double offset, out List<RawTimingPoint> points, out string? error))
            {
                ShowError($"{BpmMeasurer.MainWindow.Loc("ConfigImport_Failed")}\n{error}",
                    BpmMeasurer.MainWindow.Loc("Error"));
                Environment.Exit(1);
                return;
            }

            // 3. 构造 DTO 并序列化为 JSON。timing_points 仅保留核心三字段,
            //    字段名用 snake_case,与配置文本输入格式对齐。
            var dto = new
            {
                global_offset = offset,
                timing_points = points.Select(p => new
                {
                    beat_index = p.BeatIndex,
                    bpm = p.Bpm,
                    beats_per_bar = p.BeatsPerBar
                }).ToList()
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

            // 4. 写入 notify 文件。File.WriteAllText 语义:文件存在则先截断(清空旧内容)再写入,
            //    与现有 manifest 写法(MainWindow.Config.cs)一致。
            //    失败 → 2,弹窗用英文硬编码(不走 i18n)。
            try
            {
                File.WriteAllText(StartupNotifyPath!, json);
            }
            catch (Exception ex)
            {
                ShowError($"Failed to write notify file.\n{ex.Message}", "Error");
                Environment.Exit(2);
                return;
            }

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            // 5. 兜底:其他未预期异常(如序列化 OOM)。弹窗用英文硬编码。
            ShowError($"Unexpected error.\n{ex.Message}", "Error");
            Environment.Exit(3);
        }
    }

    // 弹窗辅助:MessageBox 是独立 Win32 对话框,可在 App 构造函数阶段(OnStartup 之前)弹出。
    private static void ShowError(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
