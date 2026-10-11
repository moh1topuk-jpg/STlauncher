using STlauncher.Core.Import;

namespace STlauncher.Core.Tests;

/// <summary>
/// The start does not walk the other launchers' folders again right after finding nothing
/// there - but a launcher installed later must not stay unnoticed for more than a week.
/// </summary>
public class ImportScanMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void With_nothing_remembered_the_start_looks()
    {
        Assert.True(ImportScanMemory.ShouldScan(null, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(24 * 6)]
    [InlineData(24 * 7 - 1)]
    public void For_a_week_after_an_empty_look_the_start_does_not_look(int hoursAgo)
    {
        Assert.False(ImportScanMemory.ShouldScan(Now.AddHours(-hoursAgo), Now));
    }

    [Theory]
    [InlineData(24 * 7)]
    [InlineData(24 * 8)]
    [InlineData(24 * 365)]
    public void A_week_later_it_looks_again(int hoursAgo)
    {
        Assert.True(ImportScanMemory.ShouldScan(Now.AddHours(-hoursAgo), Now));
    }

    [Fact]
    public void A_date_ahead_of_the_clock_does_not_silence_the_look()
    {
        // The clock was set back, or the file was edited: a "nothing found" from next
        // year would otherwise hide every launcher until then.
        Assert.True(ImportScanMemory.ShouldScan(Now.AddMinutes(1), Now));
        Assert.True(ImportScanMemory.ShouldScan(Now.AddYears(1), Now));
    }

    [Fact]
    public void The_quiet_time_is_a_week_and_no_longer()
    {
        Assert.Equal(TimeSpan.FromDays(7), ImportScanMemory.Quiet);
    }

    [Fact]
    public void Time_zones_do_not_shift_the_week()
    {
        var remembered = new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.FromHours(3));   // 12:00 UTC

        Assert.True(ImportScanMemory.ShouldScan(remembered, Now));
        Assert.False(ImportScanMemory.ShouldScan(remembered.AddSeconds(1), Now));
    }
}
