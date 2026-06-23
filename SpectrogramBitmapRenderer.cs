using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BpmMeasurer;

/// <summary>
/// Converts a <see cref="SpectrogramData"/> to a <see cref="WriteableBitmap"/>.
/// Uses <see cref="WaveSpectrogramColormap"/> and <see cref="Range.Normalize"/>
/// to map normalized magnitude to color.
/// </summary>
public static class SpectrogramBitmapRenderer
{
    public static WriteableBitmap Create(SpectrogramData data)
        => CreateTile(data, 0, data.Columns, ComputeGlobalRange(data.Magnitudes));

    /// <summary>
    /// Parallel chunked min/max over raw magnitudes. Local per-thread reduction
    /// followed by a sequential global merge (O(threads), negligible). Public so
    /// tile-based callers can compute the global range once and pass it to every
    /// <see cref="CreateTile"/> call, keeping brightness consistent across tiles.
    /// </summary>
    public static Range ComputeGlobalRange(float[,] mags)
        => ComputeRangeParallel(mags);

    /// <summary>
    /// Renders a horizontal slice of the spectrogram (columns
    /// [colStart, colStart+colCount)) into a WriteableBitmap of width
    /// <paramref name="colCount"/>. The caller-supplied <paramref name="globalRange"/>
    /// ensures consistent brightness across tiles.
    /// </summary>
    public static WriteableBitmap CreateTile(SpectrogramData data, int colStart, int colCount, Range globalRange)
    {
        int w = colCount;
        int h = data.FreqBands;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);

        var lut = WaveSpectrogramColormap.Lut;

        // Y-axis exponential remap (merged with Y-flip into the pixel fill loop),
        // avoiding a separate resampled array.
        const double yExp = 1.8;
        int maxSrcIndex = h - 1;

        var mags = data.Magnitudes;
        var range = globalRange;

        // ── Phase 2: parallel pixel fill using the computed range ──
        var pixels = new int[w * h];
        Parallel.For(0, h, PrecomputeParallel.Options, y =>
        {
            double visualNorm = (h - 1.0 - y) / (double)maxSrcIndex;
            double srcBandFloat = Math.Pow(visualNorm, yExp) * maxSrcIndex;
            int srcLo = (int)srcBandFloat;
            int srcHi = Math.Min(srcLo + 1, maxSrcIndex);
            float frac = (float)(srcBandFloat - srcLo);
            float oneMinusFrac = 1f - frac;

            int rowOffset = y * w;
            for (int x = 0; x < w; x++)
            {
                int srcCol = colStart + x;
                float mag = mags[srcLo, srcCol] * oneMinusFrac
                          + mags[srcHi, srcCol] * frac;
                double fraction = range.Normalize(mag, true);
                pixels[rowOffset + x] = lut[(int)(fraction * 255)];
            }
        });

        // ── Phase 3: single blit to GPU back buffer ──
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

    /// <summary>
    /// Parallel chunked min/max over raw magnitudes. Local per-thread reduction
    /// followed by a sequential global merge (O(threads), negligible).
    /// </summary>
    private static Range ComputeRangeParallel(float[,] mags)
    {
        int h = mags.GetLength(0);
        int w = mags.GetLength(1);
        int parallelism = PrecomputeParallel.Options.MaxDegreeOfParallelism;
        var localRange = new (double Min, double Max)[parallelism];
        int chunkRows = (h + parallelism - 1) / parallelism;

        Parallel.For(0, parallelism, PrecomputeParallel.Options, tid =>
        {
            int yStart = tid * chunkRows;
            int yEnd = Math.Min(yStart + chunkRows, h);
            if (yStart >= yEnd) { localRange[tid] = (0, 0); return; }

            double locMin = double.MaxValue;
            double locMax = double.MinValue;
            for (int y = yStart; y < yEnd; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float v = mags[y, x];
                    if (v < locMin) locMin = v;
                    if (v > locMax) locMax = v;
                }
            }
            localRange[tid] = (locMin, locMax);
        });

        double globalMin = localRange[0].Min;
        double globalMax = localRange[0].Max;
        for (int i = 1; i < parallelism; i++)
        {
            if (localRange[i].Min < globalMin) globalMin = localRange[i].Min;
            if (localRange[i].Max > globalMax) globalMax = localRange[i].Max;
        }
        return new Range(globalMin, globalMax);
    }
}
