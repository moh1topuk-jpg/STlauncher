using System;
using System.Linq;
using STlauncher.Core.Instances;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Hours played, on the main screen next to "last played". Counted from the moment the
/// game process starts to the moment it exits, per build; the launcher's own time is not
/// play time.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// "Played 12 h 30 min in this build, 3 h this week". Before the first counted session
    /// the line says so, rather than hiding: a build last played yesterday with no hours
    /// next to it looks like a counter that lost the number.
    /// </summary>
    public string PlaytimeLabel
    {
        get
        {
            if (SelectedInstance is not { } instance)
            {
                return string.Empty;
            }

            if (instance.PlaySeconds <= 0)
            {
                return Localize("Game_PlaytimeNone", "Play time is counted from the next launch");
            }

            var total = Playtime.Total(instance);
            var week = Playtime.LastWeek(instance, DateTimeOffset.Now);

            return week.TotalSeconds >= 60
                ? Localize("Game_Playtime", "Played {0}, {1} this week", FormatPlaytime(total), FormatPlaytime(week))
                : Localize("Game_PlaytimeTotalOnly", "Played {0}", FormatPlaytime(total));
        }
    }

    /// <summary>"40 h in all builds": only when other builds add something to the number.</summary>
    public string PlaytimeAllLabel
    {
        get
        {
            var all = Instances.Sum(i => i.PlaySeconds);
            var here = SelectedInstance?.PlaySeconds ?? 0;

            return all > here && all >= 60
                ? Localize("Game_PlaytimeAll", "{0} in all builds", FormatPlaytime(TimeSpan.FromSeconds(all)))
                : string.Empty;
        }
    }

    public bool HasPlaytime => !string.IsNullOrEmpty(PlaytimeLabel);

    private void RaisePlaytimeLabels()
    {
        OnPropertyChanged(nameof(PlaytimeLabel));
        OnPropertyChanged(nameof(PlaytimeAllLabel));
        OnPropertyChanged(nameof(HasPlaytime));
    }

    /// <summary>Whole hours and minutes; under an hour, minutes alone. Never seconds.</summary>
    internal static string FormatPlaytime(TimeSpan time)
    {
        var hours = (int)time.TotalHours;
        var minutes = time.Minutes;

        if (hours == 0)
        {
            return Localize("Time_Minutes", "{0} min", Math.Max(1, minutes));
        }

        return minutes == 0
            ? Localize("Time_Hours", "{0} h", hours)
            : Localize("Time_HoursMinutes", "{0} h {1} min", hours, minutes);
    }

    /// <summary>Called when the game has exited. The build is the one that was started, not the one selected now.</summary>
    private void RecordPlaytime(Instance? instance, DateTimeOffset startedAt)
    {
        if (instance is null)
        {
            return;
        }

        var duration = DateTimeOffset.Now - startedAt;

        try
        {
            if (!Playtime.Record(instance, startedAt, duration))
            {
                return;
            }

            _instances.Save(instance);
            AppendConsole($"[play] {instance.Name}: {FormatPlaytime(duration)} this session, {FormatPlaytime(Playtime.Total(instance))} in all");
        }
        catch (Exception ex)
        {
            AppendConsole($"[play] could not save the play time: {ex.Message}");
        }

        RefreshBuildListItem(instance);
        RaisePlaytimeLabels();
    }
}
