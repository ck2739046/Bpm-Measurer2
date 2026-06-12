using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.WPF;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using WPFLocalizeExtension.Extensions;

namespace BpmMeasurer.Wpf;

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

    // Frame skip: spectrogram only refreshes every Nth frame during playback
    private int _specSkipCounter;

    // Cache
    private WaveformEnvelope? _waveEnvelope;
    private SpectrogramData? _specCache;

    // Viewport
    private double _viewCenterTime;
    private double _viewHalfWidth;
    private bool _plotsConfigured;

    // ScottPlot plottables
    private ScottPlot.Plottables.VerticalLine? _waveCenterLine;
    private ScottPlot.Plottables.VerticalLine? _specCenterLine;

    // Drag state
    private bool _isDragging;
    private WpfPlot? _dragSourcePlot;
    private double _dragStartPixelX;

    public static string Loc(string key)
    {
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        var fullKey = $"{assemblyName}:Langs:{key}";
        var locExtension = new LocExtension(fullKey);
        locExtension.ResolveLocalizedValue(out string? result);
        return result ?? key;
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
    }

    private void ApplyLocalizedTexts()
    {
        Title = Loc("WindowTitle");
        OpenBtn.Content = Loc("ImportAudio");
        PlaceholderText.Text = Loc("DropHint");
        StopBtn.Content = Loc("JumpToStart");
        PlayPauseBtn.Content = Loc("Play");
        FileNameText.Text = Loc("NoAudio");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        Bass.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_DEFAULT, handle);
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRenderingFrame;
        StopAndFreeStreams();
        Bass.BASS_Free();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_audioData != null)
            RenderVisuals();
    }

    private void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc("SelectAudioFile"),
            Filter = $"{Loc("AudioFiles")}|*.mp3;*.wav;*.ogg;*.flac;*.aac;*.m4a|{Loc("AllFiles")}|*.*"
        };
        if (dlg.ShowDialog() == true)
            LoadAudioFile(dlg.FileName);
    }

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) PausePlayback();
        else StartPlayback();
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        JumpToStart();
    }

    // ── Playback ──

    private void StopAndFreeStreams()
    {
        if (_bgmStream != 0)
        {
            Bass.BASS_ChannelStop(_bgmStream);
            // BASS_FX_FREESOURCE auto-frees the decode stream,
            // so clear the handle to avoid double-free below.
            Bass.BASS_StreamFree(_bgmStream);
            _bgmStream = 0;
            _decodeStream = 0;
        }
        else if (_decodeStream != 0)
        {
            Bass.BASS_StreamFree(_decodeStream);
            _decodeStream = 0;
        }
        _isPlaying = false;
    }

    private async void LoadAudioFile(string filePath)
    {
        if (_isLoading) return;
        _isLoading = true;
        OpenBtn.IsEnabled = false;

        StopAndFreeStreams();
        CompositionTarget.Rendering -= OnRenderingFrame;

        // Clear old visual state before loading new file
        WaveformPlot.Visibility = Visibility.Collapsed;
        SpectrogramPlot.Visibility = Visibility.Collapsed;
        SampleRateText.Text = "-";
        DurationText.Text = "-";
        ChannelsText.Text = "-";

        var audioData = await Task.Run(() => BpmAudioLoader.Load(filePath));
        if (audioData == null)
        {
            _isLoading = false;
            OpenBtn.IsEnabled = true;
            PlaceholderText.Visibility = Visibility.Visible;
            FileNameText.Text = Loc("NoAudio");
            MessageBox.Show(Loc("LoadError"), Loc("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _audioData = audioData;

        _decodeStream = Bass.BASS_StreamCreateFile(filePath, 0L, 0L, BASSFlag.BASS_STREAM_DECODE);
        if (_decodeStream == 0)
        {
            _audioData = null;
            _isLoading = false;
            OpenBtn.IsEnabled = true;
            PlaceholderText.Visibility = Visibility.Visible;
            FileNameText.Text = Loc("NoAudio");
            MessageBox.Show(Loc("LoadError"), Loc("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _bgmStream = BassFx.BASS_FX_TempoCreate(_decodeStream, BASSFlag.BASS_FX_FREESOURCE);

        // Show loading indicator
        PlaceholderText.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;

        LoadingText.Text = Loc("ComputingWaveform");
        await Task.Run(() =>
        {
            _waveEnvelope = PrecomputedAudioData.ComputeWaveform(
                _audioData.RawSamples, _audioData.Channels, _audioData.Duration);
        });

        LoadingText.Text = Loc("ComputingSpectrogram");
        await Task.Run(() =>
        {
            _specCache = PrecomputedAudioData.ComputeSpectrogram(
                _audioData.FilePath, _audioData.Duration);
        });

        LoadingText.Visibility = Visibility.Collapsed;

        _viewCenterTime = 0;
        _plotsConfigured = false;

        FileNameText.Text = Path.GetFileName(filePath);
        SampleRateText.Text = $"{_audioData.SampleRate} Hz";
        DurationText.Text = $"{_audioData.Duration:F2}s";
        ChannelsText.Text = _audioData.Channels.ToString();
        TimeText.Text = "0.000s";
        FpsText.Text = "FPS: -";

        // Reset FPS tracking
        _fpsFrameCount = 0;
        _lastFpsUpdateTime = _frameClock.Elapsed.TotalSeconds;

        PlayPauseBtn.IsEnabled = true;
        StopBtn.IsEnabled = true;
        PlayPauseBtn.Content = Loc("Play");

        _isLoading = false;
        OpenBtn.IsEnabled = true;

        RenderVisuals();
    }

    private void StartPlayback()
    {
        if (_bgmStream == 0) return;

        var active = Bass.BASS_ChannelIsActive(_bgmStream);

        if (active == BASSActive.BASS_ACTIVE_PAUSED)
        {
            Bass.BASS_ChannelPlay(_bgmStream, false);
        }
        else
        {
            var pos = Bass.BASS_ChannelGetPosition(_bgmStream);
            var time = Bass.BASS_ChannelBytes2Seconds(_bgmStream, pos);
            if (_audioData != null && time >= _audioData.Duration - 0.1)
                Bass.BASS_ChannelSetPosition(_bgmStream, 0);

            Bass.BASS_ChannelPlay(_bgmStream, false);
        }

        _isPlaying = true;
        _specSkipCounter = 0;
        CompositionTarget.Rendering += OnRenderingFrame;
        PlayPauseBtn.Content = Loc("Pause");
    }

    private void PausePlayback()
    {
        CompositionTarget.Rendering -= OnRenderingFrame;
        if (_bgmStream != 0)
            Bass.BASS_ChannelPause(_bgmStream);
        _isPlaying = false;
        PlayPauseBtn.Content = Loc("Play");
    }

    private void JumpToStart()
    {
        if (_bgmStream == 0) return;

        CompositionTarget.Rendering -= OnRenderingFrame;
        if (_isPlaying)
        {
            Bass.BASS_ChannelPause(_bgmStream);
            _isPlaying = false;
            PlayPauseBtn.Content = Loc("Play");
        }

        Bass.BASS_ChannelSetPosition(_bgmStream, 0);
        _viewCenterTime = 0;
        TimeText.Text = "0.000s";
        RenderVisuals();
    }

    // ── Frame rendering: driven by WPF composition thread ──

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        if (!_isPlaying || _bgmStream == 0) return;

        var pos = Bass.BASS_ChannelGetPosition(_bgmStream);
        var time = Bass.BASS_ChannelBytes2Seconds(_bgmStream, pos);
        _viewCenterTime = time;

        // Time text update — already on UI thread, no dispatcher overhead
        TimeText.Text = $"{time:F3}s";

        RenderVisuals();
    }

    // ── ScottPlot Plots ──

    private void SeekBassTo(double seconds)
    {
        if (_bgmStream == 0) return;
        var bytePos = Bass.BASS_ChannelSeconds2Bytes(_bgmStream, seconds);
        Bass.BASS_ChannelSetPosition(_bgmStream, bytePos);
    }

    private void SetBothXLimits(double left, double right)
    {
        _viewHalfWidth = (right - left) / 2;
        WaveformPlot.Plot.Axes.SetLimitsX(left, right);
        SpectrogramPlot.Plot.Axes.SetLimitsX(left, right);
    }

    private void EnsurePlotsConfigured()
    {
        if (_plotsConfigured || _audioData == null || _waveEnvelope == null || _specCache == null) return;
        _plotsConfigured = true;

        // Clear old plottables and event handlers from previous loads
        WaveformPlot.Plot.Clear();
        SpectrogramPlot.Plot.Clear();
        WaveformPlot.PreviewMouseLeftButtonDown -= OnPlotMouseDown;
        SpectrogramPlot.PreviewMouseLeftButtonDown -= OnPlotMouseDown;
        WaveformPlot.PreviewMouseMove -= OnPlotMouseMove;
        SpectrogramPlot.PreviewMouseMove -= OnPlotMouseMove;
        WaveformPlot.PreviewMouseLeftButtonUp -= OnPlotMouseUp;
        SpectrogramPlot.PreviewMouseLeftButtonUp -= OnPlotMouseUp;
        WaveformPlot.PreviewMouseWheel -= OnPlotMouseWheel;
        SpectrogramPlot.PreviewMouseWheel -= OnPlotMouseWheel;

        // ── Waveform: single interleaved line ──
        var wavePlot = WaveformPlot.Plot;
        wavePlot.Title("");

        int cols = _waveEnvelope.Columns;
        double[] alt = new double[cols * 2];
        for (int i = 0; i < cols; i++)
        {
            alt[i * 2] = _waveEnvelope.MaxValues[i];
            alt[i * 2 + 1] = _waveEnvelope.MinValues[i];
        }
        var waveSig = wavePlot.Add.Signal(alt);
        waveSig.Data.Period = _waveEnvelope.TimeStep / 2;
        waveSig.Color = ScottPlot.Color.FromHex("#00F2FF");
        waveSig.LineWidth = 1;

        _waveCenterLine = wavePlot.Add.VerticalLine(_viewCenterTime);
        _waveCenterLine.Color = ScottPlot.Color.FromHex("#00FF88");
        _waveCenterLine.LineWidth = 2;
        _waveCenterLine.IsDraggable = false;

        wavePlot.FigureBackground.Color = ScottPlot.Color.FromHex("#0A0A0A");
        wavePlot.DataBackground.Color = ScottPlot.Color.FromHex("#0A0A0A");
        wavePlot.Axes.Color(ScottPlot.Color.FromHex("#333333"));
        wavePlot.Axes.SetLimitsY(-32768, 32767);
        wavePlot.Axes.Left.IsVisible = false;

        // ── Spectrogram: Heatmap ──
        var specPlot = SpectrogramPlot.Plot;
        specPlot.Title("");

        var heatmapData = ConvertToDouble(_specCache.Magnitudes);
        var heatmap = specPlot.Add.Heatmap(heatmapData);
        heatmap.Colormap = new WaveSpectrogramColormap();
        heatmap.Extent = new CoordinateRect(0, _specCache.Duration, _specCache.FreqBands, 0);

        _specCenterLine = specPlot.Add.VerticalLine(_viewCenterTime);
        _specCenterLine.Color = ScottPlot.Color.FromHex("#00FF88");
        _specCenterLine.LineWidth = 2;
        _specCenterLine.IsDraggable = false;

        specPlot.FigureBackground.Color = ScottPlot.Color.FromHex("#0A0A0A");
        specPlot.DataBackground.Color = ScottPlot.Color.FromHex("#0A0A0A");
        specPlot.Axes.Color(ScottPlot.Color.FromHex("#333333"));
        specPlot.Axes.SetLimitsY(_specCache.FreqBands, 0);
        specPlot.Axes.Left.IsVisible = false;

        // Initial X range: show entire duration
        SetBothXLimits(0, _audioData.Duration);

        // ── Disable ScottPlot default interaction ──
        WaveformPlot.UserInputProcessor.IsEnabled = false;
        SpectrogramPlot.UserInputProcessor.IsEnabled = false;

        // ── Custom mouse events ──
        WaveformPlot.PreviewMouseLeftButtonDown += OnPlotMouseDown;
        SpectrogramPlot.PreviewMouseLeftButtonDown += OnPlotMouseDown;
        WaveformPlot.PreviewMouseMove += OnPlotMouseMove;
        SpectrogramPlot.PreviewMouseMove += OnPlotMouseMove;
        WaveformPlot.PreviewMouseLeftButtonUp += OnPlotMouseUp;
        SpectrogramPlot.PreviewMouseLeftButtonUp += OnPlotMouseUp;
        WaveformPlot.PreviewMouseWheel += OnPlotMouseWheel;
        SpectrogramPlot.PreviewMouseWheel += OnPlotMouseWheel;

        // Disable right-click menu
        WaveformPlot.Menu = null;
        SpectrogramPlot.Menu = null;

        WaveformPlot.Visibility = Visibility.Visible;
        SpectrogramPlot.Visibility = Visibility.Visible;

        WaveformPlot.Refresh();
        SpectrogramPlot.Refresh();
    }

    // ── Rendering ──

    private static double[,] ConvertToDouble(float[,] src)
    {
        var rows = src.GetLength(0);
        var cols = src.GetLength(1);
        var result = new double[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                result[r, c] = src[r, c];
        return result;
    }

    private void RenderVisuals()
    {
        if (_audioData == null || _waveEnvelope == null || _specCache == null) return;

        if (!_plotsConfigured)
            EnsurePlotsConfigured();

        // Keep X axis centered on viewCenterTime
        double half = _viewHalfWidth;
        double left = _viewCenterTime - half;
        double right = _viewCenterTime + half;
        WaveformPlot.Plot.Axes.SetLimitsX(left, right);
        SpectrogramPlot.Plot.Axes.SetLimitsX(left, right);

        // Update center lines
        if (_waveCenterLine != null) _waveCenterLine.X = _viewCenterTime;
        if (_specCenterLine != null) _specCenterLine.X = _viewCenterTime;

        // Waveform always refreshes (Signal plot is lightweight O(screen_width))
        WaveformPlot.Refresh();

        // Spectrogram: skip every other frame during playback to reduce CPU load
        bool refreshSpec = true;
        if (_isPlaying)
        {
            _specSkipCounter++;
            refreshSpec = (_specSkipCounter & 1) == 0;
        }
        if (refreshSpec)
            SpectrogramPlot.Refresh();

        // ── FPS calculation ──
        _fpsFrameCount++;
        double now = _frameClock.Elapsed.TotalSeconds;
        double elapsed = now - _lastFpsUpdateTime;
        if (elapsed >= 1)
        {
            _currentFps = _fpsFrameCount / elapsed;
            _fpsFrameCount = 0;
            _lastFpsUpdateTime = now;
            FpsText.Text = $"FPS: {_currentFps:F0}";
        }
    }

    // ── Mouse interaction ──

    private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        var plot = (WpfPlot)sender;
        _isDragging = true;
        _dragSourcePlot = plot;
        _dragStartPixelX = e.GetPosition(plot).X;
        plot.CaptureMouse();

        // Pause if playing (matches Web behavior)
        if (_isPlaying) PausePlayback();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _dragSourcePlot == null) return;

        double currentX = e.GetPosition(_dragSourcePlot).X;
        double delta = currentX - _dragStartPixelX;
        _dragStartPixelX = currentX;

        SeekByDelta(delta);
    }

    private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        _dragSourcePlot?.ReleaseMouseCapture();
        _dragSourcePlot = null;
    }

    private void SeekByDelta(double deltaPixel)
    {
        if (_audioData == null || _dragSourcePlot == null) return;
        double plotWidth = _dragSourcePlot.ActualWidth;
        if (plotWidth <= 0) return;

        var limits = _dragSourcePlot.Plot.Axes.GetLimits();
        double dataSpan = limits.Right - limits.Left;
        double deltaTime = -deltaPixel * dataSpan / plotWidth;

        _viewCenterTime += deltaTime;
        _viewCenterTime = Math.Clamp(_viewCenterTime, 0, _audioData.Duration);
        TimeText.Text = $"{_viewCenterTime:F3}s";
        SeekBassTo(_viewCenterTime);
        RenderVisuals();
    }

    private void OnPlotMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        ZoomXOnly(e.Delta > 0);
    }

    private void ZoomXOnly(bool zoomIn)
    {
        double factor = zoomIn ? 0.85 : 1.0 / 0.85;
        double newHalf = _viewHalfWidth * factor;

        // Clamp zoom range
        if (_audioData != null && newHalf > _audioData.Duration)
            newHalf = _audioData.Duration;
        if (newHalf < 0.01)
            newHalf = 0.01;

        _viewHalfWidth = newHalf;
        RenderVisuals();
    }
}
