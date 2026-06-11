using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using WPFLocalizeExtension.Extensions;
using Timer = System.Timers.Timer;

namespace BpmMeasurer.Wpf;

public partial class MainWindow : Window
{
    private BpmAudioData? _audioData;
    private int _bgmStream;
    private int _decodeStream; // kept alive for the lifetime of loaded audio
    private readonly Timer _timeTimer = new(20);
    private bool _isPlaying;

    private WriteableBitmap? _waveBitmap;
    private WriteableBitmap? _specBitmap;

    /// <summary>Resolve localized string at runtime, matching MajdataEdit pattern.</summary>
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

        _timeTimer.Elapsed += (_, _) =>
        {
            if (!_isPlaying || _bgmStream == 0) return;
            try
            {
                var pos = Bass.BASS_ChannelGetPosition(_bgmStream);
                var time = Bass.BASS_ChannelBytes2Seconds(_bgmStream, pos);
                Dispatcher.Invoke(() => TimeText.Text = $"{time:F3}s");
            }
            catch
            {
                // channel may become invalid between check and call
            }
        };
        _timeTimer.AutoReset = true;

        AllowDrop = true;
        DragEnter += (s, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
        };
        Drop += (s, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                LoadAudioFile(files[0]);
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
        _timeTimer.Stop();
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

    // ── Playback (matching MajdataEdit stream lifecycle) ──

    private void StopAndFreeStreams()
    {
        if (_bgmStream != 0)
        {
            Bass.BASS_ChannelStop(_bgmStream);
            Bass.BASS_StreamFree(_bgmStream);
            _bgmStream = 0;
        }
        if (_decodeStream != 0)
        {
            Bass.BASS_StreamFree(_decodeStream);
            _decodeStream = 0;
        }
        _isPlaying = false;
    }

    private void LoadAudioFile(string filePath)
    {
        StopAndFreeStreams();
        _timeTimer.Stop();

        _audioData = BpmAudioLoader.Load(filePath);
        if (_audioData == null)
        {
            MessageBox.Show(Loc("LoadError"), Loc("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Create decode+tempo stream once (like MajdataEdit initFromFile)
        _decodeStream = Bass.BASS_StreamCreateFile(filePath, 0L, 0L, BASSFlag.BASS_STREAM_DECODE);
        if (_decodeStream == 0)
        {
            _audioData = null;
            MessageBox.Show(Loc("LoadError"), Loc("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _bgmStream = BassFx.BASS_FX_TempoCreate(_decodeStream, BASSFlag.BASS_FX_FREESOURCE);

        FileNameText.Text = Path.GetFileName(filePath);
        SampleRateText.Text = $"{_audioData.SampleRate} Hz";
        DurationText.Text = $"{_audioData.Duration:F2}s";
        ChannelsText.Text = _audioData.Channels.ToString();
        TimeText.Text = "0.000s";

        PlayPauseBtn.IsEnabled = true;
        StopBtn.IsEnabled = true;
        PlayPauseBtn.Content = Loc("Play");

        RenderVisuals();
    }

    private void StartPlayback()
    {
        if (_bgmStream == 0) return;

        var active = Bass.BASS_ChannelIsActive(_bgmStream);

        // If paused, resume from current position
        if (active == BASSActive.BASS_ACTIVE_PAUSED)
        {
            Bass.BASS_ChannelPlay(_bgmStream, false);
        }
        else
        {
            // If at end, seek back to beginning
            var pos = Bass.BASS_ChannelGetPosition(_bgmStream);
            var time = Bass.BASS_ChannelBytes2Seconds(_bgmStream, pos);
            if (_audioData != null && time >= _audioData.Duration - 0.1)
                Bass.BASS_ChannelSetPosition(_bgmStream, 0);

            Bass.BASS_ChannelPlay(_bgmStream, false);
        }

        _isPlaying = true;
        _timeTimer.Start();
        PlayPauseBtn.Content = Loc("Pause");
    }

    private void PausePlayback()
    {
        if (_bgmStream != 0)
            Bass.BASS_ChannelPause(_bgmStream);
        _isPlaying = false;
        PlayPauseBtn.Content = Loc("Play");
    }

    private void JumpToStart()
    {
        if (_bgmStream == 0) return;

        // If playing, pause first (matching user requirement)
        if (_isPlaying)
        {
            Bass.BASS_ChannelPause(_bgmStream);
            _isPlaying = false;
            PlayPauseBtn.Content = Loc("Play");
        }

        Bass.BASS_ChannelSetPosition(_bgmStream, 0);
        TimeText.Text = "0.000s";
    }

    // ── Rendering ──

    private void RenderVisuals()
    {
        if (_audioData == null) return;

        var actualWidth = (int)VizGrid.ActualWidth;
        var actualHeight = (int)VizGrid.ActualHeight;
        if (actualWidth <= 0 || actualHeight <= 0) return;

        var waveHeight = actualHeight / 2;
        var specHeight = actualHeight - waveHeight;

        _waveBitmap = BpmWaveformRenderer.Render(
            _audioData, actualWidth, waveHeight,
            Color.FromRgb(0x00, 0xF2, 0xFF),
            Color.FromRgb(0x0A, 0x0A, 0x0A));
        WaveformImage.Source = _waveBitmap;

        _specBitmap = BpmSpectrogramRenderer.Render(
            _audioData, actualWidth, specHeight);
        SpectrogramImage.Source = _specBitmap;

        PlaceholderText.Visibility = Visibility.Collapsed;
    }
}
