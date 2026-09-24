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
            _ => MainWindowViewModel.Localize("Check_WrongVersion", "“{0}” was made for {1}, not this game version", issue.Subject, issue.Detail)
        };

        FixLabel = issue.Kind switch
        {
            BuildIssueKind.MissingDependency when issue.DisabledFileName is not null => MainWindowViewModel.Localize("Check_FixEnable", "Switch on"),
            BuildIssueKind.MissingDependency => MainWindowViewModel.Localize("Check_FixFind", "Find in the catalog"),
            BuildIssueKind.DuplicateMod => MainWindowViewModel.Localize("Check_FixDisableOld", "Switch off the older one"),
            _ => MainWindowViewModel.Localize("Check_FixDisable", "Switch off")
        };
    }

    public BuildIssue Issue { get; }

    public string Title { get; }

    public string FixLabel { get; }

    /// <summary>The other way round for a doubled mod: keep the older file, switch off the newer.</summary>
    public string AltFixLabel => MainWindowViewModel.Localize("Check_FixDisableNew", "Switch off the newer one");

    public bool HasAltFix => Issue.Kind == BuildIssueKind.DuplicateMod;

    public bool IsBlocking => Issue.IsBlocking;

    /// <summary>True when the fix is a switch on a file, not a trip to the catalog.</summary>
    public bool CanFixBySwitch => Issue.Kind != BuildIssueKind.MissingDependency || Issue.DisabledFileName is not null;
}

/// <summary>
/// The pre-launch check and safe mode. The check reads the jars' own metadata after every
/// change to the mods folder and says what would stop the game before Play is pressed:
/// the same verdicts the crash analyser gives after the fact, minus the crash. Safe mode
/// starts the game with only the server's mods, so a player can tell "my mod" from "the
/// game" in one launch.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<BuildIssueItem> BuildIssues { get; } = new();

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

    public bool HasBlockingBuildIssues => BuildIssues.Any(i => i.IsBlocking);

    /// <summary>Two or more problems that a switch can settle: one button does them all.</summary>
    public bool HasBulkBuildFix => BuildIssues.Count(i => i.CanFixBySwitch) >= 2;

    /// <summary>Two or more doubled mods: one button keeps every older file instead.</summary>
    public bool HasBulkKeepOld => BuildIssues.Count(i => i.HasAltFix) >= 2;

    /// <summary>Safe mode only makes sense when there is something of the player's own to leave out.</summary>
    public bool CanUseSafeMode => IsCatalogInstance && InstalledMods.Any(m => m.IsMod && m.Enabled && !m.IsCatalog);

    private int _buildCheckRun;

    /// <summary>Runs off the UI thread; the newest run wins when several overlap.</summary>
    private void ScheduleBuildCheck()
    {
        var run = ++_buildCheckRun;
        var directory = InstanceDirectory;
        var loader = SelectedInstance?.Loader ?? LoaderKind.Vanilla;
        var version = SelectedInstance?.VersionId;

        IsCheckingBuild = true;

        _ = Task.Run(() =>
        {
            IReadOnlyList<BuildIssue> issues;

            try
            {
                issues = BuildChecker.Check(directory, loader, version);
            }
            catch (Exception ex)
            {
                AppendConsole($"[check] failed: {ex.Message}");
                issues = Array.Empty<BuildIssue>();
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (run != _buildCheckRun)
                {
                    return;
                }

                BuildIssues.Clear();

                foreach (var issue in issues)
                {
                    BuildIssues.Add(new BuildIssueItem(issue));
                }

                BuildIssueCount = BuildIssues.Count;
                IsCheckingBuild = false;

                var blocking = BuildIssues.Count(i => i.IsBlocking);
                BuildCheckSummary = BuildIssues.Count == 0
                    ? string.Empty
                    : blocking > 0
                        ? Localize("Check_SummaryBlocking", "The game will not start: {0}", Plural(blocking, "Check_Problems"))
                        : Localize("Check_SummaryWarnings", "May not start: {0}", Plural(BuildIssues.Count, "Check_Problems"));

                OnPropertyChanged(nameof(HasBlockingBuildIssues));
                OnPropertyChanged(nameof(HasBulkBuildFix));
                OnPropertyChanged(nameof(HasBulkKeepOld));
                OnPropertyChanged(nameof(CanUseSafeMode));
            });
        });
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
        var fixes = BuildIssues.Where(i => i.CanFixBySwitch).Select(i => i.Issue).ToList();
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

        AppendConsole($"[check] {done} of {fixes.Count} problem(s) fixed with one click");
        Status = Localize("Check_FixedAll", "Fixed: {0} of {1}", done, fixes.Count);
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
        var pairs = BuildIssues.Where(i => i.HasAltFix && i.Issue.Detail is not null).Select(i => i.Issue).ToList();
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
