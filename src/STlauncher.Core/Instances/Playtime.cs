using System;
using System.Linq;

namespace STlauncher.Core.Instances;

/// <summary>
/// Hours played, per build. The total is a running counter that never loses a second;
/// the session list behind "this week" is capped, so a build played for years does not
/// grow its json without end.
/// </summary>
public static class Playtime
{
    /// <summary>How many sessions the list keeps. A week of heavy play is well under this.</summary>
    public const int MaxSessions = 300;

    /// <summary>Records one session. Anything under a second is a launch that never got going.</summary>
    public static bool Record(Instance instance, DateTimeOffset startedAt, TimeSpan duration)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        var seconds = (long)Math.Floor(duration.TotalSeconds);

        if (seconds < 1)
        {
            return false;
        }

        instance.PlaySeconds += seconds;
        instance.PlaySessions.Add(new PlaySession { StartedAt = startedAt, Seconds = seconds });

        if (instance.PlaySessions.Count > MaxSessions)
        {
            instance.PlaySessions.RemoveRange(0, instance.PlaySessions.Count - MaxSessions);
        }

        return true;
    }

    public static TimeSpan Total(Instance instance)
        => TimeSpan.FromSeconds(instance.PlaySeconds);

    /// <summary>Time in sessions that started at or after the given moment.</summary>
    public static TimeSpan Since(Instance instance, DateTimeOffset from)
        => TimeSpan.FromSeconds(instance.PlaySessions.Where(s => s.StartedAt >= from).Sum(s => s.Seconds));

    public static TimeSpan LastWeek(Instance instance, DateTimeOffset now)
        => Since(instance, now.AddDays(-7));
}
