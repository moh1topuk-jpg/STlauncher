using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Server;
using Xunit;

namespace STlauncher.Core.Tests;

public class ServerHistoryStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"), "history.json");

    [Fact]
    public void Add_KeepsOneSamplePerMinute()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(10, now);
        store.Add(20, now.AddSeconds(20));

        Assert.Single(store.Samples);
        Assert.Equal(10, store.Samples[0].Online);
    }

    [Fact]
    public void Add_PersistsAcrossInstances()
    {
        var path = TempPath();

        var first = new ServerHistoryStore(path);
        first.Add(42);

        var second = new ServerHistoryStore(path);

        Assert.Single(second.Samples);
        Assert.Equal(42, second.Samples[0].Online);
    }

    [Fact]
    public void GetHourlyBars_ProducesOneBarPerHourWithData()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(30, now.AddHours(-2));
        store.Add(50, now.AddHours(-1));
        store.Add(70, now);

        var bars = store.GetHourlyBars(TimeSpan.FromDays(3), now);

        Assert.Equal(3, bars.Count);
        Assert.Equal(30, bars[0].Peak);
        Assert.Equal(70, bars[^1].Peak);
    }

    [Fact]
    public void GetHourlyBars_IgnoresSamplesOutsideTheWindow()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(99, now.AddDays(-10));
        store.Add(11, now.AddHours(-1));

        var bars = store.GetHourlyBars(TimeSpan.FromDays(3), now);

        Assert.Single(bars);
        Assert.Equal(11, bars[0].Peak);
    }

    [Fact]
    public void GetSummary_ReportsAverageAndPeak()
    {
        var store = new ServerHistoryStore(TempPath());
        var now = DateTimeOffset.Now;

        store.Add(10, now.AddHours(-3));
        store.Add(20, now.AddHours(-2));
        store.Add(30, now.AddHours(-1));

        var (average, peak) = store.GetSummary(TimeSpan.FromDays(3), now);

        Assert.Equal(20, average);
        Assert.Equal(30, peak);
    }

    [Fact]
    public void GetSummary_IsEmptyWithoutSamples()
    {
        var store = new ServerHistoryStore(TempPath());

        var (average, peak) = store.GetSummary(TimeSpan.FromDays(3));

        Assert.Equal(0, average);
        Assert.Equal(0, peak);
    }

    [Fact]
    public void DamagedFile_DoesNotThrow()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var store = new ServerHistoryStore(path);
        store.Add(5);

        Assert.Single(store.Samples);
    }
}