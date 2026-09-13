using System.Diagnostics;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Video;

namespace RaceVideoProcessor.Tests;

public sealed class ElegantPrestigeScoreboardTests
{
    private static readonly VideoMetadata Hd = new("x.mp4", 1920, 1080, 25, 30, "h264", "aac", "yuv420p");

    private static string Root() => Path.Combine(Path.GetTempPath(), "rvp-prestige", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PlatesAreValidTransparentPngsOfTheRequestedSize()
    {
        var style = new PlateStyle("#D9F24F", "#F5F5ED", 1.0, 1.0, StubFontResolver.TestTamilFont, StubFontResolver.TestTamilFont);
        var png = ScoreboardPlateRenderer.RenderScoreboard(1828, 162,
            new ScoreboardContent("KS சிவராம்குமார்", "கெட்டிமல்லன்புதூர்", "100", null, null), style).Png;

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        Assert.Equal(1828, width);
        Assert.Equal(162, height);
        Assert.Equal(6, png[25]); // RGBA
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void ThePanelIsWideShortAndAtTheBottom(int width, int height)
    {
        var root = Root();
        try
        {
            var builder = new FfmpegFilterBuilder(new AppSettings(), StubFontResolver.WithTempFont(root));
            var result = builder.Build(Hd with { Width = width, Height = height }, TestOverlay(), root);

            var overlay = System.Text.RegularExpressions.Regex.Match(result.Filter, @"\[0:v\]\[1:v\]overlay=x=(\d+):y=(\d+)");
            var x = int.Parse(overlay.Groups[1].Value);
            var y = int.Parse(overlay.Groups[2].Value);
            var png = File.ReadAllBytes(result.Inputs[0].Path);
            var panelWidth = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            var panelHeight = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

            Assert.InRange(x, width * 0.02, width * 0.03);
            Assert.Equal(width - 2 * x, panelWidth);
            Assert.InRange(panelHeight, 1, (int)(height * 0.15) + 1);
            Assert.True(panelWidth / (double)panelHeight > 6.5);
            Assert.InRange(y + panelHeight, height * 0.9, height * 0.97);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void LeftPrimaryCentreCardRightSecondaryWithTextFromFiles()
    {
        var root = Root();
        try
        {
            var builder = new FfmpegFilterBuilder(new AppSettings(), StubFontResolver.WithTempFont(root));
            var result = builder.Build(Hd, TestOverlay(), root);

            var text = result.RenderedText!;
            Assert.Equal("KS சிவராம்குமார்", text["primary-name"]);
            Assert.Equal("கெட்டிமல்லன்புதூர்", text["primary-location"]);
            Assert.Equal("100", text["card"]);
            Assert.Equal("ரமேஷ்", text["secondary-name"]);
            Assert.Equal("கோயம்புத்தூர்", text["secondary-location"]);
            Assert.DoesNotContain("drawtext", result.Filter);
            Assert.Equal("[vout]", result.OutputLabel);
            Assert.Equal(2, result.Inputs.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void WithoutASecondaryPlayerOnlyADashIsShown()
    {
        var root = Root();
        try
        {
            var builder = new FfmpegFilterBuilder(new AppSettings(), StubFontResolver.WithTempFont(root));
            var result = builder.Build(Hd, TestOverlay() with { SecondaryName = null, SecondaryLocation = null }, root);

            Assert.Equal("—", result.RenderedText!["secondary-name"]);
            Assert.False(result.RenderedText.ContainsKey("secondary-location"));
            Assert.Equal("—", result.SecondaryDisplay);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TheTimingPlaqueAppearsOnlyInTheFinalSecondsAndFadesIn()
    {
        var root = Root();
        try
        {
            var builder = new FfmpegFilterBuilder(new AppSettings(), StubFontResolver.WithTempFont(root));
            var result = builder.Build(Hd, TestOverlay(), root);

            // 25 s clip, 4 s window.
            Assert.Contains("fade=t=in:st=21:d=0.3:alpha=1", result.Filter);
            Assert.Contains("enable='gte(t,21)'", result.Filter);
            Assert.Contains("-loop", result.Inputs[1].InputOptions);
            Assert.Equal("00:17.88", result.RenderedText!["timing"]);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static OverlayData TestOverlay()
        => new("KS சிவராம்குமார்", "கெட்டிமல்லன்புதூர்", "100", "ரமேஷ்", "கோயம்புத்தூர்", "00:17.88");
}

/// <summary>
/// End-to-end with a real FFmpeg that has drawtext. Opt-in, because it needs the
/// tools and fonts: RVP_PRESTIGE_FFMPEG, RVP_PRESTIGE_FFPROBE, RVP_PRESTIGE_TAMIL_FONT,
/// RVP_PRESTIGE_NUMERIC_FONT, and RVP_PRESTIGE_OUT for the rendered frames.
/// </summary>
public sealed class ElegantPrestigeRealRenderTests
{
    [Fact]
    public async Task RendersTheScoreboardIntoTheVideoAtAboutTheSourceSize()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("RVP_PRESTIGE_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("RVP_PRESTIGE_FFPROBE");
        var tamil = Environment.GetEnvironmentVariable("RVP_PRESTIGE_TAMIL_FONT");
        var numeric = Environment.GetEnvironmentVariable("RVP_PRESTIGE_NUMERIC_FONT");
        var outDir = Environment.GetEnvironmentVariable("RVP_PRESTIGE_OUT");
        if (ffmpeg is null || ffprobe is null || tamil is null || outDir is null)
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
            new FfmpegFilterBuilder(settings, new StubFontResolver(tamil, numericPath: numeric)), log);
        var overlay = new OverlayData("KS சிவராம்குமார்", "கெட்டிமல்லன்புதூர்", "100", "முத்துக்குமார்", "கோயம்புத்தூர்", "00:17.88");

        var preview = await service.GeneratePreviewAsync(source, overlay, CancellationToken.None);
        File.Copy(preview.NormalPreviewPath, Path.Combine(outDir, "preview-normal.png"), true);
        File.Copy(preview.FinalPreviewPath, Path.Combine(outDir, "preview-final.png"), true);

        var output = Path.Combine(settings.OutputVideoFolder, "Race_100.mp4");
        var result = await service.ProcessAsync(new ProcessingRequest("m", "100", source, output, overlay, true), null, CancellationToken.None);
        Assert.True(result.Success, result.Error + "\n" + string.Join("\n", log.Lines));

        await service.ExtractFrameAsync(output, 5, Path.Combine(outDir, "output-mid.png"), CancellationToken.None);
        await service.ExtractFrameAsync(output, 10.5, Path.Combine(outDir, "output-final.png"), CancellationToken.None);

        var sourceSize = new FileInfo(source).Length;
        var outputSize = new FileInfo(output).Length;
        File.WriteAllText(Path.Combine(outDir, "sizes.txt"), $"{sourceSize} {outputSize}\n" + string.Join("\n", log.Lines));
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

public sealed class ShapedTextTests
{
    [Fact]
    public void MixedScriptNamesAreSplitSoTamilIsShapedAsTamil()
    {
        var runs = ShapedTextRenderer.SplitByScript("KS சிவராம்குமார்").ToList();

        Assert.Equal(2, runs.Count);
        Assert.Equal("KS ", runs[0].Run);
        Assert.Equal(HarfBuzzSharp.Script.Latin, runs[0].Script);
        Assert.Equal("சிவராம்குமார்", runs[1].Run);
        Assert.Equal(HarfBuzzSharp.Script.Tamil, runs[1].Script);
    }

    [Fact]
    public void TamilVowelSignsFormLigaturesWhenShaped()
    {
        using var renderer = new ShapedTextRenderer(StubFontResolver.TestTamilFont);

        // "கு" is one ligature glyph in Noto Sans Tamil; shaped as Latin it would be two.
        var shaped = renderer.Shape("KS கு", 40, bold: false);
        Assert.Equal(4, shaped.Runs.Sum(r => r.Glyphs.Length)); // K, S, space, கு

        var fitted = renderer.Fit("KS சிவராம்குமார்", maxWidth: 2000, preferred: 40, minimum: 20, bold: true);
        Assert.False(fitted.Truncated);
        Assert.Equal(40, fitted.Size);
    }

    [Fact]
    public void OverlongTextShrinksThenEllipsizesOnClusterBoundaries()
    {
        using var renderer = new ShapedTextRenderer(StubFontResolver.TestTamilFont);

        var fitted = renderer.Fit("வெங்கடேஷ் ராமகுமார் கோயம்புத்தூர்", maxWidth: 120, preferred: 40, minimum: 24, bold: true);

        Assert.True(fitted.Truncated);
        Assert.EndsWith("…", fitted.Text);
        Assert.Equal(24, fitted.Size);
        Assert.True(fitted.Width <= 120);
        var kept = fitted.Text[..^1];
        Assert.StartsWith(kept, "வெங்கடேஷ் ராமகுமார் கோயம்புத்தூர்", StringComparison.Ordinal);
    }
}
