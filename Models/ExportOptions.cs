namespace Slice.Models;

public enum ExportFormat
{
    Mp4,
    WebM,
    Mkv,
    Gif,
    Mp3,
    Wav
}

public enum ExportQuality
{
    Small,
    Balanced,
    High
}

public sealed record ExportOptions(
    ExportFormat Format,
    int? Height,
    double? Fps,
    ExportQuality Quality,
    bool FastCopyWhenPossible);
