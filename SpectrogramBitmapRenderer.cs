using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScottPlot;

namespace BpmMeasurer.Wpf;

/// <summary>
/// Converts a <see cref="SpectrogramData"/> to a <see cref="WriteableBitmap"/>.
/// Uses <see cref="WaveSpectrogramColormap"/> and <see cref="Range.Normalize"/>
/// exactly as ScottPlot's Heatmap does, guaranteeing pixel-level color identity.
/// </summary>
public static class SpectrogramBitmapRenderer
{
    public static WriteableBitmap Create(SpectrogramData data)
    {
        int w = data.Columns;
        int h = data.FreqBands;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);

        // Compute data range exactly as ScottPlot's Heatmap.Update() does
        var range = ComputeRange(data.Magnitudes);
        var colormap = new WaveSpectrogramColormap();

        // Fill a managed pixel buffer in parallel, then copy to back buffer in one shot.
        var pixels = new int[w * h];
        Parallel.For(0, h, PrecomputeParallel.Options, y =>
        {
            int srcY = h - 1 - y; // Flip Y: bitmap row 0 = top = highest frequency
            int rowOffset = y * w;
            for (int x = 0; x < w; x++)
            {
                double fraction = range.Normalize(data.Magnitudes[srcY, x], true);
                var spColor = colormap.GetColor(fraction);
                pixels[rowOffset + x] = unchecked((int)spColor.ARGB);
            }
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

    private static ScottPlot.Range ComputeRange(float[,] magnitudes)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        int h = magnitudes.GetLength(0);
        int w = magnitudes.GetLength(1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double v = magnitudes[y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        return new ScottPlot.Range(min, max);
    }
}
