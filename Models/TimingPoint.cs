namespace BpmMeasurer;

public readonly record struct RawTimingPoint(Guid Id, double BeatIndex, double Bpm, double MaxBeatIndex = double.MaxValue, int BeatsPerBar = 4);

public readonly record struct TimingPoint(Guid Id, double BeatIndex, double Bpm, double Time, double MaxBeatIndex = double.MaxValue, int BeatsPerBar = 4);
