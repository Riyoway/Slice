using System.Diagnostics;
using System.Globalization;
using System.Text;
using Slice.Models;

namespace Slice.Services;

public sealed class ExportService
{
    public async Task ExportAsync(
        string inputPath,
        string outputPath,
        EditSession session,
        MediaInfo mediaInfo,
        double volume,
        ExportOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = FfmpegLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("FFmpeg was not found. Put ffmpeg.exe and ffprobe.exe in the Tools folder or add them to PATH.");

        var keepRanges = session.GetKeepRanges();
        if (keepRanges.Count == 0)
            throw new InvalidOperationException("Nothing remains to export after the current trims/cuts.");

        if ((options.Format is ExportFormat.Mp3 or ExportFormat.Wav) && !mediaInfo.HasAudio)
            throw new InvalidOperationException("This media does not contain an audio stream.");

        var sourceExtension = Path.GetExtension(inputPath).ToLowerInvariant();
        var matchingCopyContainer = (options.Format == ExportFormat.Mp4 && sourceExtension is ".mp4" or ".m4v") ||
                                    (options.Format == ExportFormat.Mkv && sourceExtension == ".mkv");
        var canFastCopy = options.FastCopyWhenPossible &&
                          matchingCopyContainer &&
                          keepRanges.Count == 1 &&
                          Math.Abs(volume - 1d) < 0.001 &&
                          options.Height is null &&
                          options.Fps is null;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");

        if (canFastCopy)
        {
            var range = keepRanges[0];
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(F(range.Start));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(F(range.Length));
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-avoid_negative_ts");
            psi.ArgumentList.Add("make_zero");
            if (options.Format == ExportFormat.Mp4)
            {
                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart");
            }
        }
        else
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inputPath);
            BuildEncodedArguments(psi, keepRanges, mediaInfo, volume, options);
        }

        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start FFmpeg.");

        var totalDuration = keepRanges.Sum(x => x.Length);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                long.TryParse(line[12..], out var microseconds) &&
                totalDuration > 0)
            {
                var seconds = microseconds / 1_000_000d;
                progress?.Report(Math.Clamp(seconds / totalDuration, 0, 1));
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "FFmpeg export failed." : stderr.Trim());

        progress?.Report(1);
    }

    private static void BuildEncodedArguments(
        ProcessStartInfo psi,
        IReadOnlyList<TimeRange> ranges,
        MediaInfo info,
        double volume,
        ExportOptions options)
    {
        var isAudioOnly = options.Format is ExportFormat.Mp3 or ExportFormat.Wav;
        var isGif = options.Format == ExportFormat.Gif;
        var filter = new StringBuilder();

        if (!isAudioOnly)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                var range = ranges[i];
                filter.Append($"[0:v]trim=start={F(range.Start)}:end={F(range.End)},setpts=PTS-STARTPTS[v{i}];");
            }

            if (ranges.Count == 1)
                filter.Append("[v0]null[vcat];");
            else
            {
                for (var i = 0; i < ranges.Count; i++)
                    filter.Append($"[v{i}]");
                filter.Append($"concat=n={ranges.Count}:v=1:a=0[vcat];");
            }
        }

        if (info.HasAudio && !isGif)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                var range = ranges[i];
                filter.Append($"[0:a]atrim=start={F(range.Start)}:end={F(range.End)},asetpts=PTS-STARTPTS,volume={F(volume)}[a{i}];");
            }

            if (ranges.Count == 1)
                filter.Append("[a0]anull[acat];");
            else
            {
                for (var i = 0; i < ranges.Count; i++)
                    filter.Append($"[a{i}]");
                filter.Append($"concat=n={ranges.Count}:v=0:a=1[acat];");
            }
        }

        if (!isAudioOnly)
        {
            var videoFilters = new List<string>();
            if (options.Height is int height)
                videoFilters.Add($"scale=-2:{height}:flags=lanczos");
            if (options.Fps is double fps)
                videoFilters.Add($"fps={F(fps)}");

            if (isGif)
            {
                if (videoFilters.Count == 0)
                    videoFilters.Add("fps=15");
                filter.Append($"[vcat]{string.Join(',', videoFilters)},split[gifbase][palettebase];");
                filter.Append("[palettebase]palettegen=stats_mode=diff[palette];");
                filter.Append("[gifbase][palette]paletteuse=dither=sierra2_4a[gifout]");
            }
            else if (videoFilters.Count > 0)
            {
                filter.Append($"[vcat]{string.Join(',', videoFilters)}[vout]");
            }
        }

        psi.ArgumentList.Add("-filter_complex");
        psi.ArgumentList.Add(filter.ToString().TrimEnd(';'));

        if (isAudioOnly)
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("[acat]");
            if (options.Format == ExportFormat.Mp3)
            {
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add("libmp3lame");
                psi.ArgumentList.Add("-q:a");
                psi.ArgumentList.Add(options.Quality switch { ExportQuality.Small => "6", ExportQuality.High => "1", _ => "3" });
            }
            else
            {
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add("pcm_s16le");
            }
            return;
        }

        if (isGif)
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("[gifout]");
            psi.ArgumentList.Add("-loop");
            psi.ArgumentList.Add("0");
            return;
        }

        var videoOutput = options.Height is not null || options.Fps is not null ? "[vout]" : "[vcat]";
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add(videoOutput);

        if (info.HasAudio)
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("[acat]");
        }

        switch (options.Format)
        {
            case ExportFormat.WebM:
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libvpx-vp9");
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add(options.Quality switch { ExportQuality.Small => "40", ExportQuality.High => "24", _ => "32" });
                psi.ArgumentList.Add("-b:v");
                psi.ArgumentList.Add("0");
                if (info.HasAudio)
                {
                    psi.ArgumentList.Add("-c:a");
                    psi.ArgumentList.Add("libopus");
                    psi.ArgumentList.Add("-b:a");
                    psi.ArgumentList.Add("160k");
                }
                break;

            case ExportFormat.Mkv:
            case ExportFormat.Mp4:
            default:
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add(options.Quality switch { ExportQuality.Small => "28", ExportQuality.High => "18", _ => "22" });
                if (info.HasAudio)
                {
                    psi.ArgumentList.Add("-c:a");
                    psi.ArgumentList.Add("aac");
                    psi.ArgumentList.Add("-b:a");
                    psi.ArgumentList.Add("192k");
                }
                if (options.Format == ExportFormat.Mp4)
                {
                    psi.ArgumentList.Add("-movflags");
                    psi.ArgumentList.Add("+faststart");
                    psi.ArgumentList.Add("-pix_fmt");
                    psi.ArgumentList.Add("yuv420p");
                }
                break;
        }
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
