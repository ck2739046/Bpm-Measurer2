using System.Windows;

namespace BpmMeasurer;

/// <summary>
/// Canvas/bitmap configuration and the per-frame wave/spectrogram transforms
/// (scale + translate). Extracted from MainWindow as a partial.
/// Bitmaps are generated once in EnsurePlotsConfigured; thereafter only the
/// transform is updated each frame for GPU compositing.
/// </summary>
public partial class MainWindow
{
    private void SetBothXLimits(double left, double right)
    {
        _viewHalfWidth = (right - left) / 2;
    }

    private void EnsurePlotsConfigured()
    {
        if (_audioData == null || _waveEnvelope == null || _specCache == null) return;

        if (!_plotsConfigured)
        {
            // Initial X range first
            SetBothXLimits(0, _audioData.Duration);
            _plotsConfigured = true;

            // Build waveform tiles (one WriteableBitmap per TileWidth columns) and add their
            // Images to the canvas. Keeps every GPU texture within hardware limits, avoiding
            // the MIL internal tiling that crashed the render thread on large single bitmaps.
            _waveTileSet = new WaveformTileSet(_waveEnvelope, WaveformCanvas);
            _waveTileSet.Build();

            WaveformCanvas.Visibility = Visibility.Visible;
            WaveformCanvas.UpdateLayout();
        }

        if (!_specConfigured)
        {
            _specConfigured = true;

            _specTileSet = new SpectrogramTileSet(_specCache, SpectrogramCanvas);
            _specTileSet.Build();

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
        if (_waveTileSet == null) return;
        double canvasW = WaveformCanvas.ActualWidth;
        double canvasH = WaveformCanvas.ActualHeight;
        if (canvasW <= 0) return;

        // NaN/Infinity/extreme-scale guards live inside TileSet.UpdateTransform, which
        // skips pushing bad matrices to the render thread (the original crash vector).
        _waveTileSet.UpdateTransform(_viewCenterTime, _viewHalfWidth, canvasW, canvasH);
    }

    private void UpdateSpectrogramTransform()
    {
        if (_specTileSet == null) return;
        double canvasW = SpectrogramCanvas.ActualWidth;
        double canvasH = SpectrogramCanvas.ActualHeight;
        if (canvasW <= 0) return;

        _specTileSet.UpdateTransform(_viewCenterTime, _viewHalfWidth, canvasW, canvasH);
    }
}
