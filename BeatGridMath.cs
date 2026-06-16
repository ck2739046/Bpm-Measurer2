namespace BpmMeasurer;

/// <summary>
/// Beat density helpers (10 levels, based on pixels per beat).
/// Pure functions extracted from MainWindow for testability and reuse.
/// </summary>
public static class BeatGridMath
{
    public static int GetVertInterval(double pxPerBeat) => pxPerBeat >= 8 ? 1 : pxPerBeat >= 3 ? 4 : 16;

    public static int GetShowInterval(double pxPerBeat) => pxPerBeat >= 8 ? 4 : pxPerBeat >= 3 ? 16 : 64;
}
