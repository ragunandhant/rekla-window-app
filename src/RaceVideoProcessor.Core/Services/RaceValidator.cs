using System.Globalization;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Services;

public sealed record RaceValidationResult(
    IReadOnlyList<string> Errors,
    string RaceName,
    string RaceId,
    DateOnly? RaceDate,
    Race? ExistingRaceWithSameId)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Checks a new race before it is saved.</summary>
public static class RaceValidator
{
    public const string DuplicateMessage = "A race with this Race ID already exists.";

    private static readonly string[] AcceptedDateFormats =
        ["dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "yyyy-MM-dd"];

    public static RaceValidationResult Validate(
        string? raceName, string? raceDate, string? raceId, IEnumerable<Race> existingRaces)
    {
        var errors = new List<string>();
        var name = Collapse(raceName);
        var id = (raceId ?? string.Empty).Trim();

        if (name.Length == 0)
            errors.Add("Race Name is required.");

        DateOnly? date = null;
        if (string.IsNullOrWhiteSpace(raceDate))
            errors.Add("Race Date is required, for example 13-09-2026.");
        else if (DateOnly.TryParseExact(raceDate.Trim(), AcceptedDateFormats, CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var parsed))
            date = parsed;
        else
            errors.Add("Race Date is not a valid date. Use DD-MM-YYYY, for example 13-09-2026.");

        Race? duplicate = null;
        if (id.Length == 0)
        {
            errors.Add("Race ID is required. Copy it from the backend.");
        }
        else if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            errors.Add("Race ID is invalid. It may contain only letters, digits, '-' and '_'.");
        }
        else
        {
            duplicate = existingRaces.FirstOrDefault(r => string.Equals(r.RaceId, id, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
                errors.Add(DuplicateMessage);
        }

        return new RaceValidationResult(errors, name, id, date, duplicate);
    }

    /// <summary>Trims and collapses runs of whitespace inside the name.</summary>
    private static string Collapse(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
