using Un4seen.Bass;

namespace BpmMeasurer;

public class BpmAudioData
{
    public string FilePath { get; init; } = "";
    /// <summary>已下混为单声道（mono）的 PCM 样本，访问连续、缓存友好。</summary>
    public short[] RawSamples { get; set; } = Array.Empty<short>();
    public int SampleRate { get; init; }
    /// <summary>原始声道数（仅显示用）；RawSamples 已下混为 mono。</summary>
    public int Channels { get; init; }
    public double Duration { get; init; }
    /// <summary>mono RMS（归一化 0~1），加载时算（必须在 RawSamples 释放前）。用于节拍器自适应响度。</summary>
    public double RmsLevel { get; set; }
}

public static class BpmAudioLoader
{
    public static BpmAudioData? Load(string filePath)
    {
        var stream = Bass.BASS_StreamCreateFile(filePath, 0L, 0L, BASSFlag.BASS_STREAM_DECODE);
        if (stream == 0)
        {
            System.Diagnostics.Debug.WriteLine($"BASS_StreamCreateFile failed: {Bass.BASS_ErrorGetCode()}");
            return null;
        }

        try
        {
            var info = Bass.BASS_ChannelGetInfo(stream);
            var length = Bass.BASS_ChannelGetLength(stream, BASSMode.BASS_POS_BYTE);
            var duration = Bass.BASS_ChannelBytes2Seconds(stream, length);

            int channels = info.chans;
            var totalSamples = (int)(length / sizeof(short));
            var raw = new short[totalSamples];

            int byteLen = (int)length;
            int bytesRead = Bass.BASS_ChannelGetData(stream, raw, byteLen);
            if (bytesRead < 0) bytesRead = 0;

            // 优化点4：加载阶段下混成 mono，后续波形访问连续、缓存友好，内存减半
            short[] samples = raw;
            if (channels > 1)
            {
                int monoLen = totalSamples / channels;
                samples = new short[monoLen];
                for (int i = 0; i < monoLen; i++)
                {
                    int baseIdx = i * channels;
                    int sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                        sum += raw[baseIdx + ch];
                    samples[i] = (short)(sum / channels);
                }
            }

            // mono RMS（抽样以加速，对响度估计足够）——必须在 RawSamples 释放前算
            double rmsLevel = 0.0;
            {
                long n = samples.Length;
                int step = n > 200000 ? 8 : 1;
                double sumSq = 0.0;
                long count = 0;
                for (long k = 0; k < n; k += step)
                {
                    double v = samples[k] / 32768.0;
                    sumSq += v * v;
                    count++;
                }
                if (count > 0) rmsLevel = Math.Sqrt(sumSq / count);
            }

            return new BpmAudioData
            {
                FilePath = filePath,
                RawSamples = samples,
                SampleRate = info.freq,
                Channels = channels,
                Duration = duration,
                RmsLevel = rmsLevel
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            Bass.BASS_StreamFree(stream);
        }
    }
}
