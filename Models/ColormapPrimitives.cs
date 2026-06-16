using System.Globalization;

namespace BpmMeasurer;

public interface IColormap
{
    string Name { get; }
    ArgbColor GetColor(double fraction);
}

public readonly struct ArgbColor(byte r, byte g, byte b, byte a = 255)
{
    public byte R { get; } = r;
    public byte G { get; } = g;
    public byte B { get; } = b;
    public byte A { get; } = a;

    public uint ARGB => (uint)((A << 24) | (R << 16) | (G << 8) | B);

    public static ArgbColor FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length != 6 && hex.Length != 8)
            return new ArgbColor(0, 0, 0);

        byte r = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber);
        byte g = byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber);
        byte b = byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber);
        byte a = hex.Length == 8
            ? byte.Parse(hex.AsSpan(6, 2), NumberStyles.HexNumber)
            : (byte)255;
        return new ArgbColor(r, g, b, a);
    }
}

public readonly struct Range(double min, double max)
{
    public double Min { get; } = min;
    public double Max { get; } = max;
    public double Span => Max - Min;

    public double Normalize(double value, bool clamp)
    {
        double fraction = Span > 1e-300 ? (value - Min) / Span : 0.5;
        if (!clamp) return fraction;
        return fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
    }
}
