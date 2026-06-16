using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using WPFLocalizeExtension.Extensions;
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
    private double _globalOffset = 0.1;
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
            _globalOffset = v;
            RefreshTimingPoints();
        };
    }

    private void ApplyLocalizedTexts()
    {
        Title = Loc("WindowTitle");
        OpenBtnText.Text = Loc("ImportAudio");
        PlaceholderText.Text = Loc("DropHint");
        StopBtnText.Text = Loc("JumpToStart");
        PlayPauseText.Text = Loc("Play");
        FileNameText.Text = Loc("NoAudio");
        ImportConfigText.Text = Loc("ImportConfig_Btn");
        ExportConfigText.Text = Loc("ExportConfig_Btn");
        SegmentsHeader.Text = Loc("Segments_Title");
        AddSegmentText.Text = Loc("AddSegment_Btn");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        Bass.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_DEFAULT, handle);

        // 启动时若指定了音频(--audio= 或位置参数),自动加载。
        // 延后一帧:确保 BASS_Init 已完成、UI 控件布局就绪,避免在 Loaded 同步栈中阻塞。
        var startupPath = App.StartupAudioPath;
        if (!string.IsNullOrEmpty(startupPath))
        {
            Dispatcher.BeginInvoke(new Action(() => LoadAudioFile(startupPath)));
        }
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
        WaveformCanvas.Visibility = Visibility.Collapsed;
        SpectrogramCanvas.Visibility = Visibility.Collapsed;
        SampleRateText.Text = "-";
        DurationText.Text = "-";

        LoadTimingLogger.Begin(filePath);

        var audioData = await Task.Run(() => BpmAudioLoader.Load(filePath));
        LoadTimingLogger.Phase("Audio decode");

        if (audioData == null)
        {
            LoadTimingLogger.End("Decode failed");
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
            LoadTimingLogger.End("BASS decode stream failed");
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
        if (_bgmStream == 0)
        {
            LoadTimingLogger.End("BASS tempo stream failed");
            Bass.BASS_StreamFree(_decodeStream);
            _decodeStream = 0;
            _audioData = null;
            _isLoading = false;
            OpenBtn.IsEnabled = true;
            PlaceholderText.Visibility = Visibility.Visible;
            FileNameText.Text = Loc("NoAudio");
            MessageBox.Show(Loc("LoadError"), Loc("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        LoadTimingLogger.Phase("BASS stream create");

        // Show loading indicator
        PlaceholderText.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = Loc("LoadingAudio");
        
        await Task.Run(() =>
        {
            _waveEnvelope = PrecomputedAudioData.ComputeWaveform(
                _audioData.RawSamples, _audioData.Duration);
        });
        LoadTimingLogger.Phase("Waveform precompute");
        _audioData.RawSamples = null!; // Free ~50MB+ for long audio, no longer needed

        await Task.Run(() =>
        {
            _specCache = PrecomputedAudioData.ComputeSpectrogram(
                _audioData.FilePath, _audioData.Duration);
        });
        LoadTimingLogger.Phase("Spectrogram precompute");

        LoadingText.Visibility = Visibility.Collapsed;

        _viewCenterTime = 0;
        _plotsConfigured = false;
        _specConfigured = false;
        _waveBitmap = null;
        _specBitmap = null;

        FileNameText.Text = System.IO.Path.GetFileName(filePath);
        SampleRateText.Text = $"{_audioData.SampleRate} Hz";
        DurationText.Text = $"{_audioData.Duration:F2}s";
        TimeText.Text = "0.000s";
        FpsText.Text = "FPS: -";

        // Reset FPS tracking
        _fpsFrameCount = 0;
        _lastFpsUpdateTime = _frameClock.Elapsed.TotalSeconds;

        PlayPauseBtn.IsEnabled = true;
        StopBtn.IsEnabled = true;
        PlayPauseEmoji.Text = "▶️";
        PlayPauseText.Text = Loc("Play");

        _isLoading = false;
        OpenBtn.IsEnabled = true;

        RenderVisuals();
        LoadTimingLogger.Phase("Render visuals");

        // Initialize timing state
        _globalOffset = 0.1;
        _rawPoints = new List<RawTimingPoint> { new RawTimingPoint(Guid.NewGuid(), 0, 120) };
        OffsetStepper.SetRange(0, _audioData.Duration);
        RefreshTimingPoints();
        SidebarPanel.Visibility = Visibility.Visible;
        OverlayCanvas.Visibility = Visibility.Visible;
        BeatRowCanvas.Visibility = Visibility.Visible;

        LoadTimingLogger.End($"Duration={_audioData.Duration:F2}s  SR={_audioData.SampleRate}Hz  Ch={_audioData.Channels}");
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
        PlayPauseEmoji.Text = "⏸️";
        PlayPauseText.Text = Loc("Pause");
    }

    private void PausePlayback()
    {
        CompositionTarget.Rendering -= OnRenderingFrame;
        if (_bgmStream != 0)
            Bass.BASS_ChannelPause(_bgmStream);
        _isPlaying = false;
        PlayPauseEmoji.Text = "▶️";
        PlayPauseText.Text = Loc("Play");
        FpsText.Text = "FPS: -";
    }

    private void JumpToStart()
    {
        if (_bgmStream == 0) return;

        CompositionTarget.Rendering -= OnRenderingFrame;
        if (_isPlaying)
        {
            Bass.BASS_ChannelPause(_bgmStream);
            _isPlaying = false;
            PlayPauseEmoji.Text = "▶️";
            PlayPauseText.Text = Loc("Play");
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

    // ── Playback seeking ──

    private void SeekBassTo(double seconds)
    {
        if (_bgmStream == 0) return;
        var bytePos = Bass.BASS_ChannelSeconds2Bytes(_bgmStream, seconds);
        Bass.BASS_ChannelSetPosition(_bgmStream, bytePos);
    }

    private void SetBothXLimits(double left, double right)
    {
        _viewHalfWidth = (right - left) / 2;
    }

    private void EnsurePlotsConfigured()
    {
        if (_audioData == null || _waveEnvelope == null || _specCache == null) return;

        if (!_plotsConfigured)
        {
            _plotsConfigured = true;

            // Generate WriteableBitmap once
            _waveBitmap = WaveformBitmapRenderer.Create(_waveEnvelope);
            WaveformImage.Source = _waveBitmap;

            WaveformCanvas.Visibility = Visibility.Visible;
            WaveformCanvas.UpdateLayout();

            // Initial X range (sets _viewHalfWidth)
            SetBothXLimits(0, _audioData.Duration);
        }

        if (!_specConfigured)
        {
            _specConfigured = true;

            // Generate WriteableBitmap once
            _specBitmap = SpectrogramBitmapRenderer.Create(_specCache);
            SpectrogramImage.Source = _specBitmap;

            SpectrogramCanvas.Visibility = Visibility.Visible;
            SpectrogramCanvas.UpdateLayout();
        }
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_plotsConfigured)
            UpdateWaveformTransform();
    }

    private void SpectrogramCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_specConfigured)
            UpdateSpectrogramTransform();
    }

    // ── Rendering ──

    private void UpdateWaveformTransform()
    {
        if (_waveEnvelope == null) return;
        double canvasW = WaveformCanvas.ActualWidth;
        if (canvasW <= 0) return;

        // pixelsPerSec = data columns per second of audio
        double pixelsPerSec = _waveEnvelope.Columns / _waveEnvelope.Duration;

        // Scale: fit (2 * viewHalfWidth) seconds into canvas width
        double scaleX = canvasW / (2.0 * _viewHalfWidth * pixelsPerSec);
        double canvasH = WaveformCanvas.ActualHeight;
        WaveScale.ScaleX = scaleX;
        WaveScale.ScaleY = canvasH > 0 ? canvasH / WaveformBitmapRenderer.BitmapHeight : 1.0;

        // Translate: left-align the view to (_viewCenterTime - viewHalfWidth) seconds
        double translateX = -(_viewCenterTime - _viewHalfWidth) * pixelsPerSec * scaleX;
        WaveTranslate.X = translateX;
        WaveTranslate.Y = 0;
    }

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
    }

    private void RenderVisuals()
    {
        if (_audioData == null || _waveEnvelope == null || _specCache == null) return;

        if (!_plotsConfigured || !_specConfigured)
            EnsurePlotsConfigured();

        // Waveform — only transform, no bitmap regeneration
        UpdateWaveformTransform();

        // Spectrogram — only transform, no bitmap regeneration
        UpdateSpectrogramTransform();

        // Overlay — beat grid lines + playhead
        RenderBeatGrid();
        RenderBeatRow();

        // ── FPS calculation ──
        if (_isPlaying)
        {
            _fpsFrameCount++;
            double now = _frameClock.Elapsed.TotalSeconds;
            double elapsed = now - _lastFpsUpdateTime;
            if (elapsed >= 0.3)
            {
                _currentFps = _fpsFrameCount / elapsed;
                _fpsFrameCount = 0;
                _lastFpsUpdateTime = now;
                FpsText.Text = $"FPS: {_currentFps:F0}";
            }
        }
        else
        {
            FpsText.Text = "FPS: -";
        }
    }

    // ── Coordinate conversion (OverlayCanvas pixel ↔ time) ──

    private double TimeToCanvasX(double time)
    {
        if (_audioData == null) return 0;
        double canvasW = OverlayCanvas.ActualWidth;
        if (canvasW <= 0) return 0;

        // Same transform as waveform: left edge = _viewCenterTime - _viewHalfWidth
        double leftTime = _viewCenterTime - _viewHalfWidth;
        double dataSpan = _viewHalfWidth * 2.0;
        return (time - leftTime) * canvasW / dataSpan;
    }

    private double CanvasXToTime(double x)
    {
        if (_audioData == null) return 0;
        double canvasW = OverlayCanvas.ActualWidth;
        if (canvasW <= 0) return 0;

        double leftTime = _viewCenterTime - _viewHalfWidth;
        double dataSpan = _viewHalfWidth * 2.0;
        return leftTime + x * dataSpan / canvasW;
    }

    // ── Timing refresh ──

    private void RefreshTimingPoints()
    {
        _timingPoints = TimingEngine.RecalculateTiming(_globalOffset, _rawPoints);
        OffsetStepper.SetValue(_globalOffset);
        RebuildSegmentList();

        if (_audioData != null && (_plotsConfigured || _specConfigured))
        {
            RenderBeatGrid();
            RenderBeatRow();
        }
    }

    // ── Raw points editing helpers ──

    private void UpdateRawBpm(Guid id, double bpm)
    {
        bpm = Math.Clamp(Math.Round(bpm * 1000.0) / 1000.0, 10, 1000);
        for (int i = 0; i < _rawPoints.Count; i++)
        {
            if (_rawPoints[i].Id == id)
            {
                _rawPoints[i] = new RawTimingPoint(id, _rawPoints[i].BeatIndex, bpm, _rawPoints[i].MaxBeatIndex);
                break;
            }
        }
        RefreshTimingPoints();
    }

    private void UpdateRawBeatIndex(Guid id, double beatIndex)
    {
        beatIndex = Math.Max(1, Math.Round(beatIndex));

        // Clamp to the frozen cap captured when this segment was created (does not change afterwards).
        int segIdx = -1;
        for (int i = 0; i < _rawPoints.Count; i++)
            if (_rawPoints[i].Id == id) { segIdx = i; break; }
        if (segIdx > 0)
        {
            double frozenMax = _rawPoints[segIdx].MaxBeatIndex;
            if (frozenMax < double.MaxValue)
                beatIndex = Math.Min(beatIndex, Math.Floor(frozenMax));
        }

        if (_rawPoints.Any(p => p.Id != id && Math.Abs(p.BeatIndex - beatIndex) < 0.001))
            return; // duplicate beat index — reject silently
        for (int i = 0; i < _rawPoints.Count; i++)
        {
            if (_rawPoints[i].Id == id)
            {
                _rawPoints[i] = new RawTimingPoint(id, beatIndex, _rawPoints[i].Bpm, _rawPoints[i].MaxBeatIndex);
                break;
            }
        }
        _rawPoints.Sort((a, b) => a.BeatIndex.CompareTo(b.BeatIndex));
        RefreshTimingPoints();
    }

    private void RemoveRawPoint(Guid id)
    {
        var target = _rawPoints.FirstOrDefault(p => p.Id == id);
        if (target.BeatIndex == 0) return; // anchor (beat 0) is never removable
        _rawPoints.RemoveAll(p => p.Id == id);
        RefreshTimingPoints();
    }

    /// <summary>
    /// Max beat index the segment at <paramref name="segIdx"/> may start at, so that its
    /// start time does not exceed the audio duration and it stays before the next segment.
    /// </summary>
    private long GetMaxBeatIndexForSegment(int segIdx)
    {
        // segIdx == _timingPoints.Count means a hypothetical new segment appended after the last.
        if (_audioData == null || segIdx <= 0 || segIdx > _timingPoints.Count)
            return long.MaxValue;

        var prev = _timingPoints[segIdx - 1];
        double duration = _audioData.Duration;

        // Segment time = prev.Time + (beat - prev.BeatIndex) * 60 / prev.Bpm  <=  duration
        double timeBasedMax = prev.BeatIndex + (duration - prev.Time) * prev.Bpm / 60.0;
        double max = Math.Floor(timeBasedMax);

        if (segIdx + 1 < _timingPoints.Count)
        {
            double nextBeat = _timingPoints[segIdx + 1].BeatIndex;
            max = Math.Min(max, nextBeat - 1);
        }

        // Always keep at least a 1-beat gap from the previous segment.
        return (long)Math.Max(prev.BeatIndex + 1, max);
    }

    // ── Segment list panel (sidebar) ──

    private void RebuildSegmentList()
    {
        SegmentListPanel.Children.Clear();

        for (int i = 0; i < _timingPoints.Count; i++)
        {
            var point = _timingPoints[i];
            bool isAnchor = Math.Abs(point.BeatIndex) < 0.001;
            var accent = Color.FromRgb(0x81, 0x8C, 0xF8);

            // A segment is illegal if its start time has been pushed past the audio end
            // (e.g. by a later global-offset increase). Warn with a red background.
            bool isIllegal = !isAnchor && _audioData != null && point.Time > _audioData.Duration;
            Color rowBg = isIllegal
                ? Color.FromRgb(0x3A, 0x1E, 0x1E)
                : Color.FromRgb(0x1A, 0x1A, 0x1A);

            var row = new Border
            {
                Background = new SolidColorBrush(rowBg),
                BorderBrush = new SolidColorBrush(accent),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Header label + (optional) remove button
            var header = new TextBlock
            {
                Text = string.Format(Loc("Segment_Label"), i),
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, 0);
            grid.Children.Add(header);

            if (!isAnchor)
            {
                var removeBtn = new Button
                {
                    Content = "✕",
                    Tag = point.Id,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    Padding = new Thickness(4, 0, 4, 0),
                    Height = 20
                };
                removeBtn.Click += RemoveSegmentBtn_Click;
                Grid.SetRow(removeBtn, 0);
                Grid.SetColumn(removeBtn, 1);
                grid.Children.Add(removeBtn);
            }

            // Beat + BPM inputs
            var inputsGrid = new Grid();
            inputsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10, GridUnitType.Star) });
            inputsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(23, GridUnitType.Star) });
            Grid.SetRow(inputsGrid, 2);
            Grid.SetColumnSpan(inputsGrid, 2);
            grid.Children.Add(inputsGrid);

            FrameworkElement beatField;
            if (isAnchor)
            {
                beatField = BuildSegmentStaticField(
                    Loc("Beat_Label"), "0", Color.FromRgb(0x99, 0x99, 0x99), 0);
            }
            else
            {
                beatField = BuildSegmentStepper(
                    Loc("Beat_Label"),
                    new[] { 1.0 }, 1, point.MaxBeatIndex, 0,
                    Color.FromRgb(0xDD, 0xDD, 0xDD),
                    point.Id, false, point.BeatIndex, 0,
                    v => UpdateRawBeatIndex(point.Id, v));
            }
            inputsGrid.Children.Add(beatField);

            var bpmPanel = BuildSegmentStepper(
                "BPM",
                new[] { 10.0, 1.0, 0.1 }, 10, 1000, 3,
                Color.FromRgb(0x00, 0xF2, 0xFF),
                point.Id, false, point.Bpm, 1,
                v => UpdateRawBpm(point.Id, v));
            inputsGrid.Children.Add(bpmPanel);

            // Start time footer (red + warning when the segment is illegal)
            var time = new TextBlock
            {
                Text = isIllegal
                    ? $"{Loc("StartTime_Label")}  {point.Time:F3}s  ⚠ {Loc("Segment_Illegal")}"
                    : $"{Loc("StartTime_Label")}  {point.Time:F3}s",
                Foreground = new SolidColorBrush(isIllegal
                    ? Color.FromRgb(0xEF, 0x44, 0x44)
                    : Color.FromRgb(0x81, 0x8C, 0xF8)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                FontWeight = FontWeights.Bold
            };
            Grid.SetRow(time, 4);
            Grid.SetColumnSpan(time, 2);
            grid.Children.Add(time);

            row.Child = grid;
            SegmentListPanel.Children.Add(row);
        }
    }

    private StackPanel BuildSegmentStaticField(string label, string value, Color foreColor, int column)
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

    private StackPanel BuildSegmentStepper(
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

    private void AddSegmentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_audioData == null || _timingPoints.Count == 0) return;

        // Snap to the nearest beat at the playhead position.
        double t = Math.Clamp(_viewCenterTime, 0, _audioData.Duration);
        double beatF = TimingEngine.GetBeatIndexAtTime(t, _timingPoints);
        long newBeat = Math.Max(1, (long)Math.Round(beatF));

        // Rule: only append after the last existing beat index.
        long maxBeat = (long)Math.Floor(_rawPoints.Max(p => p.BeatIndex));
        if (newBeat <= maxBeat)
            newBeat = maxBeat + 1;

        // Constraint: the new (last) segment's start time must not exceed audio duration.
        long durationMax = GetMaxBeatIndexForSegment(_timingPoints.Count); // hypothetical appended segment
        if (newBeat > durationMax)
            return; // no valid slot before the audio end — abort silently

        // Default BPM inherits from the last (previous) segment.
        // Freeze the beat-index cap at creation time; it will not change afterwards even if
        // offset/duration shift makes this segment's time invalid (UI turns red instead).
        double prevBpm = _rawPoints[^1].Bpm;
        _rawPoints.Add(new RawTimingPoint(Guid.NewGuid(), newBeat, prevBpm, durationMax));
        _rawPoints.Sort((a, b) => a.BeatIndex.CompareTo(b.BeatIndex));
        RefreshTimingPoints();
    }

    private void RemoveSegmentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid id)
            RemoveRawPoint(id);
    }

    // ── Beat density helpers (10 levels, based on pixels per beat) ──

    private static int GetVertInterval(double pxPerBeat) => pxPerBeat >= 8 ? 1 : pxPerBeat >= 3 ? 4 : 16;

    private static int GetShowInterval(double pxPerBeat) => pxPerBeat >= 8 ? 4 : pxPerBeat >= 3 ? 16 : 64;

    // ── Beat Grid rendering on OverlayCanvas ──

    private void RenderBeatGrid()
    {
        // Clear previous elements
        foreach (var el in _overlayElements)
            OverlayCanvas.Children.Remove(el);
        _overlayElements.Clear();

        if (_audioData == null || _timingPoints.Count == 0) return;

        double canvasW = OverlayCanvas.ActualWidth;
        double canvasH = OverlayCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;

        double dataSpan = _viewHalfWidth * 2.0;

        double leftTime = _viewCenterTime - _viewHalfWidth;
        double rightTime = _viewCenterTime + _viewHalfWidth;

        for (int i = 0; i < _timingPoints.Count; i++)
        {
            var point = _timingPoints[i];
            var nextPoint = (i + 1 < _timingPoints.Count) ? _timingPoints[i + 1] : (TimingPoint?)null;

            double interval = 60.0 / point.Bpm;
            double pxPerBeat = canvasW / dataSpan * interval;
            int vertInterval = GetVertInterval(pxPerBeat);
            // Find the first beat visible
            double startTimeOffset = Math.Max(0, leftTime - point.Time);
            int startRelBeat = Math.Max(0, (int)Math.Ceiling(startTimeOffset / interval));

            int relBeat = startRelBeat;
            double waveH = WaveformCanvas.ActualHeight;
            double beatRowH = BeatRowCanvas.ActualHeight;
            double specTop = waveH + beatRowH;

            while (true)
            {
                // Stop at the next segment's start beat — old segment owns no beats past it.
                if (nextPoint.HasValue && point.BeatIndex + relBeat >= nextPoint.Value.BeatIndex) break;

                double beatTime = point.Time + relBeat * interval;
                if (beatTime > rightTime) break;
                if (relBeat > 0 && beatTime > _audioData.Duration) break;

                double x = TimeToCanvasX(beatTime);
                if (x < -50 || x > canvasW + 50) { relBeat++; continue; }

                bool isSectionStart = (relBeat == 0);
                bool isWholeBeat = Math.Abs(relBeat - Math.Round((double)relBeat)) < 0.001;

                // Skip beats based on density
                if (!isSectionStart && relBeat % vertInterval != 0) { relBeat++; continue; }

                if (isWholeBeat)
                {
                    var color = isSectionStart
                        ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
                        : Brushes.White;
                    double thickness = isSectionStart ? 2.0 : 1.0;

                    // Line in waveform area only (Y=0 to waveH)
                    var waveLine = new Line
                    {
                        X1 = x, Y1 = 0, X2 = x, Y2 = waveH,
                        Stroke = color, StrokeThickness = thickness
                    };
                    _overlayElements.Add(waveLine);
                    OverlayCanvas.Children.Add(waveLine);

                    // Line in spectrogram area only (Y=specTop to canvasH)
                    var specLine = new Line
                    {
                        X1 = x, Y1 = specTop, X2 = x, Y2 = canvasH,
                        Stroke = color, StrokeThickness = thickness
                    };
                    _overlayElements.Add(specLine);
                    OverlayCanvas.Children.Add(specLine);
                }

                relBeat++;
            }
        }

        // Playhead line (yellow, centered)
        double playheadX = TimeToCanvasX(_viewCenterTime);
        if (playheadX >= -2 && playheadX <= canvasW + 2)
        {
            var playheadLine = new Line
            {
                X1 = playheadX, Y1 = 0, X2 = playheadX, Y2 = canvasH,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x00)), StrokeThickness = 2
            };
            _overlayElements.Add(playheadLine);
            OverlayCanvas.Children.Add(playheadLine);
        }
    }

    // ── Beat Row rendering (numbers between waveform and spectrogram) ──

    private void RenderBeatRow()
    {
        foreach (var el in _beatRowElements)
            BeatRowCanvas.Children.Remove(el);
        _beatRowElements.Clear();

        if (_audioData == null || _timingPoints.Count == 0) return;

        double canvasW = OverlayCanvas.ActualWidth;
        if (canvasW <= 0) return;

        double dataSpan = _viewHalfWidth * 2.0;

        double leftTime = _viewCenterTime - _viewHalfWidth;
        double rightTime = _viewCenterTime + _viewHalfWidth;

        for (int i = 0; i < _timingPoints.Count; i++)
        {
            var point = _timingPoints[i];
            var nextPoint = (i + 1 < _timingPoints.Count) ? _timingPoints[i + 1] : (TimingPoint?)null;

            double interval = 60.0 / point.Bpm;
            double pxPerBeat = canvasW / dataSpan * interval;
            int showInterval = GetShowInterval(pxPerBeat);
            double startTimeOffset = Math.Max(0, leftTime - point.Time);
            int startRelBeat = Math.Max(0, (int)Math.Ceiling(startTimeOffset / interval));

            int relBeat = startRelBeat;
            while (true)
            {
                // Stop at the next segment's start beat — old segment owns no beats past it.
                if (nextPoint.HasValue && point.BeatIndex + relBeat >= nextPoint.Value.BeatIndex) break;

                double beatTime = point.Time + relBeat * interval;
                if (beatTime > rightTime) break;
                if (beatTime > _audioData.Duration) break;

                double x = TimeToCanvasX(beatTime);
                if (x < -50 || x > canvasW + 50) { relBeat++; continue; }

                int globalBeatIndex = (int)point.BeatIndex + relBeat;

                // Show number + triangles based on density interval
                bool isSectionStart = (relBeat == 0);
                bool showHere = isSectionStart || (globalBeatIndex % showInterval == 0);

                if (showHere)
                {
                    var beatColor = isSectionStart
                        ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
                        : Brushes.White;
                    var displayColor = _dragDisplayColor ?? beatColor;

                    // Upper triangle ▲
                    var upTri = new TextBlock
                    {
                        Text = "▲",
                        Foreground = displayColor,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        TextAlignment = TextAlignment.Center,
                        Width = 30
                    };
                    Canvas.SetLeft(upTri, x - 15);
                    Canvas.SetTop(upTri, 0);
                    _beatRowElements.Add(upTri);
                    BeatRowCanvas.Children.Add(upTri);

                    // Beat number
                    var tb = new TextBlock
                    {
                        Text = globalBeatIndex.ToString(),
                        Foreground = displayColor,
                        FontSize = isSectionStart ? 14 : 12,
                        FontWeight = isSectionStart ? FontWeights.Bold : FontWeights.Normal,
                        FontFamily = new FontFamily("Consolas"),
                        TextAlignment = TextAlignment.Center,
                        Width = 30
                    };
                    Canvas.SetLeft(tb, x - 15);
                    Canvas.SetTop(tb, 12);
                    _beatRowElements.Add(tb);
                    BeatRowCanvas.Children.Add(tb);

                    // Lower triangle ▼
                    var downTri = new TextBlock
                    {
                        Text = "▼",
                        Foreground = displayColor,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        TextAlignment = TextAlignment.Center,
                        Width = 30
                    };
                    Canvas.SetLeft(downTri, x - 15);
                    Canvas.SetTop(downTri, 22);
                    _beatRowElements.Add(downTri);
                    BeatRowCanvas.Children.Add(downTri);
                }

                relBeat++;
            }
        }
    }

    // ── OverlayCanvas mouse events ──

    private void OverlayCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_audioData == null || _timingPoints.Count == 0) return;

        var pos = e.GetPosition(OverlayCanvas);
        double x = pos.X;
        double y = pos.Y;
        double mouseTime = CanvasXToTime(x);

        // Check if in BeatRow area (between waveform bottom and spectrogram top)
        double waveBottom = WaveformCanvas.ActualHeight;
        double beatRowTop = waveBottom;
        double beatRowBottom = waveBottom + BeatRowCanvas.ActualHeight;

        if (y >= beatRowTop && y <= beatRowBottom)
        {
            // Clicking near a visible triangle (within 15px) starts a drag.
            double nearestBeatIdx = TimingEngine.GetBeatIndexAtTime(mouseTime, _timingPoints);
            long globalIdx = (long)Math.Round(nearestBeatIdx);
            double beatTimeAtIdx = TimingEngine.GetTimeAtBeatIndex(globalIdx, _timingPoints);
            double pixelDist = Math.Abs(TimeToCanvasX(beatTimeAtIdx) - x);

            // Density based on the segment under the mouse (multi-BPM aware).
            var (segAtMouse, _) = TimingEngine.GetPointAtTime(mouseTime, _timingPoints);
            double dataSpan = _viewHalfWidth * 2.0;
            double canvasW = OverlayCanvas.ActualWidth;
            double pxPerBeat = canvasW / dataSpan * (60.0 / segAtMouse.Bpm);
            int showInterval = GetShowInterval(pxPerBeat);
            bool onTriangle = (globalIdx >= 0) && (globalIdx % showInterval == 0) && pixelDist < 15;

            if (onTriangle && globalIdx == 0)
            {
                // Drag the start anchor → adjust global offset
                _dragMode = DragMode.Offset;
                _dragStartX = x;
                _dragStartTime = mouseTime;
                _dragStartOffset = _globalOffset;
                _dragDisplayColor = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            }
            else if (onTriangle)
            {
                // Decide which segment's BPM this beat controls.
                // A section-start beat (== some non-first segment's BeatIndex) is owned by the PREVIOUS segment,
                // because its time is determined by the previous segment's BPM. Interior beats belong to their own segment.
                double targetSegBeat = segAtMouse.BeatIndex;
                for (int i = 1; i < _timingPoints.Count; i++)
                {
                    if (Math.Abs(_timingPoints[i].BeatIndex - globalIdx) < 0.001)
                    {
                        targetSegBeat = _timingPoints[i - 1].BeatIndex;
                        break;
                    }
                }

                _dragMode = DragMode.Bpm;
                _dragStartX = x;
                _dragStartTime = mouseTime;
                _dragBeatTarget = globalIdx;
                _dragTargetSegBeat = targetSegBeat;
                _dragDisplayColor = new SolidColorBrush(Color.FromRgb(0x00, 0xF2, 0xFF));
            }
            else
            {
                // Not on a triangle → pan global offset anywhere in the BeatRow
                _dragMode = DragMode.Offset;
                _dragStartX = x;
                _dragStartTime = mouseTime;
                _dragStartOffset = _globalOffset;
                _dragDisplayColor = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            }

            OverlayCanvas.CaptureMouse();
            if (_isPlaying) PausePlayback();
            return;
        }

        // Seek mode: only record start, seek happens on mouse up (no drag) or pan on move
        _dragMode = DragMode.Seek;
        _dragStartX = x;
        _dragStartTime = _viewCenterTime; // record starting view center for pan
        OverlayCanvas.CaptureMouse();
        if (_isPlaying) PausePlayback();
    }

    private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None || _audioData == null) return;

        var pos = e.GetPosition(OverlayCanvas);
        double x = pos.X;
        double mouseTime = CanvasXToTime(x);

        switch (_dragMode)
        {
            case DragMode.Offset:
            {
                double deltaTime = mouseTime - _dragStartTime;
                double newOffset = _dragStartOffset + deltaTime;
                _globalOffset = Math.Max(0, Math.Min(newOffset, _audioData.Duration));
                RefreshTimingPoints();
                break;
            }
            case DragMode.Bpm:
            {
                // Locate the target segment (whose BPM we edit) by its start beat index.
                // Its own Time is independent of its own BPM, so it stays fixed during the drag.
                int segIdx = -1;
                for (int i = 0; i < _timingPoints.Count; i++)
                {
                    if (Math.Abs(_timingPoints[i].BeatIndex - _dragTargetSegBeat) < 0.001)
                    {
                        segIdx = i;
                        break;
                    }
                }
                if (segIdx < 0) break;
                var seg = _timingPoints[segIdx];

                double beatsFromStart = _dragBeatTarget - seg.BeatIndex;
                double timeDiff = mouseTime - seg.Time;
                if (beatsFromStart > 0 && timeDiff > 0.001)
                {
                    double rawBpm = (beatsFromStart * 60.0) / timeDiff;
                    UpdateRawBpm(seg.Id, rawBpm);
                }
                break;
            }
            case DragMode.Seek:
            {
                double deltaPixel = x - _dragStartX;
                double dataSpan = _viewHalfWidth * 2.0;
                double canvasW = OverlayCanvas.ActualWidth;
                if (canvasW > 0)
                {
                    double deltaTime = -deltaPixel * dataSpan / canvasW;
                    _viewCenterTime = _dragStartTime + deltaTime;
                    _viewCenterTime = Math.Clamp(_viewCenterTime, 0, _audioData.Duration);
                    TimeText.Text = $"{_viewCenterTime:F3}s";
                    SeekBassTo(_viewCenterTime);
                    RenderVisuals();
                }
                break;
            }
        }
    }

    private void OverlayCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.Seek)
        {
            // If no significant drag movement, treat as click-to-seek
            var pos = e.GetPosition(OverlayCanvas);
            double deltaPx = Math.Abs(pos.X - _dragStartX);
            if (deltaPx < 3)
            {
                double mouseTime = CanvasXToTime(pos.X);
                if (_audioData != null)
                {
                    _viewCenterTime = Math.Clamp(mouseTime, 0, _audioData.Duration);
                    TimeText.Text = $"{_viewCenterTime:F3}s";
                    SeekBassTo(_viewCenterTime);
                    RenderVisuals();
                }
            }
        }

        _dragMode = DragMode.None;
        OverlayCanvas.ReleaseMouseCapture();
        if (_dragDisplayColor != null)
        {
            _dragDisplayColor = null;
            RenderBeatRow();
        }
    }

    // ── OverlayCanvas wheel (zoom / pan) ──

    private void OverlayCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomXOnly(e.Delta > 0);
        else
            SeekByWheel(e.Delta);
    }

    private void ZoomXOnly(bool zoomIn)
    {
        double factor = zoomIn ? 0.85 : 1.0 / 0.85;
        double newHalf = _viewHalfWidth * factor;

        if (_audioData != null && newHalf > _audioData.Duration)
            newHalf = _audioData.Duration;
        if (newHalf < 0.01)
            newHalf = 0.01;

        _viewHalfWidth = newHalf;
        RenderVisuals();
    }

    private void SeekByWheel(double delta)
    {
        if (_audioData == null) return;
        if (_isPlaying) PausePlayback();
        double panAmount = -delta * _viewHalfWidth / 1000;
        _viewCenterTime += panAmount;
        _viewCenterTime = Math.Clamp(_viewCenterTime, 0, _audioData.Duration);
        TimeText.Text = $"{_viewCenterTime:F3}s";
        SeekBassTo(_viewCenterTime);
        RenderVisuals();
    }

    // ── Import / Export config buttons ──

    private void ImportConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        // Placeholder for future implementation
    }

    private void ExportConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        // Placeholder for future implementation
    }
}
