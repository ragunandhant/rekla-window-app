using System.Security.Cryptography;
using System.Text;

namespace RaceVideoProcessor.Infrastructure.Data;

/// <summary>
/// Keeps the backend password out of the database in plain text.
///
/// On Windows it is encrypted with DPAPI for the current Windows user, so the
/// database file copied to another account or machine does not reveal it.
/// Elsewhere (tests, development on another OS) it is only encoded, and marked so.
/// </summary>
internal static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string EncodedPrefix = "b64:";
    private static readonly byte[] Entropy = "RaceVideoProcessor.Backend"u8.ToArray();

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return null;

        var bytes = Encoding.UTF8.GetBytes(secret);
        if (OperatingSystem.IsWindows())
            return DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));

        return EncodedPrefix + Convert.ToBase64String(bytes);
    }

    /// <summary>Returns an empty string when the value cannot be read, e.g. under another Windows user.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;

        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) && OperatingSystem.IsWindows())
            {
                var data = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
            }

            if (stored.StartsWith(EncodedPrefix, StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[EncodedPrefix.Length..]));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
        }

        return string.Empty;
    }
}
