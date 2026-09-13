namespace RaceVideoProcessor.Core.Models;

/// <summary>
/// Everything the application stores locally about one entry of one race and category.
///
/// Identity is (RaceId, Category, EntryId): the same card number or marker in a
/// different race is a different record, and nothing here is shared across races.
/// </summary>
public sealed class EntryLocalState
{
    public required string RaceId { get; set; }
    public RaceCategory Category { get; set; } = RaceCategory.Meter200;
    public required string EntryId { get; set; }

    // Player identifiers as last received, so a stage can resume without the API.
    public string? CardNumber { get; set; }
    public string? PlayerId { get; set; }
    public string? Marker { get; set; }
    public DateTimeOffset? EntryDateUtc { get; set; }

    /// <summary>The player's videoLink as last reported by the API.</summary>
    public string? RemoteVideoLink { get; set; }

    /// <summary>Original operator video. Never modified or deleted by the application.</summary>
    public string? LocalVideoPath { get; set; }

    public LocalProcessingStatus ProcessingStatus { get; set; } = LocalProcessingStatus.VideoNotSelected;

    /// <summary>Processed video.</summary>
    public string? OutputPath { get; set; }

    public UploadStatus UploadStatus { get; set; } = UploadStatus.NotStarted;

    /// <summary>Link returned by the media upload. Kept when assignment fails, so a retry never re-uploads.</summary>
    public string? UploadedVideoLink { get; set; }

    public AssignmentStatus AssignmentStatus { get; set; } = AssignmentStatus.NotStarted;

    /// <summary>True when the last failure was the backend rejecting the credentials.</summary>
    public bool LastFailureWasAuthentication { get; set; }

    public string? ErrorMessage { get; set; }
    public string? LastProcessedDataHash { get; set; }
    public string? LastSeenDataHash { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? ProcessedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public RaceScope Scope => new(RaceId, Category);

    public OverallStatus Overall
    {
        get
        {
            switch (ProcessingStatus)
            {
                case LocalProcessingStatus.Processing: return OverallStatus.Processing;
                case LocalProcessingStatus.Cancelled: return OverallStatus.Cancelled;
                case LocalProcessingStatus.Failed:
                case LocalProcessingStatus.VideoNotFound: return OverallStatus.ProcessingFailed;
                case LocalProcessingStatus.Completed:
                    break;
                default: return OverallStatus.Ready;
            }

            if (LastFailureWasAuthentication &&
                (UploadStatus == UploadStatus.Failed || AssignmentStatus == AssignmentStatus.Failed))
                return OverallStatus.AuthenticationFailed;

            return UploadStatus switch
            {
                UploadStatus.Uploading => OverallStatus.Uploading,
                UploadStatus.Failed => OverallStatus.UploadFailed,
                UploadStatus.Completed => AssignmentStatus switch
                {
                    AssignmentStatus.Assigning => OverallStatus.Assigning,
                    AssignmentStatus.Failed => OverallStatus.AssignmentFailed,
                    AssignmentStatus.Completed => OverallStatus.Completed,
                    _ => OverallStatus.UploadCompleted
                },
                _ => OverallStatus.ProcessingCompleted
            };
        }
    }

    public EntryLocalState Clone() => (EntryLocalState)MemberwiseClone();
}
