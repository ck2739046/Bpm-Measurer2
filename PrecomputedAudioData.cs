using System.Runtime.InteropServices;
using Un4seen.Bass;

namespace BpmMeasurer;

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
    /// <summary>Source sample rate (Hz). Stored so tile mapping can compute the true
    /// time of each column instead of assuming Duration/Columns (which drifts due to
    /// integer <see cref="FramesPerColumn"/> rounding and accumulated error on long audio).</summary>
    public int SampleRate { get; init; }
    /// <summary>Mono samples aggregated into one column (integer-truncated from
    /// totalFrames/columns). Stored so the true per-column time step
    /// (<see cref="TimeStep"/>) can be derived exactly.</summary>
    public int FramesPerColumn { get; init; }
    /// <summary>True time spanned by one column = <see cref="FramesPerColumn"/>/<see cref="SampleRate"/>.
    /// Replaces the old 1/columnsPerSecond constant, eliminating the cumulative drift between
    /// the waveform's nominal and actual sample positions on long audio.</summary>
    public double TimeStep { get; init; }
    public double Duration { get; init; }
}

public class SpectrogramData
{
    public float[,] Magnitudes { get; init; } = new float[0, 0];
    public int FreqBands { get; init; }
    public int Columns { get; init; }
    public int SampleRate { get; init; }
    /// <summary>FFT window length in samples (each column's magnitude is the spectrum of
    /// an <see cref="FftSize"/>-sample window starting at <c>column*hop</c> samples). Stored
    /// so tile mapping can compensate the window-center phase: a column's energy is centered
    /// at <c>column*hop + FftSize/2</c>, i.e. <c>WindowCenterOffset</c> after the column start.</summary>
    public int FftSize { get; init; }
    /// <summary>Time from a column's window start (at <c>column*hop</c> samples) to that
    /// window's energy center = <see cref="FftSize"/>/(2*<see cref="SampleRate"/>). Added to
    /// each tile's time origin so the spectrogram lines up with the waveform/playhead,
    /// instead of leading it by half a window (up to ~186 ms at 22050 Hz).</summary>
    public double WindowCenterOffset { get; init; }
    /// <summary>True time spanned by one column = hopSamples/<see cref="SampleRate"/>. Replaces
    /// the old 1/columnsPerSecond constant, eliminating the cumulative drift on long audio
    /// when <c>sampleRate*0.005</c> is not an integer (e.g. 44100 Hz → hop 220, not 220.5).</summary>
    public double TimeStep { get; init; }
    public double Duration { get; init; }
    /// <summary>
    /// Global min/max over <see cref="Magnitudes"/>, computed once on the background
    /// thread during <see cref="PrecomputedAudioData.ComputeSpectrogram"/>. Precomputed so
    /// the tiled renderer doesn't have to rescan the whole matrix on the UI thread at
    /// build time (one O(n) sweep per audio load), and so every tile shares identical
    /// brightness. Defaults to <c>Range(0,0)</c> for the empty-data early-return paths.
    /// </summary>
    public Range GlobalRange { get; init; }
}

public static class PrecomputedAudioData
{
    private static readonly object StreamLock = new();

    public static WaveformEnvelope ComputeWaveform(short[] monoSamples, double duration, int sampleRate)
    {
        const int columnsPerSecond = 400;
        var totalFrames = monoSamples.Length; // 已下混为 mono，长度即帧数
        var columns = Math.Max(1, (int)(duration * columnsPerSecond));
        var framesPerColumn = Math.Max(1, totalFrames / columns);

        var minValues = new short[columns];
        var maxValues = new short[columns];

        var parallelism = PrecomputeParallel.Options.MaxDegreeOfParallelism;
        var chunkColumns = (columns + parallelism - 1) / parallelism;

        Parallel.For(0, parallelism, PrecomputeParallel.Options, chunkIdx =>
        {
            int startCol = chunkIdx * chunkColumns;
            int endCol = Math.Min(startCol + chunkColumns, columns);
            if (startCol >= endCol) return;

            for (int c = startCol; c < endCol; c++)
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
            }
        });

        return new WaveformEnvelope
        {
            MinValues = minValues,
            MaxValues = maxValues,
            Columns = columns,
            SampleRate = sampleRate,
            FramesPerColumn = framesPerColumn,
            // True per-column step from actual sample positions, not the nominal
            // 1/columnsPerSecond: this keeps the waveform's time axis anchored to real
            // PCM positions so it stays aligned with the spectrogram and the playhead
            // even when totalFrames is not an exact multiple of columns.
            TimeStep = sampleRate > 0 ? framesPerColumn / (double)sampleRate : 1.0 / columnsPerSecond,
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

        // ── Create shared FFTW plan (planner is NOT thread-safe) ──
        UIntPtr planInputBytes = (UIntPtr)(fftSize * sizeof(float));
        UIntPtr planOutputBytes = (UIntPtr)((fftSize + 2) * sizeof(float));
        IntPtr planIn = FftwNative.fftwf_malloc(planInputBytes);
        IntPtr planOut = FftwNative.fftwf_malloc(planOutputBytes);
        IntPtr sharedPlan = FftwNative.fftwf_plan_dft_r2c_1d(fftSize, planIn, planOut, FftwNative.FFTW_ESTIMATE);
        try
        {
            if (sharedPlan == IntPtr.Zero)
                return new SpectrogramData
                {
                    FreqBands = freqBands, Columns = 0,
                    TimeStep = timeStep, Duration = duration
                };

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
                if (samplesRead < fftSize) return;

                // ── Per-thread FFTW buffers (thread-safe new-array execute) ──
                UIntPtr inputBytes = (UIntPtr)(fftSize * sizeof(float));
                UIntPtr outputBytes = (UIntPtr)((fftSize + 2) * sizeof(float));
                IntPtr fftIn = FftwNative.fftwf_malloc(inputBytes);
                IntPtr fftOut = FftwNative.fftwf_malloc(outputBytes);
                if (fftIn == IntPtr.Zero || fftOut == IntPtr.Zero)
                {
                    FftwNative.fftwf_free(fftIn);
                    FftwNative.fftwf_free(fftOut);
                    return;
                }

                var fftInBuf = new float[fftSize];
                var fftOutBuf = new float[fftSize + 2];

                try
                {
                    for (int c = startCol; c < endCol; c++)
                    {
                        int pcmOffset = (int)(c * (long)hopSamples - pcmStartSample);
                        if (pcmOffset < 0 || pcmOffset + fftSize > samplesRead)
                        {
                            for (int b = 0; b < freqBands; b++)
                                outputMagnitudes[b, c] = 0;
                            continue;
                        }

                        // Apply Hann window → managed input buffer → FFTW input
                        for (int i = 0; i < fftSize; i++)
                            fftInBuf[i] = pcmBuffer[pcmOffset + i] * hannWindow[i];
                        Marshal.Copy(fftInBuf, 0, fftIn, fftSize);

                        FftwNative.fftwf_execute_dft_r2c(sharedPlan, fftIn, fftOut);

                        // Pull interleaved complex output back to managed buffer
                        Marshal.Copy(fftOut, fftOutBuf, 0, fftSize + 2);
                        for (int b = 0; b < freqBands; b++)
                        {
                            int bin = binIndices[b];
                            float re = fftOutBuf[2 * bin];
                            float im = fftOutBuf[2 * bin + 1];
                            float mag = MathF.Sqrt(re * re + im * im) * invFftSize;
                            float db = 20f * MathF.Log10(mag + 1e-9f);
                            outputMagnitudes[b, c] = Math.Clamp((db + 100f) / 100f, 0f, 1f);
                        }
                    }
                }
                finally
                {
                    FftwNative.fftwf_free(fftIn);
                    FftwNative.fftwf_free(fftOut);
                }
            }
            finally
            {
                lock (StreamLock)
                    Bass.BASS_StreamFree(stream);
            }
        });
        }
        finally
        {
            FftwNative.fftwf_destroy_plan(sharedPlan);
            FftwNative.fftwf_free(planIn);
            FftwNative.fftwf_free(planOut);
        }

        // ── Y-axis resampling deferred to renderer (merged with Y-flip to save ~60MB) ──
        return new SpectrogramData
        {
            Magnitudes = outputMagnitudes,
            FreqBands = freqBands,
            Columns = columns,
            SampleRate = sampleRate,
            FftSize = fftSize,
            // Window-center phase compensation: column c's energy is centered at
            // c*hop + fftSize/2 samples, i.e. fftSize/(2*sampleRate) seconds after the
            // nominal column time. Without this the spectrogram leads the waveform/playhead.
            WindowCenterOffset = sampleRate > 0 ? fftSize / (2.0 * sampleRate) : 0.0,
            // True per-column step from actual hop size, not the nominal 1/columnsPerSecond:
            // eliminates the cumulative drift on long audio whose sampleRate*0.005 is not
            // an integer (e.g. 44100 Hz → hop rounds to 220, not 220.5).
            TimeStep = sampleRate > 0 ? hopSamples / (double)sampleRate : timeStep,
            Duration = duration,
            // Precompute the global brightness range here (background thread) so the tiled
            // renderer doesn't rescan the whole matrix on the UI thread, and all tiles share
            // the same normalization.
            GlobalRange = SpectrogramBitmapRenderer.ComputeGlobalRange(outputMagnitudes)
        };
    }
}
