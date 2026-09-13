using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Video;

namespace RaceVideoProcessor.Tests;

public sealed class OutputValidatorTests
{
    [Fact]
    public async Task RejectsEmptyOutput()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new VideoMetadata("source.mp4", 1920, 1080, 25, 30, "h264", "aac", "yuv420p");
            var validator = new OutputValidator(new StubProbe(source));
            var result = await validator.ValidateAsync(path, source, CancellationToken.None);
            Assert.False(result.IsValid);
            Assert.Contains("empty", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RejectsChangedResolution()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            var source = new VideoMetadata("source.mp4", 1920, 1080, 25, 30, "h264", "aac", "yuv420p");
            var output = new VideoMetadata(path, 1280, 720, 25, 30, "h264", "aac", "yuv420p");
            var validator = new OutputValidator(new StubProbe(output));
            var result = await validator.ValidateAsync(path, source, CancellationToken.None);
            Assert.False(result.IsValid);
            Assert.Contains("resolution", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }
}
