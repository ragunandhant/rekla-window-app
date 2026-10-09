using System.Globalization;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>An extra FFmpeg input the filter graph overlays: a rendered graphics plate.</summary>
/// <param name="InputOptions">Options placed before its <c>-i</c>, e.g. looping for a timed fade.</param>
public sealed record OverlayInput(string Path, IReadOnlyList<string> InputOptions);

/// <param name="Filter">A -filter_complex graph. Input 0 is the video; the plates follow in order.</param>
/// <param name="OutputLabel">The graph's video output, for <c>-map</c>.</param>
public sealed record FilterBuildResult(
    string Filter,
    string OutputLabel,
    IReadOnlyList<OverlayInput> Inputs,
    string PrimaryDisplay,
    string SecondaryDisplay,
    string TimingDisplay,
    string FontName,
    string? Warning,
    IReadOnlyDictionary<string, string>? RenderedText = null);

/// <summary>
/// Builds the three-section digital-timer scorecard for FFmpeg, plus its
/// final-seconds twin.
///
/// The scorecard (left: primary "name, location", centre: cart number, right:
/// secondary "name, location") and the closing plate (identical row, centre:
/// timing text instead of the cart) are painted as transparent PNG plates by
/// <see cref="ScoreboardPlateRenderer"/> in the Live Digital Timer visual
/// language, then overlaid: [0] video, [1] scorecard, [2] closing plate. The
/// scorecard shows until the final-seconds mark, then the closing plate fades
/// in over the same row — the timer replaces the cart number in place, never
/// in the middle of the video, and the two are never shown together.
/// </summary>
public sealed class FfmpegFilterBuilder
{
    private const double FadeSeconds = 0.30;

    private readonly AppSettings _settings;
    private readonly IFontResolver _fontResolver;
    private readonly Func<ScoreboardFonts?> _bundledFonts;

    public FfmpegFilterBuilder(AppSettings settings, IFontResolver fontResolver)
        : this(settings, fontResolver, () => ScoreboardFonts.Bundled())
    {
    }

    internal FfmpegFilterBuilder(AppSettings settings, IFontResolver fontResolver, Func<ScoreboardFonts?> bundledFonts)
    {
        _settings = settings;
        _fontResolver = fontResolver;
        _bundledFonts = bundledFonts;
    }

    public FilterBuildResult Build(VideoMetadata metadata, OverlayData overlay, string workDirectory)
    {
        Directory.CreateDirectory(workDirectory);
        var (fonts, fontName, fontWarning) = ResolveFonts();

        var w = Math.Max(64, metadata.Width);
        var h = Math.Max(64, metadata.Height);

        var board = ScoreboardPlateRenderer.RenderScorecard(w, h,
            new ScoreboardContent(overlay.PrimaryName, overlay.PrimaryLocation, overlay.CardNumber,
                overlay.SecondaryName, overlay.SecondaryLocation), fonts);
        var boardPlate = Path.Combine(workDirectory, "scorecard-plate.png");
        File.WriteAllBytes(boardPlate, board.Png);
        var boardY = ScoreboardPlateRenderer.ScorecardPlateY(w, h);

        // The closing plate: the same row with the timing text in the centre
        // panel instead of the cart number. Same height, so the same overlay
        // origin keeps every panel edge exactly where the scorecard put it.
        var final = ScoreboardPlateRenderer.RenderScorecard(w, h,
            new ScoreboardContent(overlay.PrimaryName, overlay.PrimaryLocation, overlay.CardNumber,
                overlay.SecondaryName, overlay.SecondaryLocation), fonts,
            centerOverride: overlay.TimingText);
        var finalPlate = Path.Combine(workDirectory, "closing-plate.png");
        File.WriteAllBytes(finalPlate, final.Png);

        var start = Math.Max(0, metadata.DurationSeconds - _settings.CompletionTimeDisplaySeconds);
        var loopSeconds = Num(Math.Max(1, metadata.DurationSeconds + 1));
        var rate = Num(metadata.FrameRate > 0 ? metadata.FrameRate : 25);
        var inputs = new List<OverlayInput>
        {
            new(boardPlate, []),
            // Looped for the clip's length so it can fade in on the video's clock.
            new(finalPlate, ["-loop", "1", "-framerate", rate, "-t", loopSeconds])
        };

        var graph =
            $"[0:v][1:v]overlay=x=0:y={boardY}:enable='lt(t,{Num(start)})':eof_action=repeat[board];" +
            $"[2:v]format=rgba,fade=t=in:st={Num(start)}:d={Num(FadeSeconds)}:alpha=1[fin];" +
            $"[board][fin]overlay=x=0:y={boardY}:enable='gte(t,{Num(start)})':eof_action=pass,format=yuv420p[vout]";

        var rendered = new Dictionary<string, string>(board.RenderedText);
        foreach (var (key, value) in final.RenderedText)
            rendered[key] = value;
        rendered["timing"] = overlay.TimingText;

        var warnings = new List<string>();
        if (fontWarning is not null)
            warnings.Add(fontWarning);
        if (board.Overflowed || final.Overflowed)
            warnings.Add("Some scoreboard text did not fit even at the design's minimum size.");

        return new FilterBuildResult(
            graph,
            "[vout]",
            inputs,
            rendered["primary"],
            rendered["secondary"],
            overlay.TimingText,
            fontName,
            warnings.Count == 0 ? null : string.Join(" ", warnings),
            rendered);
    }

    /// <summary>
    /// The bundled design fonts. If an installation lost them, the resolved
    /// Tamil-capable system font stands in for all three, with a warning, rather
    /// than failing the job.
    /// </summary>
    private (ScoreboardFonts Fonts, string Name, string? Warning) ResolveFonts()
    {
        if (_bundledFonts() is { } bundled)
            return (bundled, "Barlow Condensed 700 / Noto Sans Tamil 500", null);

        var font = _fontResolver.Resolve();
        if (font.Path is null)
        {
            throw new InvalidOperationException(
                "The scoreboard fonts are missing from the installation (Fonts\\Scoreboard) and no Tamil font was found. Reinstall the application.");
        }

        return (new ScoreboardFonts(font.Path, font.Path, font.Path), font.Name,
            $"The scoreboard's design fonts (Barlow Condensed, Noto Sans Tamil) are missing from the installation; using {font.Name} instead. Reinstall to restore them." +
            (font.SupportsTamil ? string.Empty : " That font has no Tamil glyphs, so Tamil will render as boxes."));
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
