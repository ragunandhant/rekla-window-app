using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Providers;

namespace RaceVideoProcessor.Tests;

public sealed class RaceApiPayloadAdapterTests
{
    /// <summary>The payload shape supplied for the real API, trimmed to the relevant fields.</summary>
    private const string SamplePayload = """
        [{
            "playerId": "68cb9489b30d3e0027f6175b",
            "player": {
                "_id": "68cb9489b30d3e0027f6175b",
                "userId": "REK0076",
                "ownerName": "S கருப்புசாமி",
                "cartNo": "1000AAA",
                "location": "கணியூர்",
                "raceId": [
                    { "types": ["200", "300"], "raceId": "68c595642215be00255b1673" },
                    { "types": ["300"], "raceId": "6902e343cd7c860025d7f534" }
                ],
                "statistics": { "totalRacesAttended": 18 }
            },
            "secondaryPlayer": null,
            "status": "completed",
            "timings": 22.5,
            "videoLink": "https://blr1.example.invalid/namadhu-rekla/2026-08-16 06-32-38.mp4",
            "marker": "6a810c302f213f0028909b7f",
            "date": "2026-08-16T01:02:52.763Z",
            "time": "2026-08-16T01:02:52.763Z",
            "isVideoEnabled": true,
            "likeCount": 7,
            "commentCount": 3
        }]
        """;

    [Fact]
    public void MapsThePrimaryPlayerWithTamilTextIntact()
    {
        var entries = new RaceApiPayloadAdapter().Parse(SamplePayload);
        var entry = Assert.Single(entries);

        Assert.Equal("1000AAA", entry.CardNumber);
        Assert.Equal("S கருப்புசாமி", entry.PrimaryName);
        Assert.Equal("கணியூர்", entry.PrimaryLocation);
        Assert.Equal("S கருப்புசாமி, கணியூர்", entry.PrimaryDisplay);
    }

    [Fact]
    public void UsesTheMarkerAsIdentityAndNeverInventsAnEntryNumber()
    {
        var entry = new RaceApiPayloadAdapter().Parse(SamplePayload).Single();

        // The marker distinguishes one race from another for the same player.
        Assert.Equal("6a810c302f213f0028909b7f", entry.EntryId);
        Assert.Equal("68cb9489b30d3e0027f6175b", entry.PlayerId);
        Assert.Equal("REK0076", entry.UserId);
    }

    [Fact]
    public void ANullSecondaryPlayerIsNotAnError()
    {
        var entry = new RaceApiPayloadAdapter().Parse(SamplePayload).Single();

        Assert.False(entry.HasSecondary);
        Assert.Null(entry.SecondaryName);
        Assert.Equal("—", entry.SecondaryDisplay);
    }

    [Fact]
    public void ASecondaryPlayerIsMappedWithoutACardNumber()
    {
        const string payload = """
            [{
                "player": { "cartNo": "1000AAB", "ownerName": "குமார்", "location": "மதுரை" },
                "secondaryPlayer": { "cartNo": "9999ZZZ", "ownerName": "ரமேஷ்", "location": "கோயம்புத்தூர்" },
                "status": "completed",
                "timings": 19.25,
                "marker": "m2"
            }]
            """;

        var entry = new RaceApiPayloadAdapter().Parse(payload).Single();

        Assert.True(entry.HasSecondary);
        Assert.Equal("ரமேஷ், கோயம்புத்தூர்", entry.SecondaryDisplay);
        // Only the primary card number exists as far as this application is concerned.
        Assert.Equal("1000AAB", entry.CardNumber);
    }

    [Fact]
    public void TimingsIsThePerformanceTimeAndNotADate()
    {
        var entry = new RaceApiPayloadAdapter().Parse(SamplePayload).Single();

        Assert.Equal(22.5d, entry.TimingSeconds);
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 1, 2, 52, 763, TimeSpan.Zero), entry.RaceDateUtc);
    }

    [Fact]
    public void StatusMapsToRemoteExtractionState()
    {
        var completed = new RaceApiPayloadAdapter().Parse(SamplePayload).Single();
        Assert.Equal(RemoteExtractionStatus.Completed, completed.ExtractionStatus);

        var pending = new RaceApiPayloadAdapter().Parse(
            """[{ "player": { "cartNo": "1000AAC" }, "status": "processing", "marker": "m3" }]""").Single();
        Assert.Equal(RemoteExtractionStatus.NotCompleted, pending.ExtractionStatus);

        var failed = new RaceApiPayloadAdapter().Parse(
            """[{ "player": { "cartNo": "1000AAD" }, "status": "failed", "marker": "m4" }]""").Single();
        Assert.Equal(RemoteExtractionStatus.Failed, failed.ExtractionStatus);
    }

    [Fact]
    public void RaceTypesAreCollectedDistinctly()
    {
        var entry = new RaceApiPayloadAdapter().Parse(SamplePayload).Single();

        Assert.Equal(new[] { "200", "300" }, entry.RaceTypes);
        Assert.Equal("200 / 300", entry.RaceTypeDisplay);
    }

    [Fact]
    public void TheRemoteVideoLinkIsCarriedAsReferenceOnly()
    {
        var entry = new RaceApiPayloadAdapter().Parse(SamplePayload).Single();

        Assert.StartsWith("https://", entry.VideoLink);
        Assert.True(entry.IsVideoEnabled);
    }

    [Fact]
    public void ASingleObjectAndAWrappedArrayAreBothAccepted()
    {
        var single = new RaceApiPayloadAdapter().Parse(
            """{ "player": { "cartNo": "1000AAE" }, "status": "completed", "marker": "m5" }""");
        Assert.Single(single);

        var wrapped = new RaceApiPayloadAdapter().Parse(
            """{ "data": [ { "player": { "cartNo": "1000AAF" }, "status": "completed", "marker": "m6" } ] }""");
        Assert.Single(wrapped);
    }

    [Fact]
    public void ItemsWithoutACardNumberAreSkippedRatherThanFailingThePoll()
    {
        // The card number is the entry; an item without one cannot be worked on,
        // but it must not take down the rest of the response.
        var entries = new RaceApiPayloadAdapter().Parse(
            """
            [
              { "player": { "ownerName": "no card" }, "status": "completed", "marker": "m7" },
              { "player": { "cartNo": "1000AAG" }, "status": "completed", "marker": "m8" }
            ]
            """);

        Assert.Single(entries);
        Assert.Equal("1000AAG", entries[0].CardNumber);
    }

    [Fact]
    public void RepeatedItemsInOneResponseCollapseToOneEntry()
    {
        var entries = new RaceApiPayloadAdapter().Parse(SamplePayload);
        Assert.Single(entries);

        var duplicated = new RaceApiPayloadAdapter().Parse(
            """
            [
              { "player": { "cartNo": "1000AAH" }, "status": "completed", "marker": "same" },
              { "player": { "cartNo": "1000AAH" }, "status": "completed", "marker": "same" }
            ]
            """);
        Assert.Single(duplicated);
    }

    [Fact]
    public void MissingOptionalFieldsDoNotThrow()
    {
        var entry = new RaceApiPayloadAdapter().Parse(
            """[{ "player": { "cartNo": "1000AAI" } }]""").Single();

        Assert.Equal("1000AAI", entry.CardNumber);
        Assert.Equal(RemoteExtractionStatus.Unknown, entry.ExtractionStatus);
        Assert.Equal(0d, entry.TimingSeconds);
        Assert.Empty(entry.RaceTypes);
        Assert.Null(entry.VideoLink);
    }
}
