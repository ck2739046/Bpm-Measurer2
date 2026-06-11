using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace BpmMeasurer.Wpf;

public static class BpmWaveformRenderer
{
    public static WriteableBitmap Render(BpmAudioData data, int width, int height,
        MediaColor foreColor, MediaColor backColor)
    {
        var wb = new WriteableBitmap(width, height, 72, 72, PixelFormats.Pbgra32, null);
        wb.Lock();
        try
        {
            var raw = data.RawSamples;
            if (raw.Length == 0 || data.Channels == 0)
            {
                using var g = Graphics.FromImage(
                    new Bitmap(width, height, wb.BackBufferStride, PixelFormat.Format32bppArgb, wb.BackBuffer));
                g.Clear(System.Drawing.Color.FromArgb(backColor.A, backColor.R, backColor.G, backColor.B));
                g.Dispose();
                wb.AddDirtyRect(new System.Windows.Int32Rect(0, 0, width, height));
                return wb;
            }

            using var graphics = Graphics.FromImage(
                new Bitmap(width, height, wb.BackBufferStride, PixelFormat.Format32bppArgb, wb.BackBuffer));
            graphics.Clear(System.Drawing.Color.FromArgb(backColor.A, backColor.R, backColor.G, backColor.B));

            var fgPen = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(foreColor.A, foreColor.R, foreColor.G, foreColor.B), 1);

            var totalFrames = raw.Length / Math.Max(1, data.Channels);
            var framesPerCol = Math.Max(1, totalFrames / width);
            var centerY = height / 2.0;
            // scale so that full 16-bit range maps to height
            var ampScale = height / 65536.0;

            for (int x = 0; x < width; x++)
            {
                var startFrame = x * framesPerCol;
                var endFrame = Math.Min(startFrame + framesPerCol, totalFrames);

                short min = 0, max = 0;
                var stride = Math.Max(1, (endFrame - startFrame) / 20);
                for (int f = startFrame; f < endFrame; f += stride)
                {
                    var idx = f * data.Channels;
                    var val = raw[idx];
                    if (val < min) min = val;
                    if (val > max) max = val;
                }

                var yMax = centerY - max * ampScale;
                var yMin = centerY - min * ampScale;
                graphics.DrawLine(fgPen, x, (float)yMax, x, (float)yMin);
            }

            fgPen.Dispose();
            graphics.Dispose();

            wb.AddDirtyRect(new System.Windows.Int32Rect(0, 0, width, height));
            return wb;
        }
        finally
        {
            wb.Unlock();
        }
    }
}
