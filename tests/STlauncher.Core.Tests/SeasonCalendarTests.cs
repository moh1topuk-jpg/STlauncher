using STlauncher.Core.Seasons;

namespace STlauncher.Core.Tests;

public class SeasonCalendarTests
{
    [Theory]
    [InlineData(2026, 10, 23, Season.None)]
    [InlineData(2026, 10, 24, Season.Halloween)]
    [InlineData(2026, 10, 31, Season.Halloween)]
    [InlineData(2026, 11, 1, Season.Halloween)]
    [InlineData(2026, 11, 2, Season.Halloween)]
    [InlineData(2026, 11, 3, Season.None)]
    [InlineData(2026, 12, 19, Season.None)]
    [InlineData(2026, 12, 20, Season.NewYear)]
    [InlineData(2026, 12, 31, Season.NewYear)]
    [InlineData(2027, 1, 1, Season.NewYear)]
    [InlineData(2027, 1, 10, Season.NewYear)]
    [InlineData(2027, 1, 11, Season.None)]
    [InlineData(2027, 6, 15, Season.None)]
    [InlineData(2028, 2, 29, Season.None)]
    public void Dates_fall_in_their_season(int year, int month, int day, Season expected)
        => Assert.Equal(expected, SeasonCalendar.On(new DateOnly(year, month, day)));

    [Theory]
    [InlineData("halloween", Season.Halloween)]
    [InlineData(" Halloween ", Season.Halloween)]
    [InlineData("newyear", Season.NewYear)]
    [InlineData("NEWYEAR", Season.NewYear)]
    [InlineData("none", Season.None)]
    public void The_override_names_a_season(string value, Season expected)
        => Assert.Equal(expected, SeasonCalendar.ParseOverride(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("helloween")]
    public void An_unknown_override_leaves_the_calendar_in_charge(string? value)
    {
        Assert.Null(SeasonCalendar.ParseOverride(value));
        Assert.Equal(Season.None, SeasonCalendar.Resolve(new DateOnly(2026, 6, 1), SeasonCalendar.ParseOverride(value)));
        Assert.Equal(Season.Halloween, SeasonCalendar.Resolve(new DateOnly(2026, 10, 30), SeasonCalendar.ParseOverride(value)));
    }

    [Fact]
    public void A_forced_season_wins_over_the_date()
    {
        Assert.Equal(Season.Halloween, SeasonCalendar.Resolve(new DateOnly(2026, 6, 1), Season.Halloween));
        Assert.Equal(Season.None, SeasonCalendar.Resolve(new DateOnly(2026, 10, 31), Season.None));
    }

    [Fact]
    public void A_new_year_in_january_belongs_to_the_year_it_began()
    {
        Assert.Equal("newyear-2026", SeasonCalendar.KeyOf(Season.NewYear, new DateOnly(2026, 12, 25)));
        Assert.Equal("newyear-2026", SeasonCalendar.KeyOf(Season.NewYear, new DateOnly(2027, 1, 5)));
        Assert.Equal("halloween-2026", SeasonCalendar.KeyOf(Season.Halloween, new DateOnly(2026, 11, 2)));
        Assert.Equal("halloween-2027", SeasonCalendar.KeyOf(Season.Halloween, new DateOnly(2027, 10, 24)));
        Assert.Equal(string.Empty, SeasonCalendar.KeyOf(Season.None, new DateOnly(2026, 6, 1)));
    }
}
