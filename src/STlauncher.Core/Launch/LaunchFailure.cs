using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Launch;

/// <summary>Why the Java process could not be created at all.</summary>
public enum JavaStartError
{
    None,

    /// <summary>The executable is not there.</summary>
    NotFound,

    /// <summary>Windows refused to run it: an antivirus holding java.exe is the usual reason.</summary>
    AccessDenied,

    Other
}

/// <summary>How one run of the game ended, with enough to tell a session from a launch that never got going.</summary>
/// <param name="Started">The game got as far as a start marker, or stayed alive past the start timeout.</param>
/// <param name="Duration">From the process starting to it ending.</param>
/// <param name="EarlyOutput">The first lines it printed: where a JVM that cannot start says why.</param>
public sealed record LaunchResult(
    int ExitCode,
    bool Started,
    TimeSpan Duration,
    IReadOnlyList<string> EarlyOutput)
{
    public JavaStartError StartError { get; init; }

    /// <summary>The system's own words for <see cref="StartError"/>.</summary>
    public string? StartErrorMessage { get; init; }

    /// <summary>The launcher's own Java was installed again before this run.</summary>
    public bool JavaReinstalled { get; init; }

    /// <summary>The launch was tried a second time; this is the result of the second.</summary>
    public bool Retried { get; init; }

    /// <summary>
    /// True when this was not a play session: Java never ran, or it ended with an error
    /// within the first moments and before the game showed any sign of starting.
    /// </summary>
    public bool IsFailedLaunch
        => StartError != JavaStartError.None ||
           (ExitCode != 0 && !Started && Duration < LaunchFailureAnalyzer.InstantExitWindow);

    public static LaunchResult NotStarted(JavaStartError error, string? message)
        => new(-1, false, TimeSpan.Zero, Array.Empty<string>()) { StartError = error, StartErrorMessage = message };
}

/// <summary>
/// Names the cause when Java itself would not run. The JVM's launcher ends every such
/// failure with the same two lines - "Could not create the Java Virtual Machine", "A fatal
/// exception has occurred" - and the line that says what is wrong sits next to them. The
/// player was shown the consequence; this finds the cause.
/// </summary>
public static class LaunchFailureAnalyzer
{
    /// <summary>
    /// An exit with an error inside this window, with no start marker seen, is a launch
    /// that failed. A modded game takes longer than this to reach the menu, but it prints
    /// its first marker well within it.
    /// </summary>
    public static readonly TimeSpan InstantExitWindow = TimeSpan.FromSeconds(30);

    /// <summary>The JVM's launcher speaks in the first few lines or not at all.</summary>
    private const int LinesToRead = 300;

    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // "Unrecognized VM option 'UseConcMarkSweepGC'" (an -XX switch this Java does not have),
    // "Unrecognized option: --add-opens" (Java 8 meeting a Java 9 option),
    // "Improperly specified VM option 'MaxGCPauseMillis=abc'".
    private static readonly Regex UnknownOption = new(
        @"(?:Unrecognized VM option|Improperly specified VM option) '(?<option>[^']+)'|Unrecognized option: (?<option2>\S+)",
        Options);

    // "Invalid maximum heap size: -Xmx8192M", "Invalid initial heap size: -Xms4G".
    private static readonly Regex InvalidHeap = new(
        @"Invalid (?:maximum|initial) heap size: (?<option>\S+)",
        Options);

    private static readonly string[] HeapComplaints =
    {
        "The specified size exceeds the maximum representable size",
        "Initial heap size set to a larger value than the maximum heap size",
        "Too small initial heap",
        "Too small maximum heap",
        "Invalid thread stack size"
    };

    /// <summary>Lines only a Java with files missing or damaged prints.</summary>
    private static readonly string[] BrokenRuntime =
    {
        "could not find java.dll",
        "Could not find Java SE Runtime Environment",
        "missing `server' JVM",
        "missing `client' JVM",
        "Failed setting boot class path",
        "jvm.cfg",
        "Error: loading:",
        "NoClassDefFoundError: java/lang/Object",
        @"Registry key 'Software\JavaSoft"
    };

    private const string VmInitFailed = "Error occurred during initialization of VM";
    private const string VmNotCreated = "Could not create the Java Virtual Machine";

    /// <summary>
    /// The diagnosis for a launch that failed, or <see cref="CrashDiagnosis.None"/> when
    /// this was a session, or nothing here explains it and the game log has to.
    /// </summary>
    public static CrashDiagnosis Analyze(LaunchResult result, IEnumerable<string> lines)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        switch (result.StartError)
        {
            case JavaStartError.NotFound:
                return new CrashDiagnosis(CrashCause.JavaMissing, null, null, result.StartErrorMessage);

            case JavaStartError.AccessDenied:
                return new CrashDiagnosis(CrashCause.JavaBlocked, null, null, result.StartErrorMessage);

            case JavaStartError.Other:
                return new CrashDiagnosis(CrashCause.JavaNotStarted, result.StartErrorMessage, null, result.StartErrorMessage);
        }

        return result.IsFailedLaunch ? AnalyzeJvmOutput(lines) : CrashDiagnosis.None;
    }

    /// <summary>Reads what the JVM's launcher printed before giving up.</summary>
    public static CrashDiagnosis AnalyzeJvmOutput(IEnumerable<string> lines)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }

        // The game's own log lines open with a bracket; the JVM's launcher never does, and
        // a mod is free to print "Unrecognized option" about something of its own.
        var head = lines
            .Take(LinesToRead)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '[')
            .ToList();

        foreach (var line in head)
        {
            var option = UnknownOption.Match(line);

            if (option.Success)
            {
                var name = option.Groups["option"].Success ? option.Groups["option"].Value : option.Groups["option2"].Value;
                return new CrashDiagnosis(CrashCause.JavaOption, name, null, line);
            }
        }

        foreach (var line in head)
        {
            var heap = InvalidHeap.Match(line);

            if (heap.Success)
            {
                return new CrashDiagnosis(CrashCause.JavaMemory, heap.Groups["option"].Value, null, line);
            }

            if (HeapComplaints.Any(c => line.Contains(c, StringComparison.OrdinalIgnoreCase)))
            {
                return new CrashDiagnosis(CrashCause.JavaMemory, null, null, line);
            }
        }

        foreach (var line in head)
        {
            if (BrokenRuntime.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return new CrashDiagnosis(CrashCause.JavaBroken, StripErrorPrefix(line), null, line);
            }
        }

        if (FindCause(head) is not { } cause)
        {
            return CrashDiagnosis.None;
        }

        // "Could not reserve enough space for object heap" and its kind are already known
        // to the crash analyzer, with a fix; the verdict there is the better one.
        var known = CrashAnalyzer.Analyze(new[] { cause });

        return known.Cause != CrashCause.Unknown
            ? known
            : new CrashDiagnosis(CrashCause.JavaNotStarted, StripErrorPrefix(cause), null, cause);
    }

    /// <summary>
    /// The line that says why the JVM was not created: the one after "Error occurred
    /// during initialization of VM", or the one before "Could not create the Java Virtual
    /// Machine". Null when the JVM did not say it failed.
    /// </summary>
    private static string? FindCause(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(VmInitFailed, StringComparison.OrdinalIgnoreCase))
            {
                for (var next = i + 1; next < lines.Count; next++)
                {
                    if (!IsConsequence(lines[next]))
                    {
                        return lines[next];
                    }
                }

                return lines[i];
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(VmNotCreated, StringComparison.OrdinalIgnoreCase))
            {
                for (var previous = i - 1; previous >= 0; previous--)
                {
                    if (!IsConsequence(lines[previous]))
                    {
                        return lines[previous];
                    }
                }

                return lines[i];
            }
        }

        return null;
    }

    /// <summary>Lines the JVM prints after every failure of this kind: true of all of them, a reason for none.</summary>
    private static bool IsConsequence(string line)
        => line.Contains(VmNotCreated, StringComparison.OrdinalIgnoreCase) ||
           line.Contains(VmInitFailed, StringComparison.OrdinalIgnoreCase) ||
           line.Contains("A fatal exception has occurred", StringComparison.OrdinalIgnoreCase) ||
           line.StartsWith("Did you mean", StringComparison.OrdinalIgnoreCase) ||
           line.StartsWith("Please install or use the JRE", StringComparison.OrdinalIgnoreCase);

    private static string StripErrorPrefix(string line)
        => line.StartsWith("Error: ", StringComparison.OrdinalIgnoreCase) ? line["Error: ".Length..].Trim() : line;
}
