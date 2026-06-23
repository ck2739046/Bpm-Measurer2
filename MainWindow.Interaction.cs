using System.Windows.Input;
using System.Windows.Media;

namespace BpmMeasurer;

/// <summary>
/// OverlayCanvas pointer interaction: beat-drag (offset / BPM), click-to-seek, pan,
/// and mouse-wheel zoom / pan. Extracted from MainWindow as a partial.
/// </summary>
public partial class MainWindow
{
    // ── Focus management ──
    // Bubbling handler shared by top toolbar and bottom status bar (areas outside the two
    // focus regions). Clears keyboard focus and both region highlights — clicking outside
    // VizGrid / SidebarPanel deselects both regions.
    private void BlankArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Focus the overlay (a Focusable Canvas) instead of clearing keyboard focus.
        // ClearFocus() leaves FocusedElement == null, which destabilises WPF keyboard
        // routing and silently breaks the Window.PreviewKeyDown Space hotkey (play/pause)
        // when the user clicks the top toolbar or bottom status bar.
        Keyboard.Focus(OverlayCanvas);
        _plotAreaHasFocus = false;
        _sidebarHasFocus = false;
        _focusJustTransferred = false;
        UpdateFocusHighlights();
    }

    // Tunneling handler for the right sidebar: any press inside the sidebar (including on
    // buttons, text boxes, StepperInput) counts as focusing the sidebar region. Does NOT
    // clear keyboard focus, so TextBoxes can still receive keyboard focus for typing.
    private void SidebarPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _sidebarHasFocus = true;
        _plotAreaHasFocus = false;
        _focusJustTransferred = false;
        UpdateFocusHighlights();
    }

    // ── OverlayCanvas mouse events ──

    private void OverlayCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Move keyboard focus onto the overlay (a Focusable Canvas) instead of clearing it.
        // ClearFocus() during a mouse-captured drag leaves focus dangling on a non-focusable
        // element, which silently breaks Window.PreviewKeyDown (Space play/pause) until the
        // user clicks a focusable region. Focusing the overlay keeps the route valid for the
        // whole drag lifecycle and also pulls focus away from TextBoxes as before.
        Keyboard.Focus(OverlayCanvas);

        // Focus-region transfer: the first gesture entering the plot area only moves focus
        // (no seek / no offset / no BPM change). Subsequent gestures act normally.
        // A confirmed drag (>3px, detected in MouseMove) clears the flag and is processed directly.
        if (!_plotAreaHasFocus)
        {
            _plotAreaHasFocus = true;
            _sidebarHasFocus = false;
            _focusJustTransferred = true;
            UpdateFocusHighlights();
        }

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

        // First-gesture gate: while a focus transfer is pending and the pointer has not yet moved
        // beyond the 3px click-vs-drag threshold, suppress all drag actions. Once movement exceeds
        // the threshold, treat it as a real drag (focus transfer completes, normal processing resumes).
        if (_focusJustTransferred)
        {
            if (Math.Abs(x - _dragStartX) <= 3) return;
            _focusJustTransferred = false;
        }

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
        if (_dragMode == DragMode.Seek && !_focusJustTransferred)
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

        // Record a single collapsed undo entry for a completed timing drag (offset or
        // BPM). During the drag neither path calls RecordTimingIfChanged, so _lastRecorded
        // still holds the pre-drag baseline; comparing the post-drag state against it yields
        // exactly one entry per gesture (and none for a click-without-movement that changed
        // nothing). Seek is a view operation, not a timing edit, so it stays unrecorded.
        var endedDrag = _dragMode;
        if (endedDrag == DragMode.Offset || endedDrag == DragMode.Bpm)
            RecordTimingIfChanged();

        _dragMode = DragMode.None;
        _focusJustTransferred = false;
        OverlayCanvas.ReleaseMouseCapture();
        // Re-anchor keyboard focus on the overlay after releasing mouse capture, as a
        // safety net in case focus drifted during the drag. Keeps Window.PreviewKeyDown
        // (Space play/pause) responsive immediately after a drag-to-seek.
        Keyboard.Focus(OverlayCanvas);
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
