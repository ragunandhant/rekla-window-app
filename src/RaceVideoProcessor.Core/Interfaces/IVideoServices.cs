using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Interfaces;

public interface IFfprobeService
{
    Task<VideoMetadata> ProbeAsync(string path, CancellationToken cancellationToken);
}

public interface IEncoderCapabilityService
{
    Task<EncoderCapability> DetectAsync(CancellationToken cancellationToken);
    Task<(bool Success, string Detail)> TestNvencAsync(CancellationToken cancellationToken);
}

public interface IVideoProcessingService
{
    Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken);

    Task<PreviewResult> GeneratePreviewAsync(
        string inputPath,
        OverlayData overlay,
        CancellationToken cancellationToken);

    /// <summary>Writes one frame of a video to a PNG. A negative <paramref name="seekSeconds"/> counts from the end.</summary>
    Task<string> ExtractFrameAsync(string videoPath, double seekSeconds, string outputPng, CancellationToken cancellationToken);
}

public interface IOutputValidator
{
    Task<ValidationResult> ValidateAsync(
        string outputPath,
        VideoMetadata source,
        CancellationToken cancellationToken);
}

public interface IToolHealthService
{
    Task<(bool Success, string Detail)> TestFfmpegAsync(CancellationToken cancellationToken);
}
