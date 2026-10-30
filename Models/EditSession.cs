namespace Slice.Models;

public sealed class EditSession
{
    public const double MinimumClipDuration = 0.05;

    public double Duration { get; private set; }
    public List<ClipSegment> Clips { get; } = [];
    public int SelectedIndex { get; private set; } = -1;

    public double TimelineDuration => Clips.Sum(clip => clip.Length);

    public ClipSegment? SelectedClip =>
        SelectedIndex >= 0 && SelectedIndex < Clips.Count ? Clips[SelectedIndex] : null;

    public void Reset(double duration)
    {
        Duration = Math.Max(0, duration);
        Clips.Clear();

        if (Duration > 0)
        {
            Clips.Add(new ClipSegment(Guid.NewGuid(), 0, Duration));
            SelectedIndex = 0;
        }
        else
        {
            SelectedIndex = -1;
        }
    }

    public EditSessionSnapshot Snapshot() => new(
        Duration,
        SelectedIndex,
        Clips.Select(clip => new ClipSegmentSnapshot(clip.Id, clip.SourceStart, clip.SourceEnd)).ToArray());

    public void Restore(EditSessionSnapshot snapshot)
    {
        Duration = snapshot.Duration;
        Clips.Clear();
        Clips.AddRange(snapshot.Clips.Select(clip => new ClipSegment(clip.Id, clip.SourceStart, clip.SourceEnd)));
        SelectedIndex = Clips.Count == 0 ? -1 : Math.Clamp(snapshot.SelectedIndex, 0, Clips.Count - 1);
    }

    public void SelectClip(int index)
    {
        SelectedIndex = index >= 0 && index < Clips.Count ? index : -1;
    }

    public bool CanSplitAtTimeline(double timelinePosition)
    {
        if (!TryGetClipAtTimeline(timelinePosition, out _, out var clip, out var localTime, out _))
            return false;

        return localTime > MinimumClipDuration && localTime < clip.Length - MinimumClipDuration;
    }

    public bool SplitAtTimeline(double timelinePosition)
    {
        if (!TryGetClipAtTimeline(timelinePosition, out var index, out var clip, out var localTime, out _))
            return false;

        if (localTime <= MinimumClipDuration || localTime >= clip.Length - MinimumClipDuration)
            return false;

        var splitSourceTime = clip.SourceStart + localTime;
        Clips[index] = new ClipSegment(clip.Id, clip.SourceStart, splitSourceTime);
        Clips.Insert(index + 1, new ClipSegment(Guid.NewGuid(), splitSourceTime, clip.SourceEnd));
        SelectedIndex = index + 1;
        return true;
    }

    public bool DeleteSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Clips.Count)
            return false;

        var index = SelectedIndex;
        Clips.RemoveAt(index);
        SelectedIndex = Clips.Count == 0 ? -1 : Math.Min(index, Clips.Count - 1);
        return true;
    }

    public bool MoveClip(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Clips.Count || Clips.Count <= 1)
            return false;

        toIndex = Math.Clamp(toIndex, 0, Clips.Count - 1);
        if (fromIndex == toIndex)
            return false;

        var clip = Clips[fromIndex];
        Clips.RemoveAt(fromIndex);
        toIndex = Math.Clamp(toIndex, 0, Clips.Count);
        Clips.Insert(toIndex, clip);
        SelectedIndex = toIndex;
        return true;
    }

    public bool TrimClipStart(int index, double sourceStart)
    {
        if (index < 0 || index >= Clips.Count)
            return false;

        var clip = Clips[index];
        var next = Math.Clamp(sourceStart, 0, clip.SourceEnd - MinimumClipDuration);
        if (Math.Abs(next - clip.SourceStart) < 0.0001)
            return false;

        clip.SourceStart = next;
        return true;
    }

    public bool TrimClipEnd(int index, double sourceEnd)
    {
        if (index < 0 || index >= Clips.Count)
            return false;

        var clip = Clips[index];
        var next = Math.Clamp(sourceEnd, clip.SourceStart + MinimumClipDuration, Duration);
        if (Math.Abs(next - clip.SourceEnd) < 0.0001)
            return false;

        clip.SourceEnd = next;
        return true;
    }

    public bool TryGetClipAtTimeline(
        double timelinePosition,
        out int index,
        out ClipSegment clip,
        out double localTime,
        out double clipTimelineStart)
    {
        index = -1;
        clip = null!;
        localTime = 0;
        clipTimelineStart = 0;

        if (Clips.Count == 0 || TimelineDuration <= 0)
            return false;

        var clamped = Math.Clamp(timelinePosition, 0, TimelineDuration);
        var cursor = 0d;

        for (var i = 0; i < Clips.Count; i++)
        {
            var current = Clips[i];
            var end = cursor + current.Length;
            if (clamped < end - 0.0001 || i == Clips.Count - 1)
            {
                index = i;
                clip = current;
                localTime = Math.Clamp(clamped - cursor, 0, current.Length);
                clipTimelineStart = cursor;
                return true;
            }

            cursor = end;
        }

        return false;
    }

    public double GetClipTimelineStart(int index)
    {
        if (index <= 0)
            return 0;

        return Clips.Take(Math.Min(index, Clips.Count)).Sum(clip => clip.Length);
    }

    public IReadOnlyList<TimeRange> GetKeepRanges() =>
        Clips.Where(clip => clip.Length >= MinimumClipDuration)
            .Select(clip => new TimeRange(clip.SourceStart, clip.SourceEnd))
            .ToArray();
}

public sealed class ClipSegment(Guid id, double sourceStart, double sourceEnd)
{
    public Guid Id { get; } = id;
    public double SourceStart { get; set; } = sourceStart;
    public double SourceEnd { get; set; } = sourceEnd;
    public double Length => Math.Max(0, SourceEnd - SourceStart);
}

public sealed record ClipSegmentSnapshot(Guid Id, double SourceStart, double SourceEnd);

public sealed record EditSessionSnapshot(
    double Duration,
    int SelectedIndex,
    ClipSegmentSnapshot[] Clips);
