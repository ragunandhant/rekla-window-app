using System.Diagnostics;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Video;
using SkiaSharp;

namespace RaceVideoProcessor.Tests;

/// <summary>The scorecard from scorecard_center_number_matched_to_player_text.html, rendered into plates.</summary>
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

        using var orbitron = SKTypeface.FromFile(fonts!.Orbitron);
        Assert.Equal("Orbitron", orbitron.FamilyName);
        Assert.Equal(700, orbitron.FontWeight);

        using var tamil = SKTypeface.FromFile(fonts.TamilText);
        Assert.Equal("Noto Sans Tamil", tamil.FamilyName);
        Assert.Equal(500, tamil.FontWeight);
        Assert.NotEqual(0, tamil.GetGlyph(0x0B9A));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void BoxesFollowTheHtmlProportionsAtEveryResolution(int width, int height)
    {
        var result = Builder().Build(Hd with { Width = width, Height = height }, Overlay(), _root);

        var match = System.Text.RegularExpressions.Regex.Match(result.Filter, @"\[0:v\]\[1:v\]overlay=x=0:y=(\d+)");
        Assert.True(match.Success, result.Filter);
        var plateY = int.Parse(match.Groups[1].Value);

        using var plate = SKBitmap.Decode(result.Inputs[0].Path);
        Assert.Equal(width, plate.Width);

        var s = width / 1920.0;
        // .scorecard { bottom: 4% }: the 78 px boxes end 4 % above the bottom edge.
        var boxTop = plateY + 24 * s;
        Assert.InRange(boxTop + 78 * s, height * 0.96 - 2, height * 0.96 + 2);

        // width 95 %: the left box starts at 2.5 %; the card box is square and centred.
        var midRow = (int)Math.Round(24 * s + 39 * s);
        Assert.Equal(255, plate.GetPixel((int)(width * 0.025 + 6 * s), midRow).Alpha);
        Assert.Equal(0, plate.GetPixel((int)(width * 0.025 - 30 * s), midRow).Alpha);
        Assert.Equal(255, plate.GetPixel(width / 2, midRow).Alpha);
        // The 18 px gaps between the boxes are open.
        var gapX = (int)Math.Round((960 - 39 - 9) * s);
        Assert.True(plate.GetPixel(gapX, midRow).Alpha < 200);
    }

    [Fact]
    public void SurfaceColoursAndTextColoursAreTheHtmlColours()
    {
        var result = Builder().Build(Hd, Overlay(), _root);
        using var plate = SKBitmap.Decode(result.Inputs[0].Path);

        // A background pixel of the left player box, well away from text: the
        // 135deg gradient runs #0b150d → #040805, so every channel sits in between.
        var bg = plate.GetPixel(60, 24 + 70);
        Assert.InRange(bg.Red, 0x04, 0x0b);
        Assert.InRange(bg.Green, 0x08, 0x15);
        Assert.InRange(bg.Blue, 0x05, 0x0d);

        // The border is #14421b.
        var border = plate.GetPixel(400, 24);
        Assert.InRange(border.Green, 0x30, 0x48);

        // Text: #a3e635 in the player boxes, #39ff14 in the card box.
        Assert.True(Contains(plate, SKRect.Create(48, 24, 855, 78), 0xa3, 0xe6, 0x35), "player text colour");
        Assert.True(Contains(plate, SKRect.Create(921, 24, 78, 78), 0x39, 0xff, 0x14), "card number colour");
    }

    [Fact]
    public void PrimaryLeftCartCentreSecondaryRightAsNameCommaLocation()
    {
        var result = Builder().Build(Hd, Overlay(), _root);

        Assert.Equal("KS சிவராம்குமார், கெட்டிமல்லன்புதூர்", result.RenderedText!["primary"]);
        Assert.Equal("100", result.RenderedText["card"]);
        Assert.Equal("முத்துக்குமார், அரிமளம்", result.RenderedText["secondary"]);
        Assert.Equal("00:17.88", result.RenderedText["timing"]);
        Assert.Null(result.Warning);
        Assert.DoesNotContain("drawtext", result.Filter);
    }

    [Fact]
    public void WithoutASecondaryPlayerTheRightBoxShowsADash()
    {
        var result = Builder().Build(Hd, Overlay(secondary: null), _root);
        Assert.Equal("—", result.RenderedText!["secondary"]);
        Assert.Equal("—", result.SecondaryDisplay);
    }

    [Fact]
    public void TheTimerUsesTheCardBoxDesignAndShowsOnlyInTheFinalSeconds()
    {
        var result = Builder().Build(Hd, Overlay(), _root);

        // 25 s clip, 4 s window.
        Assert.Contains("fade=t=in:st=21:d=0.3:alpha=1", result.Filter);
        Assert.Contains("enable='gte(t,21)'", result.Filter);
        Assert.Contains("-loop", result.Inputs[1].InputOptions);

        using var timer = SKBitmap.Decode(result.Inputs[1].Path);
        Assert.Equal(78 + 48, timer.Height);
        Assert.True(Contains(timer, SKRect.Create(24, 24, timer.Width - 48, 78), 0x39, 0xff, 0x14), "timer text colour");
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
    public void TheCardNumberEndsAtTwelvePixelsAsInTheBrowser()
    {
        var result = Builder().Build(Hd, Overlay(), _root);
        using var plate = SKBitmap.Decode(result.Inputs[0].Path);

        // Chrome draws "100" at 12 px: glyphs about 26 px wide, centred in the 78 px box at x 921.
        var (left, right) = InkSpan(plate, SKRect.Create(921 + 2, 24 + 30, 74, 18), 0x39, 0xff, 0x14);
        Assert.InRange(right - left, 20, 30);
        Assert.InRange((left + right) / 2.0, 958, 962);
    }

    [Fact]
    public void MissingDesignFontsFallBackToTheResolvedFontWithAWarning()
    {
        var builder = new FfmpegFilterBuilder(new AppSettings(), new StubFontResolver(StubFontResolver.TestTamilFont), () => null);

        var result = builder.Build(Hd, Overlay(), _root);

        Assert.Contains("missing", result.Warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("100", result.RenderedText!["card"]);
    }

    [Fact]
    public void BuildFailsClearlyWhenNoFontAtAllIsAvailable()
    {
        var builder = new FfmpegFilterBuilder(new AppSettings(), new StubFontResolver(null), () => null);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build(Hd, Overlay(), _root));
        Assert.Contains("font", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Left, int Right) InkSpan(SKBitmap bitmap, SKRect area, byte r, byte g, byte b)
    {
        int left = int.MaxValue, right = int.MinValue;
        for (var y = (int)area.Top; y < (int)area.Bottom; y++)
        for (var x = (int)area.Left; x < (int)area.Right; x++)
        {
            var p = bitmap.GetPixel(x, y);
            if (Math.Abs(p.Red - r) <= 40 && Math.Abs(p.Green - g) <= 40 && Math.Abs(p.Blue - b) <= 40)
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
    public void CardNumberWidthMatchesTheBrowserAtTwelvePixels()
    {
        using var renderer = new ShapedTextRenderer(Fonts.NumberStack);
        var line = renderer.Shape("100", 12, 0.5f);
        Assert.InRange(line.Width, 26.21 - 1, 26.21 + 1);
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
