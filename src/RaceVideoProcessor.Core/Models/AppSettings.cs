using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Core.Models;

public sealed class AppSettings
{
    public DataSourceMode DataSourceMode { get; set; } = DataSourceMode.Demo;
    public string ApiUrl { get; set; } = "https://example.com/api/race";
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

        WindowWidth = Math.Clamp(double.IsFinite(WindowWidth) ? WindowWidth : 1500, 1100, 6000);
        WindowHeight = Math.Clamp(double.IsFinite(WindowHeight) ? WindowHeight : 950, 700, 4000);

        // A non-finite coordinate cannot be serialised, and would be meaningless
        // anyway; drop it and let the window centre itself.
        if (WindowLeft is { } left && !double.IsFinite(left)) WindowLeft = null;
        if (WindowTop is { } top && !double.IsFinite(top)) WindowTop = null;
    }

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
