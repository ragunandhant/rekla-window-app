using System.Globalization;
using System.Text;

namespace RaceVideoProcessor.Core.Services;

/// <summary>
/// Formats the API's race timing (a duration in seconds, e.g. 22.5) for display.
/// The raw number is never shown on its own: 22.5 renders as 00:22.50.
/// </summary>
public static class TimingFormatter
{
    /// <summary>Operator-facing default. Written unescaped; <see cref="Escape"/> handles TimeSpan's rules.</summary>
    public const string DefaultFormat = "mm:ss.ff";

    public static string Format(double seconds, string? format = null)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            seconds = 0;

        var span = TimeSpan.FromSeconds(seconds);
        var pattern = string.IsNullOrWhiteSpace(format) ? DefaultFormat : format.Trim();

        try
        {
            return span.ToString(Escape(pattern), CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            // Never let a bad configured pattern break rendering or a running job.
            return Fallback(span);
        }
    }

    /// <summary>
    /// TimeSpan custom format strings treat ':' and '.' as reserved, so a readable
    /// pattern like "mm:ss.ff" has to be escaped before it is handed to ToString.
    /// </summary>
    private static string Escape(string pattern)
    {
        var builder = new StringBuilder(pattern.Length + 4);
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                builder.Append(c);
                if (i + 1 < pattern.Length)
                    builder.Append(pattern[++i]);
                continue;
            }

            if (c is ':' or '.')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static string Fallback(TimeSpan span)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}.{2:00}",
            (int)span.TotalMinutes,
            span.Seconds,
            span.Milliseconds / 10);
}
