using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// A file in the build's mods folder, with what the launcher knows about it: where it
/// came from, and whether Modrinth has something newer.
/// </summary>
public partial class InstalledModItem : ObservableObject
{
    public InstalledModItem(InstalledMod mod, InstalledModRecord? record)
    {
        Mod = mod;
        Record = record;
        IsCatalog = record?.Source == ModSource.Catalog;
        IsMod = string.Equals(mod.Folder, ModManager.ModsFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A jar in mods/, as opposed to a pack listed elsewhere.</summary>
    public bool IsMod { get; }

    public InstalledMod Mod { get; }

    /// <summary>Provenance, when the launcher installed the file itself.</summary>
    public InstalledModRecord? Record { get; }

    public string FileName => Mod.FileName;

    /// <summary>
    /// The catalog's or Modrinth's title when the launcher installed the file; otherwise
    /// the title the jar carries for itself; the file name only when neither exists. A
    /// record made for a dropped or imported file holds the file name, which is no title.
    /// </summary>
    public string DisplayName
        => Record is { Name.Length: > 0, Source: ModSource.Catalog or ModSource.Modrinth or ModSource.CurseForge } titled ? titled.Name!
            : MetaName is { Length: > 0 } meta ? meta
            : Record?.Name is { Length: > 0 } name ? name
            : Mod.DisplayName;

    /// <summary>The mod's title from its own metadata, read after the list is on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _metaName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLabel))]
    [NotifyPropertyChangedFor(nameof(MetaLine))]
    private string? _metaVersion;

    /// <summary>The mod's own icon from inside the jar; null shows the placeholder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Avalonia.Media.Imaging.Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    /// <summary>The jar's own version; the record's pin only when the jar has none ("fabric-1.21.11-25.3.12" is an id, not a number).</summary>
    public string VersionLabel => MetaVersion is { Length: > 0 } own ? own : Record?.Version ?? string.Empty;

    /// <summary>"1.10.5 · 1,5 MB" under the title.</summary>
    public string MetaLine
    {
        get
        {
            var size = Converters.FileSizeConverter.Instance.Convert(Size, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? string.Empty;
            return VersionLabel.Length > 0 ? VersionLabel + " · " + size : size;
        }
    }

    /// <summary>When the file appeared in the folder, for sorting by date added.</summary>
    public DateTime Added { get; init; }

    /// <summary>Ticked in selection mode, for the actions that take several mods at once.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public string Path => Mod.Path;

    public bool Enabled => Mod.Enabled;

    public long Size => Mod.Size;

    /// <summary>
    /// Pinned by the server catalog. The catalog decides its version, so no update is
    /// offered here: the server owner bumps it for everyone by editing the catalog.
    /// </summary>
    public bool IsCatalog { get; }

    /// <summary>A newer Modrinth version for this build, after a check. Null means none, or not checked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateLabel))]
    private ModVersion? _update;

    public bool HasUpdate => Update is not null && !IsUpdating;

    public string UpdateLabel => Update is null ? string.Empty : "↑ " + Update.VersionNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private bool _isUpdating;

    /// <summary>Modrinth project id, learnt from the file's hash during a check.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Filtered out by the search box; the file is still in the build.</summary>
    [ObservableProperty]
    private bool _isHidden;

    /// <summary>Just added: outlined in the list for a while, so it can be found without reading every row.</summary>
    [ObservableProperty]
    private bool _isNew;

    /// <summary>The file this one replaced at an update, while the launcher still keeps it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    [NotifyPropertyChangedFor(nameof(PreviousLabel))]
    private ReplacedModFile? _previous;

    public bool HasPrevious => Previous is not null;

    /// <summary>"Bring back the previous version (0.5.8)": the menu entry names what comes back.</summary>
    public string PreviousLabel
    {
        get
        {
            if (Previous is null)
            {
                return MainWindowViewModel.Localize("Mods_NoPrevious", "No previous version: this mod has not been updated yet");
            }

            return MainWindowViewModel.Localize(
                "Mods_RestorePrevious",
                "Bring back the previous version ({0})",
                Previous.Label is { Length: > 0 } label ? label : Previous.FileName);
        }
    }

    /// <summary>
    /// The row's menu opens in a popup, out of reach of a binding to the page: the command
    /// is handed to the row itself.
    /// </summary>
    public System.Windows.Input.ICommand? RestorePreviousCommand { get; set; }
}

/// <summary>
/// Updates for the mods the player added. The launcher only ever tells: nothing is
/// bumped by itself, because a mod update can break a world, and that is the player's
/// call. Catalog mods are left out - the server catalog pins their versions.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool _isCheckingModUpdates;

    /// <summary>"3 updates available" / "All mods are up to date", after a check.</summary>
    [ObservableProperty]
    private string _modUpdatesSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModUpdates))]
    [NotifyPropertyChangedFor(nameof(UpdateAllLabel))]
    private int _modUpdateCount;

    public bool HasModUpdates => ModUpdateCount > 0;

    public string UpdateAllLabel => Localize("Mods_UpdateAll", "Update all ({0})", ModUpdateCount);

    /// <summary>What the check found, kept across list refreshes by file name.</summary>
    private readonly Dictionary<string, (ModVersion Version, string? ProjectId)> _knownModUpdates =
        new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task CheckModUpdatesAsync()
    {
        if (IsCheckingModUpdates || SelectedInstance is null)
        {
            return;
        }

        if (!IsBuildConfigured)
        {
            ModUpdatesSummary = Localize("Mods_UpdatesNeedBuild", "Pick the game version and loader first");
            return;
        }

        var candidates = InstalledMods.Where(m => m.IsMod && !m.IsCatalog && m.Enabled).ToList();

        if (candidates.Count == 0)
        {
            ModUpdatesSummary = InstalledMods.Count == 0
                ? Localize("Mods_UpdatesNothing", "No mods to check")
                : Localize("Mods_UpdatesOnlyCatalog", "All mods here come from the server catalog - the server updates them");
            return;
        }

        try
        {
            IsCheckingModUpdates = true;
            ModUpdatesSummary = Localize("Mods_UpdatesChecking", "Checking {0} mod(s) on Modrinth…", candidates.Count);

            var gameVersion = SelectedVersion!.Id;
            var loader = SelectedLoader;
            var instance = SelectedInstance!;

            // Hashing a folder of jars is disk work; keep it off the UI thread.
            var hashes = await Task.Run(() => candidates
                .Select(m => (Item: m, Hash: ModManager.TryComputeSha1(m.Path)))
                .Where(p => p.Hash is not null)
                .ToDictionary(p => p.Hash!, p => p.Item, StringComparer.OrdinalIgnoreCase));

            var current = await _modrinth.GetVersionsByHashesAsync(hashes.Keys);
            var latest = await _modrinth.GetLatestVersionsAsync(hashes.Keys, gameVersion, loader);

            _knownModUpdates.Clear();
            var found = 0;

            foreach (var (hash, item) in hashes)
            {
                current.TryGetValue(hash, out var installed);
                item.ProjectId = installed?.ProjectId;

                if (!latest.TryGetValue(hash, out var newest))
                {
                    item.Update = null;
                    continue;
                }

                // Same version id means the file on disk is already the newest for this
                // build. A file Modrinth does not know at all cannot be compared, so it
                // is only flagged when the newest file has a different name.
                var isNewer = installed is null
                    ? !string.Equals(newest.PrimaryFile?.FileName, item.FileName, StringComparison.OrdinalIgnoreCase)
                    : !string.Equals(newest.Id, installed.Id, StringComparison.Ordinal);

                if (isNewer)
                {
                    item.Update = newest;
                    item.ProjectId ??= newest.ProjectId;
                    _knownModUpdates[item.FileName] = (newest, item.ProjectId);
                    found++;
                }
                else
                {
                    item.Update = null;
                }
            }

            ModUpdateCount = found;
            ModUpdatesSummary = found == 0
                ? Localize("Mods_UpdatesNone", "All mods are up to date")
                : Localize("Mods_UpdatesFound", "Updates available: {0}. Nothing is installed until you say so.", found);

            var unknown = hashes.Count - current.Count;

            if (unknown > 0)
            {
                AppendConsole($"[mods] {unknown} file(s) are not on Modrinth and were not checked");
            }

            // The same answers say what the jars dropped in by hand are. Not waited for:
            // the check is done, and this only fills in the launcher's own records.
            _ = IdentifyDroppedModsAsync(instance, hashes, current);
        }
        catch (Exception ex)
        {
            ModUpdatesSummary = Localize("Mods_UpdatesFailed", "Could not check for updates: {0}", ex.Message);
            AppendConsole($"[mods] update check failed: {ex}");
        }
        finally
        {
            IsCheckingModUpdates = false;
        }
    }

    /// <summary>Puts the newer version in place of the file, with anything it now needs.</summary>
    [RelayCommand]
    private async Task UpdateModAsync(InstalledModItem? item)
    {
        if (item is null)
        {
            return;
        }

        var name = item.DisplayName;

        if (await UpdateModIntoAsync(item, CurrentInstallTarget()) is { } failure)
        {
            Status = Localize("Mods_UpdateFailedKept", "{0} was not updated and stays as it was. {1}", name, DescribeFailure(failure));

            // The check adds which sites do not answer; it has nothing to add to a cause
            // that is not about reaching them.
            _ = ExplainDownloadFailureAsync(Localize("Net_WhatMod", "the mod"), failure);
        }
    }

    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        var pending = InstalledMods.Where(m => m.HasUpdate).ToList();

        if (pending.Count == 0)
        {
            return;
        }

        // The list was read from one build: every update goes into that one, even if
        // another build is opened before the last of them is done.
        var target = CurrentInstallTarget();
        var failed = new List<string>();

        foreach (var item in pending)
        {
            var name = item.DisplayName;

            // One mod that does not come must not stop the rest, and must not go unsaid.
            if (await UpdateModIntoAsync(item, target) is { } failure)
            {
                var reason = DescribeFailure(failure);
                failed.Add(name + ": " + reason);
                AppendConsole($"[mods] {name} was not updated, left as it was: {reason}");
            }
        }

        Status = failed.Count == 0
            ? Localize("Mods_UpdateAllDone", "Updated: {0}", pending.Count)
            : Localize(
                "Mods_UpdateAllPartly",
                "Updated {0} of {1}. Left as they were: {2}",
                pending.Count - failed.Count,
                pending.Count,
                string.Join("; ", failed));
    }

    /// <summary>
    /// The one replacer, told what the launcher knows about a running game. Today that is
    /// "a game is running", whichever build it is: the per-build check
    /// (Core/Launch/RunningGames) plugs in here, in this one line.
    /// </summary>
    private ModFileReplacer Replacer
    {
        get
        {
            _mods.Replacer.IsGameRunning ??= _ => IsGameRunning;
            return _mods.Replacer;
        }
    }

    /// <returns>What went wrong, or null. The mod is as it was whenever this is not null.</returns>
    private async Task<Exception?> UpdateModIntoAsync(InstalledModItem? item, InstallTarget target)
    {
        if (item?.Update is null || item.IsUpdating || target.Instance is null)
        {
            return null;
        }

        try
        {
            item.IsUpdating = true;
            await ApplyModUpdateAsync(item, target);
            return null;
        }
        catch (Exception ex)
        {
            AppendConsole(ex.ToString());
            item.IsUpdating = false;
            return ex;
        }
    }

    /// <summary>
    /// The new version takes the old one's place, and nothing in the build moves until
    /// every file of the update is downloaded and checked. A mod that now needs another
    /// one gets it in the same step; if any of the files does not come, none is placed.
    /// </summary>
    private async Task ApplyModUpdateAsync(InstalledModItem item, InstallTarget target)
    {
        var version = item.Update!;
        var oldFile = item.FileName;
        var instance = target.Instance!;

        var project = item.ProjectId is { Length: > 0 } id
            ? await _modrinth.GetProjectAsync(id)
            : null;

        var slug = project?.Slug ?? item.Record?.Id ?? item.ProjectId ?? string.Empty;
        var title = project?.Title ?? item.DisplayName;

        Status = Localize("Mods_Updating", "Updating {0} to {1}…", title, version.VersionNumber);

        var plan = await ResolveInstallPlanAsync(version, slug, title, project?.IconUrl ?? item.Record?.IconUrl, ProjectTypes.Mod, target);

        if (plan.Failure is { } failure)
        {
            throw new InvalidOperationException(failure);
        }

        if (plan.Root.State != ModPlanState.Install || plan.Root.File is not { } rootFile)
        {
            throw new InvalidOperationException(Localize("Status_NoCompatibleFile", "No compatible file for this version and loader"));
        }

        if (plan.Missing.FirstOrDefault() is { } missing)
        {
            throw new InvalidOperationException(
                Localize("Error_DependencyMissing", "{0} needs {1}, which has no version for this build", missing.RequiredBy ?? title, missing.Title));
        }

        _modsLeftToThePlayer.Clear();
        await MaybeBackupAsync(BackupTrigger.BeforeModChange, instance);

        var replacer = Replacer;
        var staged = new List<(ModPlanItem Item, StagedModFile File)>();
        ModReplaceResult result;

        try
        {
            // First everything is fetched and checked, under hidden names. What the new
            // version requires comes before it, as in a fresh install.
            foreach (var needed in plan.Required)
            {
                if (needed.State == ModPlanState.Blocked)
                {
                    LeaveToThePlayer(needed);
                    continue;
                }

                if (needed.State != ModPlanState.Install || needed.File is not { } file ||
                    (needed.Slug.Length > 0 && IsProjectInstalled(instance, needed.Slug)))
                {
                    continue;
                }

                Status = Localize("Status_ResolvingDependency", "Adding {0}, which {1} needs…", needed.Title, needed.RequiredBy ?? title);

                staged.Add((needed, await replacer.StageAsync(
                    new ModReplaceRequest(target.Directory, ProjectTypes.FolderFor(needed.ProjectType), null, file.FileName, file.Url, file.Sha1, file.Size)
                    {
                        Sha512 = file.Sha512
                    })));
            }

            Status = Localize("Mods_Updating", "Updating {0} to {1}…", title, version.VersionNumber);

            var root = await replacer.StageAsync(
                new ModReplaceRequest(target.Directory, ModManager.ModsFolderName, item.Path, rootFile.FileName, rootFile.Url, rootFile.Sha1, rootFile.Size)
                {
                    Sha512 = rootFile.Sha512,
                    Key = item.ProjectId ?? (slug.Length > 0 ? slug : null),
                    OldLabel = item.VersionLabel
                });

            staged.Add((plan.Root, root));

            // Then the renames, which is all that is left: local and quick.
            foreach (var (_, file) in staged)
            {
                file.Commit();
            }

            result = root.Result!;
        }
        catch
        {
            // Back to exactly what was there: placed files are taken out again, the old
            // one returns to its name, hidden ones are removed.
            for (var i = staged.Count - 1; i >= 0; i--)
            {
                staged[i].File.Undo();
            }

            throw;
        }

        // The record of the old file leaves with it; the new one's is written below.
        if (!string.Equals(result.NewFileName, oldFile, StringComparison.OrdinalIgnoreCase))
        {
            instance.InstalledMods.RemoveAll(m => string.Equals(m.FileName, oldFile, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var (planItem, file) in staged)
        {
            var isRoot = ReferenceEquals(planItem, plan.Root);

            RecordPlanItem(
                planItem,
                planItem.File!,
                target,
                file.Result!.NewFileName,
                disabledByUser: isRoot && !result.Enabled && (item.Record?.DisabledByUser ?? true));
        }

        if (result.Previous is { } kept)
        {
            AppendConsole($"[mods] {oldFile} moved to {kept.StoredPath}: replaced by {result.NewFileName}, kept for going back");
        }

        _knownModUpdates.Remove(oldFile);
        ModUpdateCount = Math.Max(0, ModUpdateCount - 1);

        if (IsSelectedBuild(target))
        {
            RefreshMods();
        }

        ReportModsLeftToThePlayer(title);

        Status = result.Previous is not null
            ? Localize("Mods_UpdatedKeptPrevious", "{0} updated to {1}. The previous version is kept: right-click the mod to bring it back.", title, version.VersionNumber)
            : Localize("Mods_Updated", "{0} updated to {1}", title, version.VersionNumber);
    }

    /// <summary>
    /// "Bring back the previous version": the file the last update moved aside returns,
    /// and the newer one is kept in its turn. Nothing is downloaded.
    /// </summary>
    [RelayCommand]
    private async Task RestorePreviousModAsync(InstalledModItem? item)
    {
        if (item?.Previous is not { } previous || item.IsUpdating || SelectedInstance is not { } instance)
        {
            return;
        }

        var target = CurrentInstallTarget();
        var name = item.DisplayName;
        var fileName = item.FileName;
        var label = item.VersionLabel;
        var hadUpdate = item.Update is not null;

        try
        {
            item.IsUpdating = true;
            await MaybeBackupAsync(BackupTrigger.BeforeModChange, instance);

            var replacer = Replacer;
            var result = await Task.Run(() => replacer.Restore(target.Directory, previous, label));

            // The record follows the file, as it does when a mod is switched off.
            if (instance.InstalledMods.FirstOrDefault(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase)) is { } record)
            {
                record.FileName = result.NewFileName;
                record.Version = previous.Label;
                _instances.Save(instance);
            }

            AppendConsole($"[mods] {previous.FileName} is back in place of {fileName}; that one is kept the same way");

            if (_knownModUpdates.Remove(fileName) && hadUpdate)
            {
                ModUpdateCount = Math.Max(0, ModUpdateCount - 1);
            }

            if (IsSelectedBuild(target))
            {
                RefreshMods();
            }

            item.IsUpdating = false;
            Status = Localize("Mods_RestoredPrevious", "{0}: the previous version is back. The newer file is kept, so this can be undone the same way.", name);
        }
        catch (Exception ex)
        {
            item.IsUpdating = false;
            Status = Localize("Mods_RestoreFailed", "{0} stays as it was. {1}", name, DescribeFailure(ex));
            AppendConsole(ex.ToString());
        }
    }

    /// <summary>
    /// A failed download or replacement in the player's words: what happened first, the
    /// address last. Anything that is neither is told as it tells itself.
    /// </summary>
    private static string DescribeFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ModReplaceException { Step: ModReplaceStep.GameRunning }:
                    return Localize("Mods_ReplaceGameRunning", "The game is running: close it and try again.");

                case ModReplaceException { Step: ModReplaceStep.Restore } restore:
                    return Localize("Mods_ReplaceRestore", "The kept file could not be put back: {0}", STlauncher.Core.Http.NetworkFailures.InnermostMessage(restore));

                case ModReplaceException replace:
                    return Localize("Mods_ReplaceFiles", "The file could not be put in place, everything was returned: {0}", STlauncher.Core.Http.NetworkFailures.InnermostMessage(replace));

                case STlauncher.Core.Http.DownloadFailedException download:
                    return Localize("Net_CauseWithUrl", "{0} Address: {1}", DownloadCauseText(download.Failure), download.Url);
            }
        }

        return exception.Message;
    }

    /// <summary>Called by the list refresh so a check survives it.</summary>
    private void RestoreKnownUpdate(InstalledModItem item)
    {
        if (_knownModUpdates.TryGetValue(item.FileName, out var known))
        {
            item.Update = known.Version;
            item.ProjectId = known.ProjectId;
        }

        // The same refresh says whether there is a version to go back to.
        if (item.IsMod && !item.IsCatalog && SelectedInstance is not null)
        {
            item.Previous = _mods.Replacer.FindPrevious(InstanceDirectory, item.Mod.Folder, item.FileName);
            item.RestorePreviousCommand = RestorePreviousModCommand;
        }
    }

    /// <summary>A build switch means a different folder: yesterday's check no longer applies.</summary>
    private void ForgetModUpdates()
    {
        _knownModUpdates.Clear();
        ModUpdateCount = 0;
        ModUpdatesSummary = string.Empty;
    }
}
