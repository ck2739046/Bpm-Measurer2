using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BpmMeasurer;

/// <summary>
/// Manages a horizontally-tiled rendering of a precomputed audio bitmap (waveform or
/// spectrogram). Each tile is a small <see cref="WriteableBitmap"/> (≤ <see cref="TileWidth"/>
/// pixels wide) so every GPU texture stays well within hardware limits, avoiding the WPF
/// MIL internal tiling that triggers <c>UCEERR_RENDERTHREADFAILURE</c> on large bitmaps.
///
/// Lifecycle: <see cref="Build"/> creates all tiles once (audio load); <see cref="UpdateTransform"/>
/// is called every frame to position/scale/visibility-filter them; <see cref="Dispose"/> tears
/// them down (audio unload / window close).
/// </summary>
internal abstract class TileSet : IDisposable
{
    /// <summary>
    /// Per-tile pixel width. 2048 is safe for every GPU feature level (DX9+ cap is 4096;
    /// we leave headroom). Splitting a 200 s song (80k columns) yields ~40 tiles, well
    /// within what the WPF visual tree handles comfortably.
    /// </summary>
    public const int TileWidth = 2048;

    private protected readonly Panel _container;
    private protected readonly List<Tile> _tiles = new();
    private protected bool _disposed;

    private protected TileSet(Panel container)
    {
        _container = container;
    }

    /// <summary>Pixel columns per second of source data (Columns / Duration).</summary>
    public abstract double PixelsPerSecond { get; }

    /// <summary>Native bitmap height of one tile in pixels (256 for waveform, FreqBands for spectrogram).</summary>
    public abstract int BitmapPixelHeight { get; }

    public int TileCount => _tiles.Count;

    /// <summary>
    /// Repositions, rescales, and visibility-filters every tile for the current viewport.
    /// All tiles share the same scaleX/scaleY; only translateX and Visibility differ.
    /// </summary>
    public void UpdateTransform(double viewCenterTime, double viewHalfWidth, double canvasW, double canvasH)
    {
        if (_disposed || _tiles.Count == 0) return;
        if (canvasW <= 0) return;

        double dataSpan = viewHalfWidth * 2.0;
        double leftTime = viewCenterTime - viewHalfWidth;

        double pixelsPerSec = PixelsPerSecond;
        // Scale: fit dataSpan seconds into canvas width. Identical for every tile.
        double scaleX = canvasW / (dataSpan * pixelsPerSec);
        double scaleY = canvasH > 0 ? canvasH / BitmapPixelHeight : 1.0;

        // Guard against NaN/Infinity (e.g. viewHalfWidth collapsed to 0); skip update to avoid
        // pushing bad matrices into the render thread (the original crash vector).
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || Math.Abs(scaleX) > 1e7)
            return;

        foreach (var tile in _tiles)
        {
            // Pixel offset of this tile's left edge from the viewport's left edge.
            double translateX = (tile.TimeStart - leftTime) * canvasW / dataSpan;

            if (!double.IsFinite(translateX) || Math.Abs(translateX) > 1e9)
                continue;

            // Off-screen cull: hide tiles entirely outside [−tileWidthPx, canvasW].
            double tileWidthPx = tile.ColCount * scaleX;
            bool visible = translateX > -tileWidthPx && translateX < canvasW;
            if (tile.Image.Visibility != (visible ? Visibility.Visible : Visibility.Collapsed))
                tile.Image.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            // Only mutate transforms for visible tiles (cheap perf win + avoids feeding the
            // render thread transforms for culled content).
            if (!visible) continue;

            tile.Scale.ScaleX = scaleX;
            tile.Scale.ScaleY = scaleY;
            tile.Translate.X = translateX;
            tile.Translate.Y = 0;
        }
    }

    /// <summary>
    /// Removes every tile's Image from the container and drops references so the
    /// WriteableBitmaps can be GC'd / GPU-released. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var tile in _tiles)
        {
            _container.Children.Remove(tile.Image);
            tile.Image.Source = null;
        }
        _tiles.Clear();
    }

    private protected sealed class Tile
    {
        public required WriteableBitmap Bitmap { get; init; }
        public required Image Image { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TranslateTransform Translate { get; init; }
        /// <summary>Time (seconds) at the tile's left edge in the source data.</summary>
        public required double TimeStart { get; init; }
        public required int ColStart { get; init; }
        public required int ColCount { get; init; }
    }

    /// <summary>
    /// Builds a tile's Image element with a Scale+Translate RenderTransform group, matching
    /// the original single-Image XAML layout (Stretch=None, HighQuality scaling).
    /// </summary>
    private protected static Image CreateTileImage(WriteableBitmap bmp)
    {
        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        var image = new Image
        {
            Source = bmp,
            Stretch = Stretch.None,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.RenderTransform = new TransformGroup
        {
            Children = { scale, translate }
        };
        // Tag the transforms so the caller can fish them back out.
        image.Tag = (scale, translate);
        return image;
    }

    private protected static (ScaleTransform scale, TranslateTransform translate) GetTransforms(Image image)
    {
        var (scale, translate) = ((ScaleTransform, TranslateTransform))image.Tag!;
        return (scale, translate);
    }
}
