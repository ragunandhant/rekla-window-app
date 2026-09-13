namespace RaceVideoProcessor.Core.Models;

/// <summary>
/// Everything the scoreboard needs. The centre value is always the primary
/// player's card number; there is no secondary card number.
/// </summary>
public sealed record OverlayData(
    string PrimaryName,
    string PrimaryLocation,
    string CardNumber,
    string? SecondaryName,
    string? SecondaryLocation,
    string TimingText)
{
    public bool HasSecondary => !string.IsNullOrWhiteSpace(SecondaryName);
}

public sealed record ProcessingRequest(
    string EntryId,
    string CardNumber,
    string InputPath,
    string OutputPath,
    OverlayData Overlay,
    bool AllowOverwrite);

public sealed record ProcessingProgress(
    double Percent,
    TimeSpan ProcessedVideoTime,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining);

public sealed record ValidationResult(bool IsValid, string? Error, VideoMetadata? OutputMetadata);

public sealed record ProcessingResult(
    bool Success,
    string OutputPath,
    string? Error,
    VideoMetadata? InputMetadata,
    VideoMetadata? OutputMetadata,
    bool UsedNvenc);

public sealed record PreviewResult(
    string NormalPreviewPath,
    string FinalPreviewPath,
    VideoMetadata SourceMetadata);

public sealed record EncoderCapability(bool NvencAvailable, string DisplayName, string? Detail);
