using Un4seen.Bass;

namespace BpmMeasurer.Wpf;

public class BpmAudioData
{
    public string FilePath { get; init; } = "";
    public short[] RawSamples { get; set; } = Array.Empty<short>();
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public double Duration { get; init; }
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

            var totalSamples = (int)(length / sizeof(short));
            var raw = new short[totalSamples];

            int byteLen = (int)length;
            int bytesRead = Bass.BASS_ChannelGetData(stream, raw, byteLen);
            if (bytesRead < 0) bytesRead = 0;

            return new BpmAudioData
            {
                FilePath = filePath,
                RawSamples = raw,
                SampleRate = info.freq,
                Channels = info.chans,
                Duration = duration
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
