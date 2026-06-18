using System.IO;
using System.Text.Json;

namespace BpmMeasurer;

/// <summary>
/// 无头配置导出:当 --parse_config 与 --notify 同时出现时,
/// 解析配置文件并把 JSON 写入 --notify 指定的路径,随后立即退出,不启动 GUI。
/// 退出码: 0 = 成功 / 1 = 读取或解析配置失败 / 2 = 写 notify 文件失败 / 3 = 其他未预期异常。
/// </summary>
public partial class App
{
    internal static void RunHeadlessConfigExport()
    {
        try
        {
            // 1. 读取配置文件(失败 → 1)。
            string text;
            try
            {
                text = File.ReadAllText(StartupParseConfigPath!);
            }
            catch (Exception)
            {
                Environment.Exit(1);
                return; // 抚慰编译器:Environment.Exit 实际不返回。
            }

            // 2. 解析(失败 → 1)。TryParse 本身不抛异常。
            if (!TimingConfigParser.TryParse(text, out double offset, out List<RawTimingPoint> points, out _))
            {
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
            //    与现有 manifest 写法(MaiWindow.Config.cs)一致。
            try
            {
                File.WriteAllText(StartupNotifyPath!, json);
            }
            catch (Exception)
            {
                Environment.Exit(2);
                return;
            }

            Environment.Exit(0);
        }
        catch (Exception)
        {
            // 5. 兜底:其他未预期异常(如序列化 OOM)。
            Environment.Exit(3);
        }
    }
}
