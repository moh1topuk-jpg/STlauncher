using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Server;
using Xunit;

namespace STlauncher.Core.Tests;

public class ServerHistoryStoreTests
{
    private const string Server = "mc.example.com";

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"), "history.json");

    [Fact]
    public void Add_KeepsOneSamplePerMinute()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        Assert.True(store.Add(Server, 10, now));
        Assert.False(store.Add(Server, 20, now.AddSeconds(20)));

        Assert.Single(store.Samples);
        Assert.Equal(10, store.Samples[0].Online);
    }

    [Fact]
    public void Add_PersistsAcrossInstances()
    {
        var path = TempPath();

        var first = new ServerHistoryStore(path);
        first.Add(Server, 42);

        var second = new ServerHistoryStore(path);

        Assert.Single(second.Samples);
        Assert.Equal(42, second.Samples[0].Online);
        Assert.Equal(Server, second.Samples[0].Address);
    }

    [Fact]
    public void GetBuckets_ReturnsOneBucketPerSliceOfTheWindow()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(Server, 30, now.AddHours(-3));
        store.Add(Server, 50, now.AddHours(-2));
        store.Add(Server, 70, now);

        var buckets = store.GetBuckets(Server, TimeSpan.FromHours(24), 24, now);

        Assert.Equal(24, buckets.Count);
        Assert.Equal(3, buckets.Count(b => b.HasData));

        // Newest reading at the end of the axis, oldest towards the beginning.
        Assert.True(buckets[^1].HasData);
        Assert.Equal(70, buckets[^1].Peak);
    }

    [Fact]
    public void GetBuckets_MarksSlicesWithoutSamplesAsGaps()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(Server, 12, now);

        var buckets = store.GetBuckets(Server, TimeSpan.FromHours(24), 24, now);

        // A gap is not "nobody was online": the launcher simply was not running, and the
        // chart has to be able to tell the two apart.
        Assert.Single(buckets.Where(b => b.HasData));
        Assert.All(buckets.Where(b => !b.HasData), b =>
        {
            Assert.Equal(0, b.Peak);
            Assert.Equal(0, b.SampleCount);
        });
    }

    [Fact]
    public void GetBuckets_AveragesEverySampleInASlice()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        // Three readings inside the same six-hour slice.
        store.Add(Server, 10, now.AddMinutes(-30));
        store.Add(Server, 20, now.AddMinutes(-20));
        store.Add(Server, 60, now.AddMinutes(-10));

        var buckets = store.GetBuckets(Server, TimeSpan.FromHours(1), 1, now);

        Assert.Single(buckets);
        Assert.Equal(3, buckets[0].SampleCount);
        Assert.Equal(30, buckets[0].Average);
        Assert.Equal(60, buckets[0].Peak);
    }

    [Fact]
    public void GetBuckets_IgnoresSamplesOutsideTheWindow()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(Server, 99, now.AddDays(-10));
        store.Add(Server, 11, now.AddHours(-1));

        var buckets = store.GetBuckets(Server, TimeSpan.FromHours(24), 24, now);
        var withData = buckets.Where(b => b.HasData).ToList();

        Assert.Single(withData);
        Assert.Equal(11, withData[0].Peak);
    }

    [Fact]
    public void GetBuckets_SeparatesServers()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(Server, 40, now.AddMinutes(-10));
        store.Add("other.example.com", 5, now);

        var mine = store.GetBuckets(Server, TimeSpan.FromHours(24), 24, now);

        Assert.Single(mine.Where(b => b.HasData));
        Assert.Equal(40, mine.First(b => b.HasData).Peak);
    }

    [Fact]
    public void LegacySamplesWithoutAnAddressStillCount()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written by a version that did not record the address. There was only ever one
        // server back then, so the data belongs to whatever is configured now.
        File.WriteAllText(path, """[{ "time": "2026-09-17T10:00:00+03:00", "online": 7 }]""");

        var store = new ServerHistoryStore(path);
        var now = new DateTimeOffset(2026, 9, 17, 11, 0, 0, TimeSpan.FromHours(3));

        var (average, peak, samples) = store.GetSummary(Server, TimeSpan.FromHours(24), now);

        Assert.Equal(1, samples);
        Assert.Equal(7, average);
        Assert.Equal(7, peak);
    }

    [Fact]
    public void GetSummary_ReportsAveragePeakAndCount()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(Server, 10, now.AddHours(-3));
        store.Add(Server, 20, now.AddHours(-2));
        store.Add(Server, 30, now.AddHours(-1));

        var (average, peak, samples) = store.GetSummary(Server, TimeSpan.FromHours(24), now);

        Assert.Equal(20, average);
        Assert.Equal(30, peak);
        Assert.Equal(3, samples);
    }

    [Fact]
    public void GetSummary_IsEmptyWithoutSamples()
    {
        var store = new ServerHistoryStore(TempPath());

        var (average, peak, samples) = store.GetSummary(Server, TimeSpan.FromHours(24));

        Assert.Equal(0, average);
        Assert.Equal(0, peak);
        Assert.Equal(0, samples);
    }

    [Fact]
    public void FirstSampleAt_ReportsTheOldestReading()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(Server, 1, now.AddHours(-5));
        store.Add(Server, 2, now);

        var first = store.FirstSampleAt(Server);

        Assert.NotNull(first);
        Assert.Equal(now.AddHours(-5), first!.Value);
        Assert.Null(store.FirstSampleAt("nothing.example.com"));
    }

    [Fact]
    public void DamagedFile_DoesNotThrow()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var store = new ServerHistoryStore(path);
        store.Add(Server, 5);

        Assert.Single(store.Samples);
    }
}
