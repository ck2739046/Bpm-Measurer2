namespace BpmMeasurer;

/// <summary>
/// Serializes timing points to the textual config format (the inverse of
/// TimingConfigParser). Pure function extracted from MainWindow.ExportConfigBtn_Click.
/// </summary>
public static class TimingConfigSerializer
{
    /// <summary>
    /// Builds the config text. Illegal segments (non-anchor segments whose start time
    /// exceeds the audio duration) are skipped on export.
    /// </summary>
    public static string Serialize(double offset, IReadOnlyList<TimingPoint> points, double duration)
    {
        var lines = new List<string> { $"global_offset = {offset:F3}" };
        foreach (var p in points)
        {
            bool isAnchor = Math.Abs(p.BeatIndex) < 0.001;
            // Skip illegal segments (start time past audio end) on export.
            if (!isAnchor && p.Time > duration) continue;
            lines.Add($"beat_index = {(long)Math.Round(p.BeatIndex)}, bpm = {p.Bpm:F3}, beats_per_bar = {p.BeatsPerBar}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
