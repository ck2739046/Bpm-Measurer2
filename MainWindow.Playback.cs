using Microsoft.Win32;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;

namespace BpmMeasurer;

/// <summary>
/// Audio playback (BASS) lifecycle, file loading, and the composition-frame
/// render driver. Extracted from MainWindow as a partial — shares all private
/// instance fields with MainWindow.xaml.cs; logic is unchanged.
/// </summary>
public partial class MainWindow
{
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        Bass.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_DEFAULT, handle);
        // 降低输出缓冲，提升 mixtime sync 触发的 click 与 BGM 的对齐精度
        // 顺序：先设 update period（影响 buffer 最小值 buffer>=update+1），再设 buffer
        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD, 5);
        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_BUFFER, 10);

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
        FreeMetronomeClicks();
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
    {ClearMetronomeSyncs();
        
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

        // 节拍器：预生成 click 采样，并按歌曲 RMS 计算自适应 BGM 响度
        EnsureMetronomeClicks();
        ComputeClickAndBgmGain();
        ApplyEffectiveBgmVolume();

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
        MetronomeBtn.IsEnabled = true;
        PlayPauseEmoji.Text = "▶️";
        PlayPauseText.Text = Loc("Play");

        _isLoading = false;
        OpenBtn.IsEnabled = true;

        RenderVisuals();
        LoadTimingLogger.Phase("Render visuals");

        // Initialize timing state
        _globalOffset = 0.0;
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

        if (_metronomeEnabled)
        {
            var pos = Bass.BASS_ChannelGetPosition(_bgmStream);
            ArmMetronome(Bass.BASS_ChannelBytes2Seconds(_bgmStream, pos));
        }
        ClearMetronomeSyncs();
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
        ClearMetronomeSyncs();
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
        RefillMetronomeIfNeeded(time);
    }

    // ── Playback seeking ──

    private void SeekBassTo(double seconds)
    {
        if (_bgmStream == 0) return;
        var bytePos = Bass.BASS_ChannelSeconds2Bytes(_bgmStream, seconds);
        Bass.BASS_ChannelSetPosition(_bgmStream, bytePos);
    }

    // ── Metronome: adaptive click gain (优先) + BGM 压制(仅响歌) ──

    private double _bgmAutoVol = 1.0;   // 节拍器开启时作用于 _bgmStream 的音量
    private double _clickVol = 1.0;     // 作用于每个 click channel 的音量（0~1）
    private const double HeadroomDb = 6.0;   // click 目标比歌曲 RMS 高 6 dB
    private const double MinClickVol = 0.5;  // click 最低音量（安静歌避免太轻）
    private const double MinBgmVol = 0.6;    // 仅当 click 拉满(1.0)仍不够时，BGM 才降，且最多降到 0.6

    /// <summary>
    /// 在歌曲加载时一次性决定响度策略：优先让 click 自身适配歌曲（VOL 0.5~1.0，BGM 不动）；
    /// 仅当歌曲太响、click 拉满仍达不到目标时，才适度降低 BGM。
    /// 结果写入 _clickVol 与 _bgmAutoVol。
    /// </summary>
    private void ComputeClickAndBgmGain()
    {
        _clickVol = 1.0;
        _bgmAutoVol = 1.0;
        if (_audioData == null || _metronomeClicks == null || _metronomeClicks.Length == 0)
            return;

        double songRms = _audioData.RmsLevel;
        if (songRms < 1e-6) songRms = 1e-6;
        // 基准取最弱档 RMS：保证连弱拍都能达到目标响度
        double baselineRms = double.MaxValue;
        foreach (var asset in _metronomeClicks)
            if (asset.Rms < baselineRms) baselineRms = asset.Rms;

        double headroom = Math.Pow(10.0, HeadroomDb / 20.0);     // click 目标 = songRms * headroom
        double needGain = (songRms * headroom) / baselineRms;     // 让弱拍达标所需的 click 增益

        if (needGain <= 1.0)
        {
            // click 自身足够：调 click gain，BGM 保持原音量
            _clickVol = Math.Clamp(needGain, MinClickVol, 1.0);
            _bgmAutoVol = 1.0;
        }
        else
        {
            // 歌曲太响：click 拉满，BGM 适度降低补足 headroom
            _clickVol = 1.0;
            _bgmAutoVol = Math.Clamp(1.0 / needGain, MinBgmVol, 1.0);
        }
    }

    /// <summary>按当前节拍器开关状态应用 BGM 音量：开→_bgmAutoVol，关→原音量。</summary>
    private void ApplyEffectiveBgmVolume()
    {
        if (_bgmStream == 0) return;
        double vol = _metronomeEnabled ? _bgmAutoVol : 1.0;
        Bass.BASS_ChannelSetAttribute(_bgmStream, BASSAttribute.BASS_ATTRIB_VOL, (float)vol);
    }

    private void MetronomeBtn_Click(object sender, RoutedEventArgs e)
    {
        _metronomeEnabled = !_metronomeEnabled;
        MetronomeEmoji.Text = _metronomeEnabled ? "🔊" : "🔇";
        MetronomeBtn.Background = new SolidColorBrush(
            _metronomeEnabled ? Color.FromRgb(0x1E, 0x6B, 0x3A)   // 启用：暗绿
                              : Color.FromRgb(0x3A, 0x3A, 0x3A));   // 关闭：灰
        ApplyEffectiveBgmVolume();
        if (_isPlaying)
        {
            if (_metronomeEnabled)
            {
                EnsureMetronomeClicks();
                var pos = Bass.BASS_ChannelGetPosition(_bgmStream);
                ArmMetronome(Bass.BASS_ChannelBytes2Seconds(_bgmStream, pos));
            }
            else
            {
                ClearMetronomeSyncs();
            }
        }
    }
}
