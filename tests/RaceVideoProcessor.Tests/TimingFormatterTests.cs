using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Tests;

public sealed class TimingFormatterTests
{
    [Fact]
    public void ApiSecondsBecomeARaceTimingRatherThanABareNumber()
    {
        // The API sends 22.5; an operator and a broadcast overlay expect 00:22.50.
        Assert.Equal("00:22.50", TimingFormatter.Format(22.5));
        Assert.Equal("00:17.46", TimingFormatter.Format(17.46));
        Assert.Equal("01:02.25", TimingFormatter.Format(62.25));
    }

    [Fact]
    public void FormatIsConfigurable()
    {
        Assert.Equal("22.50", TimingFormatter.Format(22.5, "ss.ff"));
        Assert.Equal("00:00:22", TimingFormatter.Format(22.5, "hh:mm:ss"));
    }

    [Fact]
    public void AnInvalidPatternFallsBackInsteadOfBreakingARender()
    {
        Assert.Equal("00:22.50", TimingFormatter.Format(22.5, "not-a-pattern"));
    }

    [Fact]
    public void NonsenseInputIsClampedRatherThanThrowing()
    {
        Assert.Equal("00:00.00", TimingFormatter.Format(-5));
        Assert.Equal("00:00.00", TimingFormatter.Format(double.NaN));
    }
}
