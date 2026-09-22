using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

/// <summary>
/// What to say after the game dies. The log knows why; the player used to get a code and
/// a button that opened two thousand lines. Now the cause is named, a fix is offered when
/// there is a one-click one, and the whole story fits on the clipboard for whoever helps.
/// </summary>
public partial class MainWindowViewModel
{
    private CrashDiagnosis _crash = CrashDiagnosis.None;
    private IReadOnlyList<string> _crashLogLines = Array.Empty<string>();
    private IReadOnlyList<string> _crashReportLines = Array.Empty<string>();
    private int _crashExitCode;

    /// <summary>The cause in one sentence; empty when the log gave nothing away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashDiagnosis))]
    private string _crashDiagnosisTitle = string.Empty;

    /// <summary>What to do about it.</summary>
    [ObservableProperty]
    private string _crashDiagnosisAdvice = string.Empty;

    /// <summary>The button that applies the fix, when a fix is one click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashFix))]
    private string _crashFixLabel = string.Empty;

    public bool HasCrashDiagnosis => CrashDiagnosisTitle.Length > 0;

    public bool HasCrashFix => CrashFixLabel.Length > 0;

    /// <summary>The crash report the game wrote, when it managed to write one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashReportFile))]
    private string? _crashReportPath;

    public bool HasCrashReportFile => !string.IsNullOrEmpty(CrashReportPath) && File.Exists(CrashReportPath);

    /// <summary>Reads the log of the run that just ended and names the cause.</summary>
    private void AnalyzeCrash(int exitCode, string gameDirectory)
    {
        _crashExitCode = exitCode;
        _crashLogLines = ReadLines(LastGameLogPath, 20000);

        // The game's own crash report has the stack trace and the mod list in one place;
        // the launcher log only has what went to the console.
        CrashReportPath = CrashAnalyzer.FindCrashReportPath(_crashLogLines) ?? NewestCrashReport(gameDirectory);
        _crashReportLines = ReadLines(CrashReportPath, 400);

        _crash = CrashAnalyzer.Analyze(_crashReportLines.Concat(_crashLogLines));

        CrashDiagnosisTitle = DiagnosisTitle(_crash);
        CrashDiagnosisAdvice = DiagnosisAdvice(_crash);
        CrashFixLabel = FixLabel(_crash);

        AppendConsole($"[crash] {CrashReport.Describe(_crash)}");
        ReportCrash(exitCode);
    }

    /// <summary>
    /// Anonymous, like the launch ping, and with the same switch. Only a mod name goes
    /// along as the subject: for a broken file the subject is a path, and a path has the
    /// player's Windows user name in it.
    /// </summary>
    private void ReportCrash(int exitCode)
    {
        if (!UsageStats || !_stats.IsConfigured)
        {
            return;
        }

        var subject = _crash.Cause is CrashCause.MissingDependency or CrashCause.ModForOtherVersion
            or CrashCause.IncompatibleMods or CrashCause.DuplicateMod or CrashCause.MixinFailure
            ? Truncate(_crash.Subject, 60)
            : null;

        _ = _usage.ReportCrashAsync(
            _stats.StatsUrl,
            _installId,
            _updates.CurrentVersion ?? "dev",
            _crash.Cause.ToString(),
            subject,
            SelectedInstance?.VersionId,
            SelectedInstance?.Loader.ToString() ?? "Vanilla",
            exitCode);
    }

    private static string? Truncate(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];

    private void ClearCrashDiagnosis()
    {
        _crash = CrashDiagnosis.None;
        _crashLogLines = Array.Empty<string>();
        _crashReportLines = Array.Empty<string>();
        CrashReportPath = null;
        CrashDiagnosisTitle = string.Empty;
        CrashDiagnosisAdvice = string.Empty;
        CrashFixLabel = string.Empty;
    }

    private string DiagnosisTitle(CrashDiagnosis d) => d.Cause switch
    {
        CrashCause.MissingDependency => Localize("Crash_MissingDependency", "\"{0}\" needs \"{1}\", which is not in the build", d.Subject, d.Detail),
        CrashCause.ModForOtherVersion => Localize("Crash_ModForOtherVersion", "\"{0}\" is made for a different game or loader version", d.Subject),
        CrashCause.IncompatibleMods => Localize("Crash_IncompatibleMods", "\"{0}\" does not work together with \"{1}\"", d.Subject, d.Detail),
        CrashCause.DuplicateMod => d.Subject is null
            ? Localize("Crash_DuplicateModPlain", "The same mod is in the mods folder twice")
            : Localize("Crash_DuplicateMod", "The mod \"{0}\" is in the mods folder twice", d.Subject),
        CrashCause.MixinFailure => d.Subject is null
            ? Localize("Crash_MixinPlain", "A mod failed to patch the game - most likely it is for another version")
            : Localize("Crash_Mixin", "\"{0}\" failed to patch the game - most likely it is for another version", d.Subject),
        CrashCause.OutOfMemory => Localize("Crash_OutOfMemory", "The game ran out of memory"),
        CrashCause.JavaTooOld => d.Detail is null
            ? Localize("Crash_JavaTooOldPlain", "The Java that ran the game is too old for it")
            : Localize("Crash_JavaTooOld", "The game needs Java {0}, an older one was used", d.Detail),
        CrashCause.Graphics => Localize("Crash_Graphics", "The graphics driver could not open the game window"),
        CrashCause.BrokenInstallation => Localize("Crash_BrokenInstallation", "A game file is missing or damaged"),
        CrashCause.DiskFull => Localize("Crash_DiskFull", "The disk is full"),
        _ => string.Empty
    };

    private string DiagnosisAdvice(CrashDiagnosis d) => d.Cause switch
    {
        CrashCause.MissingDependency => Localize("Crash_MissingDependencyAdvice", "Add it from the \"Add mods\" tab, or remove \"{0}\".", d.Subject),
        CrashCause.ModForOtherVersion => Localize("Crash_ModForOtherVersionAdvice", "Switch it off in the build's mods, or install a version for {0}.", SelectedInstance?.VersionId ?? "?"),
        CrashCause.IncompatibleMods => Localize("Crash_IncompatibleModsAdvice", "Switch one of them off in the build's mods."),
        CrashCause.DuplicateMod => Localize("Crash_DuplicateModAdvice", "Delete the older file in the mods folder."),
        CrashCause.MixinFailure => Localize("Crash_MixinAdvice", "Switch it off in the build's mods, or install a version for {0}.", SelectedInstance?.VersionId ?? "?"),
        CrashCause.OutOfMemory => Localize("Crash_OutOfMemoryAdvice", "Give the game more memory in the settings, or remove heavy mods."),
        CrashCause.JavaTooOld => Localize("Crash_JavaTooOldAdvice", "Let the launcher pick Java itself: it downloads the right one."),
        CrashCause.Graphics => Localize("Crash_GraphicsAdvice", "Update the graphics driver. On a laptop, run the game on the discrete card."),
        CrashCause.BrokenInstallation => Localize("Crash_BrokenInstallationAdvice", "Re-download the game files: the launcher checks every file and replaces the bad ones."),
        CrashCause.DiskFull => Localize("Crash_DiskFullAdvice", "Free some space on the disk where the launcher keeps its files."),
        _ => Localize("Crash_UnknownAdvice", "Copy the report and send it to the server's support - it has everything they need.")
    };

    private string FixLabel(CrashDiagnosis d) => d.Cause switch
    {
        CrashCause.OutOfMemory when SuggestedMemoryAfterCrash() is { } more => Localize("Crash_FixMemory", "Give the game {0} MB", more),
        CrashCause.JavaTooOld when SelectedJavaChoice?.Path is { Length: > 0 } => Localize("Crash_FixJava", "Java: automatic"),
        CrashCause.BrokenInstallation when !ForceUpdate => Localize("Crash_FixReinstall", "Re-download the game files"),
        CrashCause.MissingDependency when d.Detail is { Length: > 0 } => Localize("Crash_FixFindMod", "Find \"{0}\" in the catalog", d.Detail),
        _ => string.Empty
    };

    /// <summary>A step up from the current allocation, within what the machine has.</summary>
    private int? SuggestedMemoryAfterCrash()
    {
        var current = (int)MaxMemoryMb;
        var candidate = Math.Max(RecommendedMemoryMb, current + 2048);
        candidate = Math.Min(candidate, MemorySliderMax);

        return candidate > current ? candidate : null;
    }

    [RelayCommand]
    private void ApplyCrashFix()
    {
        switch (_crash.Cause)
        {
            case CrashCause.OutOfMemory:
                if (SuggestedMemoryAfterCrash() is { } more)
                {
                    MaxMemoryMb = more;
                    Status = Localize("Crash_MemoryApplied", "Memory set to {0} MB - try again", more);
                }

                break;

            case CrashCause.JavaTooOld:
                SelectedJavaChoice = JavaChoices.FirstOrDefault();
                Status = Localize("Crash_JavaApplied", "Java is picked automatically now - try again");
                break;

            case CrashCause.BrokenInstallation:
                ForceUpdate = true;
                Status = Localize("Crash_ReinstallApplied", "The game files will be re-checked on the next launch - press Play");
                break;

            case CrashCause.MissingDependency:
                Section = ShellSection.Builds;
                BuildTab = BuildTab.Catalog;
                ModSearchQuery = _crash.Detail ?? string.Empty;
                _ = SearchModsAsync();
                break;
        }

        CrashFixLabel = string.Empty;
    }

    [RelayCommand]
    private async Task CopyCrashReportAsync()
    {
        try
        {
            var context = new CrashReportContext(
                _updates.CurrentVersion ?? "dev",
                SelectedInstance?.Name ?? "?",
                SelectedInstance?.VersionId,
                SelectedInstance?.Loader.ToString() ?? "Vanilla",
                SelectedInstance?.LoaderVersion,
                SelectedJavaChoice?.Path,
                (int)MaxMemoryMb,
                _crashExitCode,
                InstalledMods.Where(m => m.IsMod).Select(m => m.FileName).ToList());

            var report = CrashReport.Build(context, _crash, _crashLogLines, _crashReportLines);

            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
                ? window.Clipboard
                : null;

            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(report);
            Status = Localize("Crash_ReportCopied", "Report copied - paste it into the chat");
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Ui", "Something went wrong: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenCrashReport()
    {
        if (!HasCrashReportFile)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = CrashReportPath!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }

    private static IReadOnlyList<string> ReadLines(string? path, int max)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            // The file is shared with a writer that may not have closed it yet.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var lines = new List<string>();

            while (reader.ReadLine() is { } line && lines.Count < max)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The crash report written in the last few minutes, if the log did not name one.</summary>
    private static string? NewestCrashReport(string gameDirectory)
    {
        try
        {
            var folder = Path.Combine(gameDirectory, "crash-reports");

            if (!Directory.Exists(folder))
            {
                return null;
            }

            var newest = new DirectoryInfo(folder)
                .EnumerateFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            return newest is not null && DateTime.UtcNow - newest.LastWriteTimeUtc < TimeSpan.FromMinutes(10)
                ? newest.FullName
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
