using System.Globalization;

namespace RaceVideoProcessor.Core.Services;

/// <summary>Text after fitting: possibly shrunk, possibly ellipsized.</summary>
public sealed record FittedText(string Text, int FontSize, bool WasTruncated);

/// <summary>
/// Fits scoreboard text into a fixed pixel region.
///
/// The scoreboard allocates left / centre / right regions, so overlong text must
/// never cross into the card-number plate. Fitting reduces the font size first
/// (down to a floor) and only ellipsizes when even the smallest size overflows —
/// shrinking keeps a long Tamil name readable, truncating loses the name.
///
/// Width is measured in *text elements* (grapheme clusters), not UTF-16 code
/// units. "வெங்கடேஷ் ராமகுமார்" is 19 code units but only 9 clusters on screen,
/// because Tamil composes each consonant with its vowel signs into one glyph.
/// Counting code units would treat that name as twice as wide as it is and
/// shrink or cut it needlessly. For the same reason, truncation cuts on cluster
/// boundaries: slicing mid-cluster would strand a vowel sign and render broken.
/// </summary>
public static class LayoutTextFitter
{
    /// <summary>
    /// Average advance per text element as a fraction of font size. A Tamil
    /// cluster carries a base consonant plus its vowel signs and occupies close
    /// to a full em, where Latin averages near half of one.
    /// </summary>
    private const double LatinFactor = 0.55;
    private const double TamilFactor = 0.95;

    private const char TamilBlockStart = '஀';
    private const char TamilBlockEnd = '௿';

    public static FittedText Fit(string? value, int maxPixelWidth, int preferredFontSize, int minFontSize)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return new FittedText(string.Empty, preferredFontSize, false);

        maxPixelWidth = Math.Max(24, maxPixelWidth);
        minFontSize = Math.Clamp(minFontSize, 8, Math.Max(8, preferredFontSize));
        var factor = WidthFactor(text);
        var elements = VisualLength(text);

        for (var size = preferredFontSize; size >= minFontSize; size--)
        {
            if (elements * size * factor <= maxPixelWidth)
                return new FittedText(text, size, false);
        }

        // Still too wide at the smallest permitted size: ellipsize, on cluster
        // boundaries, at that size.
        var elementWidth = Math.Max(1.0, minFontSize * factor);
        var maxElements = Math.Max(2, (int)Math.Floor(maxPixelWidth / elementWidth));
        if (elements <= maxElements)
            return new FittedText(text, minFontSize, false);

        var keep = Math.Clamp(maxElements - 1, 1, elements);
        var truncated = new StringInfo(text).SubstringByTextElements(0, keep).TrimEnd();
        return new FittedText(truncated + "…", minFontSize, true);
    }

    /// <summary>"name, location" — the single display format used by the UI and the scoreboard.</summary>
    public static string ComposeDisplay(string? name, string? location)
    {
        var n = (name ?? string.Empty).Trim();
        var l = (location ?? string.Empty).Trim();
        if (n.Length == 0) return l;
        return l.Length == 0 ? n : $"{n}, {l}";
    }

    /// <summary>Number of grapheme clusters — what a reader actually sees.</summary>
    public static int VisualLength(string? text)
        => string.IsNullOrEmpty(text) ? 0 : new StringInfo(text).LengthInTextElements;

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
