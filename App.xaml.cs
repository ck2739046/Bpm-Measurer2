using System.Globalization;
using System.Windows;
using WPFLocalizeExtension.Engine;

namespace BpmMeasurer.Wpf;

public partial class App : Application
{
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
        }

        LocalizeDictionary.Instance.Culture = new CultureInfo(lang);
    }
}

