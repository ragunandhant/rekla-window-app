namespace RaceVideoProcessor.Core.Models;

public sealed record VideoMetadata(
    string Path,
    int Width,
    int Height,
    double DurationSeconds,
    double FrameRate,
    string VideoCodec,
    string? AudioCodec,
    string? PixelFormat)
{
    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec);
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}
