namespace Slice.Models;

public sealed record MediaInfo(
    double Duration,
    bool HasVideo,
    bool HasAudio,
    int Width,
    int Height,
    double FrameRate);
