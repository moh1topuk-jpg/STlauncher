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
    }

    public InstalledMod Mod { get; }

    /// <summary>Provenance, when the launcher installed the file itself.</summary>
    public InstalledModRecord? Record { get; }

    public string FileName => Mod.FileName;

    public string DisplayName => Record?.Name is { Length: > 0 } name ? name : Mod.DisplayName;

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

        var candidates = InstalledMods.Where(m => !m.IsCatalog && m.Enabled).ToList();

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
        if (item?.Update is null || item.IsUpdating || SelectedInstance is null)
        {
            return;
        }

        try
        {
            item.IsUpdating = true;
            await ApplyModUpdateAsync(item);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_InstallMod", "Mod install failed: {0}", ex.Message);
            AppendConsole(ex.ToString());
            item.IsUpdating = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        var pending = InstalledMods.Where(m => m.HasUpdate).ToList();

        foreach (var item in pending)
        {
            await UpdateModAsync(item);
        }
    }

    private async Task ApplyModUpdateAsync(InstalledModItem item)
    {
        var version = item.Update!;
        var oldFile = item.FileName;

        var project = item.ProjectId is { Length: > 0 } id
            ? await _modrinth.GetProjectAsync(id)
            : null;

        var slug = project?.Slug ?? item.Record?.Id ?? item.ProjectId ?? string.Empty;
        var title = project?.Title ?? item.DisplayName;

        Status = Localize("Mods_Updating", "Updating {0} to {1}…", title, version.VersionNumber);

        // The same path a fresh install takes: file, then whatever it requires.
        await InstallProjectWithDependenciesAsync(version, slug, title, project?.IconUrl ?? item.Record?.IconUrl);

        var newFile = ModrinthClient.SelectFile(version, SelectedVersion?.Id, SelectedLoader)?.FileName;

        if (newFile is not null && !string.Equals(newFile, oldFile, StringComparison.OrdinalIgnoreCase))
        {
            _mods.Uninstall(item.Path);
            SelectedInstance!.InstalledMods.RemoveAll(m =>
                string.Equals(m.FileName, oldFile, StringComparison.OrdinalIgnoreCase));
            _instances.Save(SelectedInstance);
        }

        _knownModUpdates.Remove(oldFile);
        ModUpdateCount = Math.Max(0, ModUpdateCount - 1);

        RefreshMods();
        Status = Localize("Mods_Updated", "{0} updated to {1}", title, version.VersionNumber);
    }

    /// <summary>Called by the list refresh so a check survives it.</summary>
    private void RestoreKnownUpdate(InstalledModItem item)
    {
        if (_knownModUpdates.TryGetValue(item.FileName, out var known))
        {
            item.Update = known.Version;
            item.ProjectId = known.ProjectId;
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
