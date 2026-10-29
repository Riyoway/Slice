namespace Slice.Models;

public sealed class EditSession
{
    public double Duration { get; private set; }
    public double TrimStart { get; set; }
    public double TrimEnd { get; set; }
    public double? MarkIn { get; set; }
    public double? MarkOut { get; set; }
    public List<TimeRange> Cuts { get; } = [];

    public void Reset(double duration)
    {
        Duration = Math.Max(0, duration);
        TrimStart = 0;
        TrimEnd = Duration;
        MarkIn = null;
        MarkOut = null;
        Cuts.Clear();
    }

    public EditSessionSnapshot Snapshot() => new(
        Duration,
        TrimStart,
        TrimEnd,
        MarkIn,
        MarkOut,
        Cuts.ToArray());

    public void Restore(EditSessionSnapshot snapshot)
    {
        Duration = snapshot.Duration;
        TrimStart = snapshot.TrimStart;
        TrimEnd = snapshot.TrimEnd;
        MarkIn = snapshot.MarkIn;
        MarkOut = snapshot.MarkOut;
        Cuts.Clear();
        Cuts.AddRange(snapshot.Cuts);
    }

    public void AddCut(double start, double end)
    {
        var range = new TimeRange(
            Math.Clamp(Math.Min(start, end), TrimStart, TrimEnd),
            Math.Clamp(Math.Max(start, end), TrimStart, TrimEnd));

        if (range.Length < 0.01)
            return;

        Cuts.Add(range);
        MergeCuts();
        MarkIn = null;
        MarkOut = null;
    }

    public IReadOnlyList<TimeRange> GetKeepRanges()
    {
        var result = new List<TimeRange>();
        var cursor = TrimStart;

        foreach (var cut in Cuts.OrderBy(x => x.Start))
        {
            var start = Math.Clamp(cut.Start, TrimStart, TrimEnd);
            var end = Math.Clamp(cut.End, TrimStart, TrimEnd);
            if (end <= TrimStart || start >= TrimEnd)
                continue;

            if (start > cursor)
                result.Add(new TimeRange(cursor, start));

            cursor = Math.Max(cursor, end);
        }

        if (cursor < TrimEnd)
            result.Add(new TimeRange(cursor, TrimEnd));

        return result.Where(x => x.Length >= 0.01).ToArray();
    }

    private void MergeCuts()
    {
        if (Cuts.Count <= 1)
            return;

        var ordered = Cuts.OrderBy(x => x.Start).ToArray();
        Cuts.Clear();
        var current = ordered[0];

        for (var i = 1; i < ordered.Length; i++)
        {
            var next = ordered[i];
            if (next.Start <= current.End + 0.001)
                current = new TimeRange(current.Start, Math.Max(current.End, next.End));
            else
            {
                Cuts.Add(current);
                current = next;
            }
        }

        Cuts.Add(current);
    }
}

public sealed record EditSessionSnapshot(
    double Duration,
    double TrimStart,
    double TrimEnd,
    double? MarkIn,
    double? MarkOut,
    TimeRange[] Cuts);
