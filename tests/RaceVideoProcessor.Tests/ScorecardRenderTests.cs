using System.Diagnostics;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Video;
using SkiaSharp;

namespace RaceVideoProcessor.Tests;

/// <summary>The three-section digital-timer scoreboard, rendered into plates.</summary>
public sealed class ScorecardRenderTests : IDisposable
{
    private static readonly VideoMetadata Hd = new("x.mp4", 1920, 1080, 25, 30, "h264", "aac", "yuv420p");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvp-scorecard", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static ScoreboardFonts Fonts => ScoreboardFonts.Bundled()
                                            ?? throw new InvalidOperationException("Bundled scoreboard fonts were not copied to the test output.");

    private FfmpegFilterBuilder Builder() => new(new AppSettings(), new StubFontResolver(StubFontResolver.TestTamilFont));

    private static OverlayData Overlay(string? secondary = "முத்துக்குமார்")
        => new("KS சிவராம்குமார்", "கெட்டிமல்லன்புதூர்", "100", secondary, secondary is null ? null : "அரிமளம்", "00:17.88");

    [Fact]
    public void TheDesignFontsShipWithTheApplication()
    {
        var fonts = ScoreboardFonts.Bundled();
        Assert.NotNull(fonts);

        using var display = SKTypeface.FromFile(fonts!.Display);
        Assert.Equal("Barlow", display.FamilyName);
        Assert.Equal(700, display.FontWeight);

        using var tamil = SKTypeface.FromFile(fonts.TamilText);
        Assert.Equal("Noto Sans Tamil", tamil.FamilyName);
        Assert.Equal(500, tamil.FontWeight);
        Assert.NotEqual(0, tamil.GetGlyph(0x0B9A));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void ThreeSectionsSpanTheFrameAtEveryResolution(int width, int height)
    {
        var result = Builder().Build(Hd with { Width = width, Height = height }, Overlay(), _root);

        // Both plates — scorecard and closing plate — are full-width rows at one y.
        var matches = System.Text.RegularExpressions.Regex.Matches(result.Filter, @"overlay=x=(\d+):y=(\d+)");
        Assert.Equal(2, matches.Count);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            Assert.Equal(0, int.Parse(m.Groups[1].Value));
        }
        Assert.Equal(matches[0].Groups[2].Value, matches[1].Groups[2].Value);
        var plateY = int.Parse(matches[0].Groups[2].Value);

        using var plate = SKBitmap.Decode(result.Inputs[0].Path);
        Assert.Equal(width, plate.Width);
        using var closing = SKBitmap.Decode(result.Inputs[1].Path);
        Assert.Equal(width, closing.Width);

        var s = width / 1920.0;
        // Bottom edge 4 % above the frame bottom (88 px boxes + 24 px shadow margin).
        Assert.InRange(plateY + (24 + 88) * s, height * 0.96 - 2, height * 0.96 + 2);

        // Width 95 %: the row starts at 2.5 %; all three sections are opaque.
        var midRow = (int)Math.Round((24 + 44) * s);
        Assert.Equal(255, plate.GetPixel((int)(width * 0.025 + 6 * s), midRow).Alpha);
        Assert.Equal(255, plate.GetPixel(width / 2, midRow).Alpha);
        Assert.Equal(255, plate.GetPixel((int)(width * 0.975 - 6 * s), midRow).Alpha);

        // The 88 px centre square is centred: its middle is the frame middle …
        var squareLeft = (int)Math.Round((960 - 44) * s);
        var squareRight = (int)Math.Round((960 + 44) * s);
        // … and the 12 px gaps beside it are open (at most shadow fringe).
        Assert.True(plate.GetPixel(squareLeft - (int)Math.Round(6 * s), midRow).Alpha < 200);
        Assert.True(plate.GetPixel(squareRight + (int)Math.Round(6 * s), midRow).Alpha < 200);
    }

    [Fact]
    public void SurfaceColoursAreTheHtmlColours()
    {
        var result = Builder().Build(Hd, Overlay(), _root);
        using var plate = SKBitmap.Decode(result.Inputs[0].Path);

        // Box background away from text: the 135deg gradient runs
        // #0b150d → #040805, so every channel sits in between.
        var bg = plate.GetPixel(54, 104);
        Assert.InRange(bg.Red, 0x04, 0x0b);
        Assert.InRange(bg.Green, 0x08, 0x15);
        Assert.InRange(bg.Blue, 0x05, 0x0d);

        // The 1.5 px border is #14421b: sampled on the left box's top edge.
        var border = plate.GetPixel(476, 24);
        Assert.InRange(border.Red, 0x0c, 0x1c);
        Assert.InRange(border.Green, 0x3a, 0x4a);
        Assert.InRange(border.Blue, 0x13, 0x23);
    }

    [Fact]
    public void ScoreboardShowsNamesLocationsAndCartOnly()
    {
        var result = Builder().Build(Hd, Overlay(), _root);

        Assert.Equal("KS சிவராம்குமார், கெட்டிமல்லன்புதூர்", result.RenderedText!["primary"]);
        Assert.Equal("கெட்டிமல்லன்புதூர்", result.RenderedText["primaryLocation"]);
        Assert.Equal("100", result.RenderedText["card"]);
        Assert.Equal("00:17.88", result.RenderedText["center"]);
        Assert.Equal("முத்துக்குமார், அரிமளம்", result.RenderedText["secondary"]);
        Assert.Equal("அரிமளம்", result.RenderedText["secondaryLocation"]);
        Assert.Equal("00:17.88", result.RenderedText["timing"]);
        Assert.Null(result.Warning);
        Assert.DoesNotContain("drawtext", result.Filter);

        // All scoreboard text is the HTML's main green; no white/navy/gold remains.
        using var plate = SKBitmap.Decode(result.Inputs[0].Path);
        Assert.True(Contains(plate, Whole(plate), 0x39, 0xff, 0x14), "main green text");
        Assert.False(Contains(plate, Whole(plate), 0xff, 0xff, 0xff), "no white text");
        Assert.False(Contains(plate, Whole(plate), 0xe3, 0xb2, 0x3c), "no gold accent");
        Assert.False(Contains(plate, Whole(plate), 0x16, 0x21, 0x3a), "no navy background");
    }

    [Fact]
    public void SideTextIsCentredInBothRectangles()
    {
        // Cart "100" keeps the 88 px square: left box x 48–904, right box x 1016–1872.
        var result = Builder().Build(Hd, Overlay(), _root);
        using var plate = SKBitmap.Decode(result.Inputs[0].Path);

        var (lLeft, lRight, lTop, lBottom) = GreenBox(plate, SKRect.Create(48, 24, 856, 88));
        Assert.InRange((lLeft + lRight) / 2.0, 476 - 5, 476 + 5);
        Assert.True(lBottom - lTop > 10, "primary text has real height (centred, not top-aligned)");

        var (rLeft, rRight, rTop, rBottom) = GreenBox(plate, SKRect.Create(1016, 24, 856, 88));
        Assert.InRange((rLeft + rRight) / 2.0, 1444 - 5, 1444 + 5);
        Assert.True(rBottom - rTop > 10, "secondary text has real height (centred, not top-aligned)");
    }

    [Fact]
    public void CartNumberKeepsAFixedSizeWhileItsPanelExpands()
    {
        // "1" through "123456": identical glyph height every time (the size
        // never shrinks), only the centre span grows with the character count.
        // Measured in x 800–1120: inside every centre panel, clear of the
        // standard side text (which ends before 800 and starts after 1120).
        var carts = new[] { "1", "10", "100", "1234", "12345", "123456" };
        var heights = new List<int>();
        var widths = new List<int>();
        foreach (var cart in carts)
        {
            var overlay = Overlay() with { CardNumber = cart };
            var result = Builder().Build(Hd, overlay, Path.Combine(_root, "cart-" + cart));
            using var plate = SKBitmap.Decode(result.Inputs[0].Path);
            var window = SKRect.Create(800, 24, 320, 88);
            var (left, right, top, bottom) = GreenBox(plate, window);
            heights.Add(bottom - top);
            widths.Add(right - left);
        }

        Assert.All(heights, h => Assert.InRange(h, heights[0] - 2, heights[0] + 2));
        for (var i = 1; i < widths.Count; i++)
            Assert.True(widths[i] > widths[i - 1], $"cart {carts[i]} should span wider than {carts[i - 1]}");

        // And the reference cart stays centred on the frame middle.
        var reference = Builder().Build(Hd, Overlay(), Path.Combine(_root, "cart-ref"));
        using var plate100 = SKBitmap.Decode(reference.Inputs[0].Path);
        var (l, r) = GreenSpan(plate100, SKRect.Create(916, 24, 88, 88));
        Assert.InRange((l + r) / 2.0, 956, 964);
    }

    [Fact]
    public void WithoutASecondaryPlayerTheRightRectangleStaysEmpty()
    {
        var twoDir = Path.Combine(_root, "two");
        var oneDir = Path.Combine(_root, "one");
        var two = Builder().Build(Hd, Overlay(), twoDir);
        var one = Builder().Build(Hd, Overlay(secondary: null), oneDir);
        Assert.Equal("—", one.RenderedText!["secondary"]);
        Assert.Equal("—", one.SecondaryDisplay);

        // Same geometry — the cart square never moves — but no green text right.
        Assert.Equal(two.Filter, one.Filter);
        using var onePlate = SKBitmap.Decode(one.Inputs[0].Path);
        var rightBox = SKRect.Create(1016, 24, 856, 88);
        Assert.False(Contains(onePlate, rightBox, 0x39, 0xff, 0x14), "no secondary text");
        Assert.False(Contains(onePlate, rightBox, 0xa3, 0xe6, 0x35), "no secondary text");
        // The empty rectangle itself is still painted (structure preserved).
        Assert.Equal(255, onePlate.GetPixel(1400, 100).Alpha);
        // Primary side is unaffected.
        Assert.True(Contains(onePlate, SKRect.Create(48, 24, 856, 88), 0x39, 0xff, 0x14), "primary text");
    }

    [Fact]
    public void ClosingPlateReplacesTheCartInTheCentrePanel()
    {
        var result = Builder().Build(Hd, Overlay(), _root);

        // 25 s clip, 4 s window: scorecard until 21 s, closing plate from 21 s.
        Assert.Contains("fade=t=in:st=21:d=0.3:alpha=1", result.Filter);
        Assert.Contains("enable='lt(t,21)'", result.Filter);
        Assert.Contains("enable='gte(t,21)'", result.Filter);
        Assert.Contains("-loop", result.Inputs[1].InputOptions);

        // Two full-width row plates — nothing parked in the middle of the video.
        var overlays = System.Text.RegularExpressions.Regex.Matches(result.Filter, @"overlay=x=(\d+):y=(\d+)");
        Assert.Equal(2, overlays.Count);
        Assert.All(overlays, m => Assert.Equal("0", m.Groups[1].Value));
        using var closing = SKBitmap.Decode(result.Inputs[1].Path);
        Assert.Equal(1920, closing.Width);

        // The centre panel shows the timing, the cart data stays intact.
        Assert.Equal("100", result.RenderedText!["card"]);
        Assert.Equal("00:17.88", result.RenderedText["center"]);
        Assert.Equal("00:17.88", result.RenderedText["timing"]);

        // Timing digits in display-face green inside the centre band, no white text.
        Assert.True(Contains(closing, SKRect.Create(800, 24, 320, 88), 0x39, 0xff, 0x14), "timer digits");
        Assert.False(Contains(closing, Whole(closing), 0xff, 0xff, 0xff), "no white text");
    }

    [Fact]
    public void LongTamilTextShrinksLikeFitSingleLine()
    {
        // In Chrome this text settles at 11 px, 829.6 px wide (fitSingleLine stops at clientWidth − 2 = 851).
        const string longName = "வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார், கோயம்புத்தூர் கோயம்புத்தூர் கோயம்புத்தூர்";
        using var renderer = new ShapedTextRenderer(Fonts.PlayerStack);

        var line = renderer.Fit(longName, maxWidth: 851, maxSize: 20, minSize: 6);

        Assert.Equal(11f, line.Size);
        Assert.InRange(line.Width, 827.6, 831.6);
    }

    [Fact]
    public void LongNamesShrinkThenEllipsizeOnOneLine()
    {
        const string longPrimary = "வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார் வெங்கடேஷ் ராமகுமார்";
        const string longSecondary = "முத்துக்குமார் முத்துக்குமார் முத்துக்குமார் முத்துக்குமார் முத்துக்குமார் முத்துக்குமார் முத்துக்குமார் முத்துக்குமார் முத்துக்குமார் முத்துக்குமார்";
        var overlay = new OverlayData(longPrimary, "கோயம்புத்தூர் கோயம்புத்தூர் கோயம்புத்தூர்", "2B6", longSecondary, "அரிமளம் அரிமளம் அரிமளம்", "00:17.88");
        var result = Builder().Build(Hd, overlay, _root);

        // Beyond the minimum size the text truncates with a clean ellipsis …
        Assert.EndsWith("…", result.RenderedText!["primary"]);
        Assert.StartsWith(longPrimary[..10], result.RenderedText["primary"]);
        Assert.EndsWith("…", result.RenderedText["secondary"]);
        Assert.Equal("2B6", result.RenderedText["card"]);
        // … and every section keeps its geometry: nothing collides, nothing leaves its box.
        using var plate = SKBitmap.Decode(result.Inputs[0].Path);
        Assert.Equal(1920, plate.Width);
        Assert.True(Contains(plate, SKRect.Create(48, 24, 856, 88), 0x39, 0xff, 0x14), "primary text");
        Assert.True(Contains(plate, SKRect.Create(916, 24, 88, 88), 0x39, 0xff, 0x14), "cart digits");
    }

    [Fact]
    public void LatinUsesDisplayFaceWhileTamilFallsBack()
    {
        using var renderer = new ShapedTextRenderer(Fonts.DigitalStack);

        var runs = renderer.SplitByFont("KS சிவராம்குமார்").ToList();

        // Display face (face 0) shapes the Latin; the Tamil subset (face 2) shapes the name.
        Assert.Equal(["KS ", "சிவராம்குமார்"], runs.Select(r => r.Run));
        Assert.Equal([0, 2], runs.Select(r => r.Face));
    }

    [Theory]
    [InlineData("100")]
    [InlineData("A100")]
    [InlineData("RKL100")]
    [InlineData("ABC123")]
    [InlineData("ABC12345")]
    [InlineData("00:17.88")]
    public void AlphanumericCenterContentStaysEntirelyInDisplayFace(string text)
    {
        using var renderer = new ShapedTextRenderer(Fonts.DigitalStack);

        var runs = renderer.SplitByFont(text).ToList();

        // One run, face 0: no letter or digit ever falls through to a fallback.
        var single = Assert.Single(runs);
        Assert.Equal(text, single.Run);
        Assert.Equal(0, single.Face);
    }

    [Fact]
    public void MixedNameKeepsTheInitialAndCommaInDisplayFace()
    {
        using var renderer = new ShapedTextRenderer(Fonts.DigitalStack);

        var runs = renderer.SplitByFont("R சிவராம்குமார், காங்கேயம்").ToList();

        Assert.Equal(["R ", "சிவராம்குமார்", ", ", "காங்கேயம்"], runs.Select(r => r.Run));
        Assert.Equal([0, 2, 0, 2], runs.Select(r => r.Face));
    }

    [Fact]
    public void MissingDesignFontsFallBackToTheResolvedFontWithAWarning()
    {
        var builder = new FfmpegFilterBuilder(new AppSettings(), new StubFontResolver(StubFontResolver.TestTamilFont), () => null);

        var result = builder.Build(Hd, Overlay(), _root);

        Assert.Contains("missing", result.Warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("KS சிவராம்குமார், கெட்டிமல்லன்புதூர்", result.RenderedText!["primary"]);
    }

    [Fact]
    public void BuildFailsClearlyWhenNoFontAtAllIsAvailable()
    {
        var builder = new FfmpegFilterBuilder(new AppSettings(), new StubFontResolver(null), () => null);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build(Hd, Overlay(), _root));
        Assert.Contains("font", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SKRect Whole(SKBitmap bitmap) => SKRect.Create(0, 0, bitmap.Width, bitmap.Height);

    private static (int Left, int Right, int Top, int Bottom) GreenBox(SKBitmap bitmap, SKRect area)
    {
        int left = int.MaxValue, right = int.MinValue, top = int.MaxValue, bottom = int.MinValue;
        for (var y = (int)area.Top; y < (int)area.Bottom; y++)
        for (var x = (int)area.Left; x < (int)area.Right; x++)
        {
            var p = bitmap.GetPixel(x, y);
            if (Math.Abs(p.Red - 0x39) <= 40 && Math.Abs(p.Green - 0xff) <= 40 && Math.Abs(p.Blue - 0x14) <= 40)
            {
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        return (left, right, top, bottom);
    }

    private static (int Left, int Right) GreenSpan(SKBitmap bitmap, SKRect area)
    {
        int left = int.MaxValue, right = int.MinValue;
        for (var y = (int)area.Top; y < (int)area.Bottom; y++)
        for (var x = (int)area.Left; x < (int)area.Right; x++)
        {
            var p = bitmap.GetPixel(x, y);
            if (Math.Abs(p.Red - 0x39) <= 40 && Math.Abs(p.Green - 0xff) <= 40 && Math.Abs(p.Blue - 0x14) <= 40)
            {
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        }
        return (left, right);
    }

    private static bool Contains(SKBitmap bitmap, SKRect area, byte r, byte g, byte b)
    {
        for (var y = (int)area.Top; y < (int)area.Bottom; y++)
        for (var x = (int)area.Left; x < (int)area.Right; x++)
        {
            var p = bitmap.GetPixel(x, y);
            if (Math.Abs(p.Red - r) <= 6 && Math.Abs(p.Green - g) <= 6 && Math.Abs(p.Blue - b) <= 6)
                return true;
        }
        return false;
    }
}

public sealed class ShapedTextTests
{
    private static ScoreboardFonts Fonts => ScoreboardFonts.Bundled()!;

    [Fact]
    public void EachClusterUsesTheFirstFontThatHasIt()
    {
        using var renderer = new ShapedTextRenderer(Fonts.PlayerStack);

        var runs = renderer.SplitByFont("KS சிவராம்குமார், மதுரை").ToList();

        // Latin subset for "KS ", Tamil subset for the name, Latin for ", ", Tamil again.
        Assert.Equal(["KS ", "சிவராம்குமார்", ", ", "மதுரை"], runs.Select(r => r.Run));
        Assert.Equal([1, 0, 1, 0], runs.Select(r => r.Face));
    }

    [Fact]
    public void TamilVowelSignsFormLigatures()
    {
        using var renderer = new ShapedTextRenderer(Fonts.PlayerStack);

        // "கு" is one ligature glyph in Noto Sans Tamil; unshaped it would be two.
        var shaped = renderer.Shape("KS கு", 20);
        Assert.Equal(4, shaped.Runs.Sum(r => r.Glyphs.Length)); // K, S, space, கு
    }

    /// <summary>
    /// Widths measured in Chrome rendering the HTML at 1920×1080 with these texts
    /// (Range.getBoundingClientRect): shaping and fonts must agree to within a pixel or two.
    /// </summary>
    [Theory]
    [InlineData("KS சிவராம்குமார், கெட்டிமல்லன்புதூர்", 20, 0, 400.48)]
    [InlineData("M. முத்துக்குமார், அரிமளம்", 20, 0, 283.86)]
    public void TextWidthsMatchTheBrowser(string text, float size, float spacing, double chromeWidth)
    {
        using var renderer = new ShapedTextRenderer(Fonts.PlayerStack);
        var line = renderer.Shape(text, size, spacing);
        Assert.InRange(line.Width, chromeWidth - 2, chromeWidth + 2);
        // Chrome's content box for these lines is 25 px tall: ascent + descent.
        Assert.InRange(line.Ascent + line.Descent, 24, 26);
    }

    [Fact]
    public void CardNumberWidthMatchesTheDisplayFace()
    {
        // Barlow Condensed Bold is narrow: "100" at 12 px with 0.5 px spacing
        // shapes to ~15.8 px (Skia advances agree), well under Orbitron's 26.21.
        using var renderer = new ShapedTextRenderer(Fonts.NumberStack);
        var line = renderer.Shape("100", 12, 0.5f);
        Assert.InRange(line.Width, 15.78 - 1, 15.78 + 1);
        Assert.InRange(line.Ascent + line.Descent, 14, 16);
    }

    [Fact]
    public void LetterSpacingIsAddedAfterEveryCharacter()
    {
        using var renderer = new ShapedTextRenderer(Fonts.NumberStack);
        var plain = renderer.Shape("100", 34);
        var spaced = renderer.Shape("100", 34, letterSpacing: 0.5f);
        Assert.Equal(plain.Width + 1.5f, spaced.Width, 3);
    }
}

/// <summary>
/// End-to-end with a real FFmpeg. Opt-in: RVP_SCORECARD_FFMPEG, RVP_SCORECARD_FFPROBE and
/// RVP_SCORECARD_OUT (where the source, output and frames are written).
/// </summary>
public sealed class ScorecardRealRenderTests
{
    [Fact]
    public async Task RendersTheScorecardIntoTheVideoAtAboutTheSourceSize()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("RVP_SCORECARD_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("RVP_SCORECARD_FFPROBE");
        var outDir = Environment.GetEnvironmentVariable("RVP_SCORECARD_OUT");
        if (ffmpeg is null || ffprobe is null || outDir is null)
            return;

        Directory.CreateDirectory(outDir);
        var source = Path.Combine(outDir, "Race_100.mp4");
        await Run(ffmpeg, $"-v error -y -f lavfi -i testsrc2=size=1920x1080:rate=30,noise=alls=8:allf=t -f lavfi -i sine=frequency=440:sample_rate=48000 -t 12 -c:v libx264 -preset veryfast -b:v 6M -maxrate 7M -bufsize 12M -pix_fmt yuv420p -c:a aac -b:a 128k \"{source}\"");

        var settings = new AppSettings
        {
            FfmpegPath = ffmpeg,
            FfprobePath = ffprobe,
            EncoderPreference = EncoderPreference.CpuX264,
            OutputVideoFolder = Path.Combine(outDir, "Processed")
        };
        var probe = new FfprobeService(settings);
        var log = new TestLog();
        var service = new VideoProcessingService(settings, probe, new NoNvenc(), new OutputValidator(probe),
            new FfmpegFilterBuilder(settings, new StubFontResolver(StubFontResolver.TestTamilFont)), log);
        var overlay = new OverlayData("KS சிவராம்குமார்", "கெட்டிமல்லன்புதூர்", "100", "M. முத்துக்குமார்", "அரிமளம்", "00:17.88");

        var output = Path.Combine(settings.OutputVideoFolder, "Race_100.mp4");
        var result = await service.ProcessAsync(new ProcessingRequest("m", "100", source, output, overlay, true), null, CancellationToken.None);
        Assert.True(result.Success, result.Error + "\n" + string.Join("\n", log.Lines));

        await Run(ffmpeg, $"-v error -y -ss 5 -i \"{output}\" -frames:v 1 -update 1 \"{Path.Combine(outDir, "output-mid.png")}\"");
        await Run(ffmpeg, $"-v error -y -ss 10.5 -i \"{output}\" -frames:v 1 -update 1 \"{Path.Combine(outDir, "output-final.png")}\"");

        var sourceSize = new FileInfo(source).Length;
        var outputSize = new FileInfo(output).Length;
        File.WriteAllText(Path.Combine(outDir, "sizes.txt"), $"{sourceSize} {outputSize}\n" + string.Join("\n", log.Lines));
        Assert.Equal(result.InputMetadata!.Width, result.OutputMetadata!.Width);
        Assert.Equal(result.InputMetadata.Height, result.OutputMetadata.Height);
        Assert.InRange(outputSize, sourceSize * 0.85, sourceSize * 1.15);
    }

    private static async Task Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardError = true })!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
    }
}
