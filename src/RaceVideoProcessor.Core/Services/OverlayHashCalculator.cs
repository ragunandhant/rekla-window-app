using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Services;

/// <summary>
/// Hashes only the fields that are burned into the video. A change here means an
/// already-rendered output no longer matches the race data and must be marked
/// OUTDATED; changes to anything else (like counts, statistics, comment counts)
/// must not invalidate a good output.
/// </summary>
public static class OverlayHashCalculator
{
    /// <summary>Unit separator: cannot appear in a field, so boundaries stay unambiguous.</summary>
    private const string Separator = "\u001F";

    public static string Calculate(RaceEntry entry)
    {
        var canonical = string.Join(Separator,
            entry.CardNumber.Trim(),
            entry.PrimaryName.Trim(),
            entry.PrimaryLocation.Trim(),
            (entry.SecondaryName ?? string.Empty).Trim(),
            (entry.SecondaryLocation ?? string.Empty).Trim(),
            entry.TimingSeconds.ToString("0.###", CultureInfo.InvariantCulture));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }
}
