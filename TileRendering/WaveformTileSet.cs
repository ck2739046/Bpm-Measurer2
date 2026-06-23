using System.Windows.Controls;

namespace BpmMeasurer;

/// <summary>
/// Tiled waveform renderer. Splits a <see cref="WaveformEnvelope"/> into fixed-width
/// <see cref="WriteableBitmap"/> tiles and composites them via per-tile RenderTransforms.
/// See <see cref="TileSet"/> for the lifecycle and transform model.
/// </summary>
internal sealed class WaveformTileSet : TileSet
{
    private readonly WaveformEnvelope _env;

    public WaveformTileSet(WaveformEnvelope env, Panel container) : base(container)
    {
        _env = env;
    }

    public override double PixelsPerSecond => _env.Columns / _env.Duration;
    public override int BitmapPixelHeight => WaveformBitmapRenderer.BitmapHeight;

    /// <summary>
    /// Creates one Image+WriteableBitmap per tile and adds it to the container canvas.
    /// Tiles are non-overlapping: tile i covers columns [i*TileWidth, (i+1)*TileWidth).
    /// The last tile is narrower when <see cref="WaveformEnvelope.Columns"/> is not a
    /// multiple of <see cref="TileSet.TileWidth"/>.
    /// </summary>
    public void Build()
    {
        int totalCols = _env.Columns;
        int fullTiles = (totalCols + TileWidth - 1) / TileWidth;
        double timePerCol = _env.Duration / totalCols;

        for (int i = 0; i < fullTiles; i++)
        {
            int colStart = i * TileWidth;
            int colCount = System.Math.Min(TileWidth, totalCols - colStart);
            if (colCount <= 0) break;

            var bmp = WaveformBitmapRenderer.CreateTile(_env, colStart, colCount);
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
