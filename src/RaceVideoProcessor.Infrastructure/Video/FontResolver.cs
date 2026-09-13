using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>
/// Resolves the scoreboard font, and only ever hands FFmpeg a font that has been
/// verified to contain Tamil glyphs when one exists on the machine.
///
/// Every candidate is checked against its own character map (see
/// <see cref="FontCoverage"/>). A font is never trusted because of its name or
/// because it was configured: a configured font without Tamil glyphs is skipped
/// in favour of one that has them, since rendering the race data with it would
/// silently produce empty boxes in the finished video.
///
/// Search order, first Tamil-covering font wins:
///   1. the font file set in Settings,
///   2. fonts bundled with the application under tools\fonts,
///   3. known Tamil-capable Windows fonts — Nirmala UI ships as Nirmala.ttf on
///      Windows 10 and as Nirmala.ttc on Windows 11, so both are tried,
///   4. every other font in the Windows Fonts folder, verified the same way.
/// Only if nothing covers Tamil is a Latin font returned, flagged as such.
/// </summary>
public sealed class FontResolver : IFontResolver
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<string> _bundledDirectories;
    private readonly string? _systemFontsDirectory;
    private readonly Func<string, bool> _coversTamil;

    private readonly object _cacheGate = new();
    private string? _cachedFor;
    private FontResolution? _cached;

    private static readonly string[] BundledCandidates =
    [
        "NotoSansTamil-Regular.ttf",
        "NotoSansTamil-SemiBold.ttf",
        "NotoSerifTamil-Regular.ttf"
    ];

    private static readonly (string File, string Name)[] KnownTamilSystemFonts =
    [
        ("Nirmala.ttc", "Nirmala UI"),
        ("Nirmala.ttf", "Nirmala UI"),
        ("NirmalaB.ttf", "Nirmala UI Bold"),
        ("NirmalaS.ttf", "Nirmala UI Semilight"),
        ("latha.ttf", "Latha"),
        ("Latha.ttf", "Latha"),
        ("lathab.ttf", "Latha Bold"),
        ("NotoSansTamil-Regular.ttf", "Noto Sans Tamil")
    ];

    private static readonly (string File, string Name)[] LatinFallbackFonts =
    [
        ("segoeui.ttf", "Segoe UI"),
        ("arial.ttf", "Arial")
    ];

    public FontResolver(AppSettings settings)
        : this(settings, DefaultBundledDirectories().ToList(), DefaultSystemFontsDirectory(), FontCoverage.CoversTamil)
    {
    }

    /// <summary>Test seam: explicit search locations and coverage check. Not visible to DI.</summary>
    internal FontResolver(
        AppSettings settings,
        IReadOnlyList<string> bundledDirectories,
        string? systemFontsDirectory,
        Func<string, bool> coversTamil)
    {
        _settings = settings;
        _bundledDirectories = bundledDirectories;
        _systemFontsDirectory = systemFontsDirectory;
        _coversTamil = coversTamil;
    }

    public FontResolution Resolve()
    {
        // Resolution reads font files, and Build() runs twice per preview; the
        // answer only changes when the configured path does.
        var key = _settings.FontFilePath ?? string.Empty;
        lock (_cacheGate)
        {
            if (_cached is not null && string.Equals(_cachedFor, key, StringComparison.OrdinalIgnoreCase))
                return _cached;

            _cached = ResolveUncached();
            _cachedFor = key;
            return _cached;
        }
    }

    private FontResolution ResolveUncached()
    {
        var configured = _settings.FontFilePath;
        var configuredExists = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured);

        // 1. The operator's choice — but only if it can actually draw Tamil.
        if (configuredExists && _coversTamil(configured!))
            return new FontResolution(Path.GetFullPath(configured!), NameOf(configured!), true, "configured");

        var skippedNote = configuredExists
            ? $" — the configured font {NameOf(configured!)} has no Tamil glyphs and was skipped"
            : string.Empty;

        // 2. Bundled with the application.
        foreach (var directory in _bundledDirectories)
        {
            foreach (var candidate in BundledCandidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path) && _coversTamil(path))
                    return new FontResolution(Path.GetFullPath(path), NameOf(candidate), true, "bundled" + skippedNote);
            }
        }

        if (_systemFontsDirectory is not null && Directory.Exists(_systemFontsDirectory))
        {
            // 3. Known Tamil fonts that ship with Windows.
            foreach (var (file, name) in KnownTamilSystemFonts)
            {
                var path = Path.Combine(_systemFontsDirectory, file);
                if (File.Exists(path) && _coversTamil(path))
                    return new FontResolution(path, name, true, "system" + skippedNote);
            }

            // 4. Anything else installed that genuinely covers Tamil.
            foreach (var path in EnumerateFonts(_systemFontsDirectory))
            {
                if (_coversTamil(path))
                    return new FontResolution(path, NameOf(path), true, "system" + skippedNote);
            }
        }

        // Nothing on this machine covers Tamil. Return something that can at least
        // render Latin, clearly flagged so the operator is warned before rendering.
        if (configuredExists)
            return new FontResolution(Path.GetFullPath(configured!), NameOf(configured!), false, "configured");

        if (_systemFontsDirectory is not null)
        {
            foreach (var (file, name) in LatinFallbackFonts)
            {
                var path = Path.Combine(_systemFontsDirectory, file);
                if (File.Exists(path))
                    return new FontResolution(path, name, false, "system");
            }
        }

        return new FontResolution(null, "none", false, "unresolved");
    }

    /// <summary>Likely candidates first, so the scan usually stops within a few files.</summary>
    private static IEnumerable<string> EnumerateFonts(string directory)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory)
                .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            return [];
        }

        static int Priority(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (name.Contains("tamil")) return 0;
            if (name.Contains("nirmala") || name.Contains("latha")) return 1;
            if (name.Contains("noto")) return 2;
            return 3;
        }

        return files.OrderBy(Priority).ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    private static string NameOf(string path) => Path.GetFileNameWithoutExtension(path);

    private static IEnumerable<string> DefaultBundledDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "fonts");

        // Running from source (dotnet run) puts the binary several levels below
        // the repository root, so also look for the vendor copy there.
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

    private static string? DefaultSystemFontsDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return string.IsNullOrWhiteSpace(fonts) || !Directory.Exists(fonts) ? null : fonts;
    }
}
