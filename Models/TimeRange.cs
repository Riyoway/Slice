namespace Slice.Models;

public readonly record struct TimeRange(double Start, double End)
{
    public double Length => Math.Max(0, End - Start);

    public TimeRange Normalize() => Start <= End ? this : new TimeRange(End, Start);
}
