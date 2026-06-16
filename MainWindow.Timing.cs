using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BpmMeasurer;

/// <summary>
/// Timing-point CRUD (raw points ↔ computed timing points), the sidebar segment-list
/// rebuild, and the add/remove-segment button handlers. Extracted from MainWindow as a
/// partial — logic unchanged. Segment row UI is built via <see cref="SegmentRowFactory"/>.
/// </summary>
public partial class MainWindow
{
    // ── Timing refresh ──

    private void RefreshTimingPoints()
    {
        _timingPoints = TimingEngine.RecalculateTiming(_globalOffset, _rawPoints);
        OffsetStepper.SetValue(_globalOffset);
        RebuildSegmentList();

        if (_audioData != null && (_plotsConfigured || _specConfigured))
        {
            RenderBeatGrid();
            RenderBeatRow();
        }
    }

    // ── Raw points editing helpers ──

    private void UpdateRawBpm(Guid id, double bpm)
    {
        if (_isPlaying) PausePlayback();
        bpm = Math.Clamp(Math.Round(bpm * 1000.0) / 1000.0, 10, 1000);
        for (int i = 0; i < _rawPoints.Count; i++)
        {
            if (_rawPoints[i].Id == id)
            {
                _rawPoints[i] = new RawTimingPoint(id, _rawPoints[i].BeatIndex, bpm, _rawPoints[i].MaxBeatIndex);
                break;
            }
        }
        RefreshTimingPoints();
    }

    private void UpdateRawBeatIndex(Guid id, double beatIndex)
    {
        if (_isPlaying) PausePlayback();
        beatIndex = Math.Max(1, Math.Round(beatIndex));

        // Clamp to the frozen cap captured when this segment was created (does not change afterwards).
        int segIdx = -1;
        for (int i = 0; i < _rawPoints.Count; i++)
            if (_rawPoints[i].Id == id) { segIdx = i; break; }
        if (segIdx > 0)
        {
            double frozenMax = _rawPoints[segIdx].MaxBeatIndex;
            if (frozenMax < double.MaxValue)
                beatIndex = Math.Min(beatIndex, Math.Floor(frozenMax));
        }

        if (_rawPoints.Any(p => p.Id != id && Math.Abs(p.BeatIndex - beatIndex) < 0.001))
            return; // duplicate beat index — reject silently
        for (int i = 0; i < _rawPoints.Count; i++)
        {
            if (_rawPoints[i].Id == id)
            {
                _rawPoints[i] = new RawTimingPoint(id, beatIndex, _rawPoints[i].Bpm, _rawPoints[i].MaxBeatIndex);
                break;
            }
        }
        _rawPoints.Sort((a, b) => a.BeatIndex.CompareTo(b.BeatIndex));
        RefreshTimingPoints();
    }

    private void RemoveRawPoint(Guid id)
    {
        var target = _rawPoints.FirstOrDefault(p => p.Id == id);
        if (target.BeatIndex == 0) return; // anchor (beat 0) is never removable
        _rawPoints.RemoveAll(p => p.Id == id);
        RefreshTimingPoints();
    }

    /// <summary>
    /// Max beat index the segment at <paramref name="segIdx"/> may start at, so that its
    /// start time does not exceed the audio duration and it stays before the next segment.
    /// </summary>
    private long GetMaxBeatIndexForSegment(int segIdx)
    {
        // segIdx == _timingPoints.Count means a hypothetical new segment appended after the last.
        if (_audioData == null || segIdx <= 0 || segIdx > _timingPoints.Count)
            return long.MaxValue;

        var prev = _timingPoints[segIdx - 1];
        double duration = _audioData.Duration;

        // Segment time = prev.Time + (beat - prev.BeatIndex) * 60 / prev.Bpm  <=  duration
        double timeBasedMax = prev.BeatIndex + (duration - prev.Time) * prev.Bpm / 60.0;
        double max = Math.Floor(timeBasedMax);

        if (segIdx + 1 < _timingPoints.Count)
        {
            double nextBeat = _timingPoints[segIdx + 1].BeatIndex;
            max = Math.Min(max, nextBeat - 1);
        }

        // Always keep at least a 1-beat gap from the previous segment.
        return (long)Math.Max(prev.BeatIndex + 1, max);
    }

    // ── Segment list panel (sidebar) ──

    private void RebuildSegmentList()
    {
        SegmentListPanel.Children.Clear();

        for (int i = 0; i < _timingPoints.Count; i++)
        {
            var point = _timingPoints[i];
            bool isAnchor = Math.Abs(point.BeatIndex) < 0.001;
            var accent = Color.FromRgb(0x81, 0x8C, 0xF8);

            // A segment is illegal if its start time has been pushed past the audio end
            // (e.g. by a later global-offset increase). Warn with a red background.
            bool isIllegal = !isAnchor && _audioData != null && point.Time > _audioData.Duration;
            Color rowBg = isIllegal
                ? Color.FromRgb(0x3A, 0x1E, 0x1E)
                : Color.FromRgb(0x1A, 0x1A, 0x1A);

            var row = new Border
            {
                Background = new SolidColorBrush(rowBg),
                BorderBrush = new SolidColorBrush(accent),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Header label + (optional) remove button
            var header = new TextBlock
            {
                Text = string.Format(Loc("Segment_Label"), i),
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, 0);
            grid.Children.Add(header);

            if (!isAnchor)
            {
                var removeBtn = new Button
                {
                    Content = "✕",
                    Tag = point.Id,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    Padding = new Thickness(4, 0, 4, 0),
                    Height = 20
                };
                removeBtn.Click += RemoveSegmentBtn_Click;
                Grid.SetRow(removeBtn, 0);
                Grid.SetColumn(removeBtn, 1);
                grid.Children.Add(removeBtn);
            }

            // Beat + BPM inputs
            var inputsGrid = new Grid();
            inputsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10, GridUnitType.Star) });
            inputsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(23, GridUnitType.Star) });
            Grid.SetRow(inputsGrid, 2);
            Grid.SetColumnSpan(inputsGrid, 2);
            grid.Children.Add(inputsGrid);

            FrameworkElement beatField;
            if (isAnchor)
            {
                beatField = SegmentRowFactory.BuildStaticField(
                    Loc("Beat_Label"), "0", Color.FromRgb(0x99, 0x99, 0x99), 0);
            }
            else
            {
                beatField = SegmentRowFactory.BuildStepper(
                    Loc("Beat_Label"),
                    new[] { 1.0 }, 1, point.MaxBeatIndex, 0,
                    Color.FromRgb(0xDD, 0xDD, 0xDD),
                    point.Id, false, point.BeatIndex, 0,
                    v => UpdateRawBeatIndex(point.Id, v));
            }
            inputsGrid.Children.Add(beatField);

            var bpmPanel = SegmentRowFactory.BuildStepper(
                Loc("Bpm_Label"),
                new[] { 10.0, 1.0, 0.1 }, 10, 1000, 3,
                Color.FromRgb(0x00, 0xF2, 0xFF),
                point.Id, false, point.Bpm, 1,
                v => UpdateRawBpm(point.Id, v));
            inputsGrid.Children.Add(bpmPanel);

            // Start time footer (red + warning when the segment is illegal)
            var time = new TextBlock
            {
                Text = isIllegal
                    ? $"{Loc("StartTime_Label")}  {point.Time:F3}s  ⚠ {Loc("Segment_Illegal")}"
                    : $"{Loc("StartTime_Label")}  {point.Time:F3}s",
                Foreground = new SolidColorBrush(isIllegal
                    ? Color.FromRgb(0xEF, 0x44, 0x44)
                    : Color.FromRgb(0x81, 0x8C, 0xF8)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                FontWeight = FontWeights.Bold
            };
            Grid.SetRow(time, 4);
            Grid.SetColumnSpan(time, 2);
            grid.Children.Add(time);

            row.Child = grid;
            SegmentListPanel.Children.Add(row);
        }
    }

    private void AddSegmentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_audioData == null || _timingPoints.Count == 0) return;

        // Snap to the nearest beat at the playhead position.
        double t = Math.Clamp(_viewCenterTime, 0, _audioData.Duration);
        double beatF = TimingEngine.GetBeatIndexAtTime(t, _timingPoints);
        long newBeat = Math.Max(1, (long)Math.Round(beatF));

        // Rule: only append after the last existing beat index.
        long maxBeat = (long)Math.Floor(_rawPoints.Max(p => p.BeatIndex));
        if (newBeat <= maxBeat)
            newBeat = maxBeat + 1;

        // Constraint: the new (last) segment's start time must not exceed audio duration.
        long durationMax = GetMaxBeatIndexForSegment(_timingPoints.Count); // hypothetical appended segment
        if (newBeat > durationMax)
            return; // no valid slot before the audio end — abort silently

        // Default BPM inherits from the last (previous) segment.
        // Freeze the beat-index cap at creation time; it will not change afterwards even if
        // offset/duration shift makes this segment's time invalid (UI turns red instead).
        double prevBpm = _rawPoints[^1].Bpm;
        _rawPoints.Add(new RawTimingPoint(Guid.NewGuid(), newBeat, prevBpm, durationMax));
        _rawPoints.Sort((a, b) => a.BeatIndex.CompareTo(b.BeatIndex));
        RefreshTimingPoints();
    }

    private void RemoveSegmentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid id)
            RemoveRawPoint(id);
    }
}
