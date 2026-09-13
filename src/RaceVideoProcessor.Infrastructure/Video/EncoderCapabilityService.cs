using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Video;

public sealed class EncoderCapabilityService : IEncoderCapabilityService
{
    private readonly AppSettings _settings;

    public EncoderCapabilityService(AppSettings settings) => _settings = settings;

    public async Task<EncoderCapability> DetectAsync(CancellationToken cancellationToken)
    {
        var test = await TestNvencAsync(cancellationToken).ConfigureAwait(false);
        return test.Success
            ? new EncoderCapability(true, "NVIDIA NVENC ✓", test.Detail)
            : new EncoderCapability(false, "CPU / x264", test.Detail);
    }

    public async Task<(bool Success, string Detail)> TestNvencAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ffmpeg = FfprobeService.ResolveToolPath(_settings.FfmpegPath, "ffmpeg");
            var result = await ProcessRunner.RunCaptureAsync(ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error",
                    "-f", "lavfi", "-i", "color=c=black:s=128x128:r=1",
                    "-frames:v", "1", "-c:v", "h264_nvenc", "-f", "null", "-"
                ], cancellationToken).ConfigureAwait(false);

            return result.ExitCode == 0
                ? (true, "NVENC hardware encode test succeeded.")
                : (false, "NVENC test failed; CPU fallback will be used. " + ProcessRunner.Tail(result.StdErr, 8));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
