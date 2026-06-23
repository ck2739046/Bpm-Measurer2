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
}
