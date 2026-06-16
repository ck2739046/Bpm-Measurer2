using System.Windows;

namespace BpmMeasurer;

public partial class MainWindow
{
    private void HelpBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HelpDialog { Owner = this };
        dialog.ShowDialog();
    }
}
