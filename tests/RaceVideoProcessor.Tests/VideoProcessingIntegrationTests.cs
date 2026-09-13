using System.Diagnostics;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Video;

namespace RaceVideoProcessor.Tests;

public sealed class VideoProcessingIntegrationTests
{
    [Fact]
    public async Task MissingInputReturnsFailureWithoutCreatingOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettings
            {
                EncoderPreference = EncoderPreference.CpuX264,
                OutputVideoFolder = root,
                FontFilePath = "missing.ttf"
            };
            var probe = new FfprobeService(settings);
            var validator = new OutputValidator(probe);
            var service = new VideoProcessingService(settings, probe, new NoNvenc(), validator,
                new FfmpegFilterBuilder(settings, StubFontResolver.WithTempFont(root)), new TestLog());
            var output = Path.Combine(root, "missing.mp4");
            var request = new ProcessingRequest("marker-1", "1000AAA", Path.Combine(root, "nope.mp4"), output,
                new OverlayData("S கருப்புசாமி", "கணியூர்", "1000AAA", null, null, "00:22.50"), false);

            var result = await service.ProcessAsync(request, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.False(File.Exists(output));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task RealFfmpegProcessingAndNvencFallback_WorkWhenIntegrationFlagIsEnabled()
    {
        // Opt-in because CI/developer machines may not have a full FFmpeg build.
        if (!string.Equals(Environment.GetEnvironmentVariable("RVP_RUN_FFMPEG_TESTS"), "1", StringComparison.Ordinal))
            return;

        var root = Path.Combine(Path.GetTempPath(), "rvp-ffmpeg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "sample.mp4");
        var outputDir = Path.Combine(root, "Processed");
        Directory.CreateDirectory(outputDir);
        var output = Path.Combine(outputDir, "sample.mp4");

        try
        {
            await RunAsync("ffmpeg", [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
                "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input
            ]);

            var settings = new AppSettings
            {
                FfmpegPath = "ffmpeg",
                FfprobePath = "ffprobe",
                EncoderPreference = EncoderPreference.NvidiaNvenc, // Stub says unavailable -> CPU fallback.
                EncodingQuality = EncodingQuality.High,
                OutputVideoFolder = outputDir,
                FontFilePath = "missing.ttf",
                CompletionTimeDisplaySeconds = 1
            };
            var probe = new FfprobeService(settings);
            var validator = new OutputValidator(probe);
            var service = new VideoProcessingService(settings, probe, new NoNvenc(), validator,
                new FfmpegFilterBuilder(settings, StubFontResolver.WithTempFont(root)), new TestLog());
            var request = new ProcessingRequest("marker-1", "1000AAA", input, output,
                new OverlayData("S கருப்புசாமி", "கணியூர்", "1000AAA", "ரமேஷ்", "கோயம்புத்தூர்", "00:22.50"), false);

            var progressValues = new List<double>();
            var result = await service.ProcessAsync(request,
                new Progress<RaceVideoProcessor.Core.Models.ProcessingProgress>(p => progressValues.Add(p.Percent)),
                CancellationToken.None);

            Assert.True(result.Success, result.Error);
            Assert.False(result.UsedNvenc);
            Assert.True(File.Exists(output));
            Assert.Equal("sample.mp4", Path.GetFileName(output));
            Assert.True(new FileInfo(output).Length > 0);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task RunAsync(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
    }
}
