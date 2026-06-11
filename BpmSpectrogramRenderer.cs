using System.Windows.Media;
using System.Windows.Media.Imaging;
using Un4seen.Bass;

namespace BpmMeasurer.Wpf;

public static class BpmSpectrogramRenderer
{
    private const int FftSize = 8192;
    private const int NumBins = FftSize / 2;

    public static WriteableBitmap Render(BpmAudioData data, int width, int height, double logBase = 50.0)
    {
        var wb = new WriteableBitmap(width, height, 72, 72, PixelFormats.Pbgra32, null);
        wb.Lock();
        try
        {
            var backBuffer = wb.BackBuffer;
            var stride = wb.BackBufferStride;

            // Create decode stream for offline FFT reading
            var decodeStream = Bass.BASS_StreamCreateFile(
                data.FilePath, 0L, 0L,
                BASSFlag.BASS_STREAM_DECODE);
            if (decodeStream == 0) return wb;

            try
            {
                var fftData = new float[FftSize];

                for (int x = 0; x < width; x++)
                {
                    var t = x / (double)width * data.Duration;
                    var bytePos = Bass.BASS_ChannelSeconds2Bytes(decodeStream, t);
                    Bass.BASS_ChannelSetPosition(decodeStream, bytePos);
                    var result = Bass.BASS_ChannelGetData(decodeStream, fftData, (int)BASSData.BASS_DATA_FFT8192);
                    if (result <= 0)
                    {
                        // fill column with black
                        for (int y = 0; y < height; y++)
                            SetPixel(backBuffer, stride, x, y, 255, 0, 0, 0);
                        continue;
                    }

                    for (int y = 0; y < height; y++)
                    {
                        var row = height - 1 - y;

                        int binIdx;
                        if (logBase <= 1.0)
                        {
                            binIdx = (int)((y / (double)height) * (NumBins - 1));
                        }
                        else
                        {
                            var yNorm = y / (double)height;
                            binIdx = (int)(((Math.Pow(logBase, yNorm) - 1.0) / (logBase - 1.0)) * (NumBins - 1));
                        }

                        binIdx = Math.Clamp(binIdx, 0, NumBins - 1);
                        var magnitude = fftData[binIdx];

                        var (r, g, b) = MagnitudeToColor(magnitude);
                        SetPixel(backBuffer, stride, x, row, 255, r, g, b);
                    }
                }
            }
            finally
            {
                Bass.BASS_StreamFree(decodeStream);
            }

            wb.AddDirtyRect(new System.Windows.Int32Rect(0, 0, width, height));
            return wb;
        }
        finally
        {
            wb.Unlock();
        }
    }

    private static (byte r, byte g, byte b) MagnitudeToColor(double magnitude)
    {
        if (!double.IsFinite(magnitude)) return (0, 0, 0);

        // -80dB floor, matching original TS getSpectrogramColor
        var db = 20.0 * Math.Log10(magnitude + 1e-9);
        var val = Math.Max(0.0, Math.Min(1.0, (db + 80.0) / 80.0));

        double r, g, b;

        if (val < 0.25)
        {
            var t = val / 0.25;
            r = t * 128; g = 0; b = t * 128;
        }
        else if (val < 0.5)
        {
            var t = (val - 0.25) / 0.25;
            r = 128 + t * 127; g = 0; b = 128 * (1 - t);
        }
        else if (val < 0.75)
        {
            var t = (val - 0.5) / 0.25;
            r = 255; g = t * 255; b = 0;
        }
        else
        {
            var t = (val - 0.75) / 0.25;
            r = 255; g = 255; b = t * 255;
        }

        return ((byte)r, (byte)g, (byte)b);
    }

    private static unsafe void SetPixel(IntPtr backBuffer, int stride, int x, int y, byte a, byte r, byte g, byte b)
    {
        var ptr = (byte*)backBuffer + y * stride + x * 4;
        ptr[0] = b; // Blue
        ptr[1] = g; // Green
        ptr[2] = r; // Red
        ptr[3] = a; // Alpha
    }
}
