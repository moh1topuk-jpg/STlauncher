using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Everything that keeps a build in step with the catalog: creating the recommended
/// build on a fresh installation, re-applying what the catalog changed, and downloading
/// the files a build consists of.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<CatalogBuild> CatalogBuilds { get; } = new();

    /// <summary>True while the recommended build is being fetched and installed.</summary>
    [ObservableProperty]
    private bool _isBuildImportBusy;

    /// <summary>True while the startup sync is downloading what the build is missing.</summary>
    [ObservableProperty]
    private bool _isBuildSyncBusy;

    /// <summary>
    /// The startup sync, kept so a launch can wait for it instead of racing it into the
    /// same mods folder.
    /// </summary>
    private Task? _buildSync;

    /// <summary>
    /// The build the catalog marks as recommended, falling back to the first entry so an
    /// older catalog without the flag keeps working.
    /// </summary>
    public CatalogBuild? RecommendedBuild => CatalogBuildSync.Recommended(_loadedCatalog)
                                             ?? CatalogBuilds.FirstOrDefault(b => b.Recommended)
                                             ?? CatalogBuilds.FirstOrDefault();

    /// <summary>The selected build came from the catalog and is kept in step with it.</summary>
    public bool IsCatalogInstance => !string.IsNullOrWhiteSpace(SelectedInstance?.CatalogBuildId);

    /// <summary>Adds one build of the catalog, or brings it up to date when it is already here.</summary>
    [RelayCommand]
    private Task AddCatalogBuildAsync(CatalogBuild? build) => InstallCatalogBuildAsync(build?.Id);

    /// <summary>Re-checks the selected catalog build against the repository.</summary>
    [RelayCommand]
    private Task SyncSelectedBuildAsync()
    {
        var build = CatalogBuilds.FirstOrDefault(b =>
            string.Equals(b.Id, SelectedInstance?.CatalogBuildId, StringComparison.OrdinalIgnoreCase));

        return AddCatalogBuildAsync(build);
    }

    /// <summary>
    /// Brings the build list in line with the catalog. Creates the recommended build when
    /// it is missing and re-applies version, loader, server and item list to the build
    /// that came from the catalog. A build the player made themselves is never touched.
    /// </summary>
    /// <returns>The instance that belongs to the recommended build, if there is one.</returns>
    private Instance? SyncRecommendedBuild(AppSettingsSnapshot settings)
    {
        var build = RecommendedBuild;

        if (build is null)
        {
            // Offline first run, or a catalog without builds: fall back to an empty
            // profile so the launcher is still usable.
            if (_allInstances.Count == 0 && !_instances.HasAnyInstanceDirectory())
            {
                _allInstances.Add(CreateLegacyInstance(settings));
            }

            return null;
        }

        var instance = CatalogBuildSync.FindLinked(_allInstances, build);
        var isNew = instance is null;

        if (isNew)
        {
            // The player deleted this build on purpose: recreating it on every start
            // would be the launcher arguing with them.
            if (settings.DismissedBuildIds.Contains(build.Id, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            instance = _instances.Create(UniqueInstanceName(build.Name));
            _allInstances.Add(instance);
        }

        var before = BuildSnapshot.Of(instance!);
        var changes = CatalogBuildSync.Apply(instance!, build, isNew);

        if (isNew || changes.Any)
        {
            _instances.Save(instance!);
        }

        if (isNew)
        {
            AppendConsole($"[builds] created '{instance!.Name}' from the catalog");
        }
        else if (changes.Any)
        {
            AppendConsole($"[builds] '{instance!.Name}' updated from the catalog");

            // Said on the main screen, not only in the console: the owner changed the
            // build, and the player deserves to know what is different today.
            AnnounceBuildChanges(instance!.Name, BuildChangeNotice.Between(before, BuildSnapshot.Of(instance), CatalogItemName));
        }

        return instance;
    }

    /// <summary>An empty profile, used only when the catalog offers nothing to start from.</summary>
    private Instance CreateLegacyInstance(AppSettingsSnapshot settings)
    {
        var instance = _instances.Create("Default");
        instance.MaxMemoryMb = settings.MaxMemoryMb;
        instance.MinMemoryMb = settings.MinMemoryMb;
        instance.Loader = settings.Loader;
        instance.LoaderVersion = settings.LoaderVersion;
        instance.VersionId = settings.SelectedVersionId;
        _instances.Save(instance);

        return instance;
    }

    /// <summary>
    /// Downloads whatever the recommended build is missing, in the background, so the
    /// Play button works on a fresh installation without a detour through the build page.
    /// </summary>
    private Task StartBuildSyncAsync(Instance? instance)
    {
        if (instance is null || _loadedCatalog is null)
        {
            return Task.CompletedTask;
        }

        _buildSync = RunBuildSyncAsync(instance);
        return _buildSync;
    }

    private async Task RunBuildSyncAsync(Instance instance)
    {
        try
        {
            IsBuildSyncBusy = true;

            var downloaded = await EnsureBuildItemsInstalledAsync(instance);

            if (downloaded > 0)
            {
                Status = Localize("Builds_SyncDone", "Build updated: {0} file(s) downloaded", downloaded);
            }
            else if (string.Equals(instance.Id, SelectedInstance?.Id, StringComparison.OrdinalIgnoreCase))
            {
                Status = Localize("Builds_SyncReady", "Build is up to date - ready to play");
            }
        }
        catch (Exception ex)
        {
            Status = Localize("Builds_SyncFailed", "Could not update the build: {0}", ex.Message);
            AppendConsole($"[builds] sync failed: {ex}");
        }
        finally
        {
            IsBuildSyncBusy = false;
            RefreshMods();
        }
    }

    /// <summary>Waits for a startup sync that is still running, so a launch never races it.</summary>
    private async Task WaitForBuildSyncAsync()
    {
        var pending = _buildSync;

        if (pending is null || pending.IsCompleted)
        {
            return;
        }

        Status = Localize("Builds_SyncWaiting", "Finishing the build update…");
        await pending;
    }



    /// <summary>
    /// Re-reads the catalog and brings the recommended build up to date, creating it if
    /// the player deleted it. Unlike the old version this never leaves a second copy of
    /// the same build in the list.
    /// </summary>
    [RelayCommand]
    private Task AddRecommendedBuildAsync() => InstallCatalogBuildAsync(null);

    /// <summary>
    /// Adds a catalog build, or brings it up to date when it is already in the list.
    /// Null means the recommended one. Always works on the build's own instance - never
    /// on whatever happens to be open, which the old picker did.
    /// </summary>
    private async Task InstallCatalogBuildAsync(string? buildId)
    {
        if (IsBuildImportBusy)
        {
            return;
        }

        try
        {
            IsBuildImportBusy = true;

            // The startup sync may be downloading into the same folder right now.
            await WaitForBuildSyncAsync();

            Status = Localize("Builds_ImportFetching", "Fetching the recommended build…");

            // Read the catalog again rather than trusting the loaded copy: the build lives
            // in the repository, so a change there should be picked up without a restart.
            var result = await _catalog.LoadAsync();

            if (result.Catalog is null)
            {
                Status = Localize(
                    "Builds_ImportNoCatalog",
                    "Could not load the catalog: {0}",
                    result.Error ?? "?");
                return;
            }

            _loadedCatalog = result.Catalog;

            CatalogBuilds.Clear();
            foreach (var catalogBuild in result.Catalog.Builds)
            {
                CatalogBuilds.Add(catalogBuild);
            }

            var build = buildId is null
                ? RecommendedBuild
                : CatalogBuilds.FirstOrDefault(b => string.Equals(b.Id, buildId, StringComparison.OrdinalIgnoreCase));

            if (build is null)
            {
                Status = Localize("Builds_ImportNone", "The catalog does not offer a build");
                return;
            }

            var instance = CatalogBuildSync.FindLinked(_allInstances, build);
            var isNew = instance is null;

            if (isNew)
            {
                instance = _instances.Create(UniqueInstanceName(build.Name));
                _allInstances.Add(instance);

                // Asked for explicitly, so it is wanted again.
                _dismissedBuildIds.Remove(build.Id);
            }

            CatalogBuildSync.Apply(instance!, build, isNew);
            _instances.Save(instance!);

            ApplyBuildFilter();
            SelectedInstance = Instances.FirstOrDefault(i =>
                string.Equals(i.Id, instance!.Id, StringComparison.OrdinalIgnoreCase));

            var versionMissing = await ApplyBuildToEditorAsync(build);

            if (versionMissing)
            {
                // The build is in the list and usable, it just needs a version picked by hand.
                Status = Localize(
                    "Builds_ImportVersionMissing",
                    "Build \"{0}\" was added, but version {1} is not available - pick one in the build settings.",
                    instance!.Name,
                    build.GameVersion);
                return;
            }

            Status = Localize("Builds_ImportInstalling", "Installing build mods…");
            await EnsureBuildItemsInstalledAsync(instance!);

            RefreshBackups();
            RefreshMods();

            Status = isNew
                ? Localize("Builds_ImportDone", "Build \"{0}\" added ({1} item(s))", instance!.Name, build.Items.Count)
                : Localize("Builds_ImportUpdated", "Build \"{0}\" is up to date ({1} item(s))", instance!.Name, build.Items.Count);

            AppendConsole($"[builds] '{instance!.Name}' synced with the catalog");
        }
        catch (Exception ex)
        {
            Status = Localize("Error_ImportBuild", "Failed to add the build: {0}", ex.Message);
        }
        finally
        {
            IsBuildImportBusy = false;
        }
    }

    /// <summary>
    /// Points the editor at what the build describes. Returns true when the build's game
    /// version could not be resolved, which the caller reports instead of failing.
    /// </summary>
    private async Task<bool> ApplyBuildToEditorAsync(CatalogBuild build)
    {
        var versionMissing = false;

        _applyingInstance = true;
        try
        {
            if (build.GameVersion is not null)
            {
                var version = _allVersions.FirstOrDefault(v => v.Id == build.GameVersion);

                if (version is null)
                {
                    versionMissing = true;
                }
                else
                {
                    SelectedVersion = version;
                }
            }

            SelectedLoader = build.Loader;

            if (build.MemoryMb is > 0)
            {
                MaxMemoryMb = build.MemoryMb.Value;
            }
        }
        finally
        {
            _applyingInstance = false;
        }

        await LoadLoaderVersionsAsync();

        if (build.LoaderVersion is { Length: > 0 } pinned)
        {
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Version == pinned)
                                    ?? SelectedLoaderVersion;
        }

        SyncInstance();
        OnPropertyChanged(nameof(BuildModCount));
        return versionMissing;
    }

    /// <summary>
    /// Makes an instance match its catalog items. The catalog file is the source of truth:
    /// adding a mod there installs it for everyone, removing one uninstalls it. Mods are
    /// not bumped to newer Modrinth releases by themselves - to update one, pin its
    /// version in the catalog.
    /// </summary>
    /// <returns>How many files were downloaded.</returns>
    private async Task<int> EnsureBuildItemsInstalledAsync(Instance instance)
    {
        if (instance is null)
        {
            return 0;
        }

        // Backups are scoped to the build that is open, so only back up when the sync is
        // touching that one.
        if (string.Equals(instance.Id, SelectedInstance?.Id, StringComparison.OrdinalIgnoreCase))
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange);
        }

        // The instance is the authority here, not the editor: the startup sync runs for
        // the recommended build even when the player has another one selected.
        var directory = _instances.GameDirectory(instance);
        var gameVersion = instance.VersionId;
        var loader = instance.Loader;
        var enabledIds = instance.EnabledCatalogItems;

        // Catalog mods that are no longer part of the build are removed.
        var removed = instance.InstalledMods
            .Where(m => m.Source == ModSource.Catalog &&
                        m.Id is not null &&
                        !enabledIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var record in removed)
        {
            DeleteInstalledFile(instance, record);
        }

        var pending = new List<CatalogItem>();
        var bumped = new List<string>();

        foreach (var id in enabledIds)
        {
            var item = _loadedCatalog?.FindItem(id);

            if (item is null)
            {
                continue;
            }

            var record = instance.InstalledMods.FirstOrDefault(m =>
                m.Source == ModSource.Catalog &&
                string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

            var fileExists = record is not null &&
                             System.IO.File.Exists(System.IO.Path.Combine(
                                 ModManager.ModsDirectory(directory),
                                 record.FileName));

            var action = CatalogSyncDecision.Decide(item, record, fileExists);

            if (action.NeedsInstall())
            {
                pending.Add(item);
            }

            if (action == CatalogSyncAction.Update)
            {
                bumped.Add(item.Name);
            }
        }

        // A pinned version moved in the catalog: that is an update the player should
        // hear about, unlike a plain re-download of a missing file.
        if (bumped.Count > 0 && !string.IsNullOrWhiteSpace(instance.CatalogBuildId))
        {
            AnnounceBuildUpdates(instance.Name, bumped);
        }

        if (pending.Count == 0)
        {
            AppendConsole($"[build] {enabledIds.Count} item(s) checked, nothing to do");
            return 0;
        }

        AppendConsole($"--- Build: {pending.Count} mod(s) to install ---");

        var downloadedCount = 0;
        var index = 0;

        foreach (var item in pending)
        {
            index++;
            Status = Localize("Builds_SyncItem", "Build: {0} ({1}/{2})", item.Name, index, pending.Count);

            var result = await _catalogInstaller.InstallAsync(item, directory, gameVersion, loader);

            AppendConsole($"[build] {item.Name}: {result.Message}");

            if (!result.Success || result.Path is null)
            {
                continue;
            }

            var fileName = System.IO.Path.GetFileName(result.Path);

            ReplaceInstalledFile(instance, item.Id, fileName);
            RecordInstalledMod(instance, new InstalledModRecord
            {
                FileName = fileName,
                Source = ModSource.Catalog,
                Id = item.Id,
                Name = item.Name,
                IconUrl = item.IconUrl,
                Required = item.Required,
                Version = result.Version
            });

            // Only files that actually came down the wire are counted: a verified file
            // reported as "downloaded" is how a no-op sync looked like a full reinstall.
            if (result.Downloaded)
            {
                downloadedCount++;
            }
        }

        return downloadedCount;
    }

    /// <summary>Deletes a recorded file and forgets it.</summary>
    private void DeleteInstalledFile(Instance instance, InstalledModRecord record)
    {
        try
        {
            var path = System.IO.Path.Combine(
                ModManager.ModsDirectory(_instances.GameDirectory(instance)),
                record.FileName);

            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
                AppendConsole($"[build] removed {record.FileName}");
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[build] could not remove {record.FileName}: {ex.Message}");
        }

        instance.InstalledMods.Remove(record);
        _instances.Save(instance);
    }

    /// <summary>
    /// Removes the file previously installed for a project when a different one replaced it.
    /// </summary>
    private void ReplaceInstalledFile(Instance instance, string? projectId, string newFileName)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        var stale = instance.InstalledMods
            .Where(m => string.Equals(m.Id, projectId, StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.Equals(m.FileName, newFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (stale.Count == 0)
        {
            return;
        }

        var directory = ModManager.ModsDirectory(_instances.GameDirectory(instance));

        foreach (var record in stale)
        {
            try
            {
                var path = System.IO.Path.Combine(directory, record.FileName);

                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    AppendConsole($"[build] removed outdated {record.FileName}");
                }

                instance.InstalledMods.Remove(record);
            }
            catch (Exception ex)
            {
                AppendConsole($"[build] could not remove {record.FileName}: {ex.Message}");
            }
        }

        _instances.Save(instance);
    }

    /// <summary>
    /// Keeps build names distinct: adding the same recommended build twice should not
    /// produce two entries that read identically in the list.
    /// </summary>
    private string UniqueInstanceName(string baseName)
    {
        var taken = new HashSet<string>(
            _allInstances.Select(i => i.Name),
            StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseName} ({suffix})";

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return baseName;
    }
}

/// <summary>
/// The settings values the build sync needs, so it does not depend on the whole settings
/// object being reloaded.
/// </summary>
public sealed record AppSettingsSnapshot(
    int MaxMemoryMb,
    int MinMemoryMb,
    STlauncher.Core.Loaders.LoaderKind Loader,
    string? LoaderVersion,
    string? SelectedVersionId,
    IReadOnlyCollection<string> DismissedBuildIds);
