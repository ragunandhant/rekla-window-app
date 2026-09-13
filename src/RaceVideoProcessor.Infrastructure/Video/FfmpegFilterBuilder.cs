using System.Globalization;
using System.Text;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Infrastructure.Video;

public sealed record FilterBuildResult(
    string Filter,
    string PrimaryDisplay,
    string SecondaryDisplay,
    string TimingDisplay,
    string FontName,
    string? Warning);

/// <summary>
/// Builds the broadcast lower-third and the closing timing plaque.
///
/// Design, and why:
///   - A single full-width panel anchored above the safe-area bottom margin, with
///     an accent rule along its top edge and a soft drop shadow beneath it. The
///     rule reads as a deliberate graphic rather than a box of text on video.
///   - The card number sits in a filled accent plate at dead centre: it is the
///     entry identifier, so it is the focal point, and a filled plate survives
///     busy footage far better than text alone.
///   - Primary left, secondary right, both as name over location with a micro
///     label above. Two weights of size plus one of opacity give the hierarchy.
///   - Left and right regions are symmetric around the centre plate, so the plate
///     stays in the same place whether or not a secondary player exists.
///
/// Every dimension derives from the probed frame size, so 720p through 4K scale
/// proportionally. All text is written to UTF-8 files and passed with textfile=,
/// which sidesteps filter-string escaping entirely and is what makes Tamil safe.
/// </summary>
public sealed class FfmpegFilterBuilder
{
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

        var w = Math.Max(64, metadata.Width);
        var h = Math.Max(64, metadata.Height);
        var scale = _settings.FontSizeScale;

        // ---- Panel geometry -------------------------------------------------
        var sideMargin = Round(w * 0.030, 12);
        var panelHeight = Round(h * 0.112, 72);
        var bottomMargin = Round(h * 0.042, 12);
        var panelX = sideMargin;
        var panelWidth = Math.Max(200, w - sideMargin * 2);
        var panelY = Math.Max(0, h - bottomMargin - panelHeight);
        var pad = Round(panelWidth * 0.016, 14);
        var accentBar = Round(h * 0.0035, 3);
        var shadow = Round(h * 0.004, 3);

        var contentTop = panelY + accentBar;
        var contentHeight = panelHeight - accentBar;

        // ---- Regions: symmetric sides around a centred card plate -----------
        var centreWidth = Round(panelWidth * 0.22, 140);
        var sideWidth = (panelWidth - centreWidth) / 2;
        var centreX = panelX + sideWidth;
        var rightRegionX = centreX + centreWidth;

        // ---- Type scale -----------------------------------------------------
        var nameSize = Round(h * 0.040 * scale, 18);
        var nameMin = Round(h * 0.026 * scale, 14);
        var locationSize = Round(h * 0.026 * scale, 13);
        var locationMin = Round(h * 0.019 * scale, 11);
        var labelSize = Round(h * 0.0155 * scale, 10);
        var cardSize = Round(h * 0.052 * scale, 20);
        var cardMin = Round(h * 0.030 * scale, 14);

        var textWidth = Math.Max(60, sideWidth - pad * 2);
        var hasSecondary = overlay.HasSecondary;

        var primaryDisplay = LayoutTextFitter.ComposeDisplay(overlay.PrimaryName, null);
        var primaryLocation = (overlay.PrimaryLocation ?? string.Empty).Trim();
        var secondaryDisplay = hasSecondary ? overlay.SecondaryName!.Trim() : string.Empty;
        var secondaryLocation = hasSecondary ? (overlay.SecondaryLocation ?? string.Empty).Trim() : string.Empty;

        var primaryFit = LayoutTextFitter.Fit(primaryDisplay, textWidth, nameSize, nameMin);
        var primaryLocFit = LayoutTextFitter.Fit(primaryLocation, textWidth, locationSize, locationMin);
        var secondaryFit = LayoutTextFitter.Fit(secondaryDisplay, textWidth, nameSize, nameMin);
        var secondaryLocFit = LayoutTextFitter.Fit(secondaryLocation, textWidth, locationSize, locationMin);
        var cardFit = LayoutTextFitter.Fit(overlay.CardNumber.Trim(),
            Math.Max(60, centreWidth - pad), cardSize, cardMin);

        // ---- Colours --------------------------------------------------------
        var panelColor = Color(_settings.ScoreboardBackgroundColor, _settings.ScoreboardOpacity);
        var panelSolid = Color(_settings.ScoreboardBackgroundColor, 1.0);
        var accent = Color(_settings.ScoreboardAccentColor, 1.0);
        var text = Color(_settings.ScoreboardTextColor, 1.0);
        var textSoft = Color(_settings.ScoreboardTextColor, 0.74);
        var labelColor = Color(_settings.ScoreboardAccentColor, 0.92);
        var divider = Color(_settings.ScoreboardTextColor, 0.16);
        var shadowColor = "0x000000@0.40";

        var fontOption = $"fontfile='{FilterPath(font.Path)}'";
        var shadowX = Math.Max(1, h / 900);
        var shadowY = Math.Max(1, h / 700);

        // ---- Text files (UTF-8, no BOM) -------------------------------------
        var files = new TextFileWriter(workDirectory);
        var primaryFile = files.Write("primary-name.txt", primaryFit.Text);
        var primaryLocFile = files.Write("primary-location.txt", primaryLocFit.Text);
        var cardFile = files.Write("card.txt", cardFit.Text);
        var labelPrimaryFile = files.Write("label-primary.txt", "PRIMARY");
        var labelCardFile = files.Write("label-card.txt", "CARD");
        var timingFile = files.Write("timing.txt", overlay.TimingText);
        var timingLabelFile = files.Write("label-timing.txt", "TIMING");

        var filters = new List<string>
        {
            // Drop shadow, then the panel, then the accent rule along its top edge.
            Box(panelX, panelY + shadow, panelWidth, panelHeight, shadowColor),
            Box(panelX, panelY, panelWidth, panelHeight, panelColor),
            Box(panelX, panelY, panelWidth, accentBar, accent),

            // Divider between the primary block and the card plate.
            Box(centreX, contentTop + contentHeight / 6, 1, contentHeight * 2 / 3, divider),

            // Primary: micro label, name, location.
            Text(labelPrimaryFile, fontOption, labelColor, labelSize,
                (panelX + pad).ToString(CultureInfo.InvariantCulture),
                LineY(contentTop, contentHeight, 0.13, labelSize), shadowColor, shadowX, shadowY),
            Text(primaryFile, fontOption, text, primaryFit.FontSize,
                (panelX + pad).ToString(CultureInfo.InvariantCulture),
                LineY(contentTop, contentHeight, 0.42, primaryFit.FontSize), shadowColor, shadowX, shadowY),
            Text(primaryLocFile, fontOption, textSoft, primaryLocFit.FontSize,
                (panelX + pad).ToString(CultureInfo.InvariantCulture),
                LineY(contentTop, contentHeight, 0.74, primaryLocFit.FontSize), shadowColor, shadowX, shadowY)
        };

        // ---- Centre: the card plate ----------------------------------------
        var plateInset = Round(panelHeight * 0.16, 6);
        var plateY = contentTop + plateInset / 2;
        var plateHeight = Math.Max(10, contentHeight - plateInset);
        var plateX = centreX + pad / 2;
        var plateWidth = Math.Max(40, centreWidth - pad);

        filters.Add(Box(plateX, plateY, plateWidth, plateHeight, accent));
        filters.Add(Text(labelCardFile, fontOption, Color(_settings.ScoreboardBackgroundColor, 0.70), labelSize,
            Centred(plateX, plateWidth),
            (plateY + Round(plateHeight * 0.14, 2)).ToString(CultureInfo.InvariantCulture),
            null, 0, 0));
        filters.Add(Text(cardFile, fontOption, panelSolid, cardFit.FontSize,
            Centred(plateX, plateWidth),
            (plateY + Round(plateHeight * 0.36, 2)).ToString(CultureInfo.InvariantCulture),
            null, 0, 0));

        // ---- Right: secondary player, only when one exists -------------------
        if (hasSecondary)
        {
            var labelSecondaryFile = files.Write("label-secondary.txt", "SECONDARY");
            var secondaryFile = files.Write("secondary-name.txt", secondaryFit.Text);
            var secondaryLocFile = files.Write("secondary-location.txt", secondaryLocFit.Text);
            var rightEdge = panelX + panelWidth - pad;

            filters.Add(Box(rightRegionX, contentTop + contentHeight / 6, 1, contentHeight * 2 / 3, divider));
            filters.Add(Text(labelSecondaryFile, fontOption, labelColor, labelSize,
                RightAligned(rightEdge), LineY(contentTop, contentHeight, 0.13, labelSize), shadowColor, shadowX, shadowY));
            filters.Add(Text(secondaryFile, fontOption, text, secondaryFit.FontSize,
                RightAligned(rightEdge), LineY(contentTop, contentHeight, 0.42, secondaryFit.FontSize), shadowColor, shadowX, shadowY));
            filters.Add(Text(secondaryLocFile, fontOption, textSoft, secondaryLocFit.FontSize,
                RightAligned(rightEdge), LineY(contentTop, contentHeight, 0.74, secondaryLocFit.FontSize), shadowColor, shadowX, shadowY));
        }

        // ---- Closing timing plaque ------------------------------------------
        var plaqueWidth = Round(w * 0.34, 220);
        var plaqueHeight = Round(h * 0.17, 110);
        var plaqueX = (w - plaqueWidth) / 2;
        var plaqueY = (h - plaqueHeight) / 2;
        var plaqueRule = Round(h * 0.0035, 3);
        var timingSize = Round(h * 0.088 * scale, 34);
        var timingLabelSize = Round(h * 0.018 * scale, 11);

        var start = Math.Max(0, metadata.DurationSeconds - _settings.CompletionTimeDisplaySeconds);
        var enable = completionAlwaysVisible
            ? string.Empty
            : $":enable='between(t,{Num(start)},{Num(metadata.DurationSeconds + 1)})'";

        filters.Add(Box(plaqueX, plaqueY + shadow, plaqueWidth, plaqueHeight, shadowColor) + enable);
        filters.Add(Box(plaqueX, plaqueY, plaqueWidth, plaqueHeight, panelColor) + enable);
        filters.Add(Box(plaqueX, plaqueY, plaqueWidth, plaqueRule, accent) + enable);
        filters.Add(Text(timingLabelFile, fontOption, labelColor, timingLabelSize,
            Centred(plaqueX, plaqueWidth),
            (plaqueY + Round(plaqueHeight * 0.18, 2)).ToString(CultureInfo.InvariantCulture),
            null, 0, 0) + enable);
        filters.Add(Text(timingFile, fontOption, text, timingSize,
            Centred(plaqueX, plaqueWidth),
            (plaqueY + Round(plaqueHeight * 0.38, 2)).ToString(CultureInfo.InvariantCulture),
            shadowColor, shadowX, shadowY) + enable);

        var anyTruncated = primaryFit.WasTruncated || primaryLocFit.WasTruncated ||
                           secondaryFit.WasTruncated || secondaryLocFit.WasTruncated || cardFit.WasTruncated;
        var warning = BuildWarning(font, overlay, anyTruncated);

        return new FilterBuildResult(
            string.Join(',', filters),
            LayoutTextFitter.ComposeDisplay(overlay.PrimaryName, overlay.PrimaryLocation),
            hasSecondary ? LayoutTextFitter.ComposeDisplay(overlay.SecondaryName, overlay.SecondaryLocation) : "—",
            overlay.TimingText,
            font.Name,
            warning);
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

    // ---- Filter fragment helpers -------------------------------------------

    private static string Box(int x, int y, int width, int height, string color)
        => $"drawbox=x={x}:y={y}:w={Math.Max(1, width)}:h={Math.Max(1, height)}:color={color}:t=fill";

    private static string Text(
        string textFile, string fontOption, string color, int fontSize,
        string x, string y, string? shadowColor, int shadowX, int shadowY)
    {
        var builder = new StringBuilder();
        builder.Append("drawtext=").Append(fontOption)
               .Append(":textfile='").Append(FilterPath(textFile)).Append('\'')
               .Append(":fontcolor=").Append(color)
               .Append(":fontsize=").Append(fontSize.ToString(CultureInfo.InvariantCulture))
               .Append(":x=").Append(x)
               .Append(":y=").Append(y)
               .Append(":expansion=none");
        if (shadowColor is not null)
        {
            builder.Append(":shadowcolor=").Append(shadowColor)
                   .Append(":shadowx=").Append(shadowX.ToString(CultureInfo.InvariantCulture))
                   .Append(":shadowy=").Append(shadowY.ToString(CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>Top coordinate for a text line placed at a fraction of the content height.</summary>
    private static string LineY(int contentTop, int contentHeight, double fraction, int fontSize)
        => (contentTop + (int)Math.Round(contentHeight * fraction) - fontSize / 2)
            .ToString(CultureInfo.InvariantCulture);

    private static string Centred(int regionX, int regionWidth)
        => $"{regionX}+({regionWidth}-text_w)/2";

    private static string RightAligned(int rightEdge) => $"{rightEdge}-text_w";

    private static int Round(double value, int minimum)
        => Math.Max(minimum, (int)Math.Round(value));

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FilterPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Replace(":", "\\:").Replace("'", "\\'");
    }

    private static string Color(string hex, double alpha)
    {
        var clean = string.IsNullOrWhiteSpace(hex) ? "FFFFFF" : hex.Trim().TrimStart('#');
        if (clean.Length != 6)
            clean = "FFFFFF";
        return $"0x{clean}@{alpha.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Writes overlay strings to UTF-8 files without a BOM. drawtext reads them with
    /// textfile=, so no text ever passes through filter-string escaping — which is
    /// what keeps Tamil, commas and quotes intact.
    /// </summary>
    private sealed class TextFileWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        private readonly string _directory;

        public TextFileWriter(string directory) => _directory = directory;

        public string Write(string name, string value)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, value ?? string.Empty, Utf8NoBom);
            return path;
        }
    }
}
