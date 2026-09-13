namespace RaceVideoProcessor.Core.Models;

/// <summary>
/// One race entry as supplied by the provider.
///
/// Identity vs. identifier: <see cref="EntryId"/> is the stable technical key used
/// for de-duplication and for the local-state table. <see cref="CardNumber"/> — the
/// primary player's cart number — is the only identifier the operator ever sees.
/// The application never invents a sequential entry number.
/// </summary>
public sealed record RaceEntry
{
    /// <summary>Stable internal key (marker, else playerId, else card number). Never shown to the operator.</summary>
    public required string EntryId { get; init; }

    /// <summary>Primary player's cart number. This is the operator-facing entry identifier.</summary>
    public required string CardNumber { get; init; }

    public required string PrimaryName { get; init; }
    public required string PrimaryLocation { get; init; }

    /// <summary>Null when the payload has no secondary player. There is no secondary card number.</summary>
    public string? SecondaryName { get; init; }
    public string? SecondaryLocation { get; init; }

    /// <summary>Race performance timing in seconds (API <c>timings</c>). Not a date.</summary>
    public double TimingSeconds { get; init; }

    /// <summary>Race categories for this entry, e.g. ["200", "300"].</summary>
    public IReadOnlyList<string> RaceTypes { get; init; } = [];

    public RemoteExtractionStatus ExtractionStatus { get; init; } = RemoteExtractionStatus.Unknown;

    /// <summary>Provider status string exactly as received, for display and diagnostics.</summary>
    public string? RemoteStatusText { get; init; }

    /// <summary>Remote reference only. The operator always maps a local file for processing.</summary>
    public string? VideoLink { get; init; }
    public bool IsVideoEnabled { get; init; } = true;

    public DateTimeOffset? RaceDateUtc { get; init; }

    // Technical identifiers: diagnostics only, never shown in the operator UI.
    public string? PlayerId { get; init; }
    public string? UserId { get; init; }
    public string? Marker { get; init; }

    public bool HasSecondary => !string.IsNullOrWhiteSpace(SecondaryName);

    /// <summary>"name, location" — the format used in both the UI and the scoreboard.</summary>
    public string PrimaryDisplay => Compose(PrimaryName, PrimaryLocation);

    public string SecondaryDisplay => HasSecondary ? Compose(SecondaryName!, SecondaryLocation) : "—";

    public string RaceTypeDisplay => RaceTypes.Count == 0 ? "—" : string.Join(" / ", RaceTypes);

    private static string Compose(string name, string? location)
        => string.IsNullOrWhiteSpace(location) ? name.Trim() : $"{name.Trim()}, {location.Trim()}";
}
