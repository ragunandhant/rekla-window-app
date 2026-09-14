using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Tests;

public sealed class RaceValidatorTests
{
    private static readonly Race Existing = new() { RaceId = "6a8fa0dd70486e002831d000", RaceName = "Kumbakonam", RaceDate = new DateOnly(2026, 9, 13) };

    [Fact]
    public void AValidRaceIsTrimmedAndParsed()
    {
        var result = RaceValidator.Validate("  Kumbakonam   Rekla Race ", " 13-09-2026 ", "  AAA  ", []);

        Assert.True(result.IsValid);
        Assert.Equal("Kumbakonam Rekla Race", result.RaceName);
        Assert.Equal("AAA", result.RaceId);
        Assert.Equal(new DateOnly(2026, 9, 13), result.RaceDate);
    }

    [Theory]
    [InlineData("", "13-09-2026", "AAA", "Race Name is required")]
    [InlineData("Race", "", "AAA", "Race Date is required")]
    [InlineData("Race", "31-02-2026", "AAA", "not a valid date")]
    [InlineData("Race", "2026/13/40", "AAA", "not a valid date")]
    [InlineData("Race", "13-09-2026", "   ", "Race ID is required")]
    [InlineData("Race", "13-09-2026", "abc/def", "Race ID is invalid")]
    [InlineData("Race", "13-09-2026", "abc def", "Race ID is invalid")]
    public void InvalidInputIsRejectedWithAClearMessage(string name, string date, string id, string expected)
    {
        var result = RaceValidator.Validate(name, date, id, []);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ADuplicateRaceIdIsRejectedAndTheExistingRaceIsOffered()
    {
        var result = RaceValidator.Validate("Some other name", "20-09-2026", " 6A8FA0DD70486E002831D000 ", [Existing]);

        Assert.False(result.IsValid);
        Assert.Contains(RaceValidator.DuplicateMessage, result.Errors);
        Assert.Same(Existing, result.ExistingRaceWithSameId);
    }

    [Fact]
    public void ADuplicateRaceNameIsAllowed()
        => Assert.True(RaceValidator.Validate("Kumbakonam", "20-09-2026", "BBB", [Existing]).IsValid);
}

public sealed class BackendUrlTests
{
    [Fact]
    public void PlayerAndAssignUrlsTakeTheRaceIdFromTheScopeAndTheTypeFromTheCategory()
    {
        var settings = new AppSettings();
        var race = "6a8fa0dd70486e002831d000";

        Assert.Equal(
            "https://rekla-backend-fx7x9.ondigitalocean.app/v1/races/6a8fa0dd70486e002831d000/players/all?type=200",
            BackendUrls.Players(settings, new RaceScope(race, RaceCategory.Meter200)).ToString());
        Assert.Equal(
            "https://rekla-backend-fx7x9.ondigitalocean.app/v1/races/6a8fa0dd70486e002831d000/players/all?type=300",
            BackendUrls.Players(settings, new RaceScope(race, RaceCategory.Meter300)).ToString());
        Assert.Equal(
            "https://rekla-backend-fx7x9.ondigitalocean.app/v1/races/6a8fa0dd70486e002831d000/player/68b6bc77cfff6e0026a6f19c/video?type=300",
            BackendUrls.Assign(settings, new RaceScope(race, RaceCategory.Meter300), "68b6bc77cfff6e0026a6f19c").ToString());
    }

    [Fact]
    public void ValuesAreEscapedIntoTheUrl()
        => Assert.Equal("/v1/races/A%20B/players/all?type=200",
            BackendUrls.Players(new AppSettings(), new RaceScope("A B", RaceCategory.Meter200)).PathAndQuery);
}

public sealed class EntryOrderingTests
{
    private sealed record Item(string Card, DateTimeOffset? Date, bool Eligible = true);

    private static int Compare(Item a, Item b) => EntryOrdering.CompareNewestFirst(a.Date, a.Card, b.Date, b.Card);

    [Fact]
    public void NewestDateComesFirstByActualTimeNotByText()
    {
        var items = new List<Item>
        {
            new("A", DateTimeOffset.Parse("2026-09-12T18:20:00Z")),
            new("B", DateTimeOffset.Parse("2026-09-13T06:42:00Z")),
            new("C", null),
            new("D", DateTimeOffset.Parse("2026-09-13T05:30:00+05:30")), // 00:00Z — earlier than B despite "05:30"
            new("E", DateTimeOffset.Parse("2026-09-13T05:30:00Z"))
        };

        items.Sort(Compare);

        Assert.Equal(["B", "E", "D", "A", "C"], items.Select(i => i.Card));
    }

    [Fact]
    public void NextIsTheFollowingEligibleEntryAndWrapsAround()
    {
        var a = new Item("100", null);
        var b = new Item("101", null, Eligible: false);
        var c = new Item("102", null);
        var list = new List<Item> { a, b, c };

        Assert.Same(c, EntryOrdering.FindNext(list, a, i => i.Eligible));
        Assert.Same(a, EntryOrdering.FindNext(list, c, i => i.Eligible));
        Assert.Same(a, EntryOrdering.FindNext(list, null, i => i.Eligible));
    }

    [Fact]
    public void ThereIsNoNextWhenNothingElseIsEligible()
    {
        var a = new Item("100", null);
        var list = new List<Item> { a, new("101", null, Eligible: false) };

        Assert.Null(EntryOrdering.FindNext(list, a, i => i.Eligible));
    }
}

public sealed class OverallStatusTests
{
    [Fact]
    public void OverallStatusFollowsTheStages()
    {
        var s = TestScopes.State(TestScopes.RaceA200, "e");
        Assert.Equal(OverallStatus.Ready, s.Overall);

        s.ProcessingStatus = LocalProcessingStatus.Processing;
        Assert.Equal(OverallStatus.Processing, s.Overall);

        s.ProcessingStatus = LocalProcessingStatus.Completed;
        Assert.Equal(OverallStatus.ProcessingCompleted, s.Overall);

        // Upload OFF is a normal outcome, not a failure.
        s.UploadStatus = UploadStatus.Disabled;
        Assert.Equal(OverallStatus.UploadDisabled, s.Overall);

        s.UploadStatus = UploadStatus.Uploading;
        Assert.Equal(OverallStatus.Uploading, s.Overall);

        s.UploadStatus = UploadStatus.Failed;
        Assert.Equal(OverallStatus.UploadFailed, s.Overall);

        s.LastFailureWasAuthentication = true;
        Assert.Equal(OverallStatus.AuthenticationFailed, s.Overall);

        s.LastFailureWasAuthentication = false;
        s.UploadStatus = UploadStatus.Completed;
        s.AssignmentStatus = AssignmentStatus.Assigning;
        Assert.Equal(OverallStatus.Assigning, s.Overall);

        s.AssignmentStatus = AssignmentStatus.Failed;
        Assert.Equal(OverallStatus.AssignmentFailed, s.Overall);

        s.AssignmentStatus = AssignmentStatus.Completed;
        Assert.Equal(OverallStatus.Completed, s.Overall);

        s.ProcessingStatus = LocalProcessingStatus.Cancelled;
        Assert.Equal(OverallStatus.Cancelled, s.Overall);
    }
}
