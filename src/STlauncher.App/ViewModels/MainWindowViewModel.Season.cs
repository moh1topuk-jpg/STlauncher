using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Controls;
using STlauncher.Core.Seasons;

namespace STlauncher.App.ViewModels;

/// <summary>
/// The holiday look of the home screen. The calendar decides when (Core/Seasons), the
/// player can switch it off in Settings, and nothing about it is saved anywhere but the
/// switch and the closed greeting: the skin, its files and what is shared stay as they are.
/// </summary>
/// <remarks>
/// For testing, a developer forces a season with the environment variable
/// <c>STLAUNCHER_SEASON=halloween|newyear|none</c>, read once when the launcher starts
/// (see <see cref="SeasonCalendar.OverrideVariable"/>). It is not in the interface.
/// New Year is a season the calendar knows but that has no look yet: while it is on the
/// home screen is the ordinary one.
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>The season a developer forced through the environment, or null for "by the calendar".</summary>
    private static readonly Season? ForcedSeason =
        SeasonCalendar.ParseOverride(Environment.GetEnvironmentVariable(SeasonCalendar.OverrideVariable));

    /// <summary>The player's switch: "Праздничное оформление".</summary>
    [ObservableProperty]
    private bool _seasonalLook = true;

    /// <summary>The season today is in, whether or not the look is on.</summary>
    [ObservableProperty]
    private Season _calendarSeason = SeasonCalendar.Resolve(DateOnly.FromDateTime(DateTime.Now), ForcedSeason);

    private string? _seasonGreetingClosed;
    private DispatcherTimer? _seasonTimer;

    /// <summary>The season the home screen is dressed for: none while the switch is off.</summary>
    public Season ActiveSeason => SeasonalLook ? CalendarSeason : Season.None;

    public bool IsHalloweenLook => ActiveSeason == Season.Halloween;

    /// <summary>What the figure on the home stage wears on its head. Only the home stage binds this.</summary>
    public HeadDecoration StageHeadDecoration => IsHalloweenLook ? HeadDecoration.Pumpkin : HeadDecoration.None;

    /// <summary>"С Хэллоуином!" beside the greeting, until the player closes it for this year's season.</summary>
    public bool ShowSeasonGreeting => IsHalloweenLook && _seasonGreetingClosed != SeasonKey;

    private string SeasonKey => SeasonCalendar.KeyOf(CalendarSeason, DateOnly.FromDateTime(DateTime.Now));

    private void LoadSeason(bool seasonalLook, string? greetingClosed)
    {
        _seasonGreetingClosed = greetingClosed;
        SeasonalLook = seasonalLook;
        RefreshSeason();

        // The date is looked at again every quarter of an hour: a launcher left open over
        // the night of 2 to 3 November takes its pumpkins off by itself.
        if (_seasonTimer is null)
        {
            _seasonTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _seasonTimer.Tick += (_, _) => RefreshSeason();
            _seasonTimer.Start();
        }
    }

    private void RefreshSeason()
    {
        CalendarSeason = SeasonCalendar.Resolve(DateOnly.FromDateTime(DateTime.Now), ForcedSeason);
        RaiseSeason();
    }

    partial void OnSeasonalLookChanged(bool value)
    {
        RaiseSeason();
        PersistSettings();
    }

    partial void OnCalendarSeasonChanged(Season value) => RaiseSeason();

    private void RaiseSeason()
    {
        OnPropertyChanged(nameof(ActiveSeason));
        OnPropertyChanged(nameof(IsHalloweenLook));
        OnPropertyChanged(nameof(StageHeadDecoration));
        OnPropertyChanged(nameof(ShowSeasonGreeting));
    }

    [RelayCommand]
    private void CloseSeasonGreeting()
    {
        _seasonGreetingClosed = SeasonKey;
        OnPropertyChanged(nameof(ShowSeasonGreeting));
        PersistSettings();
    }
}
