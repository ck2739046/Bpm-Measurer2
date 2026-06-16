using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BpmMeasurer;

/// <summary>
/// Converts a <see cref="WaveformEnvelope"/> to a <see cref="WriteableBitmap"/>.
/// Each envelope column (min/max) becomes one vertical pixel column; the min→max
/// span is filled with the waveform color, leaving the rest transparent. Mirrors
/// the GPU-composited, zero-per-frame-cost approach of <see cref="SpectrogramBitmapRenderer"/>.
/// </summary>
public static class WaveformBitmapRenderer
{
    /// <summary>
    /// Bitmap height in pixels. Wide enough to resolve short-sample excursions while
    /// keeping memory tiny (e.g. 256 px × 600 cols × 4 B ≈ 0.6 MB). Scaled by the
    /// Canvas at display time.
    /// </summary>
    public const int BitmapHeight = 256;

    // Very light red, fully opaque.
    private const int WaveColor = unchecked((int)0xFFFF7777);

    private const double ShortMin = -32768.0;
    private const double ShortMax = 32767.0;
    private const double ShortRange = ShortMax - ShortMin; // 65535

    public static WriteableBitmap Create(WaveformEnvelope env)
    {
        int w = env.Columns;
        int h = BitmapHeight;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);

        var mins = env.MinValues;
        var maxs = env.MaxValues;

        // Zero-initialized → fully transparent. Only the min→max span per column is painted.
        var pixels = new int[w * h];

        Parallel.For(0, w, PrecomputeParallel.Options, x =>
        {
            // Map short → normalized [0,1], then flip Y so positive values sit at the top.
            double normMin = (mins[x] - ShortMin) / ShortRange;
            double normMax = (maxs[x] - ShortMin) / ShortRange;

            int yTop = (int)Math.Round((1.0 - normMax) * (h - 1));
            int yBot = (int)Math.Round((1.0 - normMin) * (h - 1));

            if (yTop < 0) yTop = 0;
            if (yBot > h - 1) yBot = h - 1;
            // Guarantee ≥1 px: silence (min == max == 0) renders a single center pixel.
            if (yBot < yTop) yBot = yTop;

            for (int y = yTop; y <= yBot; y++)
                pixels[y * w + x] = WaveColor;
        });

        bmp.Lock();
        try
        {
            Marshal.Copy(pixels, 0, bmp.BackBuffer, pixels.Length);
            bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            bmp.Unlock();
        }

        return bmp;
    }
}
