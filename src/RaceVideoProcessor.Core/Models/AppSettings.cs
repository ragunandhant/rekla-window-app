using System.Text.Json.Serialization;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Core.Models;

public sealed class AppSettings
{
    /// <summary>
    /// Version of the stored settings shape. Settings persist across upgrades, so a
    /// value that was only ever an old default must be migrated rather than kept:
    /// otherwise an upgraded install keeps rendering with the previous build's
    /// defaults, which are invisible to the operator as "defaults".
    /// Absent from settings written before versioning, which therefore read as 0.
    /// </summary>
    public const int CurrentSettingsVersion = 3;

    public const string DefaultBackendBaseUrl = "https://rekla-backend-fx7x9.ondigitalocean.app";

    public int SettingsVersion { get; set; }

    public DataSourceMode DataSourceMode { get; set; } = DataSourceMode.Demo;

    // ---- Backend -----------------------------------------------------------
    // Race IDs are deliberately absent: they belong to the saved races, and the
    // selected race supplies one Race ID for both categories.

    public string LoginEmail { get; set; } = string.Empty;

    /// <summary>Held in memory only. Persisted encrypted as <see cref="LoginPasswordProtected"/>.</summary>
    [JsonIgnore]
    public string LoginPassword { get; set; } = string.Empty;

    /// <summary>The password as stored: encrypted by the repository, never plain text on Windows.</summary>
    public string? LoginPasswordProtected { get; set; }

    public string LoginUrl { get; set; } = DefaultBackendBaseUrl + "/v1/auth/login";

    /// <summary>Placeholders: {raceId}, {type}.</summary>
    public string Players200UrlTemplate { get; set; } = DefaultBackendBaseUrl + "/v1/races/{raceId}/players/all?type={type}";
    public string Players300UrlTemplate { get; set; } = DefaultBackendBaseUrl + "/v1/races/{raceId}/players/all?type={type}";
    public string Type200 { get; set; } = "200";
    public string Type300 { get; set; } = "300";

    /// <summary>Placeholders: {raceId}, {playerId}, {type}.</summary>
    public string Assign200UrlTemplate { get; set; } = DefaultBackendBaseUrl + "/v1/races/{raceId}/player/{playerId}/video?type={type}";
    public string Assign300UrlTemplate { get; set; } = DefaultBackendBaseUrl + "/v1/races/{raceId}/player/{playerId}/video?type={type}";

    public string MediaUploadUrl { get; set; } = DefaultBackendBaseUrl + "/v1/media/upload";

    /// <summary>After processing, upload the video and assign it to the player.</summary>
    public bool UploadEnabled { get; set; } = true;

    // ---- Work context, restored on the next launch --------------------------

    /// <summary>Backend Race ID of the race selected in Race Management. Null when none is.</summary>
    public string? SelectedRaceId { get; set; }
    public RaceCategory SelectedCategory { get; set; } = RaceCategory.Meter200;

    public string TypeFor(RaceCategory category) => category == RaceCategory.Meter300 ? Type300 : Type200;

    public string PlayersUrlTemplateFor(RaceCategory category)
        => category == RaceCategory.Meter300 ? Players300UrlTemplate : Players200UrlTemplate;

    public string AssignUrlTemplateFor(RaceCategory category)
        => category == RaceCategory.Meter300 ? Assign300UrlTemplate : Assign200UrlTemplate;

    public int PollingIntervalSeconds { get; set; } = 20;
    public int DemoEntryIntervalSeconds { get; set; } = 20;
    public bool DemoAutoAdvance { get; set; } = true;
    public int DemoReleasedCount { get; set; } = 1;

    public string InputVideoFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RaceVideos");

    public string OutputVideoFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RaceVideos", "Processed");

    public string DemoVideoFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RaceVideoProcessor", "DemoVideos");

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>
    /// Empty means "resolve automatically": the bundled Tamil font first, then a
    /// Tamil-capable system font. Set a path only to override that choice.
    /// Race data is Tamil, so a font without Tamil coverage renders boxes.
    /// </summary>
    public string FontFilePath { get; set; } = string.Empty;

    /// <summary>How the API's <c>timings</c> value (seconds) is rendered. 22.5 → 00:22.50.</summary>
    public string TimingFormat { get; set; } = TimingFormatter.DefaultFormat;

    public double CompletionTimeDisplaySeconds { get; set; } = 4.0;
    public EncoderPreference EncoderPreference { get; set; } = EncoderPreference.Auto;
    public EncodingQuality EncodingQuality { get; set; } = EncodingQuality.VeryHigh;

    public double FontSizeScale { get; set; } = 1.0;
    public string ScoreboardBackgroundColor { get; set; } = "#0A1020";
    public string ScoreboardTextColor { get; set; } = "#FFFFFF";
    public string ScoreboardAccentColor { get; set; } = "#E3B23C";
    public double ScoreboardOpacity { get; set; } = 0.88;
    public bool AllowOverwriteExistingOutput { get; set; } = false;

    // Window placement, so the application reopens where the operator left it.
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 950;
    /// <summary>Null until the window has been placed. Never NaN: JSON cannot represent it.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    public void Normalize()
    {
        MigrateFromOlderVersions();

        PollingIntervalSeconds = PollingIntervalSeconds is 10 or 20 or 30 or 60
            ? PollingIntervalSeconds
            : 20;
        DemoEntryIntervalSeconds = Math.Clamp(DemoEntryIntervalSeconds, 1, 3600);
        DemoReleasedCount = Math.Clamp(DemoReleasedCount, 1, 100);
        CompletionTimeDisplaySeconds = Math.Clamp(CompletionTimeDisplaySeconds, 0.5, 30.0);
        FontSizeScale = Math.Clamp(FontSizeScale, 0.65, 1.75);
        ScoreboardOpacity = Math.Clamp(ScoreboardOpacity, 0.1, 1.0);
        ScoreboardBackgroundColor = NormalizeHexColor(ScoreboardBackgroundColor, "#0A1020");
        ScoreboardTextColor = NormalizeHexColor(ScoreboardTextColor, "#FFFFFF");
        ScoreboardAccentColor = NormalizeHexColor(ScoreboardAccentColor, "#E3B23C");
        if (string.IsNullOrWhiteSpace(TimingFormat))
            TimingFormat = TimingFormatter.DefaultFormat;

        var defaults = new AppSettings();
        LoginEmail = LoginEmail?.Trim() ?? string.Empty;
        LoginPassword ??= string.Empty;
        LoginUrl = OrDefault(LoginUrl, defaults.LoginUrl);
        Players200UrlTemplate = OrDefault(Players200UrlTemplate, defaults.Players200UrlTemplate);
        Players300UrlTemplate = OrDefault(Players300UrlTemplate, defaults.Players300UrlTemplate);
        Assign200UrlTemplate = OrDefault(Assign200UrlTemplate, defaults.Assign200UrlTemplate);
        Assign300UrlTemplate = OrDefault(Assign300UrlTemplate, defaults.Assign300UrlTemplate);
        MediaUploadUrl = OrDefault(MediaUploadUrl, defaults.MediaUploadUrl);
        Type200 = OrDefault(Type200, "200");
        Type300 = OrDefault(Type300, "300");
        SelectedRaceId = string.IsNullOrWhiteSpace(SelectedRaceId) ? null : SelectedRaceId.Trim();
        if (!Enum.IsDefined(SelectedCategory))
            SelectedCategory = RaceCategory.Meter200;

        WindowWidth = Math.Clamp(double.IsFinite(WindowWidth) ? WindowWidth : 1500, 1100, 6000);
        WindowHeight = Math.Clamp(double.IsFinite(WindowHeight) ? WindowHeight : 950, 700, 4000);

        // A non-finite coordinate cannot be serialised, and would be meaningless
        // anyway; drop it and let the window centre itself.
        if (WindowLeft is { } left && !double.IsFinite(left)) WindowLeft = null;
        if (WindowTop is { } top && !double.IsFinite(top)) WindowTop = null;
    }

    /// <summary>
    /// Replaces values that were defaults in earlier builds and are wrong now.
    /// Only exact old defaults are touched, so anything the operator chose stays.
    /// </summary>
    private void MigrateFromOlderVersions()
    {
        if (SettingsVersion >= CurrentSettingsVersion)
            return;

        // v1 defaulted the scoreboard font to Segoe UI, which has no Tamil glyphs:
        // every Tamil name rendered as boxes. Empty means "resolve a Tamil font".
        if (string.Equals(FontFilePath?.Trim(), @"C:\Windows\Fonts\segoeui.ttf", StringComparison.OrdinalIgnoreCase))
            FontFilePath = string.Empty;

        // v1 scoreboard palette, superseded by the broadcast design.
        if (string.Equals(ScoreboardAccentColor, "#21C7A8", StringComparison.OrdinalIgnoreCase))
            ScoreboardAccentColor = "#E3B23C";
        if (string.Equals(ScoreboardBackgroundColor, "#101725", StringComparison.OrdinalIgnoreCase))
            ScoreboardBackgroundColor = "#0A1020";
        if (Math.Abs(ScoreboardOpacity - 0.84) < 0.0001)
            ScoreboardOpacity = 0.88;

        SettingsVersion = CurrentSettingsVersion;
    }

    private static string OrDefault(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeHexColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var clean = value.Trim().TrimStart('#');
        if (clean.Length != 6 || clean.Any(c => !Uri.IsHexDigit(c)))
            return fallback;
        return "#" + clean.ToUpperInvariant();
    }
}
