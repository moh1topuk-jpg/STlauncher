using System;
using System.Linq;
using STlauncher.Core.Instances;
using Xunit;

namespace STlauncher.Core.Tests;

public class PlaytimeTests
{
    [Fact]
    public void Record_adds_to_total_and_list()
    {
        var instance = new Instance();
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        Assert.True(Playtime.Record(instance, now, TimeSpan.FromMinutes(90)));

        Assert.Equal(TimeSpan.FromMinutes(90), Playtime.Total(instance));
        Assert.Single(instance.PlaySessions);
        Assert.Equal(5400, instance.PlaySessions[0].Seconds);
    }

    [Fact]
    public void Sessions_under_a_second_are_ignored()
    {
        var instance = new Instance();

        Assert.False(Playtime.Record(instance, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(400)));
        Assert.Equal(0, instance.PlaySeconds);
        Assert.Empty(instance.PlaySessions);
    }

    [Fact]
    public void Last_week_counts_only_recent_sessions()
    {
        var instance = new Instance();
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        Playtime.Record(instance, now.AddDays(-10), TimeSpan.FromHours(5));
        Playtime.Record(instance, now.AddDays(-3), TimeSpan.FromHours(2));
        Playtime.Record(instance, now.AddHours(-1), TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromHours(7.5), Playtime.Total(instance));
        Assert.Equal(TimeSpan.FromHours(2.5), Playtime.LastWeek(instance, now));
    }

    [Fact]
    public void List_is_capped_but_total_keeps_counting()
    {
        var instance = new Instance();
        var now = DateTimeOffset.UtcNow;

        // Recorded in the order they happened, oldest first.
        var count = Playtime.MaxSessions + 50;

        for (var i = 0; i < count; i++)
        {
            Playtime.Record(instance, now.AddMinutes(-(count - 1 - i)), TimeSpan.FromSeconds(10));
        }

        Assert.Equal(Playtime.MaxSessions, instance.PlaySessions.Count);
        Assert.Equal((Playtime.MaxSessions + 50) * 10, instance.PlaySeconds);
        // The oldest sessions are the ones dropped.
        Assert.Equal(now.AddMinutes(-(Playtime.MaxSessions - 1)), instance.PlaySessions.First().StartedAt);
    }
}
