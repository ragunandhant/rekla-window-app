namespace RaceVideoProcessor.Core.Services;

/// <summary>Text after fitting: possibly shrunk, possibly ellipsized.</summary>
public sealed record FittedText(string Text, int FontSize, bool WasTruncated);

/// <summary>
/// Fits scoreboard text into a fixed pixel region.
///
/// The scoreboard allocates left / centre / right regions, so overlong text must
/// never cross into the card-number block. Fitting reduces the font size first
/// (down to a floor) and only ellipsizes when even the smallest size overflows —
/// shrinking keeps more of a long Tamil name readable than truncating it does.
/// </summary>
public static class LayoutTextFitter
{
    /// <summary>
    /// Average glyph advance as a fraction of font size. Tamil composes base
    /// glyphs with vowel signs and runs visually wider per character than Latin,
    /// so it gets a larger factor; mixed text takes the safer (wider) value.
    /// </summary>
    private const double LatinFactor = 0.55;
    private const double TamilFactor = 0.72;

    private const char TamilBlockStart = '஀';
    private const char TamilBlockEnd = '௿';

    public static FittedText Fit(string? value, int maxPixelWidth, int preferredFontSize, int minFontSize)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return new FittedText(string.Empty, preferredFontSize, false);

        maxPixelWidth = Math.Max(24, maxPixelWidth);
        minFontSize = Math.Clamp(minFontSize, 8, preferredFontSize);
        var factor = WidthFactor(text);

        for (var size = preferredFontSize; size >= minFontSize; size--)
        {
            if (EstimateWidth(text, size, factor) <= maxPixelWidth)
                return new FittedText(text, size, false);
        }

        // Still too wide at the smallest permitted size: ellipsize at that size.
        var charWidth = Math.Max(1.0, minFontSize * factor);
        var maxChars = Math.Max(2, (int)Math.Floor(maxPixelWidth / charWidth));
        if (text.Length <= maxChars)
            return new FittedText(text, minFontSize, false);

        return new FittedText(text[..Math.Max(1, maxChars - 1)].TrimEnd() + "…", minFontSize, true);
    }

    /// <summary>"name, location" — the single display format used by the UI and the scoreboard.</summary>
    public static string ComposeDisplay(string? name, string? location)
    {
        var n = (name ?? string.Empty).Trim();
        var l = (location ?? string.Empty).Trim();
        if (n.Length == 0) return l;
        return l.Length == 0 ? n : $"{n}, {l}";
    }

    private static double EstimateWidth(string text, int fontSize, double factor)
        => text.Length * fontSize * factor;

    private static double WidthFactor(string text)
        => ContainsTamil(text) ? TamilFactor : LatinFactor;

    /// <summary>True when the string contains any character from the Tamil block.</summary>
    public static bool ContainsTamil(string text)
    {
        foreach (var c in text)
        {
            if (c >= TamilBlockStart && c <= TamilBlockEnd)
                return true;
        }
        return false;
    }
}
