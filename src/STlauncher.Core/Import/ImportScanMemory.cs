using System;

namespace STlauncher.Core.Import;

/// <summary>
/// Whether the start should look for builds of other launchers. A look that found nothing
/// is remembered, so the same empty folders are not walked on every start; but not for
/// long, because a launcher installed tomorrow must still be noticed.
/// </summary>
/// <remarks>
/// Only the look at start goes by this. The button in the import dialog always looks.
/// </remarks>
public static class ImportScanMemory
{
    /// <summary>The longest a "nothing found" is believed.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromDays(7);

    /// <param name="lastEmptyScan">When a look last found nothing to bring over; null when it never did, or found something since.</param>
    public static bool ShouldScan(DateTimeOffset? lastEmptyScan, DateTimeOffset now)
    {
        if (lastEmptyScan is not { } last)
        {
            return true;
        }

        // A date ahead of the clock is a clock that was set back, or a file edited by
        // hand: either way it must not silence the look for longer than the week.
        return last > now || now - last >= Quiet;
    }
}
