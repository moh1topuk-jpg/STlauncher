using System;
using System.Collections.Generic;
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

    /// <summary>How many places a build remembers. The ones not visited longest go first.</summary>
    public const int MaxPlaces = 40;

    /// <summary>
    /// Adds one run's time per place to the build. Under a second in a place is a join
    /// that never got through. Returns true when anything was added.
    /// </summary>
    public static bool RecordPlaces(
        Instance instance,
        IReadOnlyDictionary<STlauncher.Core.Launch.GamePlace, TimeSpan> spent,
        DateTimeOffset now)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        var added = false;

        foreach (var (place, time) in spent)
        {
            var seconds = (long)Math.Floor(time.TotalSeconds);

            if (seconds < 1 || place.Kind == STlauncher.Core.Launch.GamePlaceKind.Menu)
            {
                continue;
            }

            var address = place.Kind == STlauncher.Core.Launch.GamePlaceKind.Server ? place.Address : null;
            var entry = instance.PlayPlaces.FirstOrDefault(p => string.Equals(p.Address, address, StringComparison.Ordinal));

            if (entry is null)
            {
                entry = new PlayPlace { Address = address };
                instance.PlayPlaces.Add(entry);
            }

            entry.Seconds += seconds;
            entry.LastPlayedAt = now;
            added = true;
        }

        if (instance.PlayPlaces.Count > MaxPlaces)
        {
            instance.PlayPlaces = instance.PlayPlaces
                .OrderByDescending(p => p.LastPlayedAt)
                .Take(MaxPlaces)
                .ToList();
        }

        return added;
    }

    /// <summary>The places with the most time, longest first.</summary>
    public static IReadOnlyList<PlayPlace> TopPlaces(Instance instance, int count)
        => instance.PlayPlaces
            .Where(p => p.Seconds > 0)
            .OrderByDescending(p => p.Seconds)
            .ThenBy(p => p.Address, StringComparer.Ordinal)
            .Take(count)
            .ToList();

    public static TimeSpan Total(Instance instance)
        => TimeSpan.FromSeconds(instance.PlaySeconds);

    /// <summary>Time in sessions that started at or after the given moment.</summary>
    public static TimeSpan Since(Instance instance, DateTimeOffset from)
        => TimeSpan.FromSeconds(instance.PlaySessions.Where(s => s.StartedAt >= from).Sum(s => s.Seconds));

    public static TimeSpan LastWeek(Instance instance, DateTimeOffset now)
        => Since(instance, now.AddDays(-7));
}
