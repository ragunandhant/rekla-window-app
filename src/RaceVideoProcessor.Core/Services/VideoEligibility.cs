namespace RaceVideoProcessor.Core.Services;

/// <summary>
/// Whether a player already has a video, and therefore whether it may receive one.
///
/// Only the video link decides. A null, empty or whitespace link means no video
/// has been assigned yet, so the entry is eligible for the video workflow. The
/// race result status is deliberately not a condition: a missing video is not an
/// incomplete result, and the backend is the authority if it ever refuses.
/// </summary>
public static class VideoEligibility
{
    public static bool HasVideo(string? videoLink) => !string.IsNullOrWhiteSpace(videoLink);

    /// <summary>True when selecting a video needs the operator to confirm replacing the existing one.</summary>
    public static bool RequiresReplaceConfirmation(string? videoLink) => HasVideo(videoLink);
}
