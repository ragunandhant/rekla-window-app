using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>
/// Chooses the video bitrate for a size-matched encode.
///
/// File size is bitrate × duration. Audio is stream-copied, so it keeps its own
/// size, and the resolution, frame rate and duration do not change — the only
/// thing that decides whether the output grows is the video bitrate. Encoding
/// at the source's own video bitrate therefore keeps the file about the size of
/// the original. A fixed quality target (the previous CRF/CQ 16) ignores the
/// source entirely and spends more bits than a phone or camera encoder did,
/// which is how a 33 MB clip became 44 MB.
/// </summary>
internal static class EncodingBudget
{
    /// <summary>MP4 container overhead, roughly, taken out of the video budget.</summary>
    private const double ContainerOverhead = 0.01;

    /// <summary>Never go below this, whatever the probe says: keeps the scoreboard text legible.</summary>
    private const long MinimumVideoBitRate = 500_000;

    /// <summary>
    /// The source's video bitrate, in bits per second, or null when it cannot be
    /// determined reliably (then the caller falls back to quality-based encoding).
    /// </summary>
    public static long? TargetVideoBitRate(VideoMetadata source)
    {
        if (source.DurationSeconds <= 0)
            return null;

        long? videoBitRate = source.VideoBitRate;
        if (videoBitRate is null)
        {
            // Derive from the whole file: total bits over duration, minus audio.
            var total = source.FormatBitRate
                        ?? (source.FileSizeBytes is > 0 ? (long)(source.FileSizeBytes.Value * 8 / source.DurationSeconds) : null);
            if (total is null)
                return null;
            videoBitRate = (long)(total.Value * (1 - ContainerOverhead)) - (source.AudioBitRate ?? 0);
        }

        return videoBitRate <= 0 ? null : Math.Max(videoBitRate.Value, MinimumVideoBitRate);
    }

    /// <summary>Signed size difference of the output against the source, in percent.</summary>
    public static double SizeDifferencePercent(long sourceBytes, long outputBytes)
        => sourceBytes <= 0 ? 0 : (outputBytes - sourceBytes) * 100.0 / sourceBytes;
}
