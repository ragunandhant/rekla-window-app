using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Video;

public sealed class ToolHealthService : IToolHealthService
{
    private readonly AppSettings _settings;

    public ToolHealthService(AppSettings settings) => _settings = settings;

    public async Task<(bool Success, string Detail)> TestFfmpegAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ffmpeg = FfprobeService.ResolveToolPath(_settings.FfmpegPath, "ffmpeg");
            var ffprobe = FfprobeService.ResolveToolPath(_settings.FfprobePath, "ffprobe");

            var version = await ProcessRunner.RunCaptureAsync(ffmpeg, ["-hide_banner", "-version"], cancellationToken)
                .ConfigureAwait(false);
            if (version.ExitCode != 0)
                return (false, "FFmpeg failed: " + ProcessRunner.Tail(version.StdErr, 5));

            var probeVersion = await ProcessRunner.RunCaptureAsync(ffprobe, ["-hide_banner", "-version"], cancellationToken)
                .ConfigureAwait(false);
            if (probeVersion.ExitCode != 0)
                return (false, "FFprobe failed: " + ProcessRunner.Tail(probeVersion.StdErr, 5));

            var filters = await ProcessRunner.RunCaptureAsync(ffmpeg, ["-hide_banner", "-filters"], cancellationToken)
                .ConfigureAwait(false);
            if (!filters.StdOut.Contains("drawtext", StringComparison.OrdinalIgnoreCase))
                return (false, "This FFmpeg build does not expose the drawtext filter required for the scoreboard.");

            var encoders = await ProcessRunner.RunCaptureAsync(ffmpeg, ["-hide_banner", "-encoders"], cancellationToken)
                .ConfigureAwait(false);
            if (!encoders.StdOut.Contains("libx264", StringComparison.OrdinalIgnoreCase))
                return (false, "This FFmpeg build does not expose libx264, which is required as the CPU fallback encoder.");

            var firstLine = version.StdOut.Replace("\r", string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "FFmpeg available";
            return (true, firstLine + Environment.NewLine + "FFprobe, drawtext and libx264 checks succeeded.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
