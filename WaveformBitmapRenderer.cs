using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BpmMeasurer;

/// <summary>
/// Converts a <see cref="WaveformEnvelope"/> to a <see cref="WriteableBitmap"/>.
/// Each envelope column (min/max) becomes one vertical pixel column; the min→max
/// span is filled with the waveform color, and the background is opaque black.
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

    // Opaque black background (Pbgra32: A=255, R=G=B=0).
    private const int BgColor = unchecked((int)0xFF000000);

    private const double ShortMin = -32768.0;
    private const double ShortMax = 32767.0;
    private const double ShortRange = ShortMax - ShortMin; // 65535

    public static WriteableBitmap Create(WaveformEnvelope env)
        => CreateTile(env, 0, env.Columns);

    /// <summary>
    /// Renders a horizontal slice of the envelope (columns [colStart, colStart+colCount))
    /// into a WriteableBitmap of width <paramref name="colCount"/>. Slicing the full
    /// envelope into small tiles keeps each WriteableBitmap within GPU texture limits
    /// (≤2048 px), avoiding WPF MIL internal tiling that triggers UCEERR_RENDERTHREADFAILURE.
    /// </summary>
    public static WriteableBitmap CreateTile(WaveformEnvelope env, int colStart, int colCount)
    {
        int w = colCount;
        int h = BitmapHeight;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);

        var mins = env.MinValues;
        var maxs = env.MaxValues;

        // Opaque black background.
        var pixels = new int[w * h];
        Array.Fill(pixels, BgColor);

        Parallel.For(0, w, PrecomputeParallel.Options, x =>
        {
            int srcCol = colStart + x;

            // Map short → normalized [0,1], then flip Y so positive values sit at the top.
            double normMin = (mins[srcCol] - ShortMin) / ShortRange;
            double normMax = (maxs[srcCol] - ShortMin) / ShortRange;

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
