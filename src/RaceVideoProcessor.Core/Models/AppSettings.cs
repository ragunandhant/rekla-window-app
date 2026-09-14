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
    public const int CurrentSettingsVersion = 5;

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

    /// <summary>Placeholders: {raceId}, {playerId}, {type}.</summary>
    public string Assign200UrlTemplate { get; set; } = DefaultBackendBaseUrl + "/v1/races/{raceId}/player/{playerId}/video?type={type}";
    public string Assign300UrlTemplate { get; set; } = DefaultBackendBaseUrl + "/v1/races/{raceId}/player/{playerId}/video?type={type}";

    public string MediaUploadUrl { get; set; } = DefaultBackendBaseUrl + "/v1/media/upload";

    /// <summary>
    /// Run the selected video through FFmpeg to add the scoreboard. OFF: the
    /// selected file is used as it is — uploaded directly when upload is ON.
    /// Independent of <see cref="UploadEnabled"/>; both apply to the next operation.
    /// </summary>
    public bool ProcessingEnabled { get; set; } = true;

    /// <summary>Upload the video (processed, or the selected file when processing is OFF) and assign it to the player.</summary>
    public bool UploadEnabled { get; set; } = true;

    /// <summary>Desktop interface scale. Never affects the video.</summary>
    public double UiZoom { get; set; } = 1.0;

    public static readonly double[] ZoomLevels = [0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

    // ---- Work context, restored on the next launch --------------------------

    /// <summary>Backend Race ID of the race selected in Race Management. Null when none is.</summary>
    public string? SelectedRaceId { get; set; }
    public RaceCategory SelectedCategory { get; set; } = RaceCategory.Meter200;

    /// <summary>The API type is fixed by the category and is not configurable: 200 Meter → 200, 300 Meter → 300.</summary>
    public static string TypeFor(RaceCategory category) => category == RaceCategory.Meter300 ? "300" : "200";

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

    public bool AllowOverwriteExistingOutput { get; set; } = false;

    /// <summary>
    /// Encode at the source's own bitrate so the processed file stays about the
    /// size of the original. Off: fixed-quality CRF/CQ, which can grow the file.
    /// </summary>
    public bool MatchSourceFileSize { get; set; } = true;

    /// <summary>Allowed size difference from the original, in percent, before a warning is logged.</summary>
    public double FileSizeTolerancePercent { get; set; } = 10;

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
        FileSizeTolerancePercent = Math.Clamp(double.IsFinite(FileSizeTolerancePercent) ? FileSizeTolerancePercent : 10, 1, 100);
        UiZoom = ZoomLevels.OrderBy(level => Math.Abs(level - (double.IsFinite(UiZoom) ? UiZoom : 1.0))).First();
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

        // Scoreboard colours, opacity and text scale were settings up to v4. The
        // scoreboard now follows its design HTML exactly, so they no longer exist;
        // stored values are simply ignored when read.

        SettingsVersion = CurrentSettingsVersion;
    }

    private static string OrDefault(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

}
