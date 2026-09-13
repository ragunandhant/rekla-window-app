using System.Globalization;

namespace RaceVideoProcessor.Core.Models;

/// <summary>
/// A race the operator works on. One race has exactly one backend Race ID; the
/// 200 m and 300 m categories are the same Race ID queried with a different type.
///
/// <see cref="RaceDate"/> identifies the race for the operator. It is not the
/// players' API date/time, which is per entry and only used for display and sorting.
/// </summary>
public sealed record Race
{
    public const string DateFormat = "dd-MM-yyyy";

    /// <summary>Local SQLite row id.</summary>
    public long Id { get; init; }

    /// <summary>Backend Race ID, entered by the operator. The stable, unique identity of the race.</summary>
    public required string RaceId { get; init; }

    public required string RaceName { get; init; }
    public required DateOnly RaceDate { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string RaceDateDisplay => RaceDate.ToString(DateFormat, CultureInfo.InvariantCulture);
}

/// <summary>Race distance. The only thing a category changes is the API <c>type</c> value.</summary>
public enum RaceCategory
{
    Meter200,
    Meter300
}

/// <summary>
/// The context every entry, request and stored record belongs to. Race ID is part
/// of identity: card 100 in race A and card 100 in race B are different entries.
/// </summary>
public sealed record RaceScope(string RaceId, RaceCategory Category)
{
    public string CategoryLabel => Category == RaceCategory.Meter300 ? "300 Meter" : "200 Meter";
    public override string ToString() => $"{RaceId} / {CategoryLabel}";
}
