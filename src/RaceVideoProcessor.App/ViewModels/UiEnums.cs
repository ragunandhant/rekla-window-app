namespace RaceVideoProcessor.App.ViewModels;

/// <summary>
/// Top-level destinations. Work is home: the entry list and the current entry.
/// Races, Settings, Logs and Database are reached from the header navigation.
/// </summary>
public enum AppPage
{
    Work,
    Races,
    Settings,
    Logs,
    Database
}

/// <summary>Status filters above the entry list. Each entry belongs to exactly one besides All.</summary>
public enum EntryFilter
{
    All,

    /// <summary>The video is on the player: assigned here, or already present in the API.</summary>
    Uploaded,

    /// <summary>A stage failed, was cancelled, or needs attention. Disabled stages are never here.</summary>
    Failed,

    /// <summary>Waiting for the operator: no video yet, a selection kept, or a processed file not yet uploaded.</summary>
    ReadyToUpload,

    /// <summary>Processing, uploading or assigning right now, or between those stages.</summary>
    Processing
}

public enum SettingsSection
{
    General,
    VideoProcessing,
    UploadServer,
    Application,
    System,
    Backup
}

/// <summary>Severity of the inline banner shown above the workspace.</summary>
public enum BannerKind
{
    Info,
    Success,
    Warning,
    Error
}
