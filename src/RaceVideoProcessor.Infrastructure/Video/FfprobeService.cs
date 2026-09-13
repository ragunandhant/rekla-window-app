using System.Globalization;
using System.Text.Json;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Video;

public sealed class FfprobeService : IFfprobeService
{
    private readonly AppSettings _settings;

    public FfprobeService(AppSettings settings) => _settings = settings;

    public async Task<VideoMetadata> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Video file not found.", path);

        var result = await ProcessRunner.RunCaptureAsync(
            ResolveToolPath(_settings.FfprobePath, "ffprobe"),
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException("FFprobe failed: " + ProcessRunner.Tail(result.StdErr));

        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => GetString(s, "codec_type") == "video");
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("FFprobe did not find a video stream.");

        var audio = streams.FirstOrDefault(s => GetString(s, "codec_type") == "audio");
        var width = GetInt(video, "width");
        var height = GetInt(video, "height");
        var videoCodec = GetString(video, "codec_name") ?? "unknown";
        var pixelFormat = GetString(video, "pix_fmt");
        var audioCodec = audio.ValueKind == JsonValueKind.Undefined ? null : GetString(audio, "codec_name");

        var duration = 0.0;
        if (root.TryGetProperty("format", out var format))
            duration = ParseDouble(GetString(format, "duration"));
        if (duration <= 0)
            duration = ParseDouble(GetString(video, "duration"));
        if (duration <= 0)
            throw new InvalidOperationException("Could not determine video duration.");

        var frameRate = ParseFraction(GetString(video, "avg_frame_rate"));
        if (frameRate <= 0)
            frameRate = ParseFraction(GetString(video, "r_frame_rate"));

        // Bitrates drive the size-matched encode. Some containers omit a stream's
        // bit_rate, so the format bitrate and file size are kept as fallbacks.
        var videoBitRate = ParseLong(GetString(video, "bit_rate"));
        var audioBitRate = audio.ValueKind == JsonValueKind.Undefined ? null : ParseLong(GetString(audio, "bit_rate"));
        long? formatBitRate = null;
        long? size = null;
        if (root.TryGetProperty("format", out var fmt))
        {
            formatBitRate = ParseLong(GetString(fmt, "bit_rate"));
            size = ParseLong(GetString(fmt, "size"));
        }
        size ??= new FileInfo(path).Length;

        return new VideoMetadata(path, width, height, duration, frameRate, videoCodec, audioCodec, pixelFormat,
            videoBitRate, audioBitRate, formatBitRate, size);
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static int GetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static long? ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;

    private static double ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static double ParseFraction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            Math.Abs(denominator) > double.Epsilon)
            return numerator / denominator;
        return ParseDouble(value);
    }

    internal static string ResolveToolPath(string configuredPath, string fallbackName)
    {
        if (Path.IsPathRooted(configuredPath) || configuredPath.Contains(Path.DirectorySeparatorChar) || configuredPath.Contains(Path.AltDirectorySeparatorChar))
            return configuredPath;

        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", fallbackName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        return File.Exists(bundled) ? bundled : configuredPath;
    }
}
