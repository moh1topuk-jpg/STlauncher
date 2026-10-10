using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace STlauncher.Core.Launch;

/// <summary>Everything the player would otherwise have to type out when asking for help.</summary>
public sealed record CrashReportContext(
    string LauncherVersion,
    string BuildName,
    string? GameVersion,
    string Loader,
    string? LoaderVersion,
    string? JavaPath,
    int MaxMemoryMb,
    int ExitCode,
    IReadOnlyList<string> Mods);

/// <summary>
/// Assembles the text that goes onto the clipboard after a crash: the launcher, the
/// build, the diagnosis and the part of the log that matters. Whoever gets it in a chat
/// can answer without asking for anything else.
/// </summary>
public static class CrashReport
{
    /// <summary>Lines from the end of the log that are included in full.</summary>
    public const int TailLines = 60;

    /// <summary>Lines with an exception or error taken from earlier in the log.</summary>
    public const int ErrorLines = 40;

    public static string Build(
        CrashReportContext context,
        CrashDiagnosis diagnosis,
        IReadOnlyList<string> logLines,
        IReadOnlyList<string>? crashReportLines = null)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var text = new StringBuilder();

        text.AppendLine($"STlauncher {context.LauncherVersion} · {OperatingSystem()} · exit code {NtStatus.Format(context.ExitCode)}");
        text.AppendLine($"Build: {context.BuildName} · Minecraft {context.GameVersion ?? "?"} · {context.Loader} {context.LoaderVersion}".TrimEnd());
        text.AppendLine($"Java: {(string.IsNullOrWhiteSpace(context.JavaPath) ? "automatic" : context.JavaPath)} · memory {context.MaxMemoryMb} MB");
        text.AppendLine($"Diagnosis: {Describe(diagnosis)}");

        if (diagnosis.Evidence is { Length: > 0 } evidence)
        {
            text.AppendLine($"Evidence: {evidence}");
        }

        text.AppendLine();
        text.AppendLine($"Mods ({context.Mods.Count}):");

        foreach (var mod in context.Mods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine("  " + mod);
        }

        if (crashReportLines is { Count: > 0 })
        {
            text.AppendLine();
            text.AppendLine("--- crash report ---");

            foreach (var line in crashReportLines.Take(80))
            {
                text.AppendLine(line);
            }
        }

        text.AppendLine();
        text.AppendLine("--- log ---");

        foreach (var line in Excerpt(logLines))
        {
            text.AppendLine(line);
        }

        // The report is pasted into chats: the home folder, and the Windows user name in
        // it, stay behind. Every path in a log runs through it.
        return PathMask.Mask(text.ToString().TrimEnd());
    }

    /// <summary>
    /// The lines worth reading: every line with an exception from the body of the log,
    /// and the tail in full. A whole modded log is megabytes, which no chat accepts.
    /// </summary>
    public static IReadOnlyList<string> Excerpt(IReadOnlyList<string> lines)
    {
        if (lines.Count <= TailLines)
        {
            return lines;
        }

        var tailStart = lines.Count - TailLines;

        var errors = new List<string>();

        for (var i = 0; i < tailStart && errors.Count < ErrorLines; i++)
        {
            var line = lines[i];

            if (line.Contains("Exception", StringComparison.Ordinal) ||
                line.Contains("Error", StringComparison.Ordinal) ||
                line.Contains("Caused by", StringComparison.Ordinal) ||
                line.Contains("/FATAL", StringComparison.Ordinal))
            {
                errors.Add(line);
            }
        }

        var result = new List<string>(errors.Count + TailLines + 2);
        result.AddRange(errors);

        if (errors.Count > 0)
        {
            result.Add("…");
        }

        for (var i = tailStart; i < lines.Count; i++)
        {
            result.Add(lines[i]);
        }

        return result;
    }

    public static string Describe(CrashDiagnosis diagnosis) => diagnosis.Cause switch
    {
        CrashCause.MissingDependency => $"missing dependency: {diagnosis.Subject} needs {diagnosis.Detail}",
        CrashCause.ModForOtherVersion => $"mod for another version: {diagnosis.Subject}" + (diagnosis.Detail is null ? string.Empty : $" ({diagnosis.Detail})"),
        CrashCause.IncompatibleMods => $"incompatible mods: {diagnosis.Subject} and {diagnosis.Detail}",
        CrashCause.DuplicateMod => ("duplicate mod " + diagnosis.Subject).TrimEnd(),
        CrashCause.MixinFailure => $"mixin failure in {diagnosis.Subject ?? "a mod"}",
        CrashCause.OutOfMemory => "out of memory",
        CrashCause.SystemMemory => "the computer could not give Java the memory asked for",
        CrashCause.LoaderTooOld => $"loader too old: {diagnosis.Subject} needs {diagnosis.Detail ?? "a newer one"}",
        CrashCause.JavaTooOld => $"Java too old (needs Java {diagnosis.Detail ?? "?"})",
        CrashCause.Graphics => "graphics driver",
        CrashCause.BrokenInstallation => ("broken game file " + diagnosis.Subject).TrimEnd(),
        CrashCause.DiskFull => "disk full",
        CrashCause.JavaMissing => "Java was not started: the executable is missing",
        CrashCause.JavaBlocked => "Java was not started: access denied",
        CrashCause.JavaBroken => ("damaged Java: " + diagnosis.Subject).TrimEnd(' ', ':'),
        CrashCause.JavaOption => $"Java does not know the option {diagnosis.Subject}",
        CrashCause.JavaMemory => ("Java refused the memory setting " + diagnosis.Subject).TrimEnd(),
        CrashCause.JavaNotStarted => ("Java did not start: " + diagnosis.Subject).TrimEnd(' ', ':'),
        CrashCause.WindowsError => $"stopped by Windows with {diagnosis.Subject} ({diagnosis.Detail})",
        _ => "unknown"
    };

    private static string OperatingSystem()
        => System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();
}
