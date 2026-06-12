using Un4seen.Bass;

namespace BpmMeasurer.Wpf;

internal static class PrecomputeParallel
{
    /// <summary>
    /// Max worker threads for precomputation loops. Caps at <see cref="Environment.ProcessorCount"/>
    /// to avoid oversubscription. BASS decode streams are lightweight per-thread.
    /// </summary>
    public static readonly ParallelOptions Options = new()
    {
        MaxDegreeOfParallelism = Environment.ProcessorCount
    };
}

public class WaveformEnvelope
{
    public short[] MinValues { get; init; } = Array.Empty<short>();
    public short[] MaxValues { get; init; } = Array.Empty<short>();
    public int Columns { get; init; }
    public double TimeStep { get; init; }
    public double Duration { get; init; }
}

public class SpectrogramData
{
    public float[,] Magnitudes { get; init; } = new float[0, 0];
    public int FreqBands { get; init; }
    public int Columns { get; init; }
    public double TimeStep { get; init; }
    public double Duration { get; init; }
}

public static class PrecomputedAudioData
{
    public static WaveformEnvelope ComputeWaveform(short[] rawSamples, int channels, double duration)
    {
        const int columnsPerSecond = 100;
        var totalFrames = rawSamples.Length / Math.Max(1, channels);
        var columns = Math.Max(1, (int)(duration * columnsPerSecond));
        var framesPerColumn = Math.Max(1, totalFrames / columns);

        var minValues = new short[columns];
        var maxValues = new short[columns];

        Parallel.For(0, columns, PrecomputeParallel.Options, c =>
        {
            var startFrame = c * framesPerColumn;
            var endFrame = Math.Min(startFrame + framesPerColumn, totalFrames);
            short colMin = 0, colMax = 0;

            for (int f = startFrame; f < endFrame; f++)
            {
                var val = rawSamples[f * channels];
                if (val < colMin) colMin = val;
                if (val > colMax) colMax = val;
            }

            minValues[c] = colMin;
            maxValues[c] = colMax;
        });

        return new WaveformEnvelope
        {
            MinValues = minValues,
            MaxValues = maxValues,
            Columns = columns,
            TimeStep = 1.0 / columnsPerSecond,
            Duration = duration
        };
    }

    public static SpectrogramData ComputeSpectrogram(string filePath, double duration)
    {
        const int fftSize = 8192;
        const int numBins = fftSize / 2;
        const int freqBands = 256;          // 纵向分辨率
        const int columnsPerSecond = 100;   // 横向分辨率
        const double logBase = 50.0;
        const int fftDataFlag = (int)BASSData.BASS_DATA_FFT8192; // matches fftSize=8192

        var columns = Math.Max(1, (int)(duration * columnsPerSecond));

        // Quick smoke test: can we open the file at all?
        var testStream = Bass.BASS_StreamCreateFile(filePath, 0L, 0L, BASSFlag.BASS_STREAM_DECODE);
        if (testStream == 0)
            return new SpectrogramData
            {
                FreqBands = freqBands, Columns = 0,
                TimeStep = 1.0 / columnsPerSecond, Duration = duration
            };
        Bass.BASS_StreamFree(testStream);

        // Pre-compute bin indices for log-spaced frequency bands (single-threaded, only 256 iterations)
        var binIndices = new int[freqBands];
        for (int b = 0; b < freqBands; b++)
        {
            var bandNorm = b / (double)freqBands;
            var binIdx = (int)(((Math.Pow(logBase, bandNorm) - 1.0) / (logBase - 1.0)) * (numBins - 1));
            binIndices[b] = Math.Clamp(binIdx, 0, numBins - 1);
        }

        var timeStep = 1.0 / columnsPerSecond;
        var magnitudes = new float[freqBands, columns];

        // ── Parallel FFT columns ──
        // BASS decode streams are NOT thread-safe — each thread opens its own.
        Parallel.For(0, columns, PrecomputeParallel.Options, c =>
        {
            var stream = Bass.BASS_StreamCreateFile(filePath, 0L, 0L, BASSFlag.BASS_STREAM_DECODE);
            if (stream == 0)
            {
                for (int b = 0; b < freqBands; b++)
                    magnitudes[b, c] = 0;
                return;
            }

            try
            {
                var t = c * timeStep;
                var bytePos = Bass.BASS_ChannelSeconds2Bytes(stream, t);
                Bass.BASS_ChannelSetPosition(stream, bytePos);

                var fftData = new float[fftSize];
                var result = Bass.BASS_ChannelGetData(stream, fftData, fftDataFlag);

                if (result <= 0)
                {
                    for (int b = 0; b < freqBands; b++)
                        magnitudes[b, c] = 0;
                    return;
                }

                for (int b = 0; b < freqBands; b++)
                {
                    var mag = fftData[binIndices[b]];
                    var db = 20.0 * Math.Log10(mag + 1e-9);
                    magnitudes[b, c] = (float)Math.Clamp((db + 100.0) / 100.0, 0.0, 1.0);
                }
            }
            finally
            {
                Bass.BASS_StreamFree(stream);
            }
        });

        // ── Parallel Y-axis resampling ──
        const double yExp = 1.8;
        var resampled = new float[freqBands, columns];
        Parallel.For(0, freqBands, PrecomputeParallel.Options, y =>
        {
            var visualNorm = y / (double)(freqBands - 1);
            var srcBandFloat = Math.Pow(visualNorm, yExp) * (freqBands - 1);
            var srcLo = (int)srcBandFloat;
            var srcHi = Math.Min(srcLo + 1, freqBands - 1);
            var frac = srcBandFloat - srcLo;

            for (int c = 0; c < columns; c++)
            {
                resampled[y, c] = magnitudes[srcLo, c] * (float)(1.0 - frac)
                                + magnitudes[srcHi, c] * (float)frac;
            }
        });

        return new SpectrogramData
        {
            Magnitudes = resampled,
            FreqBands = freqBands,
            Columns = columns,
            TimeStep = timeStep,
            Duration = duration
        };
    }
}
