using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BpmMeasurer.Controls;

namespace BpmMeasurer;

public partial class MainWindow : Window
{
    private BpmAudioData? _audioData;
    private volatile int _bgmStream;
    private volatile int _decodeStream;
    private volatile bool _isPlaying;
    private volatile bool _isLoading;

    private readonly Stopwatch _frameClock = Stopwatch.StartNew();

    // FPS tracking
    private int _fpsFrameCount;
    private double _lastFpsUpdateTime;
    private double _currentFps;

    // Cache
    private WaveformEnvelope? _waveEnvelope;
    private SpectrogramData? _specCache;

    // Spectrogram WriteableBitmap (filled once, GPU-composited thereafter)
    private WriteableBitmap? _specBitmap;

    // Viewport
    private double _viewCenterTime;
    private double _viewHalfWidth;
    private bool _plotsConfigured;
    private bool _specConfigured;

    // Waveform WriteableBitmap (filled once, GPU-composited thereafter)
    private WriteableBitmap? _waveBitmap;

    // Timing state
    private double _globalOffset = 0.0;
    private List<RawTimingPoint> _rawPoints = new() { new RawTimingPoint(Guid.NewGuid(), 0, 120) };
    private IReadOnlyList<TimingPoint> _timingPoints = Array.Empty<TimingPoint>();

    // Overlay canvas elements (dynamically managed)
    private readonly List<UIElement> _overlayElements = new();
    private readonly List<UIElement> _beatRowElements = new();

    // Drag state
    private enum DragMode { None, Seek, Offset, Bpm }
    private DragMode _dragMode;
    private double _dragStartX;
    private double _dragStartTime;
    private double _dragStartOffset;
    private double _dragBeatTarget;
    private double _dragTargetSegBeat;
    private SolidColorBrush? _dragDisplayColor;

    // Focus region state (independent of WPF keyboard focus)
    // _plotAreaHasFocus: VizGrid currently holds focus; _sidebarHasFocus: SidebarPanel currently holds focus.
    // _focusJustTransferred: marks the first gesture entering a region — suppresses click actions
    // (no seek / no offset / no BPM change) until a >3px drag confirms intent.
    private bool _plotAreaHasFocus;
    private bool _sidebarHasFocus;
    private bool _focusJustTransferred;

    private static readonly SolidColorBrush FocusHighlightBrush =
        new(Color.FromRgb(0x81, 0x8C, 0xF8));

    /// <summary>
    /// Toggles the overlay highlight borders around VizGrid and SidebarPanel based on the
    /// current focus-region state. BorderThickness stays constant (2px) — only the brush
    /// switches between transparent and the indigo highlight, so layout never shifts.
    /// </summary>
    private void UpdateFocusHighlights()
    {
        if (PlotAreaHighlight != null)
            PlotAreaHighlight.BorderBrush = _plotAreaHasFocus ? FocusHighlightBrush : Brushes.Transparent;
        if (SidebarHighlight != null)
            SidebarHighlight.BorderBrush = _sidebarHasFocus ? FocusHighlightBrush : Brushes.Transparent;
    }

    public MainWindow()
    {
        InitializeComponent();
        ApplyLocalizedTexts();

        AllowDrop = true;
        DragEnter += (s, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
        };
        Drop += (s, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                e.Handled = true;
                var path = files[0];
                Dispatcher.BeginInvoke(() => LoadAudioFile(path));
            }
        };

        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
                if (_isPlaying) PausePlayback();
                else StartPlayback();
            }
        };

        OffsetStepper.Configure(
            new[] { 1.0, 0.1, 0.01 },
            0, double.PositiveInfinity, 3,
            Color.FromRgb(0x4A, 0xDE, 0x80), false);
        OffsetStepper.SetValue(_globalOffset);
        OffsetStepper.ValueChanged += (s, v) =>
        {
            if (_isPlaying) PausePlayback();
            _globalOffset = v;
            RefreshTimingPoints();
        };
    }
}
