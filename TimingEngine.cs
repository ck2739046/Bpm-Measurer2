using System;
using System.Collections.Generic;
using System.Linq;

namespace BpmMeasurer;

public static class TimingEngine
{
    public static IReadOnlyList<TimingPoint> RecalculateTiming(
        double offset,
        IReadOnlyList<RawTimingPoint> rawPoints)
    {
        if (rawPoints.Count == 0)
        {
            return new[] { new TimingPoint(Guid.NewGuid(), 0, 120, offset) };
        }

        var sorted = rawPoints.OrderBy(p => p.BeatIndex).ToList();
        var result = new List<TimingPoint>(sorted.Count);

        result.Add(new TimingPoint(sorted[0].Id, 0, sorted[0].Bpm, offset, sorted[0].MaxBeatIndex, sorted[0].BeatsPerBar));

        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = result[i - 1];
            var curr = sorted[i];

            var beatDiff = curr.BeatIndex - prev.BeatIndex;
            var duration = beatDiff * (60.0 / prev.Bpm);

            result.Add(new TimingPoint(curr.Id, curr.BeatIndex, curr.Bpm, prev.Time + duration, curr.MaxBeatIndex, curr.BeatsPerBar));
        }

        return result;
    }

    public static (TimingPoint Point, int Index) GetPointAtTime(
        double time,
        IReadOnlyList<TimingPoint> points)
    {
        for (int i = points.Count - 1; i >= 0; i--)
        {
            if (time >= points[i].Time)
                return (points[i], i);
        }
        return (points[0], 0);
    }

    public static double GetBeatIndexAtTime(
        double time,
        IReadOnlyList<TimingPoint> points)
    {
        var (point, _) = GetPointAtTime(time, points);
        var timeDiff = time - point.Time;
        var secondsPerBeat = 60.0 / point.Bpm;
        return point.BeatIndex + (timeDiff / secondsPerBeat);
    }

    public static double GetTimeAtBeatIndex(
        double beatIndex,
        IReadOnlyList<TimingPoint> points)
    {
        var point = points[0];
        for (int i = points.Count - 1; i >= 0; i--)
        {
            if (beatIndex >= points[i].BeatIndex)
            {
                point = points[i];
                break;
            }
        }

        var beatDiff = beatIndex - point.BeatIndex;
        return point.Time + beatDiff * (60.0 / point.Bpm);
    }
}
