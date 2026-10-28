using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>One line of the pre-launch check, with the fix it offers.</summary>
public sealed class BuildIssueItem
{
    public BuildIssueItem(BuildIssue issue)
    {
        Issue = issue;

        Title = issue.Kind switch
        {
            BuildIssueKind.MissingDependency => MainWindowViewModel.Localize("Check_Missing", "“{0}” needs “{1}”, which is not in the build", issue.Subject, issue.Detail),
            BuildIssueKind.DuplicateMod => MainWindowViewModel.Localize("Check_Duplicate", "“{0}” is in the build twice: {1} and {2}", issue.Subject, issue.FileName, issue.Detail),
            BuildIssueKind.WrongLoader => MainWindowViewModel.Localize("Check_WrongLoader", "“{0}” is a {1} mod and will not load here", issue.Subject, issue.Detail),

            // "Breaks fabric-api <0.144.3" is a request for a newer Fabric API, so the line
            // names the version in the build and the versions that do not work.
            BuildIssueKind.Incompatible or BuildIssueKind.Discouraged when issue.Range is not null && issue.OtherVersion is not null
                => MainWindowViewModel.Localize(
                    issue.Kind == BuildIssueKind.Incompatible ? "Check_IncompatibleRange" : "Check_DiscouragedRange",
                    "“{0}” does not work with “{1}” {2} (versions {3})",
                    issue.Subject, issue.Detail, issue.OtherVersion, issue.Range),
            BuildIssueKind.Incompatible => MainWindowViewModel.Localize("Check_Incompatible", "“{0}” does not work together with “{1}”", issue.Subject, issue.Detail),
            BuildIssueKind.Discouraged => MainWindowViewModel.Localize("Check_Discouraged", "“{0}” may misbehave together with “{1}”", issue.Subject, issue.Detail),
            BuildIssueKind.TwoRenderers => MainWindowViewModel.Localize("Check_TwoRenderers", "Two renderer mods at once: “{0}” and “{1}”. They usually get in each other's way - better keep one.", issue.Detail, issue.Subject),
            BuildIssueKind.WrongDependencyVersion => MainWindowViewModel.Localize(
                "Check_WrongDependencyVersion",
                "“{0}” needs “{1}” {2}, the build has {3}",
                issue.Subject, issue.Detail, issue.Range ?? "*", issue.OtherVersion ?? "?"),
            _ => MainWindowViewModel.Localize("Check_WrongVersion", "“{0}” is made for another game version: Minecraft {1}", issue.Subject, Readable(issue.Detail))
        };

        FixLabel = issue.Kind switch
        {
            BuildIssueKind.MissingDependency when issue.DisabledFileName is not null => MainWindowViewModel.Localize("Check_FixEnable", "Switch on"),
            BuildIssueKind.MissingDependency => MainWindowViewModel.Localize("Check_FixFind", "Find in the catalog"),
            BuildIssueKind.DuplicateMod => MainWindowViewModel.Localize("Check_FixDisableOld", "Switch off the older one"),

            // Either of the two would do, so the button says which one it takes.
            BuildIssueKind.TwoRenderers => MainWindowViewModel.Localize("Check_FixDisableNamed", "Switch off {0}", issue.Subject),
            _ => MainWindowViewModel.Localize("Check_FixDisable", "Switch off")
        };
    }

    public BuildIssue Issue { get; }

    /// <summary>"&gt;=26.3- &lt;=26.3 || ~26.3" reads as "&gt;=26.3 &lt;=26.3 or ~26.3".</summary>
    private static string? Readable(string? range)
        => range?.Replace(" || ", " " + MainWindowViewModel.Localize("Check_Or", "or") + " ").Replace("- ", " ").TrimEnd('-');

    public string Title { get; }

    public string FixLabel { get; }

    /// <summary>The other way round for a doubled mod: keep the older file, switch off the newer.</summary>
    public string AltFixLabel => MainWindowViewModel.Localize("Check_FixDisableNew", "Switch off the newer one");

    public bool HasAltFix => Issue.Kind == BuildIssueKind.DuplicateMod;

    public bool IsBlocking => Issue.IsBlocking;

    /// <summary>True when the fix is a switch on a file, not a trip to the catalog.</summary>
    public bool CanFixBySwitch => Issue.FixableBySwitch;

    /// <summary>"Do not show for this mod": only a soft warning can be put away.</summary>
    public bool CanDismiss => !Issue.IsBlocking;

    public string DismissLabel => MainWindowViewModel.Localize("Check_DismissForMod", "Do not show for this mod");
}

/// <summary>
/// The pre-launch check and safe mode. The check reads the jars' own metadata after every
/// change to the mods folder and says, before Play is pressed, what the loader will
/// refuse ("the game will not start") and, quietly, what may work incorrectly. Play is
/// never held back by it: the player can always start the game as it is. Safe mode
/// starts the game with only the server's mods, so a player can tell "my mod" from "the
/// game" in one launch.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Everything the check says that is on screen: what stops the game, then the soft warnings not put away.</summary>
    public ObservableCollection<BuildIssueItem> BuildIssues { get; } = new();

    /// <summary>"The game will not start": only what the loader itself refuses.</summary>
    public ObservableCollection<BuildIssueItem> HardBuildIssues { get; } = new();

    /// <summary>"May work incorrectly": a quiet line each, never a question, never in the way of Play.</summary>
    public ObservableCollection<BuildIssueItem> SoftBuildIssues { get; } = new();

    public bool HasHardBuildIssues => HardBuildIssues.Count > 0;

    public bool HasSoftBuildIssues => SoftBuildIssues.Count > 0;

    /// <summary>"May work incorrectly: 2 mods".</summary>
    [ObservableProperty]
    private string _softBuildCheckSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildIssues))]
    [NotifyPropertyChangedFor(nameof(HasBlockingBuildIssues))]
    private int _buildIssueCount;

    [ObservableProperty]
    private bool _isCheckingBuild;

    [ObservableProperty]
    private string _buildCheckSummary = string.Empty;

    /// <summary>Set for the launch that runs with only the server's mods; cleared when it ends.</summary>
    [ObservableProperty]
    private bool _isSafeMode;

    public bool HasBuildIssues => BuildIssueCount > 0;

    public bool HasBlockingBuildIssues => HardBuildIssues.Count > 0;

    /// <summary>Two or more problems that a switch can settle: one button does them all.</summary>
    public bool HasBulkBuildFix => HardBuildIssues.Count(i => i.CanFixBySwitch) >= 2;

    /// <summary>Two or more doubled mods: one button keeps every older file instead.</summary>
    public bool HasBulkKeepOld => HardBuildIssues.Count(i => i.HasAltFix) >= 2;

    /// <summary>Safe mode only makes sense when there is something of the player's own to leave out.</summary>
    public bool CanUseSafeMode => IsCatalogInstance && InstalledMods.Any(m => m.IsMod && m.Enabled && !m.IsCatalog);

    private int _buildCheckRun;

    /// <summary>True while a check is reading jars on a pool thread.</summary>
    private bool _buildCheckRunning;

    /// <summary>Asked for again while one was running: it starts when that one ends.</summary>
    private bool _buildCheckQueued;

    /// <summary>
    /// Runs off the UI thread, one at a time. A burst of changes - a start, an install of
    /// several mods - asked for a check each, and every one of them opened every jar of
    /// the build beside the others; now the burst is one check running and one waiting,
    /// and the one waiting sees the folder as it is when its turn comes.
    /// </summary>
    private void ScheduleBuildCheck()
    {
        if (_buildCheckRunning)
        {
            _buildCheckQueued = true;
            ++_buildCheckRun;
            IsCheckingBuild = true;
            return;
        }

        _buildCheckRunning = true;
        _buildCheckQueued = false;

        var run = ++_buildCheckRun;
        var directory = InstanceDirectory;
        var loader = SelectedInstance?.Loader ?? LoaderKind.Vanilla;
        var version = SelectedInstance?.VersionId;
        var loaderVersion = SelectedInstance?.LoaderVersion;
        var cache = MetadataCacheFor(SelectedInstance);
        var instance = SelectedInstance;
        var dismissed = instance?.DismissedWarnings?.ToList() ?? new List<DismissedBuildWarning>();

        IsCheckingBuild = true;

        _ = Task.Run(() =>
        {
            IReadOnlyList<BuildIssue> issues;
            var stale = new List<DismissedBuildWarning>();
            var hidden = 0;

            try
            {
                issues = BuildChecker.Check(directory, loader, version, loaderVersion, cache);

                // A warning the player put away stays away while its file is the same one.
                var modsDirectory = ModManager.ModsDirectory(directory);
                STlauncher.Core.Mods.ModFileStamp? StampOf(string fileName) => STlauncher.Core.Mods.ModFileStamp.Of(System.IO.Path.Combine(modsDirectory, fileName));

                var kept = dismissed.ToList();
                BuildWarningDismissals.Prune(kept, StampOf);
                stale = dismissed.Except(kept).ToList();

                var visible = issues.Where(i => !BuildWarningDismissals.IsDismissed(kept, i, StampOf)).ToList();
                hidden = issues.Count - visible.Count;
                issues = visible;
            }
            catch (Exception ex)
            {
                AppendConsole($"[check] failed: {ex.Message}");
                issues = Array.Empty<BuildIssue>();
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _buildCheckRunning = false;

                if (_buildCheckQueued)
                {
                    // Something changed while this one read: its answer is about a folder
                    // that is no longer so. The one that was waiting runs now.
                    ScheduleBuildCheck();
                    return;
                }

                if (run != _buildCheckRun)
                {
                    return;
                }

                // A file that changed since it was put away is looked at again.
                if (stale.Count > 0 && instance?.DismissedWarnings is { } stored)
                {
                    stored.RemoveAll(d => stale.Contains(d));
                    _instances.Save(instance);
                }

                ShowBuildIssues(issues);
                IsCheckingBuild = false;

                if (hidden > 0)
                {
                    AppendConsole($"[check] {hidden} warning(s) put away by the player are not shown");
                }
            });
        });
    }

    /// <summary>Fills the three lists from one check and words the two headings.</summary>
    private void ShowBuildIssues(IReadOnlyList<BuildIssue> issues)
    {
        BuildIssues.Clear();
        HardBuildIssues.Clear();
        SoftBuildIssues.Clear();

        foreach (var issue in issues.OrderBy(i => i.Severity))
        {
            var item = new BuildIssueItem(issue);
            BuildIssues.Add(item);
            (issue.IsBlocking ? HardBuildIssues : SoftBuildIssues).Add(item);
        }

        BuildIssueCount = BuildIssues.Count;

        BuildCheckSummary = HardBuildIssues.Count == 0
            ? string.Empty
            : Localize("Check_SummaryBlocking", "The game will not start: {0}", Plural(HardBuildIssues.Count, "Check_Problems"));

        SoftBuildCheckSummary = SoftBuildIssues.Count == 0
            ? string.Empty
            : Localize("Check_SummarySoft", "May work incorrectly: {0}", Plural(SoftBuildIssues.Count, "Check_Notes"));

        OnPropertyChanged(nameof(HasHardBuildIssues));
        OnPropertyChanged(nameof(HasSoftBuildIssues));
        OnPropertyChanged(nameof(HasBlockingBuildIssues));
        OnPropertyChanged(nameof(HasBulkBuildFix));
        OnPropertyChanged(nameof(HasBulkKeepOld));
        OnPropertyChanged(nameof(CanUseSafeMode));
    }

    /// <summary>
    /// "Do not show for this mod": the soft warnings about this file go away for this
    /// build, and come back by themselves when the file changes (an update, another copy).
    /// Kept in the build's own data.
    /// </summary>
    [RelayCommand]
    private void DismissBuildWarning(BuildIssueItem? item)
    {
        if (item is null || !item.CanDismiss || SelectedInstance is not { } instance)
        {
            return;
        }

        var path = System.IO.Path.Combine(ModManager.ModsDirectory(InstanceDirectory), item.Issue.FileName);

        if (STlauncher.Core.Mods.ModFileStamp.Of(path) is not { } stamp)
        {
            return;
        }

        instance.DismissedWarnings ??= new List<DismissedBuildWarning>();
        BuildWarningDismissals.Dismiss(instance.DismissedWarnings, item.Issue.FileName, stamp);
        _instances.Save(instance);

        AppendConsole($"[check] warnings about {item.Issue.FileName} put away until the file changes");

        var name = BuildWarningDismissals.Normalize(item.Issue.FileName);
        ShowBuildIssues(BuildIssues
            .Select(i => i.Issue)
            .Where(i => i.IsBlocking || !string.Equals(BuildWarningDismissals.Normalize(i.FileName), name, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    private static string Plural(int count, string keyBase)
    {
        var form = count % 10 == 1 && count % 100 != 11 ? "One"
            : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "Few"
            : "Many";

        return Localize(keyBase + form, "{0} problems", count);
    }

    [RelayCommand]
    private async Task ApplyBuildFixAsync(BuildIssueItem? item)
    {
        if (item is null)
        {
            return;
        }

        var issue = item.Issue;

        try
        {
            switch (issue.Kind)
            {
                case BuildIssueKind.MissingDependency when issue.DisabledFileName is not null:
                    ToggleModByFileName(issue.DisabledFileName);
                    break;

                case BuildIssueKind.MissingDependency:
                    Section = ShellSection.Builds;
                    BuildTab = BuildTab.Catalog;
                    SelectBrowserKind(ProjectTypes.Mod);
                    ModSearchQuery = issue.Detail ?? string.Empty;
                    await SearchModsAsync();
                    break;

                case BuildIssueKind.DuplicateMod:
                case BuildIssueKind.WrongLoader:
                case BuildIssueKind.WrongGameVersion:
                case BuildIssueKind.Incompatible:
                case BuildIssueKind.Discouraged:
                case BuildIssueKind.TwoRenderers:
                case BuildIssueKind.WrongDependencyVersion:
                    // For a conflict this is the mod that declared it, and only on the
                    // row's own button: "fix everything" leaves that choice to the player.
                    ToggleModByFileName(issue.FileName);
                    break;
            }
        }
        catch (Exception ex)
        {
            Status = Localize("Error_ToggleMod", "Failed to toggle the mod: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Switches off every file the check named: the older copies of doubled mods, the
    /// jars for the wrong loader or game version, and switches on the disabled ones a mod
    /// depends on. Nothing is deleted, so it is all reversible from the mod list.
    /// </summary>
    [RelayCommand]
    private void ApplyAllBuildFixes()
    {
        var fixes = HardBuildIssues.Where(i => i.CanFixBySwitch).Select(i => i.Issue).ToList();
        var done = ApplySwitchFixes(fixes);

        AppendConsole($"[check] {done} of {fixes.Count} problem(s) fixed with one click");
        Status = Localize("Check_FixedAll", "Fixed: {0} of {1}", done, fixes.Count);
    }

    /// <summary>Flips the switch each issue asks for and counts the ones that took.</summary>
    private int ApplySwitchFixes(IReadOnlyList<BuildIssue> fixes)
    {
        var done = 0;

        foreach (var issue in fixes)
        {
            try
            {
                var target = issue.Kind == BuildIssueKind.MissingDependency ? issue.DisabledFileName! : issue.FileName;

                if (ToggleModByFileName(target))
                {
                    done++;
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"[check] could not fix {issue.FileName}: {ex.Message}");
            }
        }

        return done;
    }

    /// <summary>The player prefers the version they had: the newer file is switched off instead.</summary>
    [RelayCommand]
    private void ApplyBuildAltFix(BuildIssueItem? item)
    {
        if (item is null || !item.HasAltFix || item.Issue.Detail is null)
        {
            return;
        }

        try
        {
            ToggleModByFileName(item.Issue.Detail);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_ToggleMod", "Failed to toggle the mod: {0}", ex.Message);
        }
    }

    /// <summary>For every doubled mod, keeps the older file and switches off the newer one.</summary>
    [RelayCommand]
    private void KeepOlderVersions()
    {
        var pairs = HardBuildIssues.Where(i => i.HasAltFix && i.Issue.Detail is not null).Select(i => i.Issue).ToList();
        var done = 0;

        foreach (var issue in pairs)
        {
            try
            {
                if (ToggleModByFileName(issue.Detail!))
                {
                    done++;
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"[check] could not switch off {issue.Detail}: {ex.Message}");
            }
        }

        AppendConsole($"[check] kept the older file for {done} of {pairs.Count} doubled mod(s)");
        Status = Localize("Check_FixedAll", "Fixed: {0} of {1}", done, pairs.Count);
    }

    private bool ToggleModByFileName(string fileName)
    {
        var mod = InstalledMods.FirstOrDefault(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (mod is null)
        {
            return false;
        }

        ToggleMod(mod);
        return true;
    }

    [RelayCommand]
    private void OpenBuildIssues()
    {
        Section = ShellSection.Builds;
        BuildTab = BuildTab.Mods;
    }

    // ===================== Safe mode =====================

    /// <summary>
    /// Switches the player's own mods off, starts the game, and switches them back on when
    /// it exits. The list is saved first: if the launcher closes with the game, the next
    /// start restores them.
    /// </summary>
    [RelayCommand]
    private async Task PlaySafeAsync()
    {
        if (IsBusy || IsGameRunning || SelectedInstance is null)
        {
            return;
        }

        var own = InstalledMods.Where(m => m.IsMod && m.Enabled && !m.IsCatalog).ToList();

        if (own.Count == 0)
        {
            await StartAsync(joinServer: false);
            return;
        }

        var restore = new List<string>();

        try
        {
            foreach (var mod in own)
            {
                if (_mods.SetEnabled(mod.Path, false))
                {
                    restore.Add(mod.FileName + ".disabled");
                }
            }

            _safeModeRestore = restore;
            PersistSettings();
            RefreshMods();
            IsSafeMode = true;
            Status = Localize("Safe_Starting", "Starting with the server's mods only: {0} of yours switched off", own.Count);

            await StartAsync(joinServer: false);
        }
        finally
        {
            RestoreSafeMode();
        }
    }

    private List<string> _safeModeRestore = new();

    /// <summary>Puts the player's mods back; also called on startup for a list left over from last time.</summary>
    private void RestoreSafeMode()
    {
        if (_safeModeRestore.Count == 0)
        {
            IsSafeMode = false;
            return;
        }

        var directory = ModManager.ModsDirectory(InstanceDirectory);

        foreach (var fileName in _safeModeRestore)
        {
            try
            {
                _mods.SetEnabled(System.IO.Path.Combine(directory, fileName), true);
            }
            catch (Exception ex)
            {
                AppendConsole($"[safe mode] could not restore {fileName}: {ex.Message}");
            }
        }

        _safeModeRestore = new List<string>();
        IsSafeMode = false;
        PersistSettings();
        RefreshMods();
    }
}
