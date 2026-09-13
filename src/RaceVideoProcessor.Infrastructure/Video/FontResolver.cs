using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>
/// Resolves the scoreboard font, preferring fonts that are known to cover Tamil.
///
/// Order:
///   1. An explicit path in settings (the operator's override wins).
///   2. A font bundled with the application under tools\fonts.
///   3. A Tamil-capable font that ships with Windows (Nirmala UI, Latha).
///   4. A Latin-only system font — reported as Tamil-incapable so the UI can warn,
///      because rendering Tamil with it produces boxes rather than an error.
/// </summary>
public sealed class FontResolver : IFontResolver
{
    private readonly AppSettings _settings;

    public FontResolver(AppSettings settings) => _settings = settings;

    /// <summary>Bundled candidates, in preference order, relative to tools\fonts.</summary>
    private static readonly string[] BundledCandidates =
    [
        "NotoSansTamil-Regular.ttf",
        "NotoSansTamil-SemiBold.ttf",
        "NotoSerifTamil-Regular.ttf"
    ];

    /// <summary>Windows fonts that cover the Tamil block. Nirmala UI ships with Windows 8 and later.</summary>
    private static readonly (string File, string Name)[] TamilSystemFonts =
    [
        ("Nirmala.ttf", "Nirmala UI"),
        ("NirmalaB.ttf", "Nirmala UI Bold"),
        ("latha.ttf", "Latha"),
        ("Latha.ttf", "Latha"),
        ("NotoSansTamil-Regular.ttf", "Noto Sans Tamil")
    ];

    /// <summary>Latin-only last resort. Usable, but Tamil will not render.</summary>
    private static readonly (string File, string Name)[] FallbackSystemFonts =
    [
        ("segoeui.ttf", "Segoe UI"),
        ("arial.ttf", "Arial")
    ];

    public FontResolution Resolve()
    {
        var configured = _settings.FontFilePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return new FontResolution(
                Path.GetFullPath(configured),
                Path.GetFileNameWithoutExtension(configured),
                LooksTamilCapable(configured),
                "configured");
        }

        foreach (var directory in BundledFontDirectories())
        {
            foreach (var candidate in BundledCandidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                    return new FontResolution(Path.GetFullPath(path), Path.GetFileNameWithoutExtension(candidate), true, "bundled");
            }
        }

        var fontsDirectory = SystemFontsDirectory();
        if (fontsDirectory is not null)
        {
            foreach (var (file, name) in TamilSystemFonts)
            {
                var path = Path.Combine(fontsDirectory, file);
                if (File.Exists(path))
                    return new FontResolution(path, name, true, "system");
            }

            foreach (var (file, name) in FallbackSystemFonts)
            {
                var path = Path.Combine(fontsDirectory, file);
                if (File.Exists(path))
                    return new FontResolution(path, name, false, "system");
            }
        }

        return new FontResolution(null, "none", false, "unresolved");
    }

    private static IEnumerable<string> BundledFontDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "fonts");

        // Running from source (dotnet run) puts the binary several levels below the
        // repository root, so also look for the checked-in vendor copy.
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && probe is not null; i++)
        {
            var candidate = Path.Combine(probe.FullName, "vendor", "fonts");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }
            probe = probe.Parent;
        }
    }

    private static string? SystemFontsDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return string.IsNullOrWhiteSpace(fonts) || !Directory.Exists(fonts) ? null : fonts;
    }

    private static bool LooksTamilCapable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name.Contains("tamil") || name.Contains("nirmala") || name.Contains("latha")
               || name.Contains("noto") || name.Contains("bamini") || name.Contains("vijaya");
    }
}
