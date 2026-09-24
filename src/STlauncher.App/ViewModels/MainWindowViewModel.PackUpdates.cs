using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using STlauncher.Core.Packs;

namespace STlauncher.App.ViewModels;

/// <summary>What Modrinth has that is newer than the file, once a check has run.</summary>
public partial class ResourcePackItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateLabel))]
    private ModVersion? _update;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private bool _isUpdating;

    public bool HasUpdate => Update is not null && !IsUpdating;

    public string UpdateLabel => Update is null ? string.Empty : "↑ " + Update.VersionNumber;

    public string? ProjectId { get; set; }
}

public partial class ShaderPackItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateLabel))]
    private ModVersion? _update;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private bool _isUpdating;

    public bool HasUpdate => Update is not null && !IsUpdating;

    public string UpdateLabel => Update is null ? string.Empty : "↑ " + Update.VersionNumber;

    public string? ProjectId { get; set; }
}

/// <summary>
/// Updates for resource packs and shaders, the same way mods get them: the file's hash
/// asks Modrinth what is newer for this game version, the launcher only tells, and the
/// player presses the button. Packs from the server catalog are the server's to update.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly string[] ResourcePackLoaders = { "minecraft" };
    private static readonly string[] ShaderLoaders = { "iris", "optifine", "canvas", "vanilla" };

    [ObservableProperty]
    private bool _isCheckingPackUpdates;

    [ObservableProperty]
    private string _packUpdatesSummary = string.Empty;

    /// <summary>Survives a list refresh: file name to the version found for it.</summary>
    private readonly Dictionary<string, (ModVersion Version, string? ProjectId)> _knownPackUpdates = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task CheckPackUpdatesAsync()
    {
        if (IsCheckingPackUpdates)
        {
            return;
        }

        var packs = EnabledResourcePacks.Concat(DisabledResourcePacks).Where(p => !p.IsCatalog && !p.Info.IsFolder).ToList();
        var shaders = ShaderPacks.Where(s => !s.IsCatalog && System.IO.File.Exists(s.Path)).ToList();

        if (packs.Count + shaders.Count == 0)
        {
            PackUpdatesSummary = Localize("Packs_UpdatesNothing", "Nothing to check: no packs of your own");
            return;
        }

        try
        {
            IsCheckingPackUpdates = true;
            PackUpdatesSummary = Localize("Packs_UpdatesChecking", "Checking {0} pack(s) on Modrinth…", packs.Count + shaders.Count);

            var gameVersion = SelectedInstance?.VersionId;
            var found = 0;

            found += await CheckGroupAsync(
                packs.Select(p => (p.Path, p.FileName, (Action<ModVersion?, string?>)((v, id) => { p.Update = v; p.ProjectId = id; }))).ToList(),
                gameVersion, ResourcePackLoaders);
            found += await CheckGroupAsync(
                shaders.Select(s => (s.Path, s.FileName, (Action<ModVersion?, string?>)((v, id) => { s.Update = v; s.ProjectId = id; }))).ToList(),
                gameVersion, ShaderLoaders);

            PackUpdatesSummary = found == 0
                ? Localize("Packs_UpdatesNone", "All packs and shaders are up to date")
                : Localize("Packs_UpdatesFound", "Updates available: {0}. Nothing is installed until you say so.", found);
        }
        catch (Exception ex)
        {
            PackUpdatesSummary = Localize("Packs_UpdatesFailed", "Could not check for updates: {0}", ex.Message);
            AppendConsole($"[packs] update check failed: {ex}");
        }
        finally
        {
            IsCheckingPackUpdates = false;
        }
    }

    private async Task<int> CheckGroupAsync(
        IReadOnlyList<(string Path, string FileName, Action<ModVersion?, string?> Apply)> files,
        string? gameVersion,
        IEnumerable<string> loaders)
    {
        if (files.Count == 0)
        {
            return 0;
        }

        var hashes = await Task.Run(() => files
            .Select(f => (File: f, Hash: ModManager.TryComputeSha1(f.Path)))
            .Where(p => p.Hash is not null)
            .ToDictionary(p => p.Hash!, p => p.File, StringComparer.OrdinalIgnoreCase));

        if (hashes.Count == 0)
        {
            return 0;
        }

        var current = await _modrinth.GetVersionsByHashesAsync(hashes.Keys);
        var latest = await _modrinth.GetLatestVersionsAsync(hashes.Keys, gameVersion, loaders);
        var found = 0;

        foreach (var (hash, file) in hashes)
        {
            current.TryGetValue(hash, out var installed);
            var projectId = installed?.ProjectId;

            if (!latest.TryGetValue(hash, out var newest))
            {
                file.Apply(null, projectId);
                _knownPackUpdates.Remove(file.FileName);
                continue;
            }

            var isNewer = installed is null
                ? !string.Equals(newest.PrimaryFile?.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                : !string.Equals(newest.Id, installed.Id, StringComparison.Ordinal);

            if (isNewer)
            {
                projectId ??= newest.ProjectId;
                file.Apply(newest, projectId);
                _knownPackUpdates[file.FileName] = (newest, projectId);
                found++;
            }
            else
            {
                file.Apply(null, projectId);
                _knownPackUpdates.Remove(file.FileName);
            }
        }

        return found;
    }

    /// <summary>Called by the list refresh so a check survives it.</summary>
    private void RestoreKnownPackUpdates()
    {
        foreach (var pack in EnabledResourcePacks.Concat(DisabledResourcePacks))
        {
            if (_knownPackUpdates.TryGetValue(pack.FileName, out var known))
            {
                pack.Update = known.Version;
                pack.ProjectId = known.ProjectId;
            }
        }

        foreach (var shader in ShaderPacks)
        {
            if (_knownPackUpdates.TryGetValue(shader.FileName, out var known))
            {
                shader.Update = known.Version;
                shader.ProjectId = known.ProjectId;
            }
        }
    }

    [RelayCommand]
    private async Task UpdateResourcePackAsync(ResourcePackItem? item)
    {
        if (item?.Update is null || item.IsUpdating || PacksLocked())
        {
            return;
        }

        try
        {
            item.IsUpdating = true;
            var wasEnabled = item.IsEnabled;
            var position = EnabledResourcePacks.IndexOf(item);

            var newFile = await ReplacePackFileAsync(item.Update, item.ProjectId, item.Record, item.Path, item.FileName, item.Name, CatalogPlacement.ResourcePacksFolder);

            if (newFile is not null && wasEnabled)
            {
                // The new file takes the old one's place in the order, so the priority holds.
                var names = EnabledResourcePacks.Select(p => p.FileName).ToList();
                names[Math.Max(0, position)] = newFile;
                ResourcePackOrder.WriteEnabled(InstanceDirectory, names);
            }

            _knownPackUpdates.Remove(item.FileName);
            RefreshMods();
            Status = Localize("Packs_Updated", "{0} updated to {1}", item.Name, item.Update.VersionNumber);
        }
        catch (Exception ex)
        {
            item.IsUpdating = false;
            Status = Localize("Error_Packs", "Failed to change the resource packs: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
    }

    [RelayCommand]
    private async Task UpdateShaderAsync(ShaderPackItem? item)
    {
        if (item?.Update is null || item.IsUpdating || PacksLocked())
        {
            return;
        }

        try
        {
            item.IsUpdating = true;
            var wasActive = item.IsActive;

            var newFile = await ReplacePackFileAsync(item.Update, item.ProjectId, item.Record, item.Path, item.FileName, item.Name, CatalogPlacement.ShaderPacksFolder);

            if (newFile is not null && wasActive)
            {
                WriteShaderChoice(newFile);
            }

            _knownPackUpdates.Remove(item.FileName);
            RefreshMods();
            Status = Localize("Packs_Updated", "{0} updated to {1}", item.Name, item.Update.VersionNumber);
        }
        catch (Exception ex)
        {
            item.IsUpdating = false;
            Status = Localize("Error_Shaders", "Failed to change the shaders: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
    }

    /// <summary>Downloads the newer file, removes the old one and moves the record over. Returns the new file name.</summary>
    private async Task<string?> ReplacePackFileAsync(
        ModVersion version, string? projectId, InstalledModRecord? record,
        string oldPath, string oldFileName, string title, string folder)
    {
        var file = ModrinthClient.SelectFile(version, SelectedInstance?.VersionId, LoaderKind.Vanilla) ?? version.PrimaryFile;

        if (file is null || string.IsNullOrEmpty(file.Url))
        {
            Status = Localize("Status_NoCompatibleFile", "No compatible file for this version and loader");
            return null;
        }

        await MaybeBackupAsync(BackupTrigger.BeforeModChange);
        Status = Localize("Packs_Updating", "Updating {0} to {1}…", title, version.VersionNumber);
        await _mods.InstallAsync(InstanceDirectory, folder, file.FileName, file.Url, file.Sha1, file.Size);

        if (!string.Equals(file.FileName, oldFileName, StringComparison.OrdinalIgnoreCase))
        {
            _mods.Uninstall(oldPath);
            ForgetRecord(oldFileName);
        }

        var project = projectId is { Length: > 0 } id ? await _modrinth.GetProjectAsync(id) : null;

        RecordInstalledMod(new InstalledModRecord
        {
            FileName = file.FileName,
            Source = ModSource.Modrinth,
            Id = project?.Slug ?? record?.Id ?? projectId,
            Name = project?.Title ?? record?.Name ?? title,
            IconUrl = project?.IconUrl ?? record?.IconUrl,
            Version = version.VersionNumber,
            Folder = folder
        });

        return file.FileName;
    }
}
