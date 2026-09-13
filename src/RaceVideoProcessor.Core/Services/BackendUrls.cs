using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Services;

/// <summary>
/// Builds backend URLs from the configured templates. The Race ID always comes
/// from the selected race; the category only chooses the <c>type</c> value.
/// </summary>
public static class BackendUrls
{
    public static Uri Players(AppSettings settings, RaceScope scope)
        => Build(settings.PlayersUrlTemplateFor(scope.Category), scope.RaceId, AppSettings.TypeFor(scope.Category), playerId: null);

    public static Uri Assign(AppSettings settings, RaceScope scope, string playerId)
        => Build(settings.AssignUrlTemplateFor(scope.Category), scope.RaceId, AppSettings.TypeFor(scope.Category), playerId);

    public static Uri Build(string template, string raceId, string type, string? playerId)
    {
        if (string.IsNullOrWhiteSpace(raceId))
            throw new InvalidOperationException("No race is selected, so there is no Race ID to call the API with.");

        var url = template
            .Replace("{raceId}", Uri.EscapeDataString(raceId.Trim()), StringComparison.OrdinalIgnoreCase)
            .Replace("{type}", Uri.EscapeDataString(type.Trim()), StringComparison.OrdinalIgnoreCase);

        if (url.Contains("{playerId}", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new InvalidOperationException("The entry has no player id.");
            url = url.Replace("{playerId}", Uri.EscapeDataString(playerId.Trim()), StringComparison.OrdinalIgnoreCase);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"The configured URL is not a valid absolute URL: {template}");
    }
}
