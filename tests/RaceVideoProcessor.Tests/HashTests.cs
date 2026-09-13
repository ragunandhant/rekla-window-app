using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Tests;

public sealed class HashTests
{
    [Fact]
    public void ExtractionStatusDoesNotInvalidateVideoButOverlayDataDoes()
    {
        var baseEntry = TestEntries.Create(status: RemoteExtractionStatus.NotCompleted);
        var extractionChanged = baseEntry with { ExtractionStatus = RemoteExtractionStatus.Completed };
        var cardChanged = baseEntry with { CardNumber = "1000AAB" };
        var timingChanged = baseEntry with { TimingSeconds = 23.1 };

        Assert.Equal(OverlayHashCalculator.Calculate(baseEntry), OverlayHashCalculator.Calculate(extractionChanged));
        Assert.NotEqual(OverlayHashCalculator.Calculate(baseEntry), OverlayHashCalculator.Calculate(cardChanged));
        Assert.NotEqual(OverlayHashCalculator.Calculate(baseEntry), OverlayHashCalculator.Calculate(timingChanged));
    }

    [Fact]
    public void SocialCountsAndStatisticsDoNotInvalidateAnExistingOutput()
    {
        // Fields that are not burned into the video must never mark an output OUTDATED.
        var baseEntry = TestEntries.Create();
        var unrelatedChange = baseEntry with { VideoLink = "https://example.invalid/other.mp4", UserId = "REK9999" };

        Assert.Equal(OverlayHashCalculator.Calculate(baseEntry), OverlayHashCalculator.Calculate(unrelatedChange));
    }
}
