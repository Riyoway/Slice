namespace Slice.Services;

public static class FfmpegLocator
{
    public static string? FindFfmpeg() => Find("ffmpeg.exe");
    public static string? FindFfprobe() => Find("ffprobe.exe");

    private static string? Find(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var part in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(part.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
