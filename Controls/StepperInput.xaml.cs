using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BpmMeasurer.Controls;

public partial class StepperInput : UserControl
{
    private static readonly string[] Palette =
        { "#FF1E3A5F", "#FF2A5090", "#FF3A6AB0" }; // deep, mid, light

    private readonly TextBox _textBox = new();
    private double[] _steps = { 1 };
    private double _min = double.NegativeInfinity;
    private double _max = double.PositiveInfinity;
    private int _decimals = 2;
    private bool _readOnly;

    public double Value { get; private set; }
    public event Action<StepperInput, double>? ValueChanged;

    public StepperInput()
    {
        InitializeComponent();
    }

    public void Configure(
        double[] steps, double min, double max,
        int decimals, Color foreColor, bool readOnly)
    {
        _steps = steps.Length > 0 ? steps : new[] { 1.0 };
        _min = min;
        _max = max;
        _decimals = decimals;
        _readOnly = readOnly;
        Build(foreColor);
    }

    private void Build(Color foreColor)
    {
        RootGrid.Children.Clear();
        RootGrid.ColumnDefinitions.Clear();

        int n = _steps.Length;
        for (int i = 0; i < n; i++)
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < n; i++)
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Palette selection (outermost -> innermost): always take the n colors closest to the input.
        // 3 -> deep, mid, light ; 2 -> mid, light ; 1 -> light.
        List<string> palette = Palette.Skip(3 - n).Take(n).ToList();

        // Left buttons: i=0 is outermost (largest step). Decrease.
        for (int i = 0; i < n; i++)
        {
            double step = _steps[i];
            string bg = palette[i];
            string corner = GetLeftCornerRadius(i, n);
            var btn = MakeButton(bg, corner, $"−{FormatStep(step)}", !IsButtonVisible(i));
            btn.Click += (s, e) => ApplyDelta(-step);
            Grid.SetColumn(btn, i);
            RootGrid.Children.Add(btn);
        }

        // Center text box
        _textBox.TextAlignment = TextAlignment.Center;
        _textBox.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A));
        _textBox.Foreground = new SolidColorBrush(foreColor);
        _textBox.CaretBrush = new SolidColorBrush(foreColor);
        _textBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        _textBox.BorderThickness = new Thickness(1);
        _textBox.FontFamily = new FontFamily("Consolas");
        _textBox.FontSize = 12;
        _textBox.FontWeight = FontWeights.Bold;
        _textBox.Padding = new Thickness(2, 3, 2, 3);
        _textBox.Height = 24;
        _textBox.IsReadOnly = _readOnly;
        _textBox.LostKeyboardFocus += TextBox_Commit;
        _textBox.KeyDown += TextBox_KeyDown;
        Grid.SetColumn(_textBox, n);
        RootGrid.Children.Add(_textBox);

        // Right buttons: i=0 is innermost (smallest step). Increase.
        for (int i = 0; i < n; i++)
        {
            double step = _steps[n - 1 - i];
            string bg = palette[n - 1 - i];
            string corner = GetRightCornerRadius(i, n);
            var btn = MakeButton(bg, corner, $"+{FormatStep(step)}", !IsButtonVisible(n - 1 - i));
            btn.Click += (s, e) => ApplyDelta(+step);
            Grid.SetColumn(btn, n + 1 + i);
            RootGrid.Children.Add(btn);
        }

        RefreshText();
    }

    private bool IsButtonVisible(int paletteIndex) => !_readOnly;

    private static string GetLeftCornerRadius(int i, int n)
    {
        if (n == 1) return "4,4,4,4";
        if (i == 0) return "4,0,0,4";          // outermost left
        if (i == n - 1) return "0,4,4,0";      // innermost left (adjacent to text)
        return "0,0,0,0";
    }

    private static string GetRightCornerRadius(int i, int n)
    {
        if (n == 1) return "4,4,4,4";
        if (i == 0) return "4,0,0,4";          // innermost right (adjacent to text)
        if (i == n - 1) return "0,4,4,0";      // outermost right
        return "0,0,0,0";
    }

    private static Button MakeButton(string bgHex, string cornerHex, string tooltip, bool hidden)
    {
        var bg = (Color)ColorConverter.ConvertFromString(bgHex);
        var cornerParts = cornerHex.Split(',');
        var radius = new CornerRadius(
            double.Parse(cornerParts[0]), double.Parse(cornerParts[1]),
            double.Parse(cornerParts[2]), double.Parse(cornerParts[3]));

        var btn = new Button
        {
            Width = 16,
            Height = 24,
            Foreground = Brushes.White,
            FontSize = 9,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            ToolTip = tooltip,
            Visibility = hidden ? Visibility.Collapsed : Visibility.Visible,
            Focusable = true
        };
        ToolTipService.SetInitialShowDelay(btn, 500);
        btn.Template = CreateButtonTemplate(bg, radius);
        return btn;
    }

    private static ControlTemplate CreateButtonTemplate(Color bg, CornerRadius radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, new SolidColorBrush(bg));
        factory.SetValue(Border.CornerRadiusProperty, radius);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        factory.AppendChild(presenter);
        template.VisualTree = factory;
        template.Seal();
        return template;
    }

    private static string FormatStep(double step)
    {
        if (step >= 1) return step.ToString("0");
        return step.ToString("0.0##").TrimEnd('0').TrimEnd('.');
    }

    private void ApplyDelta(double delta)
    {
        if (_readOnly) return;
        Value = ClampAndRound(Value + delta);
        RefreshText();
        ValueChanged?.Invoke(this, Value);
    }

    private void TextBox_Commit(object? sender, RoutedEventArgs e)
    {
        if (_readOnly) { RefreshText(); return; }
        if (double.TryParse(_textBox.Text, out double v))
        {
            Value = ClampAndRound(v);
            RefreshText();
            ValueChanged?.Invoke(this, Value);
        }
        else
        {
            RefreshText();
        }
    }

    private void TextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TextBox_Commit(sender, e);
            Keyboard.ClearFocus();
        }
    }

    private double ClampAndRound(double v)
    {
        double factor = Math.Pow(10, _decimals);
        v = Math.Round(v * factor) / factor;
        if (v < _min) v = _min;
        if (v > _max) v = _max;
        return v;
    }

    private void RefreshText()
    {
        _textBox.Text = _decimals <= 0
            ? ((long)Math.Round(Value)).ToString()
            : Value.ToString($"F{_decimals}");
    }

    /// <summary>External sync: update value without raising ValueChanged.</summary>
    public void SetValue(double v, bool raise = false)
    {
        Value = ClampAndRound(v);
        RefreshText();
        if (raise) ValueChanged?.Invoke(this, Value);
    }

    /// <summary>Update bounds (e.g. when audio duration becomes known).</summary>
    public void SetRange(double min, double max)
    {
        _min = min;
        _max = max;
    }
}
