using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;
using RaceVideoProcessor.Infrastructure.Video;

namespace RaceVideoProcessor.Tests;

public sealed class OverlayLayoutTests
{
    private const string LongTamilName = "வெங்கடேஷ் ராமகுமார்";
    private const string LongTamilLocation = "திருவனந்தபுரம்";

    [Fact]
    public void TextShrinksBeforeItIsEverTruncated()
    {
        // A narrow region should first cost font size, not characters: shortening a
        // Tamil name loses meaning, shrinking it does not.
        var fitted = LayoutTextFitter.Fit(LongTamilName, maxPixelWidth: 220, preferredFontSize: 40, minFontSize: 18);

        Assert.False(fitted.WasTruncated);
        Assert.Equal(LongTamilName, fitted.Text);
        Assert.True(fitted.FontSize < 40);
        Assert.True(fitted.FontSize >= 18);
    }

    [Fact]
    public void TextIsTruncatedOnlyWhenTheSmallestSizeStillOverflows()
    {
        var fitted = LayoutTextFitter.Fit(
            LayoutTextFitter.ComposeDisplay(LongTamilName, LongTamilLocation),
            maxPixelWidth: 90, preferredFontSize: 40, minFontSize: 30);

        Assert.True(fitted.WasTruncated);
        Assert.EndsWith("…", fitted.Text);
        Assert.Equal(30, fitted.FontSize);
    }

    [Fact]
    public void TamilIsMeasuredMoreGenerouslyThanLatin()
    {
        // Tamil glyph clusters are wider, so the same character count must fit at a
        // smaller size than Latin — otherwise Tamil overruns its region on the video.
        var tamil = LayoutTextFitter.Fit("கருப்புசாமி", 200, 40, 10);
        var latin = LayoutTextFitter.Fit("karuppusami", 200, 40, 10);

        Assert.True(tamil.FontSize < latin.FontSize);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void ScoreboardBuildsProportionallyForEveryCommonResolution(int width, int height)
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-filter", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings();
            var builder = new FfmpegFilterBuilder(settings, StubFontResolver.WithTempFont(root));
            var metadata = new VideoMetadata("x.mp4", width, height, 25, 30, "h264", "aac", "yuv420p");
            var overlay = new OverlayData(LongTamilName, LongTamilLocation, "1000AAA", "ரமேஷ்", "கோயம்புத்தூர்", "00:22.50");

            var result = builder.Build(metadata, overlay, root);

            Assert.Contains("drawbox=", result.Filter);
            Assert.Contains("drawtext=", result.Filter);
            // The timing plaque appears only in the final seconds of the clip.
            Assert.Contains("between(t,26,31)", result.Filter);
            // Text reaches FFmpeg through files, never through the filter string.
            Assert.Equal("1000AAA", File.ReadAllText(Path.Combine(root, "card.txt")));
            Assert.Equal("00:22.50", File.ReadAllText(Path.Combine(root, "timing.txt")));
            Assert.Contains("கருப்பு", File.ReadAllText(Path.Combine(root, "primary-name.txt")) + LongTamilName);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void SecondaryBlockIsOmittedWhenThereIsNoSecondaryPlayer()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-filter", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings();
            var builder = new FfmpegFilterBuilder(settings, StubFontResolver.WithTempFont(root));
            var metadata = new VideoMetadata("x.mp4", 1920, 1080, 25, 30, "h264", "aac", "yuv420p");
            var overlay = new OverlayData("S கருப்புசாமி", "கணியூர்", "1000AAA", null, null, "00:22.50");

            var result = builder.Build(metadata, overlay, root);

            Assert.False(File.Exists(Path.Combine(root, "secondary-name.txt")));
            Assert.DoesNotContain("label-secondary.txt", result.Filter);
            Assert.Equal("—", result.SecondaryDisplay);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void TamilTextWithALatinOnlyFontIsReportedRatherThanRenderedAsBoxes()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-filter", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var fontPath = Path.Combine(root, "latin-only.ttf");
            File.WriteAllText(fontPath, "stub");
            var builder = new FfmpegFilterBuilder(new AppSettings(), new StubFontResolver(fontPath, supportsTamil: false));
            var metadata = new VideoMetadata("x.mp4", 1920, 1080, 25, 30, "h264", "aac", "yuv420p");
            var overlay = new OverlayData("S கருப்புசாமி", "கணியூர்", "1000AAA", null, null, "00:22.50");

            var result = builder.Build(metadata, overlay, root);

            Assert.NotNull(result.Warning);
            Assert.Contains("Tamil", result.Warning!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void BuildFailsClearlyWhenNoFontCanBeResolved()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-filter", Guid.NewGuid().ToString("N"));
        var builder = new FfmpegFilterBuilder(new AppSettings(), new StubFontResolver(null));
        var metadata = new VideoMetadata("x.mp4", 1920, 1080, 25, 30, "h264", "aac", "yuv420p");
        var overlay = new OverlayData("A", "B", "1000AAA", null, null, "00:22.50");

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build(metadata, overlay, root));
        Assert.Contains("font", error.Message, StringComparison.OrdinalIgnoreCase);
        try { Directory.Delete(root, true); } catch { }
    }
}
