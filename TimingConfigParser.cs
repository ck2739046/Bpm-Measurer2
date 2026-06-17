using System.Globalization;
using System.Text.RegularExpressions;

namespace BpmMeasurer;

/// <summary>
/// Validates and parses a timing config text. Pure function extracted from
/// MainWindow.TryParseConfig — no instance state, only uses the static Loc() lookup
/// for localized error messages.
/// </summary>
public static class TimingConfigParser
{
    /// <summary>
    /// Validates and parses a timing config text. Returns a localized error reason on failure,
    /// or null on success. Rules: (1) exactly one global_offset; (2) ≥1 segment; (3) each segment has
    /// both keys; (4) beat_index is a non-negative integer; (5) bpm is a non-negative number;
    /// (6) beat_index strictly ascending in file order; (7) first segment beat_index = 0.
    /// </summary>
    public static bool TryParse(
        string text, out double offset, out List<RawTimingPoint> points, out string? error)
    {
        offset = 0;
        points = new List<RawTimingPoint>();
        error = null;

        bool hasOffset = false;
        var parsed = new List<(long Beat, double Bpm, int Beats)>();
        long prevBeat = long.MinValue;
        int segNo = 0;

        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#") || line.StartsWith("//")) continue;

            // global_offset line
            if (Regex.IsMatch(line, @"global_offset\s*=", RegexOptions.IgnoreCase))
            {
                // Rule 1: exactly one global_offset is allowed.
                if (hasOffset)
                {
                    error = MainWindow.Loc("ConfigImport_Err_DuplicateOffset");
                    return false;
                }
                // Capture the value, allowing thousand separators (e.g. 1,200).
                var m = Regex.Match(line,
                    @"global_offset\s*=\s*(-?[\d,]+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (!m.Success
                    || !double.TryParse(m.Groups[1].Value, NumberStyles.Number,
                        CultureInfo.InvariantCulture, out offset))
                {
                    error = MainWindow.Loc("ConfigImport_Err_NoOffset");
                    return false;
                }
                // A global_offset line must not also contain a segment or a second offset.
                int offsetCount = Regex.Matches(line, @"global_offset\s*=", RegexOptions.IgnoreCase).Count;
                bool lineHasBeat = Regex.IsMatch(line, @"beat_index\s*=", RegexOptions.IgnoreCase);
                bool lineHasBpm = Regex.IsMatch(line, @"\bbpm\s*=", RegexOptions.IgnoreCase);
                if (offsetCount > 1 || lineHasBeat || lineHasBpm)
                {
                    error = MainWindow.Loc("ConfigImport_Err_MultipleInLine");
                    return false;
                }
                // Rule 1b: offset must be finite and non-negative.
                // (NumberStyles.Number rejects scientific notation like 1e2.)
                if (!double.IsFinite(offset) || offset < 0)
                {
                    error = MainWindow.Loc("ConfigImport_Err_NegativeOffset");
                    return false;
                }
                hasOffset = true;
                continue;
            }

            bool hasBeat = Regex.IsMatch(line, @"beat_index\s*=", RegexOptions.IgnoreCase);
            bool hasBpm = Regex.IsMatch(line, @"\bbpm\s*=", RegexOptions.IgnoreCase);

            // A line containing either keyword is a segment candidate.
            if (hasBeat || hasBpm)
            {
                segNo++;

                // Rule 3: both keys must be present.
                if (!hasBeat || !hasBpm)
                {
                    error = string.Format(MainWindow.Loc("ConfigImport_Err_MalformedSegment"), segNo);
                    return false;
                }

                // Rule 3b: a segment line must contain exactly one beat_index and one bpm
                // (no second segment, no global_offset mixed in).
                int beatCount = Regex.Matches(line, @"beat_index\s*=", RegexOptions.IgnoreCase).Count;
                int bpmCount = Regex.Matches(line, @"\bbpm\s*=", RegexOptions.IgnoreCase).Count;
                bool lineHasOffset = Regex.IsMatch(line, @"global_offset\s*=", RegexOptions.IgnoreCase);
                if (beatCount > 1 || bpmCount > 1 || lineHasOffset)
                {
                    error = string.Format(MainWindow.Loc("ConfigImport_Err_MultipleInLineSeg"), segNo);
                    return false;
                }

                // Rule 4: beat_index must be a non-negative integer.
                var bm = Regex.Match(line, @"beat_index\s*=\s*([^\s,]+)", RegexOptions.IgnoreCase);
                if (!bm.Success
                    || !long.TryParse(bm.Groups[1].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long beat)
                    || beat < 0)
                {
                    error = string.Format(MainWindow.Loc("ConfigImport_Err_BadBeatIndex"), segNo);
                    return false;
                }

                // Rule 5: bpm must be a positive finite number (int or float). Reject NaN/Infinity
                // and scientific notation (e.g. 1e2). Thousand separators are allowed (e.g. 1,200).
                var pm = Regex.Match(line, @"\bbpm\s*=\s*([^\s,]+(?:,[^\s,]+)*)", RegexOptions.IgnoreCase);
                if (!pm.Success
                    || !double.TryParse(pm.Groups[1].Value, NumberStyles.Number,
                        CultureInfo.InvariantCulture, out double bpm)
                    || !double.IsFinite(bpm)
                    || bpm <= 0)
                {
                    error = string.Format(MainWindow.Loc("ConfigImport_Err_BadBpm"), segNo);
                    return false;
                }

                // Rule 6: strictly ascending.
                if (beat <= prevBeat)
                {
                    error = string.Format(MainWindow.Loc("ConfigImport_Err_NotIncreasing"), segNo);
                    return false;
                }
                prevBeat = beat;

                bpm = Math.Clamp(bpm, 10, 1000);

                // Optional beats_per_bar (default 4, clamp 1-20 if present/out-of-range).
                int beats = 4;
                var bpb = Regex.Match(line, @"beats_per_bar\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                if (bpb.Success && int.TryParse(bpb.Groups[1].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int bpbVal))
                {
                    beats = Math.Clamp(bpbVal, 1, 20);
                }

                parsed.Add((beat, bpm, beats));
            }
            // Lines with neither keyword are ignored (comments / unknown).
        }

        // Rule 1: global_offset required (and must be exactly one — duplicates caught in the loop).
        if (!hasOffset)
        {
            error = MainWindow.Loc("ConfigImport_Err_NoOffset");
            return false;
        }

        // Rule 2: at least one segment.
        if (parsed.Count == 0)
        {
            error = MainWindow.Loc("ConfigImport_Err_NoSegment");
            return false;
        }

        // Rule 7: the first segment must be at beat_index 0 (the offset anchor).
        if (parsed[0].Beat != 0)
        {
            error = MainWindow.Loc("ConfigImport_Err_FirstBeatNotZero");
            return false;
        }

        // Build raw points. Imported points have no frozen cap.
        foreach (var (beat, bpm, beats) in parsed)
            points.Add(new RawTimingPoint(Guid.NewGuid(), beat, bpm, double.MaxValue, beats));

        // Ensure a beat-0 anchor exists (engine forces the first point to beat 0 anyway).
        if (!points.Any(p => Math.Abs(p.BeatIndex) < 0.001))
            points.Insert(0, new RawTimingPoint(Guid.NewGuid(), 0, 120, double.MaxValue));

        return true;
    }
}
