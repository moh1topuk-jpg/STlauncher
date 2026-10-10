using System.Globalization;

namespace STlauncher.Core.Launch;

/// <summary>What a Windows status code means for a player, grouped by what they can do about it.</summary>
public enum NtStatusKind
{
    /// <summary>A status code the table does not name.</summary>
    Unknown,

    /// <summary>0xC0000005: nearly always a graphics driver or an overlay drawing into the game.</summary>
    AccessViolation,

    /// <summary>0xC0000135 and 0xC000007B: a DLL is missing or is for another architecture.</summary>
    MissingDll,

    /// <summary>0xC0000142: a DLL was found and refused to initialise.</summary>
    InitFailed,

    /// <summary>0xC00000FD.</summary>
    StackOverflow,

    /// <summary>0xC0000409: a protection check stopped the process.</summary>
    StackBufferOverrun,

    /// <summary>0xC0000017 and 0xC000012D: no memory, or no page file left to back it.</summary>
    OutOfMemory
}

/// <summary>
/// Exit codes that are not the game's own. When Windows kills a process it leaves an
/// NTSTATUS value as the exit code, and as a signed number that reads "-1073741819": a
/// code nobody can search for. Shown as 0xC0000005 it is the first hit everywhere.
/// </summary>
public static class NtStatus
{
    /// <summary>
    /// True for the error and warning ranges (0xC... and 0x8...). A game that calls
    /// System.exit(-1) is not one of these: 0xFFFFFFFF is no status code.
    /// </summary>
    public static bool IsNtStatus(int exitCode)
    {
        var top = unchecked((uint)exitCode) >> 28;

        return top is 0xC or 0x8;
    }

    /// <summary>"0xC0000005" for a status code, the plain number for anything else.</summary>
    public static string Format(int exitCode)
        => IsNtStatus(exitCode)
            ? "0x" + unchecked((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture)
            : exitCode.ToString(CultureInfo.InvariantCulture);

    public static NtStatusKind Classify(int exitCode) => unchecked((uint)exitCode) switch
    {
        0xC0000005 => NtStatusKind.AccessViolation,
        0xC0000135 or 0xC000007B => NtStatusKind.MissingDll,
        0xC0000142 => NtStatusKind.InitFailed,
        0xC00000FD => NtStatusKind.StackOverflow,
        0xC0000409 => NtStatusKind.StackBufferOverrun,
        0xC0000017 or 0xC000012D => NtStatusKind.OutOfMemory,
        _ => NtStatusKind.Unknown
    };

    /// <summary>One English sentence, for the report that goes to whoever helps.</summary>
    public static string Describe(int exitCode) => Classify(exitCode) switch
    {
        NtStatusKind.AccessViolation => "access violation: usually a graphics driver or an overlay",
        NtStatusKind.MissingDll => "a DLL is missing or is for another architecture",
        NtStatusKind.InitFailed => "a DLL failed to initialise",
        NtStatusKind.StackOverflow => "stack overflow",
        NtStatusKind.StackBufferOverrun => "stack buffer overrun: the process was stopped by a protection check",
        NtStatusKind.OutOfMemory => "not enough memory or page file",
        _ => "stopped by Windows"
    };
}
