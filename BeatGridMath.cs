namespace BpmMeasurer;

/// <summary>
/// Beat density helpers.
/// <para><see cref="GetVertInterval"/> selects the vertical-beat-line density (fixed gears by
/// pixels per beat).</para>
/// <para><see cref="GetBarDensityInterval"/> selects the triangle/number density: anchored to each
/// segment's time signature (1 per bar baseline), halving beat density (doubling the interval) while
/// too dense — no gears. Pure functions extracted from MainWindow.</para>
/// </summary>
public static class BeatGridMath
{
    public static int GetVertInterval(double pxPerBeat) => pxPerBeat >= 8 ? 1 : pxPerBeat >= 3 ? 4 : 16;

    /// <summary>
    /// Returns the beat interval for triangle/number markers, anchored to a segment's time
    /// signature. Baseline = 1 marker per bar (<paramref name="beatsPerBar"/> beats); while the
    /// on-screen spacing stays below <paramref name="minPxPerTriangle"/> (default ~triangle block
    /// width), the interval doubles (1 bar, 2 bars, 4 bars, ...) — i.e. beat density halves.
    /// Result is always a multiple of <paramref name="beatsPerBar"/>, so only bar-first beats show.
    /// </summary>
    public static int GetBarDensityInterval(double pxPerBeat, int beatsPerBar, double minPxPerTriangle = 30.0)
    {
        int interval = beatsPerBar > 0 ? beatsPerBar : 1;
        while (pxPerBeat * interval < minPxPerTriangle)
            interval *= 2;
        return interval;
    }
}
