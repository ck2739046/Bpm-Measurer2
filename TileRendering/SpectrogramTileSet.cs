using System.Windows.Controls;

namespace BpmMeasurer;

/// <summary>
/// Tiled spectrogram renderer. Splits a <see cref="SpectrogramData"/> into fixed-width
/// <see cref="WriteableBitmap"/> tiles. A single global <see cref="Range"/> (computed once)
/// is passed to every tile so brightness stays consistent across tile boundaries.
/// See <see cref="TileSet"/> for the lifecycle and transform model.
/// </summary>
internal sealed class SpectrogramTileSet : TileSet
{
    private readonly SpectrogramData _data;
    private readonly Range _globalRange;

    public SpectrogramTileSet(SpectrogramData data, Panel container) : base(container)
    {
        _data = data;
        // Compute the global min/max once over the entire spectrogram so that every tile
        // uses the same normalization — otherwise tile seams would show as brightness steps.
        _globalRange = SpectrogramBitmapRenderer.ComputeGlobalRange(data.Magnitudes);
    }

    public override double PixelsPerSecond => _data.Columns / _data.Duration;
    public override int BitmapPixelHeight => _data.FreqBands;

    public void Build()
    {
        int totalCols = _data.Columns;
        int fullTiles = (totalCols + TileWidth - 1) / TileWidth;
        double timePerCol = _data.Duration / totalCols;

        for (int i = 0; i < fullTiles; i++)
        {
            int colStart = i * TileWidth;
            int colCount = System.Math.Min(TileWidth, totalCols - colStart);
            if (colCount <= 0) break;

            var bmp = SpectrogramBitmapRenderer.CreateTile(_data, colStart, colCount, _globalRange);
            var image = CreateTileImage(bmp);
            var (scale, translate) = GetTransforms(image);

            _tiles.Add(new Tile
            {
                Bitmap = bmp,
                Image = image,
                Scale = scale,
                Translate = translate,
                TimeStart = colStart * timePerCol,
                ColStart = colStart,
                ColCount = colCount,
            });
            _container.Children.Add(image);
        }
    }
}
