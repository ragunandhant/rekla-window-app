using System.Net;
using System.Net.Sockets;
using System.Text;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;
using RaceVideoProcessor.Infrastructure.Backend;
using RaceVideoProcessor.Infrastructure.Video;

namespace RaceVideoProcessor.Tests;

public sealed class UploadFilenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvp-filename", Guid.NewGuid().ToString("N"));

    public UploadFilenameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>
    /// Sends the real multipart body through a real HttpClient to a local socket
    /// and returns the raw bytes that went over the wire — what the server parses.
    /// </summary>
    private async Task<string> CaptureUploadAsync(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("video-bytes"));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var received = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                received.Write(buffer, 0, n);
                var text = Encoding.UTF8.GetString(received.ToArray());
                if (text.Contains("video-bytes") && text.TrimEnd().EndsWith("--"))
                    break;
            }
            var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
            await stream.WriteAsync(reply);
            return Encoding.UTF8.GetString(received.ToArray());
        });

        using var http = new HttpClient();
        using var form = RaceBackendClient.CreateUploadForm(path, null);
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/v1/media/upload", form);
        var raw = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
        return raw;
    }

    [Theory]
    [InlineData("TEST_RACE_CART_100_ABC123.mp4")]
    [InlineData("Race_100.mp4")]
    [InlineData("Race 100 Final.mp4")]
    [InlineData("Race_100_Final_processed.MP4")]
    [InlineData("கார்ட் 100.mp4")]
    public async Task TheMultipartFilePartCarriesTheExactFileName(string fileName)
    {
        var raw = await CaptureUploadAsync(fileName);

        Assert.Contains("multipart/form-data; boundary=", raw);
        Assert.Contains($"Content-Disposition: form-data; name=\"upload\"; filename=\"{fileName}\"", raw);
        Assert.DoesNotContain("filename*", raw);
        Assert.Single(raw.Split("Content-Disposition:").Skip(1));
    }
}

public sealed class VideoEligibilityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyVideoLinkMeansNoVideoAndNoConfirmation(string? link)
    {
        Assert.False(VideoEligibility.HasVideo(link));
        Assert.False(VideoEligibility.RequiresReplaceConfirmation(link));
    }

    [Fact]
    public void AnExistingVideoLinkRequiresReplaceConfirmation()
        => Assert.True(VideoEligibility.RequiresReplaceConfirmation("https://media.namadhurekla.com/uploads/existing.mp4"));

    [Fact]
    public void AnApiPlayerWithANullVideoLinkParsesAsHavingNoVideo()
    {
        var entries = new RaceVideoProcessor.Infrastructure.Providers.RaceApiPayloadAdapter().Parse("""
            [{ "playerId": "p1", "marker": "m1", "status": "completed", "videoLink": null,
               "player": { "_id": "p1", "cartNo": "100", "ownerName": "KS சிவராம்குமார்", "location": "கெட்டிமல்லன்புதூர்" } },
             { "playerId": "p2", "marker": "m2", "status": "pending", "videoLink": "",
               "player": { "_id": "p2", "cartNo": "101", "ownerName": "B", "location": "L" } }]
            """);

        Assert.All(entries, e => Assert.False(VideoEligibility.HasVideo(e.VideoLink)));
    }

    [Fact]
    public void TheApiTypeComesFromTheCategoryAndIsNotASetting()
    {
        Assert.Equal("200", AppSettings.TypeFor(RaceCategory.Meter200));
        Assert.Equal("300", AppSettings.TypeFor(RaceCategory.Meter300));
        Assert.DoesNotContain(typeof(AppSettings).GetProperties(), p => p.Name.StartsWith("Type", StringComparison.Ordinal));
    }
}

public sealed class SizeMatchedEncodingTests
{
    private static VideoMetadata Source(long? videoBitRate = 8_007_389, long? audioBitRate = 127_993,
        long? formatBitRate = 8_143_355, long? size = 30_537_583) =>
        new("in.mp4", 1920, 1080, 30.0, 30, "h264", "aac", "yuv420p", videoBitRate, audioBitRate, formatBitRate, size);

    private static List<string> Args(AppSettings settings, VideoMetadata source, bool nvenc)
    {
        var service = new VideoProcessingService(settings, new StubProbe(source), new NoNvenc(),
            new OutputValidator(new StubProbe(source)), new FfmpegFilterBuilder(settings, new StubFontResolver(null)), new TestLog());
        var filter = new FilterBuildResult("[0:v]null[vout]", "[vout]", [], "", "", "", "stub", null);
        return service.BuildProcessArguments("in.mp4", "out.mp4", filter, nvenc, source);
    }

    private static string After(List<string> args, string flag) => args[args.IndexOf(flag) + 1];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheVideoIsEncodedAtTheSourceVideoBitrateNotAFixedQuality(bool nvenc)
    {
        var args = Args(new AppSettings(), Source(), nvenc);

        Assert.Equal("8007389", After(args, "-b:v"));
        Assert.Equal("12011083", After(args, "-maxrate"));
        Assert.Equal("16014778", After(args, "-bufsize"));
        Assert.DoesNotContain("-crf", args);
        Assert.DoesNotContain("-cq", args);
        Assert.Equal("copy", After(args, "-c:a"));
        Assert.DoesNotContain("-r", args);
        Assert.DoesNotContain("-s", args);
    }

    [Fact]
    public void AMissingStreamBitrateIsDerivedFromTheFileAndExcludesAudio()
    {
        var target = EncodingBudget.TargetVideoBitRate(Source(videoBitRate: null, formatBitRate: null));

        // 30,537,583 bytes × 8 / 30 s ≈ 8.143 Mbps total, less 1 % container, less audio.
        Assert.InRange(target!.Value, 7_800_000, 8_050_000);
    }

    [Fact]
    public void WithoutAnyBitrateInformationTheEncodeFallsBackToQuality()
    {
        var args = Args(new AppSettings(), Source(videoBitRate: null, formatBitRate: null, size: null), nvenc: false);
        Assert.Contains("-crf", args);
    }

    [Fact]
    public void SizeMatchingCanBeTurnedOff()
    {
        var args = Args(new AppSettings { MatchSourceFileSize = false }, Source(), nvenc: false);
        Assert.Equal("16", After(args, "-crf"));
        Assert.DoesNotContain("-b:v", args);
    }

    [Fact]
    public void AVeryLowSourceBitrateIsFloored()
        => Assert.Equal(500_000, EncodingBudget.TargetVideoBitRate(Source(videoBitRate: 100_000)));

    [Fact]
    public void SizeDifferenceIsSignedPercent()
    {
        Assert.Equal(33.3, EncodingBudget.SizeDifferencePercent(33_000_000, 44_000_000), 1);
        Assert.Equal(-3.0, EncodingBudget.SizeDifferencePercent(33_000_000, 32_010_000), 1);
    }
}
