using Un4seen.Bass;

namespace BpmMeasurer.Wpf;

public class BpmAudioData
{
    public string FilePath { get; init; } = "";
    public short[] RawSamples { get; init; } = Array.Empty<short>();
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public double Duration { get; init; }
}

public static class BpmAudioLoader
{
    public static BpmAudioData? Load(string filePath)
    {
        var decodeStream = Bass.BASS_StreamCreateFile(filePath, 0L, 0L, BASSFlag.BASS_STREAM_DECODE);
        if (decodeStream == 0)
        {
            var err = Bass.BASS_ErrorGetCode();
            System.Diagnostics.Debug.WriteLine($"BASS_StreamCreateFile failed: {err}");
            return null;
        }

        try
        {
            var info = Bass.BASS_ChannelGetInfo(decodeStream);
            var duration = Bass.BASS_ChannelBytes2Seconds(decodeStream,
                Bass.BASS_ChannelGetLength(decodeStream, BASSMode.BASS_POS_BYTE));

            Bass.BASS_StreamFree(decodeStream);

            var sample = Bass.BASS_SampleLoad(filePath, 0, 0, 1, BASSFlag.BASS_DEFAULT);
            if (sample == 0) return null;

            var sampleInfo = Bass.BASS_SampleGetInfo(sample);
            var totalSamples = (long)(duration * sampleInfo.freq * sampleInfo.chans);
            var raw = new short[totalSamples];
            Bass.BASS_SampleGetData(sample, raw);

            Bass.BASS_SampleFree(sample);

            return new BpmAudioData
            {
                FilePath = filePath,
                RawSamples = raw,
                SampleRate = sampleInfo.freq,
                Channels = sampleInfo.chans,
                Duration = duration
            };
        }
        catch
        {
            if (decodeStream != 0) Bass.BASS_StreamFree(decodeStream);
            return null;
        }
    }
}
