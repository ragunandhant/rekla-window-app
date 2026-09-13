using System.Globalization;
using System.Text.Json;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Providers;

/// <summary>
/// Maps the race API payload to <see cref="RaceEntry"/>.
///
/// This is the only place that knows the provider's JSON shape: field names,
/// nesting and status vocabulary stop here. Everything above works with
/// <see cref="RaceEntry"/>, so a contract change is a change to this class alone.
///
/// Shape handled (single object, bare array, or an array under a wrapper key):
///   { playerId, player: { _id, userId, ownerName, cartNo, location,
///                         raceId: [ { types: [...] } ] },
///     secondaryPlayer: null | { ownerName, location },
///     status, timings, videoLink, marker, date, time, isVideoEnabled }
///
/// Items without a primary card number are skipped: the card number *is* the
/// operator-facing entry, so an item without one cannot be worked on.
/// </summary>
public sealed class RaceApiPayloadAdapter : IRealApiPayloadAdapter
{
    private static readonly string[] ArrayWrapperKeys =
        ["data", "results", "entries", "races", "items", "docs", "records"];

    public IReadOnlyList<RaceEntry> Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return [];

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => UnwrapObject(root),
            _ => throw new InvalidOperationException("The API response was neither a JSON object nor an array.")
        };

        var entries = new List<RaceEntry>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var entry = MapEntry(item);
            if (entry is null)
                continue;

            // The same entry can appear more than once within one response.
            if (seen.Add(entry.EntryId))
                entries.Add(entry);
        }

        return entries;
    }

    private static List<JsonElement> UnwrapObject(JsonElement root)
    {
        foreach (var key in ArrayWrapperKeys)
        {
            if (root.TryGetProperty(key, out var wrapped) && wrapped.ValueKind == JsonValueKind.Array)
                return wrapped.EnumerateArray().ToList();
        }

        // A single entry object is a valid response too.
        return [root];
    }

    private static RaceEntry? MapEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var player = Object(item, "player");
        var cardNumber = String(player, "cartNo") ?? String(item, "cartNo");
        if (string.IsNullOrWhiteSpace(cardNumber))
            return null;

        var playerId = String(item, "playerId") ?? String(player, "_id");
        var marker = String(item, "marker");
        var secondary = Object(item, "secondaryPlayer");

        return new RaceEntry
        {
            // Prefer the per-race marker: one player can appear in several races,
            // and each of those is a separate unit of work for the operator.
            EntryId = FirstNonEmpty(marker, playerId, cardNumber)!,
            CardNumber = cardNumber.Trim(),
            PrimaryName = String(player, "ownerName")?.Trim() ?? cardNumber.Trim(),
            PrimaryLocation = String(player, "location")?.Trim() ?? string.Empty,
            SecondaryName = String(secondary, "ownerName")?.Trim(),
            SecondaryLocation = String(secondary, "location")?.Trim(),
            TimingSeconds = Double(item, "timings"),
            RaceTypes = ReadRaceTypes(player),
            ExtractionStatus = MapStatus(String(item, "status")),
            RemoteStatusText = String(item, "status"),
            VideoLink = String(item, "videoLink"),
            IsVideoEnabled = Bool(item, "isVideoEnabled") ?? true,
            RaceDateUtc = ReadDate(item, "date") ?? ReadDate(item, "time"),
            PlayerId = playerId,
            UserId = String(player, "userId"),
            Marker = marker
        };
    }

    /// <summary>Distinct category values across the player's race entries, e.g. ["200", "300"].</summary>
    private static IReadOnlyList<string> ReadRaceTypes(JsonElement? player)
    {
        if (player is null || !player.Value.TryGetProperty("raceId", out var races) || races.ValueKind != JsonValueKind.Array)
            return [];

        var types = new List<string>();
        foreach (var race in races.EnumerateArray())
        {
            if (race.ValueKind != JsonValueKind.Object ||
                !race.TryGetProperty("types", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var type in list.EnumerateArray())
            {
                var value = type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : type.ToString();
                if (!string.IsNullOrWhiteSpace(value) && !types.Contains(value, StringComparer.OrdinalIgnoreCase))
                    types.Add(value.Trim());
            }
        }

        types.Sort(StringComparer.OrdinalIgnoreCase);
        return types;
    }

    private static RemoteExtractionStatus MapStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" => RemoteExtractionStatus.Unknown,
        "completed" or "complete" or "done" or "success" or "succeeded" => RemoteExtractionStatus.Completed,
        "failed" or "error" or "cancelled" or "canceled" => RemoteExtractionStatus.Failed,
        _ => RemoteExtractionStatus.NotCompleted
    };

    // ---- JSON readers: every one tolerates a missing or null property ----

    private static JsonElement? Object(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? String(JsonElement? parent, string name)
    {
        if (parent is null || parent.Value.ValueKind != JsonValueKind.Object)
            return null;
        if (!parent.Value.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static string? String(JsonElement parent, string name) => String((JsonElement?)parent, name);

    private static double Double(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static bool? Bool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static DateTimeOffset? ReadDate(JsonElement parent, string name)
    {
        var raw = String(parent, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
