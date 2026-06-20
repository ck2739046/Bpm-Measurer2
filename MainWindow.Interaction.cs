using System.Windows.Input;
using System.Windows.Media;

namespace BpmMeasurer;

/// <summary>
/// OverlayCanvas pointer interaction: beat-drag (offset / BPM), click-to-seek, pan,
/// and mouse-wheel zoom / pan. Extracted from MainWindow as a partial — logic unchanged.
/// </summary>
public partial class MainWindow
{
    // ── Focus management ──
    // Bubbling handler shared by top bar, bottom status bar, and sidebar
    // clicking their blank areas clears keyboard focus
    private void BlankArea_MouseDown(object sender, MouseButtonEventArgs e) => Keyboard.ClearFocus();

    // ── OverlayCanvas mouse events ──

    private void OverlayCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.ClearFocus();

        if (_audioData == null || _timingPoints.Count == 0) return;

        var pos = e.GetPosition(OverlayCanvas);
        double x = pos.X;
        double y = pos.Y;
        double mouseTime = CanvasXToTime(x);

        // Check if in BeatRow area (between waveform bottom and spectrogram top)
        double waveBottom = WaveformCanvas.ActualHeight;
        double beatRowTop = waveBottom;
        double beatRowBottom = waveBottom + BeatRowCanvas.ActualHeight;

        if (y >= beatRowTop && y <= beatRowBottom)
        {
            // Clicking near a visible triangle (within 15px) starts a drag.
            double nearestBeatIdx = TimingEngine.GetBeatIndexAtTime(mouseTime, _timingPoints);
            long globalIdx = (long)Math.Round(nearestBeatIdx);
            double beatTimeAtIdx = TimingEngine.GetTimeAtBeatIndex(globalIdx, _timingPoints);
            double pixelDist = Math.Abs(TimeToCanvasX(beatTimeAtIdx) - x);

            // Density based on the segment under the mouse (multi-BPM aware, per-segment time signature).
            var (segAtMouse, _) = TimingEngine.GetPointAtTime(mouseTime, _timingPoints);
            double dataSpan = _viewHalfWidth * 2.0;
            double canvasW = OverlayCanvas.ActualWidth;
            double pxPerBeat = canvasW / dataSpan * (60.0 / segAtMouse.Bpm);
            int densityInterval = BeatGridMath.GetBarDensityInterval(pxPerBeat, segAtMouse.BeatsPerBar);
            long relBeatAtMouse = globalIdx - (long)segAtMouse.BeatIndex;
            // A triangle is draggable iff it is visually shown: section-start (relBeat==0) is always
            // shown; otherwise the beat must land on the per-segment bar-density grid.
            bool onTriangle = (globalIdx >= 0) && pixelDist < 15
                              && (relBeatAtMouse == 0 || relBeatAtMouse % densityInterval == 0);

            if (onTriangle && globalIdx == 0)
            {
                // Drag the start anchor → adjust global offset
                _dragMode = DragMode.Offset;
                _dragStartX = x;
                _dragStartTime = mouseTime;
                _dragStartOffset = _globalOffset;
                _dragDisplayColor = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            }
            else if (onTriangle)
            {
                // Decide which segment's BPM this beat controls.
                // A section-start beat (== some non-first segment's BeatIndex) is owned by the PREVIOUS segment,
                // because its time is determined by the previous segment's BPM. Interior beats belong to their own segment.
                double targetSegBeat = segAtMouse.BeatIndex;
                for (int i = 1; i < _timingPoints.Count; i++)
                {
                    if (Math.Abs(_timingPoints[i].BeatIndex - globalIdx) < 0.001)
                    {
                        targetSegBeat = _timingPoints[i - 1].BeatIndex;
                        break;
                    }
                }

                _dragMode = DragMode.Bpm;
                _dragStartX = x;
                _dragStartTime = mouseTime;
                _dragBeatTarget = globalIdx;
                _dragTargetSegBeat = targetSegBeat;
                _dragDisplayColor = new SolidColorBrush(Color.FromRgb(0x00, 0xF2, 0xFF));
            }
            else
            {
                // Not on a triangle → pan global offset anywhere in the BeatRow
                _dragMode = DragMode.Offset;
                _dragStartX = x;
                _dragStartTime = mouseTime;
                _dragStartOffset = _globalOffset;
                _dragDisplayColor = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            }

            OverlayCanvas.CaptureMouse();
            if (_isPlaying) PausePlayback();
            return;
        }

        // Seek mode: only record start, seek happens on mouse up (no drag) or pan on move
        _dragMode = DragMode.Seek;
        _dragStartX = x;
        _dragStartTime = _viewCenterTime; // record starting view center for pan
        OverlayCanvas.CaptureMouse();
        if (_isPlaying) PausePlayback();
    }

    private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None || _audioData == null) return;

        var pos = e.GetPosition(OverlayCanvas);
        double x = pos.X;
        double mouseTime = CanvasXToTime(x);

        switch (_dragMode)
        {
            case DragMode.Offset:
            {
                double deltaTime = mouseTime - _dragStartTime;
                double newOffset = _dragStartOffset + deltaTime;
                _globalOffset = Math.Max(0, Math.Min(newOffset, _audioData.Duration));
                RefreshTimingPoints();
                break;
            }
            case DragMode.Bpm:
            {
                // Locate the target segment (whose BPM we edit) by its start beat index.
                // Its own Time is independent of its own BPM, so it stays fixed during the drag.
                int segIdx = -1;
                for (int i = 0; i < _timingPoints.Count; i++)
                {
                    if (Math.Abs(_timingPoints[i].BeatIndex - _dragTargetSegBeat) < 0.001)
                    {
                        segIdx = i;
                        break;
                    }
                }
                if (segIdx < 0) break;
                var seg = _timingPoints[segIdx];

                double beatsFromStart = _dragBeatTarget - seg.BeatIndex;
                double timeDiff = mouseTime - seg.Time;
                if (beatsFromStart > 0 && timeDiff > 0.001)
                {
                    double rawBpm = (beatsFromStart * 60.0) / timeDiff;
                    UpdateRawBpm(seg.Id, rawBpm);
                }
                break;
            }
            case DragMode.Seek:
            {
                double deltaPixel = x - _dragStartX;
                double dataSpan = _viewHalfWidth * 2.0;
                double canvasW = OverlayCanvas.ActualWidth;
                if (canvasW > 0)
                {
                    double deltaTime = -deltaPixel * dataSpan / canvasW;
                    _viewCenterTime = _dragStartTime + deltaTime;
                    _viewCenterTime = Math.Clamp(_viewCenterTime, 0, _audioData.Duration);
                    TimeText.Text = $"{_viewCenterTime:F3}s";
                    SeekBassTo(_viewCenterTime);
                    RenderVisuals();
                }
                break;
            }
        }
    }

    private void OverlayCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.Seek)
        {
            // If no significant drag movement, treat as click-to-seek
            var pos = e.GetPosition(OverlayCanvas);
            double deltaPx = Math.Abs(pos.X - _dragStartX);
            if (deltaPx < 3)
            {
                double mouseTime = CanvasXToTime(pos.X);
                if (_audioData != null)
                {
                    _viewCenterTime = Math.Clamp(mouseTime, 0, _audioData.Duration);
                    TimeText.Text = $"{_viewCenterTime:F3}s";
                    SeekBassTo(_viewCenterTime);
                    RenderVisuals();
                }
            }
        }

        _dragMode = DragMode.None;
        OverlayCanvas.ReleaseMouseCapture();
        if (_dragDisplayColor != null)
        {
            _dragDisplayColor = null;
            RenderBeatRow();
        }
    }

    // ── OverlayCanvas wheel (zoom / pan) ──

    private void OverlayCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomXOnly(e.Delta > 0);
        else
            SeekByWheel(e.Delta);
    }

    private void ZoomXOnly(bool zoomIn)
    {
        double factor = zoomIn ? 0.85 : 1.0 / 0.85;
        double newHalf = _viewHalfWidth * factor;

        if (_audioData != null && newHalf > _audioData.Duration)
            newHalf = _audioData.Duration;
        if (newHalf < 0.01)
            newHalf = 0.01;

        _viewHalfWidth = newHalf;
        RenderVisuals();
    }

    private void SeekByWheel(double delta)
    {
        if (_audioData == null) return;
        if (_isPlaying) PausePlayback();
        double panAmount = -delta * _viewHalfWidth / 1000;
        _viewCenterTime += panAmount;
        _viewCenterTime = Math.Clamp(_viewCenterTime, 0, _audioData.Duration);
        TimeText.Text = $"{_viewCenterTime:F3}s";
        SeekBassTo(_viewCenterTime);
        RenderVisuals();
    }
}
