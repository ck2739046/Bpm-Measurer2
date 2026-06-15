using MathNet.Numerics.IntegralTransforms;
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
    private static readonly object StreamLock = new();

    public static WaveformEnvelope ComputeWaveform(short[] monoSamples, double duration)
    {
        const int columnsPerSecond = 100;
        var totalFrames = monoSamples.Length; // 已下混为 mono，长度即帧数
        var columns = Math.Max(1, (int)(duration * columnsPerSecond));
        var framesPerColumn = Math.Max(1, totalFrames / columns);

        var minValues = new short[columns];
        var maxValues = new short[columns];

        Parallel.For(0, columns, PrecomputeParallel.Options, c =>
        {
            int startFrame = c * framesPerColumn;
            int endFrame = Math.Min(startFrame + framesPerColumn, totalFrames);
            short colMin = 0, colMax = 0;

            for (int f = startFrame; f < endFrame; f++)
            {
                short val = monoSamples[f];
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
        const int freqBands = 256;
        const int columnsPerSecond = 200;
        const double logBase = 50.0;
        const float invFftSize = 1f / fftSize;

        // ── Precompute log bin indices (single-threaded, 256 iterations) ──
        var binIndices = new int[freqBands];
        for (int b = 0; b < freqBands; b++)
        {
            var bandNorm = b / (double)(freqBands - 1);
            var binIdx = (int)(((Math.Pow(logBase, bandNorm) - 1.0) / (logBase - 1.0)) * (numBins - 1));
            binIndices[b] = Math.Clamp(binIdx, 0, numBins - 1);
        }

        // ── Precompute Hann window ──
        var hannWindow = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
            hannWindow[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (fftSize - 1)));

        // ── Open a test stream to query sample rate ──
        var testStream = Bass.BASS_StreamCreateFile(filePath, 0L, 0L,
            BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT);
        if (testStream == 0)
            return new SpectrogramData
            {
                FreqBands = freqBands, Columns = 0,
                TimeStep = 1.0 / columnsPerSecond, Duration = duration
            };

        var channelInfo = Bass.BASS_ChannelGetInfo(testStream);
        int sampleRate = channelInfo.freq;
        Bass.BASS_StreamFree(testStream);

        // ── Derived timing / chunking parameters ──
        var timeStep = 1.0 / columnsPerSecond;
        var columns = Math.Max(1, (int)(duration * columnsPerSecond));
        int hopSamples = (int)Math.Round(sampleRate * timeStep);
        long totalMonoSamples = (long)(duration * sampleRate);

        var outputMagnitudes = new float[freqBands, columns];

        // ── Chunked parallel FFT ──
        // Each chunk: 1 Seek + 1 sequential PCM read → in-memory FFT
        var parallelism = PrecomputeParallel.Options.MaxDegreeOfParallelism;
        var chunkColumns = (columns + parallelism - 1) / parallelism;

        // ── Overflow guard: ensure each chunk's sample count fits in int ──
        long maxChunkSamples = (long)(chunkColumns - 1) * hopSamples + fftSize;
        if (maxChunkSamples > int.MaxValue || maxChunkSamples * sizeof(float) > int.MaxValue)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Audio too long ({duration:F1}s, {totalMonoSamples} samples). Cannot process spectrogram.");
            return new SpectrogramData
            {
                FreqBands = freqBands, Columns = 0,
                TimeStep = timeStep, Duration = duration
            };
        }
        var decodeFlags = BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT;

        Parallel.For(0, parallelism, PrecomputeParallel.Options, chunkIdx =>
        {
            int startCol = chunkIdx * chunkColumns;
            int endCol = Math.Min(startCol + chunkColumns, columns);
            if (startCol >= endCol) return;

            // ── PCM range for this chunk ──
            long pcmStartSample = startCol * (long)hopSamples;
            pcmStartSample = Math.Max(0, pcmStartSample);
            long pcmEndSample = (endCol - 1) * (long)hopSamples + fftSize;
            pcmEndSample = Math.Min(pcmEndSample, totalMonoSamples);

            int chunkSampleCount = (int)(pcmEndSample - pcmStartSample);
            if (chunkSampleCount <= 0) return;

            // ── Per-thread decode stream ──
            int stream;
            lock (StreamLock)
                stream = Bass.BASS_StreamCreateFile(filePath, 0L, 0L, decodeFlags);
            if (stream == 0) return;

            try
            {
                // Seek once to chunk start, read entire PCM block sequentially
                long pcmBytePos = pcmStartSample * sizeof(float);
                if (!Bass.BASS_ChannelSetPosition(stream, pcmBytePos))
                    return;

                var pcmBuffer = new float[chunkSampleCount];
                int bytesToRead = chunkSampleCount * sizeof(float);
                int bytesRead = Bass.BASS_ChannelGetData(stream, pcmBuffer, bytesToRead);
                int samplesRead = bytesRead / sizeof(float);
                if (samplesRead < bytesToRead / sizeof(float))
                    System.Diagnostics.Debug.WriteLine(
                        $"Partial read: got {samplesRead}/{chunkSampleCount} samples in chunk {chunkIdx}");
                if (samplesRead < fftSize) return;

                // ── Process columns in this chunk with MathNet FFT ──
                var real = new double[fftSize];
                var imag = new double[fftSize];
                for (int c = startCol; c < endCol; c++)
                {
                    int pcmOffset = (int)(c * (long)hopSamples - pcmStartSample);
                    if (pcmOffset < 0 || pcmOffset + fftSize > samplesRead)
                    {
                        for (int b = 0; b < freqBands; b++)
                            outputMagnitudes[b, c] = 0;
                        continue;
                    }

                    // Apply Hann window → real array (imag zero)
                    for (int i = 0; i < fftSize; i++)
                    {
                        real[i] = pcmBuffer[pcmOffset + i] * hannWindow[i];
                        imag[i] = 0.0;
                    }

                    Fourier.Forward(real, imag, FourierOptions.NoScaling);

                    for (int b = 0; b < freqBands; b++)
                    {
                        int bin = binIndices[b];
                        var mag = Math.Sqrt(real[bin] * real[bin] + imag[bin] * imag[bin]) * invFftSize;
                        var db = 20.0 * Math.Log10(mag + 1e-9);
                        outputMagnitudes[b, c] = (float)Math.Clamp((db + 100.0) / 100.0, 0.0, 1.0);
                    }
                }
            }
            finally
            {
                lock (StreamLock)
                    Bass.BASS_StreamFree(stream);
            }
        });

        // ── Y-axis resampling deferred to renderer (merged with Y-flip to save ~60MB) ──
        return new SpectrogramData
        {
            Magnitudes = outputMagnitudes,
            FreqBands = freqBands,
            Columns = columns,
            TimeStep = timeStep,
            Duration = duration
        };
    }
}
