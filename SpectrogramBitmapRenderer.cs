using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScottPlot;

namespace BpmMeasurer;

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

        var lut = WaveSpectrogramColormap.Lut;

        // Y-axis exponential remap (merged with Y-flip) — eliminates the ~60MB resampled array
        // that was previously allocated in PrecomputedAudioData.ComputeSpectrogram.
        const double yExp = 1.8;
        int maxSrcIndex = h - 1;

        // ── Phase 1: parallel min/max over raw magnitudes (cheap comparison, no Y remap) ──
        var mags = data.Magnitudes;
        var range = ComputeRangeParallel(mags);

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
                float mag = mags[srcLo, x] * oneMinusFrac
                          + mags[srcHi, x] * frac;
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
    /// Replaces the original single-threaded O(h·w) scan.
    /// </summary>
    internal static ScottPlot.Range ComputeRangeParallel(float[,] mags)
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
        return new ScottPlot.Range(globalMin, globalMax);
    }
}
