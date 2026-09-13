namespace RaceVideoProcessor.Core.Models;

public sealed record VideoMetadata(
    string Path,
    int Width,
    int Height,
    double DurationSeconds,
    double FrameRate,
    string VideoCodec,
    string? AudioCodec,
    string? PixelFormat,
    long? VideoBitRate = null,
    long? AudioBitRate = null,
    long? FormatBitRate = null,
    long? FileSizeBytes = null)
{
    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec);
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}
