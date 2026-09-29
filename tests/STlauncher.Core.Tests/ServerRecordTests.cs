using System;
using System.IO;
using STlauncher.Core.Server;
using Xunit;

namespace STlauncher.Core.Tests;

public class ServerRecordTests
{
    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "server-history.json");
    }

    [Fact]
    public void Record_follows_the_highest_reading_and_survives_reload()
    {
        var path = TempFile();
        var now = DateTimeOffset.UtcNow;
        var store = new ServerHistoryStore(path);

        store.Add("mc.example.org", 12, now.AddMinutes(-30));
        store.Add("mc.example.org", 71, now.AddMinutes(-20));
        store.Add("mc.example.org", 40, now.AddMinutes(-10));

        var record = store.GetRecord("mc.example.org");
        Assert.NotNull(record);
        Assert.Equal(71, record!.Online);
        Assert.Equal(now.AddMinutes(-20), record.Time);

        var reloaded = new ServerHistoryStore(path);
        Assert.Equal(71, reloaded.GetRecord("MC.EXAMPLE.ORG")!.Online);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "server-record.json")));
    }

    [Fact]
    public void Record_outlives_the_samples_it_came_from()
    {
        var path = TempFile();
        var store = new ServerHistoryStore(path);
        var old = DateTimeOffset.UtcNow - ServerHistoryStore.Retention - TimeSpan.FromDays(1);

        store.Add("mc.example.org", 90, old);
        // A new reading drops the old sample past retention, the record stays.
        store.Add("mc.example.org", 5, DateTimeOffset.UtcNow);

        Assert.Equal(90, store.GetRecord("mc.example.org")!.Online);
        Assert.Single(store.Samples);
    }

    [Fact]
    public void Existing_samples_seed_the_record()
    {
        var path = TempFile();
        var now = DateTimeOffset.UtcNow;
        var first = new ServerHistoryStore(path);
        first.Add("mc.example.org", 33, now.AddMinutes(-5));
        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "server-record.json"));

        var second = new ServerHistoryStore(path);
        Assert.Equal(33, second.GetRecord("mc.example.org")!.Online);
    }
}
