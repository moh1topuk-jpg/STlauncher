using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Server;
using Xunit;

namespace STlauncher.Core.Tests;

public class ServerStatsTests
{
    /// <summary>A trimmed copy of what the collector in workers/stats/worker.js serves.</summary>
    private const string Payload = """
        {
          "schemaVersion": 1,
          "updatedAt": "2026-09-17T21:30:00.000Z",
          "sampleCount": 812,
          "server": {
            "name": "ShowTime",
            "address": "mc.showtime.su",
            "online": 33,
            "max": 2026,
            "isOnline": true
          },
          "summary": {
            "averageWeek": 40,
            "peak": 366,
            "peakAt": "2026-08-07T20:49:32.000Z",
            "uptime": 100,
            "rank": 26
          },
          "ranges": {
            "day": {
              "from": "2026-09-16T21:30:00.000Z",
              "to": "2026-09-17T21:30:00.000Z",
              "buckets": [
                { "t": "2026-09-16T21:30:00.000Z", "avg": 12.5, "peak": 18 },
                { "t": "2026-09-16T22:30:00.000Z", "avg": null, "peak": null },
                { "t": "2026-09-16T23:30:00.000Z", "avg": 30, "peak": 41 }
              ]
            },
            "week": {
              "from": "2026-09-10T21:30:00.000Z",
              "to": "2026-09-17T21:30:00.000Z",
              "buckets": [
                { "t": "2026-09-10T21:30:00.000Z", "avg": null, "peak": null }
              ]
            }
          }
        }
        """;

    [Fact]
    public void Parse_ReadsTheCollectorPayload()
    {
        var snapshot = ServerStatsSnapshot.Parse(Payload);

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal(812, snapshot.SampleCount);
        Assert.Equal("ShowTime", snapshot.Server?.Name);
        Assert.Equal("mc.showtime.su", snapshot.Server?.Address);
        Assert.Equal(33, snapshot.Server?.Online);
        Assert.True(snapshot.Server?.IsOnline);
    }

    [Fact]
    public void Parse_ReadsTheAggregatesNoLauncherCanCompute()
    {
        var summary = ServerStatsSnapshot.Parse(Payload).Summary;

        Assert.NotNull(summary);
        Assert.True(summary!.HasAnything);
        Assert.Equal(40, summary.AverageWeek);
        Assert.Equal(366, summary.Peak);
        Assert.Equal(new DateTimeOffset(2026, 8, 7, 20, 49, 32, TimeSpan.Zero), summary.PeakAt);
        Assert.Equal(100, summary.Uptime);
        Assert.Equal(26, summary.Rank);
    }

    [Fact]
    public void ToBuckets_KeepsGapsSeparateFromZero()
    {
        var day = ServerStatsSnapshot.Parse(Payload).Range("day");

        Assert.NotNull(day);
        Assert.True(day!.HasData);

        var buckets = day.ToBuckets();

        Assert.Equal(3, buckets.Count);
        Assert.True(buckets[0].HasData);
        Assert.Equal(12.5, buckets[0].Average);
        Assert.Equal(18, buckets[0].Peak);

        // The collector was not running for this slice - that is a gap, not an hour with
        // nobody online, and the chart draws the two differently.
        Assert.False(buckets[1].HasData);
        Assert.Equal(0, buckets[1].Peak);

        Assert.True(buckets[2].HasData);
    }

    [Fact]
    public void ToBuckets_EndsEachBucketWhereTheNextBegins()
    {
        var buckets = ServerStatsSnapshot.Parse(Payload).Range("day")!.ToBuckets();

        Assert.Equal(buckets[1].Start, buckets[0].End);
        Assert.Equal(buckets[2].Start, buckets[1].End);

        // The last one runs to the end of the window.
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 21, 30, 0, TimeSpan.Zero), buckets[2].End);
    }

    [Fact]
    public void HasData_IsFalseForARangeOfNothingButGaps()
    {
        var week = ServerStatsSnapshot.Parse(Payload).Range("week");

        Assert.NotNull(week);
        Assert.False(week!.HasData);
    }

    [Fact]
    public void IsFresh_RejectsAStoppedCollector()
    {
        var snapshot = ServerStatsSnapshot.Parse(Payload);
        var updated = new DateTimeOffset(2026, 9, 17, 21, 30, 0, TimeSpan.Zero);

        Assert.True(snapshot.IsFresh(TimeSpan.FromHours(6), updated.AddHours(1)));

        // A collector that died days ago must not keep a frozen chart on screen.
        Assert.False(snapshot.IsFresh(TimeSpan.FromHours(6), updated.AddDays(3)));
    }

    [Fact]
    public void Parse_RejectsANewerSchema()
    {
        var future = Payload.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");

        Assert.Throws<InvalidDataException>(() => ServerStatsSnapshot.Parse(future));
    }

    [Fact]
    public void Parse_SurvivesAPayloadWithoutOptionalSections()
    {
        var snapshot = ServerStatsSnapshot.Parse("""{ "schemaVersion": 1 }""");

        Assert.Null(snapshot.Server);
        Assert.Null(snapshot.Summary);
        Assert.Empty(snapshot.Ranges);
        Assert.Null(snapshot.Range("day"));
        Assert.False(snapshot.IsFresh(TimeSpan.FromHours(6)));
    }

    [Theory]
    [InlineData("https://name.workers.dev/", true)]
    [InlineData("http://localhost:8787/", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("C:\\stats.json", false)]
    public void IsHttpUrl_AcceptsOnlyWebAddresses(string? value, bool expected)
        => Assert.Equal(expected, ServerStatsClient.IsHttpUrl(value));
}
