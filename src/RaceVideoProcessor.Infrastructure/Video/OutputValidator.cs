using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Video;

public sealed class OutputValidator : IOutputValidator
{
    private readonly IFfprobeService _ffprobe;

    public OutputValidator(IFfprobeService ffprobe) => _ffprobe = ffprobe;

    public async Task<ValidationResult> ValidateAsync(string outputPath, VideoMetadata source, CancellationToken cancellationToken)
    {
        if (!File.Exists(outputPath))
            return new ValidationResult(false, "Output file does not exist.", null);

        var info = new FileInfo(outputPath);
        if (info.Length <= 0)
            return new ValidationResult(false, "Output file is empty.", null);

        try
        {
            var output = await _ffprobe.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (output.Width <= 0 || output.Height <= 0)
                return new ValidationResult(false, "Output has no valid video stream dimensions.", output);

            var tolerance = Math.Max(1.0, source.DurationSeconds * 0.03);
            if (Math.Abs(output.DurationSeconds - source.DurationSeconds) > tolerance)
                return new ValidationResult(false,
                    $"Output duration differs too much from source ({output.DurationSeconds:0.###}s vs {source.DurationSeconds:0.###}s).",
                    output);

            if (output.Width != source.Width || output.Height != source.Height)
                return new ValidationResult(false,
                    $"Output resolution changed unexpectedly ({output.Width}x{output.Height} vs {source.Width}x{source.Height}).",
                    output);

            if (source.FrameRate > 0 && output.FrameRate > 0 &&
                Math.Abs(output.FrameRate - source.FrameRate) > Math.Max(0.05, source.FrameRate * 0.01))
                return new ValidationResult(false,
                    $"Output frame rate changed unexpectedly ({output.FrameRate:0.###} vs {source.FrameRate:0.###} fps).",
                    output);

            if (source.HasAudio && !output.HasAudio)
                return new ValidationResult(false, "The source has audio but the output does not.", output);

            return new ValidationResult(true, null, output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ValidationResult(false, "FFprobe validation failed: " + ex.Message, null);
        }
    }
}
