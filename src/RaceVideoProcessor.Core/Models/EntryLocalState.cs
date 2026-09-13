namespace RaceVideoProcessor.Core.Models;

public sealed class EntryLocalState
{
    public required string EntryId { get; set; }
    public string? LocalVideoPath { get; set; }
    public LocalProcessingStatus ProcessingStatus { get; set; } = LocalProcessingStatus.VideoNotSelected;
    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LastProcessedDataHash { get; set; }
    public string? LastSeenDataHash { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? ProcessedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public EntryLocalState Clone() => new()
    {
        EntryId = EntryId,
        LocalVideoPath = LocalVideoPath,
        ProcessingStatus = ProcessingStatus,
        OutputPath = OutputPath,
        ErrorMessage = ErrorMessage,
        LastProcessedDataHash = LastProcessedDataHash,
        LastSeenDataHash = LastSeenDataHash,
        LastSeenUtc = LastSeenUtc,
        ProcessedUtc = ProcessedUtc,
        UpdatedUtc = UpdatedUtc
    };
}
