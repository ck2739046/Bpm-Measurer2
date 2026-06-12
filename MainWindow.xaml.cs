using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    // ScottPlot plottables
    private ScottPlot.Plottables.VerticalLine? _waveCenterLine;

    // Drag state
    private bool _isDragging;
    private FrameworkElement? _dragSourceElement;
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
        SpectrogramCanvas.Visibility = Visibility.Collapsed;
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
    }

    private void EnsurePlotsConfigured()
    {
        if (_audioData == null || _waveEnvelope == null || _specCache == null) return;

        if (!_plotsConfigured)
        {
            _plotsConfigured = true;

            // Clear old plottables and event handlers from previous loads
            WaveformPlot.Plot.Clear();
            WaveformPlot.PreviewMouseLeftButtonDown -= OnPlotMouseDown;
            WaveformPlot.PreviewMouseMove -= OnPlotMouseMove;
            WaveformPlot.PreviewMouseLeftButtonUp -= OnPlotMouseUp;
            WaveformPlot.PreviewMouseWheel -= OnPlotMouseWheel;

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

            // Initial X range
            SetBothXLimits(0, _audioData.Duration);

            WaveformPlot.UserInputProcessor.IsEnabled = false;
            WaveformPlot.Menu = null;

            WaveformPlot.PreviewMouseLeftButtonDown += OnPlotMouseDown;
            WaveformPlot.PreviewMouseMove += OnPlotMouseMove;
            WaveformPlot.PreviewMouseLeftButtonUp += OnPlotMouseUp;
            WaveformPlot.PreviewMouseWheel += OnPlotMouseWheel;

            WaveformPlot.Visibility = Visibility.Visible;
            WaveformPlot.Refresh();
        }

        if (!_specConfigured)
        {
            _specConfigured = true;

            SpectrogramCanvas.PreviewMouseLeftButtonDown -= OnCanvasMouseDown;
            SpectrogramCanvas.PreviewMouseMove -= OnCanvasMouseMove;
            SpectrogramCanvas.PreviewMouseLeftButtonUp -= OnCanvasMouseUp;
            SpectrogramCanvas.PreviewMouseWheel -= OnPlotMouseWheel;

            // Generate WriteableBitmap once
            _specBitmap = SpectrogramBitmapRenderer.Create(_specCache);
            SpectrogramImage.Source = _specBitmap;

            // Fit canvas height
            SpecPlayheadLine.Y2 = SpectrogramCanvas.ActualHeight;

            SpectrogramCanvas.PreviewMouseLeftButtonDown += OnCanvasMouseDown;
            SpectrogramCanvas.PreviewMouseMove += OnCanvasMouseMove;
            SpectrogramCanvas.PreviewMouseLeftButtonUp += OnCanvasMouseUp;
            SpectrogramCanvas.PreviewMouseWheel += OnPlotMouseWheel;

            SpectrogramCanvas.Visibility = Visibility.Visible;
        }
    }

    private void SpectrogramCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SpecPlayheadLine.Y2 = SpectrogramCanvas.ActualHeight;
        if (_specConfigured)
            UpdateSpectrogramTransform();
    }

    // ── Rendering ──

    private void UpdateSpectrogramTransform()
    {
        if (_specCache == null) return;
        double canvasW = SpectrogramCanvas.ActualWidth;
        if (canvasW <= 0) return;

        // pixelsPerSec = data columns per second of audio
        double pixelsPerSec = _specCache.Columns / _specCache.Duration;

        // Scale: fit (2 * viewHalfWidth) seconds into canvas width
        double scaleX = canvasW / (2.0 * _viewHalfWidth * pixelsPerSec);
        double canvasH = SpectrogramCanvas.ActualHeight;
        SpecScale.ScaleX = scaleX;
        SpecScale.ScaleY = canvasH > 0 ? canvasH / _specCache.FreqBands : 1.0;

        // Translate: left-align the view to (_viewCenterTime - viewHalfWidth) seconds
        double translateX = -(_viewCenterTime - _viewHalfWidth) * pixelsPerSec * scaleX;
        SpecTranslate.X = translateX;
        SpecTranslate.Y = 0;

        // Playhead vertical line at canvas center
        double mid = canvasW * 0.5;
        SpecPlayheadLine.X1 = mid;
        SpecPlayheadLine.X2 = mid;
        SpecPlayheadLine.Visibility = Visibility.Visible;
    }

    private void RenderVisuals()
    {
        if (_audioData == null || _waveEnvelope == null || _specCache == null) return;

        if (!_plotsConfigured || !_specConfigured)
            EnsurePlotsConfigured();

        // Waveform
        double half = _viewHalfWidth;
        double left = _viewCenterTime - half;
        double right = _viewCenterTime + half;
        WaveformPlot.Plot.Axes.SetLimitsX(left, right);
        if (_waveCenterLine != null) _waveCenterLine.X = _viewCenterTime;
        WaveformPlot.Refresh();

        // Spectrogram — only transform, no bitmap regeneration
        UpdateSpectrogramTransform();

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
        StartDrag(plot, e.GetPosition(plot).X);
        plot.CaptureMouse();
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        var canvas = (Canvas)sender;
        StartDrag(canvas, e.GetPosition(canvas).X);
        canvas.CaptureMouse();
    }

    private void StartDrag(FrameworkElement element, double pixelX)
    {
        _isDragging = true;
        _dragSourceElement = element;
        _dragStartPixelX = pixelX;
        if (_isPlaying) PausePlayback();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _dragSourceElement == null) return;
        double currentX = e.GetPosition(_dragSourceElement).X;
        double delta = currentX - _dragStartPixelX;
        _dragStartPixelX = currentX;
        SeekByDelta(delta);
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _dragSourceElement == null) return;
        double currentX = e.GetPosition(_dragSourceElement).X;
        double delta = currentX - _dragStartPixelX;
        _dragStartPixelX = currentX;
        SeekByDelta(delta);
    }

    private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_isDragging) return;
        _isDragging = false;
        _dragSourceElement?.ReleaseMouseCapture();
        _dragSourceElement = null;
    }

    private void SeekByDelta(double deltaPixel)
    {
        if (_audioData == null || _dragSourceElement == null) return;
        double elemWidth = _dragSourceElement.ActualWidth;
        if (elemWidth <= 0) return;

        // Data span = 2 * viewHalfWidth (both waveform and spectrogram use same range)
        double dataSpan = _viewHalfWidth * 2.0;
        double deltaTime = -deltaPixel * dataSpan / elemWidth;

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
