namespace BpmMeasurer;

/// <summary>
/// Immutable snapshot of the timing state for UndoService. Captures the global
/// offset plus a defensive copy of the raw timing points. Equality is by value
/// (offset + element-wise point comparison) so a no-op edit (e.g. a rejected
/// duplicate beat index, or a stepper value that did not actually change) yields
/// no new undo entry.
/// </summary>
internal readonly record struct TimingSnapshot(double Offset, RawTimingPoint[] Points)
{
    public bool Equals(TimingSnapshot other)
    {
        if (Offset != other.Offset) return false;
        if (Points.Length != other.Points.Length) return false;
        return Points.AsSpan().SequenceEqual(other.Points.AsSpan());
    }

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Offset);
        foreach (var p in Points) h.Add(p);
        return h.ToHashCode();
    }
}
