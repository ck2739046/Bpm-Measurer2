using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using WPFLocalizeExtension.Extensions;

namespace BpmMeasurer;

public partial class HelpDialog : Window
{
    public record HelpEntry(string Operation, string Description);

    public HelpDialog()
    {
        InitializeComponent();

        var items = new ObservableCollection<HelpEntry>();
        for (int i = 1; i <= 6; i++)
        {
            items.Add(new HelpEntry(Loc($"Help_Op{i}"), Loc($"Help_Desc{i}")));
        }
        HelpItemsControl.ItemsSource = items;
    }

    private static string Loc(string key)
    {
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        var fullKey = $"{assemblyName}:Langs:{key}";
        var locExtension = new LocExtension(fullKey);
        locExtension.ResolveLocalizedValue(out string? result);
        return result ?? key;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
