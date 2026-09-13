using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Video;

namespace RaceVideoProcessor.Tests;

public sealed class ProcessingGuardTests
{
    [Fact]
    public async Task SecondProcessingRequestIsRejectedInsteadOfQueued()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.mp4");
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        var settings = new AppSettings { OutputVideoFolder = root };
        var probe = new BlockingProbe();
        var validator = new OutputValidator(probe);
        var service = new VideoProcessingService(settings, probe, new NoNvenc(), validator,
            new FfmpegFilterBuilder(settings, StubFontResolver.WithTempFont(root)), new TestLog());
        var request = new ProcessingRequest("marker-1", "1000AAA", input, Path.Combine(root, "output.mp4"),
            new OverlayData("S கருப்புசாமி", "கணியூர்", "1000AAA", "ரமேஷ்", "கோயம்புத்தூர்", "00:22.50"), false);
        using var firstCts = new CancellationTokenSource();

        var first = service.ProcessAsync(request, null, firstCts.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = await service.ProcessAsync(request with { EntryId = "marker-2", CardNumber = "1000AAB" }, null, CancellationToken.None);
        Assert.False(second.Success);
        Assert.Contains("already processing", second.Error!, StringComparison.OrdinalIgnoreCase);

        firstCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public async Task FfprobeFailureMarksProcessingAsFailureWithoutTouchingOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-probe-fail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.mp4");
        await File.WriteAllBytesAsync(input, [1, 2, 3, 4]);
        var output = Path.Combine(root, "Processed", "input.mp4");
        var settings = new AppSettings { OutputVideoFolder = Path.GetDirectoryName(output)! };
        var service = new VideoProcessingService(settings, new ThrowingProbe(), new NoNvenc(),
            new OutputValidator(new ThrowingProbe()), new FfmpegFilterBuilder(settings, StubFontResolver.WithTempFont(root)), new TestLog());
        var request = new ProcessingRequest("marker-1", "1000AAA", input, output,
            new OverlayData("S கருப்புசாமி", "கணியூர்", "1000AAA", "ரமேஷ்", "கோயம்புத்தூர்", "00:22.50"), false);

        var before = await File.ReadAllBytesAsync(input);
        var result = await service.ProcessAsync(request, null, CancellationToken.None);
        var after = await File.ReadAllBytesAsync(input);

        Assert.False(result.Success);
        Assert.Equal(before, after);
        Assert.False(File.Exists(output));
        try { Directory.Delete(root, true); } catch { }
    }

    private sealed class BlockingProbe : IFfprobeService
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<VideoMetadata> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ThrowingProbe : IFfprobeService
    {
        public Task<VideoMetadata> ProbeAsync(string path, CancellationToken cancellationToken)
            => throw new InvalidOperationException("probe failed");
    }
}
