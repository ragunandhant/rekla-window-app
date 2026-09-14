using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Video;

namespace RaceVideoProcessor.Tests;

public sealed class FontResolutionTests : IDisposable
{
    private const int TamilStart = 0x0B80;
    private const int TamilEnd = 0x0BFF;
    private const int LatinStart = 0x0020;
    private const int LatinEnd = 0x007E;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvp-fonts", Guid.NewGuid().ToString("N"));

    public FontResolutionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- Coverage: read from the font's own character map ------------------

    [Fact]
    public void AFontWithTamilGlyphsIsRecognised()
    {
        var font = SyntheticFont.Write(_root, "tamil.ttf", (LatinStart, LatinEnd), (TamilStart, TamilEnd));
        Assert.True(FontCoverage.CoversTamil(font));
    }

    [Fact]
    public void ALatinOnlyFontIsNotMistakenForTamil()
    {
        // This is the font that rendered S கருப்புசாமி as "S □□□□□".
        var font = SyntheticFont.Write(_root, "segoeui.ttf", (LatinStart, LatinEnd));
        Assert.False(FontCoverage.CoversTamil(font));
    }

    [Fact]
    public void AFontCollectionIsReadThroughItsFirstFace()
    {
        // Windows 11 ships Nirmala UI as Nirmala.ttc rather than a .ttf.
        var font = SyntheticFont.Write(_root, "Nirmala.ttc", collection: true, (LatinStart, LatinEnd), (TamilStart, TamilEnd));
        Assert.True(FontCoverage.CoversTamil(font));
    }

    [Fact]
    public void AFileThatIsNotAFontIsNotCovering()
    {
        var path = Path.Combine(_root, "notes.ttf");
        File.WriteAllText(path, "not a font");
        Assert.False(FontCoverage.CoversTamil(path));
    }

    // ---- Resolution ---------------------------------------------------------

    [Fact]
    public void AConfiguredFontWithoutTamilIsSkippedWhenATamilFontExists()
    {
        var latin = SyntheticFont.Write(Dir("config"), "segoeui.ttf", (LatinStart, LatinEnd));
        var bundled = Dir("bundled");
        SyntheticFont.Write(bundled, "NotoSansTamil-Regular.ttf", (LatinStart, LatinEnd), (TamilStart, TamilEnd));

        var resolver = new FontResolver(
            new AppSettings { FontFilePath = latin }, [bundled], systemFontsDirectory: null, FontCoverage.CoversTamil);

        var result = resolver.Resolve();

        Assert.True(result.SupportsTamil);
        Assert.EndsWith("NotoSansTamil-Regular.ttf", result.Path);
        Assert.Contains("skipped", result.Source);
    }

    [Fact]
    public void AConfiguredTamilFontIsUsed()
    {
        var chosen = SyntheticFont.Write(Dir("config"), "MyTamilFont.ttf", (TamilStart, TamilEnd));
        var bundled = Dir("bundled");
        SyntheticFont.Write(bundled, "NotoSansTamil-Regular.ttf", (TamilStart, TamilEnd));

        var resolver = new FontResolver(
            new AppSettings { FontFilePath = chosen }, [bundled], systemFontsDirectory: null, FontCoverage.CoversTamil);

        var result = resolver.Resolve();

        Assert.True(result.SupportsTamil);
        Assert.Equal(Path.GetFullPath(chosen), result.Path);
        Assert.Equal("configured", result.Source);
    }

    [Fact]
    public void ATamilFontIsFoundByItsGlyphsNotItsName()
    {
        // An installed Tamil font with an unhelpful file name is still found.
        var system = Dir("system");
        SyntheticFont.Write(system, "arial.ttf", (LatinStart, LatinEnd));
        SyntheticFont.Write(system, "zz-regional-font.ttf", (LatinStart, LatinEnd), (TamilStart, TamilEnd));

        var resolver = new FontResolver(new AppSettings(), [], system, FontCoverage.CoversTamil);

        var result = resolver.Resolve();

        Assert.True(result.SupportsTamil);
        Assert.EndsWith("zz-regional-font.ttf", result.Path);
    }

    [Fact]
    public void WithNoTamilFontAnywhereTheResultIsFlaggedRatherThanPresentedAsFine()
    {
        var system = Dir("system");
        SyntheticFont.Write(system, "segoeui.ttf", (LatinStart, LatinEnd));

        var resolver = new FontResolver(new AppSettings(), [], system, FontCoverage.CoversTamil);

        var result = resolver.Resolve();

        Assert.NotNull(result.Path);
        Assert.False(result.SupportsTamil);
    }

    // ---- Migration of settings saved by an earlier build ---------------------

    [Fact]
    public void TheOldLatinOnlyDefaultFontIsMigratedAway()
    {
        var settings = new AppSettings { SettingsVersion = 0, FontFilePath = @"C:\Windows\Fonts\segoeui.ttf" };

        settings.Normalize();

        Assert.Equal(string.Empty, settings.FontFilePath);
        Assert.Equal(AppSettings.CurrentSettingsVersion, settings.SettingsVersion);
    }

    [Fact]
    public void ValuesTheOperatorChoseSurviveMigration()
    {
        var settings = new AppSettings { SettingsVersion = 0, FontFilePath = @"D:\Fonts\MyTamilFont.ttf" };

        settings.Normalize();

        Assert.Equal(@"D:\Fonts\MyTamilFont.ttf", settings.FontFilePath);
    }

    [Fact]
    public void SettingsFromAnOlderBuildStartWithProcessingOnAndNormalZoom()
    {
        // Settings JSON written before v5 has neither field; the defaults apply.
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"settingsVersion\":4,\"uploadEnabled\":false}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        settings.Normalize();

        Assert.True(settings.ProcessingEnabled);
        Assert.False(settings.UploadEnabled);
        Assert.Equal(1.0, settings.UiZoom);
    }

    [Theory]
    [InlineData(0.3, 0.75)]
    [InlineData(1.12, 1.1)]
    [InlineData(9.0, 1.5)]
    [InlineData(double.NaN, 1.0)]
    public void ZoomIsClampedToTheSupportedLevels(double stored, double expected)
    {
        var settings = new AppSettings { UiZoom = stored };
        settings.Normalize();
        Assert.Equal(expected, settings.UiZoom);
    }
}

/// <summary>
/// Writes a minimal but structurally valid TrueType file — just a table directory
/// and a format-4 'cmap' — covering the given code point ranges. Enough to test
/// glyph-coverage detection exactly, without depending on installed fonts.
/// </summary>
internal static class SyntheticFont
{
    public static string Write(string directory, string fileName, params (int Start, int End)[] ranges)
        => Write(directory, fileName, collection: false, ranges);

    public static string Write(string directory, string fileName, bool collection, params (int Start, int End)[] ranges)
    {
        Directory.CreateDirectory(directory);
        var cmap = BuildCmap(ranges);

        // A collection prefixes a 16-byte 'ttcf' header; table offsets stay absolute.
        var sfntStart = collection ? 16 : 0;
        const int directorySize = 12 + 16;
        var cmapOffset = sfntStart + directorySize;

        using var output = new MemoryStream();
        if (collection)
        {
            WriteTag(output, "ttcf");
            WriteU32(output, 0x00010000);
            WriteU32(output, 1);
            WriteU32(output, (uint)sfntStart);
        }

        WriteU32(output, 0x00010000);  // sfnt version
        WriteU16(output, 1);           // numTables
        WriteU16(output, 16);          // searchRange
        WriteU16(output, 0);           // entrySelector
        WriteU16(output, 0);           // rangeShift

        WriteTag(output, "cmap");
        WriteU32(output, 0);                    // checksum (unchecked by readers here)
        WriteU32(output, (uint)cmapOffset);
        WriteU32(output, (uint)cmap.Length);

        output.Write(cmap);

        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    private static byte[] BuildCmap((int Start, int End)[] ranges)
    {
        var segments = ranges
            .OrderBy(r => r.End)
            .Select(r => (Start: r.Start, End: r.End, Delta: 1))   // glyph = cp + 1, never 0
            .Append((Start: 0xFFFF, End: 0xFFFF, Delta: 1))       // required terminator
            .ToList();
        var segCount = segments.Count;

        using var sub = new MemoryStream();
        WriteU16(sub, 4);                        // format
        WriteU16(sub, 0);                        // length, patched below
        WriteU16(sub, 0);                        // language
        WriteU16(sub, (ushort)(segCount * 2));   // segCountX2
        WriteU16(sub, 0);                        // searchRange
        WriteU16(sub, 0);                        // entrySelector
        WriteU16(sub, 0);                        // rangeShift
        foreach (var s in segments) WriteU16(sub, (ushort)s.End);
        WriteU16(sub, 0);                        // reservedPad
        foreach (var s in segments) WriteU16(sub, (ushort)s.Start);
        foreach (var s in segments) WriteU16(sub, (ushort)s.Delta);
        foreach (var _ in segments) WriteU16(sub, 0);   // idRangeOffset
        var subtable = sub.ToArray();
        subtable[2] = (byte)(subtable.Length >> 8);
        subtable[3] = (byte)subtable.Length;

        using var cmap = new MemoryStream();
        WriteU16(cmap, 0);      // version
        WriteU16(cmap, 1);      // numTables
        WriteU16(cmap, 3);      // platform: Windows
        WriteU16(cmap, 1);      // encoding: Unicode BMP
        WriteU32(cmap, 12);     // subtable offset from cmap start
        cmap.Write(subtable);
        return cmap.ToArray();
    }

    private static void WriteU16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    private static void WriteU16(Stream s, int v) => WriteU16(s, (ushort)v);

    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void WriteTag(Stream s, string tag)
    {
        foreach (var c in tag) s.WriteByte((byte)c);
    }
}
