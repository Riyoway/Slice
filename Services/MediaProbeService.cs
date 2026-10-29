using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Slice.Models;

namespace Slice.Services;

public static class MediaProbeService
{
    public static async Task<MediaInfo?> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var ffprobe = FfmpegLocator.FindFfprobe();
        if (ffprobe is null)
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration:stream=codec_type,width,height,r_frame_rate");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(inputPath);

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        var jsonTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var json = await jsonTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            return null;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var duration = 0d;
        var hasVideo = false;
        var hasAudio = false;
        var width = 0;
        var height = 0;
        var fps = 0d;

        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationProperty))
        {
            double.TryParse(durationProperty.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var typeProperty)
                    ? typeProperty.GetString()
                    : null;

                if (type == "video" && !hasVideo)
                {
                    hasVideo = true;
                    if (stream.TryGetProperty("width", out var widthProperty))
                        width = widthProperty.GetInt32();
                    if (stream.TryGetProperty("height", out var heightProperty))
                        height = heightProperty.GetInt32();
                    if (stream.TryGetProperty("r_frame_rate", out var frameRateProperty))
                        fps = ParseFraction(frameRateProperty.GetString());
                }
                else if (type == "audio")
                {
                    hasAudio = true;
                }
            }
        }

        return new MediaInfo(duration, hasVideo, hasAudio, width, height, fps);
    }

    private static double ParseFraction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            Math.Abs(denominator) > double.Epsilon)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct)
            ? direct
            : 0;
    }
}
