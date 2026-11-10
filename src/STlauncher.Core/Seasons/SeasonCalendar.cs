using System;

namespace STlauncher.Core.Seasons;

/// <summary>A holiday the launcher dresses up for, or none.</summary>
public enum Season
{
    None,
    Halloween,
    Winter
}

/// <summary>
/// When each season is on, by the player's own calendar date. Pure date logic: the
/// launcher asks with today's local date and decides what to show from the answer.
/// </summary>
/// <remarks>
/// Halloween: 24 October to 2 November, both days included. Winter: 15 November to the
/// last day of February, both included, across the turn of the year. The seasons do not overlap.
/// </remarks>
public static class SeasonCalendar
{
    /// <summary>
    /// The environment variable a developer sets to see a season out of its dates:
    /// <c>STLAUNCHER_SEASON=halloween</c>, <c>winter</c> or <c>none</c>. Read once at start;
    /// any other value (or none) leaves the calendar in charge. Not offered in the interface.
    /// </summary>
    public const string OverrideVariable = "STLAUNCHER_SEASON";

    /// <summary>The season on this date.</summary>
    public static Season On(DateOnly date)
    {
        var (month, day) = (date.Month, date.Day);

        if ((month == 10 && day >= 24) || (month == 11 && day <= 2))
        {
            return Season.Halloween;
        }

        if ((month == 11 && day >= 15) || month is 12 or 1 or 2)
        {
            return Season.Winter;
        }

        return Season.None;
    }

    /// <summary>The season on this date, unless a developer forced one (see <see cref="ParseOverride"/>).</summary>
    public static Season Resolve(DateOnly date, Season? forced) => forced ?? On(date);

    /// <summary>
    /// The value of <see cref="OverrideVariable"/> as a season: null when it is not set or
    /// not one of the three words, so a typo means "by the calendar", never "no season".
    /// </summary>
    public static Season? ParseOverride(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "halloween":
                return Season.Halloween;
            case "winter":
            case "newyear":
            case "new-year":
            case "new_year":
                return Season.Winter;
            case "none":
            case "off":
                return Season.None;
            default:
                return null;
        }
    }

    /// <summary>
    /// The year a season belongs to: a winter that runs into the new year is the one that
    /// began in November. A greeting closed for a season is closed for that year's season only.
    /// </summary>
    public static int YearOf(Season season, DateOnly date)
        => season == Season.Winter && date.Month <= 2 ? date.Year - 1 : date.Year;

    /// <summary>A key naming one season of one year, "halloween-2026", for remembering what the player closed.</summary>
    public static string KeyOf(Season season, DateOnly date)
        => season == Season.None ? string.Empty : $"{season.ToString().ToLowerInvariant()}-{YearOf(season, date)}";
}
