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
/// Builds the "Elegant Prestige" scoreboard and closing timing plaque for FFmpeg.
///
/// The design reference is variant_3_elegant_prestige_exact_reference.html:
/// a very wide, short, rectangular dark-green panel with a thin green frame and
/// top edge, a bright mindaro bottom line, soft curved lighting at both ends and
/// a centre column holding the cart number between two mindaro glow rules.
/// Primary player left, secondary right; there is no secondary cart number.
///
/// The panel and plaque, text included, are painted as transparent PNG plates by
/// <see cref="ScoreboardPlateRenderer"/> and overlaid. drawbox cannot express the
/// reference's gradients and glows, and drawtext shapes a line with one script, so
/// Tamil after Latin initials ("KS சிவராம்குமார்") rendered wrong; the plates are
/// shaped per script with HarfBuzz. No text passes through filter-string escaping.
///
/// Every dimension is derived from the probed frame size in the reference's own
/// proportions, so 720p through 4K look the same.
/// </summary>
public sealed class FfmpegFilterBuilder
{
    /// <summary>Reference panel: 95.2 % of a 1536-wide canvas by 22.7 % of its 864 height.</summary>
    private const double ReferenceAspect = 1462.3 / 196.1;

    /// <summary>Upper bound on panel height as a share of the frame, so the video stays clear.</summary>
    private const double MaxPanelHeightShare = 0.15;

    private const double FadeSeconds = 0.30;

    private readonly AppSettings _settings;
    private readonly IFontResolver _fontResolver;

    public FfmpegFilterBuilder(AppSettings settings, IFontResolver fontResolver)
    {
        _settings = settings;
        _fontResolver = fontResolver;
    }

    public FilterBuildResult Build(
        VideoMetadata metadata,
        OverlayData overlay,
        string workDirectory,
        bool completionAlwaysVisible = false)
    {
        Directory.CreateDirectory(workDirectory);

        var font = _fontResolver.Resolve();
        if (font.Path is null)
        {
            throw new InvalidOperationException(
                "No usable font was found for the scoreboard. Install a Tamil-capable font " +
                "(Nirmala UI ships with Windows) or set an explicit font file in Settings.");
        }
        var numeric = _fontResolver.ResolveNumeric();
        var numericPath = numeric.Path ?? font.Path;

        var w = Math.Max(64, metadata.Width);
        var h = Math.Max(64, metadata.Height);

        // ---- Panel geometry (reference: left/right 2.4 %, very wide, short) ----
        var panelX = Round(w * 0.024, 4);
        var panelWidth = Math.Max(200, w - panelX * 2);
        var panelHeight = Round(Math.Min(panelWidth / ReferenceAspect, h * MaxPanelHeightShare), 48);
        var bottomMargin = Round(h * 0.045, 8);
        var panelY = Math.Max(0, h - bottomMargin - panelHeight);

        var style = new PlateStyle(_settings.ScoreboardAccentColor, _settings.ScoreboardTextColor,
            _settings.ScoreboardOpacity, _settings.FontSizeScale, font.Path, numericPath);

        var board = ScoreboardPlateRenderer.RenderScoreboard(panelWidth, panelHeight,
            new ScoreboardContent(overlay.PrimaryName, overlay.PrimaryLocation, overlay.CardNumber,
                overlay.SecondaryName, overlay.SecondaryLocation), style);
        var panelPlate = Path.Combine(workDirectory, "scoreboard-plate.png");
        File.WriteAllBytes(panelPlate, board.Png);

        // ---- Timing plaque: centred, the same graphics package ------------------
        var plaqueWidth = Round(w * 0.26, 180);
        var plaqueHeight = Round(plaqueWidth / 3.1, 60);
        var plaqueX = (w - plaqueWidth) / 2;
        var plaqueY = (h - plaqueHeight) / 2;
        var plaque = ScoreboardPlateRenderer.RenderTimingPlaque(plaqueWidth, plaqueHeight, overlay.TimingText, style);
        var plaquePlate = Path.Combine(workDirectory, "timing-plate.png");
        File.WriteAllBytes(plaquePlate, plaque.Png);

        var start = Math.Max(0, metadata.DurationSeconds - _settings.CompletionTimeDisplaySeconds);

        // ---- Graph: [0] video, [1] scoreboard plate, [2] timing plate -------------
        var inputs = new List<OverlayInput> { new(panelPlate, []) };
        string plaqueSource;
        string enable;
        if (completionAlwaysVisible)
        {
            inputs.Add(new OverlayInput(plaquePlate, []));
            plaqueSource = "[2:v]format=rgba[plaque]";
            enable = string.Empty;
        }
        else
        {
            // Looped for the clip's length so it can fade in on the video's own clock
            // during the final seconds, rather than cutting in.
            var loopSeconds = Num(Math.Max(1, metadata.DurationSeconds + 1));
            var rate = Num(metadata.FrameRate > 0 ? metadata.FrameRate : 25);
            inputs.Add(new OverlayInput(plaquePlate, ["-loop", "1", "-framerate", rate, "-t", loopSeconds]));
            plaqueSource = $"[2:v]format=rgba,fade=t=in:st={Num(start)}:d={Num(FadeSeconds)}:alpha=1[plaque]";
            enable = $":enable='gte(t,{Num(start)})'";
        }

        var graph =
            $"[0:v][1:v]overlay=x={panelX}:y={panelY}:eof_action=repeat[board];" +
            plaqueSource + ";" +
            $"[board][plaque]overlay=x={plaqueX}:y={plaqueY}:eof_action=pass{enable},format=yuv420p[vout]";

        var rendered = new Dictionary<string, string>(board.RenderedText);
        foreach (var (key, value) in plaque.RenderedText)
            rendered[key] = value;

        return new FilterBuildResult(
            graph,
            "[vout]",
            inputs,
            LayoutTextFitter.ComposeDisplay(overlay.PrimaryName, overlay.PrimaryLocation),
            overlay.HasSecondary ? LayoutTextFitter.ComposeDisplay(overlay.SecondaryName, overlay.SecondaryLocation) : "—",
            overlay.TimingText,
            font.Name,
            BuildWarning(font, overlay, board.Truncated || plaque.Truncated),
            rendered);
    }

    private static string? BuildWarning(FontResolution font, OverlayData overlay, bool anyTruncated)
    {
        var messages = new List<string>();

        var tamilPresent =
            LayoutTextFitter.ContainsTamil(overlay.PrimaryName) ||
            LayoutTextFitter.ContainsTamil(overlay.PrimaryLocation ?? string.Empty) ||
            LayoutTextFitter.ContainsTamil(overlay.SecondaryName ?? string.Empty) ||
            LayoutTextFitter.ContainsTamil(overlay.SecondaryLocation ?? string.Empty);

        if (tamilPresent && !font.SupportsTamil)
        {
            messages.Add(
                $"This entry contains Tamil text but the only available font ({font.Name}) has no Tamil glyphs; " +
                "Tamil will render as boxes. Install a Tamil font or set one in Settings.");
        }

        if (anyTruncated)
            messages.Add("Some scoreboard text was shortened to fit its region.");

        return messages.Count == 0 ? null : string.Join(" ", messages);
    }

    private static int Round(double value, int minimum)
        => Math.Max(minimum, (int)Math.Round(value));

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
