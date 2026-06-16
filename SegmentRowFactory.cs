using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BpmMeasurer.Controls;

namespace BpmMeasurer;

/// <summary>
/// Builds the WPF controls for a single segment row in the segment list.
/// Pure UI factories extracted from MainWindow — they take all dependencies as
/// parameters and capture no instance state. The value-change callback is passed
/// in as an Action so the row stays decoupled from the timing mutation logic.
/// </summary>
public static class SegmentRowFactory
{
    public static StackPanel BuildStaticField(string label, string value, Color foreColor, int column)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize = 9,
            Margin = new Thickness(0, 0, 0, 2)
        });

        var box = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            Height = 24,
            Child = new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(foreColor),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        panel.Children.Add(box);

        Grid.SetColumn(panel, column);
        return panel;
    }

    public static StackPanel BuildStepper(
        string label, double[] steps, double min, double max, int decimals,
        Color foreColor, Guid id, bool readOnly, double initialValue, int column,
        Action<double> onChanged)
    {
        var panel = new StackPanel();
        if (column == 1)
            panel.Margin = new Thickness(6, 0, 0, 0);

        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize = 9,
            Margin = new Thickness(0, 0, 0, 2)
        });

        var stepper = new StepperInput();
        stepper.Tag = id;
        stepper.Configure(steps, min, max, decimals, foreColor, readOnly);
        stepper.SetValue(initialValue);
        if (!readOnly)
            stepper.ValueChanged += (s, v) => onChanged(v);
        panel.Children.Add(stepper);

        Grid.SetColumn(panel, column);
        return panel;
    }
}
