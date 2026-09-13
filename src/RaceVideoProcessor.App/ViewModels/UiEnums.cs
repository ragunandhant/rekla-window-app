namespace RaceVideoProcessor.App.ViewModels;

/// <summary>
/// Top-level destinations. The operator works on the Work page; Races, Settings
/// and Logs are supporting pages, so the entry list and workspace are never buried.
/// </summary>
public enum AppPage
{
    Work,
    Races,
    Settings,
    Logs
}

/// <summary>Quick filters above the entry list.</summary>
public enum EntryFilter
{
    All,
    Pending,
    Active,
    Completed,
    Failed
}

/// <summary>Severity of the inline banner shown above the workspace.</summary>
public enum BannerKind
{
    Info,
    Success,
    Warning,
    Error
}
