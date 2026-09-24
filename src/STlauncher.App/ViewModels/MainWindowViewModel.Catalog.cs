using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using STlauncher.Core.Modpacks;

namespace STlauncher.App.ViewModels;

/// <summary>
/// The content catalog and the files a build ends up with: loading the catalog, installing
/// a single item, importing a modpack and the installed-mods list.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string _catalogUrl = string.Empty;

    [ObservableProperty]
    private string _catalogStatus = string.Empty;

    [ObservableProperty]
    private bool _isCatalogBusy;

    [ObservableProperty]
    private string _modSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isModsBusy;


    public ObservableCollection<InstalledModItem> InstalledMods { get; } = new();

    /// <summary>The line above the mod list: how many there are, and how many are off.</summary>
    [ObservableProperty]
    private string _installedModsSummary = string.Empty;

    public bool HasNoInstalledMods => InstalledMods.Count == 0;

    [RelayCommand]
    private void OpenModsFolder()
    {
        try
        {
            var folder = System.IO.Path.Combine(InstanceDirectory, "mods");
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }


    [RelayCommand]
    private async Task LoadCatalogAsync()
    {
        if (IsCatalogBusy)
        {
            return;
        }

        try
        {
            IsCatalogBusy = true;
            CatalogStatus = Localize("Catalog_Loading", "Loading catalog…");

            var result = await _catalog.LoadAsync();

            CatalogBuilds.Clear();

            if (result.Catalog is not null)
            {
                _loadedCatalog = result.Catalog;

                foreach (var build in result.Catalog.Builds)
                {
                    CatalogBuilds.Add(build);
                }

                // The collector address travels with the catalog, so it can be changed
                // or switched off without releasing a new launcher.
                ApplyServerStatsUrl(result.Catalog.ServerStatsUrl);

                // Same reasoning, and more urgent: a player who cannot reach GitHub can
                // never receive a build that fixes it.
                _updates.UseFeeds(result.Catalog.AllUpdateFeeds().Concat(Services.AppSettings.BuiltInUpdateFeeds));
            }
            else
            {
                _loadedCatalog = null;

                // No catalog at all - which is exactly when the built-in mirror matters.
                _updates.UseFeeds(Services.AppSettings.BuiltInUpdateFeeds);
            }

            ConfigureDiscord();

            var summary = Localize(
                "Catalog_Summary",
                "{0}: {1} build(s)",
                result.Catalog?.Name ?? "Catalog",
                CatalogBuilds.Count);

            if (result.Catalog is null)
            {
                CatalogStatus = string.IsNullOrWhiteSpace(CatalogUrl)
                    ? Localize("Catalog_NotFound", "No catalog found", _catalog.DropInPath)
                    : Localize("Catalog_Failed", "Failed to load the catalog: {0}", result.Error);
                return;
            }

            CatalogStatus = result.Origin switch
            {
                CatalogOrigin.Remote => summary,
                CatalogOrigin.LocalFile => Localize("Catalog_SummaryLocal", "{0} (local file)", summary),
                CatalogOrigin.Cache => Localize("Catalog_SummaryCache", "{0} (cached: source unavailable)", summary),
                CatalogOrigin.DropIn => Localize("Catalog_SummaryLocal", "{0} (local file)", summary),
                _ => summary
            };

            // The technical reason is noise for players who still got a working catalog.
            if (result.Error is not null && result.Origin is not CatalogOrigin.Remote && ShowDeveloperConsole)
            {
                CatalogStatus += $" · {result.Error}";
            }
        }
        catch (Exception ex)
        {
            CatalogStatus = Localize("Catalog_Failed", "Failed to load the catalog: {0}", ex.Message);
        }
        finally
        {
            IsCatalogBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallCatalogItemAsync(CatalogItem? item)
    {
        if (item is null || IsCatalogBusy)
        {
            return;
        }

        try
        {
            IsCatalogBusy = true;
            await MaybeBackupAsync(BackupTrigger.BeforeModChange);
            Status = Localize("Status_InstallingFile", "Installing {0}…", item.Name);
            AppendConsole($"--- Installing '{item.Name}' ({item.Type}) into '{SelectedInstance?.Name}' ---");

            var result = await _catalogInstaller.InstallAsync(
                item,
                InstanceDirectory,
                SelectedVersion?.Id,
                SelectedLoader);

            Status = result.Message;
            AppendConsole(result.Message);

            if (result.Success)
            {
                RecordInstalledMod(new InstalledModRecord
                {
                    FileName = System.IO.Path.GetFileName(result.Path ?? string.Empty),
                    Source = ModSource.Catalog,
                    Id = item.Id,
                    Name = item.Name,
                    IconUrl = item.IconUrl,
                    Required = item.Required
                });

                RefreshMods();
            }
        }
        catch (Exception ex)
        {
            Status = Localize("Error_CatalogInstall", "Catalog install failed: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsCatalogBusy = false;
        }
    }

    [RelayCommand]
    private void RefreshMods()
    {
        try
        {
            ReconcileInstalledMods();

            InstalledMods.Clear();

            // Packs and shaders live on their own tabs now; this list is jars only.
            foreach (var mod in _mods.ListMods(InstanceDirectory))
            {
                var record = SelectedInstance?.InstalledMods.FirstOrDefault(m =>
                    string.Equals(m.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Folder ?? ModManager.ModsFolderName, mod.Folder, StringComparison.OrdinalIgnoreCase));

                var item = new InstalledModItem(mod, record);
                RestoreKnownUpdate(item);
                InstalledMods.Add(item);
            }

            var mods = InstalledMods.Where(m => m.IsMod).ToList();
            var enabled = mods.Count(m => m.Enabled);
            var summary = mods.Count == enabled
                ? Localize("Mods_SummaryAll", "Mods in the build: {0}", enabled)
                : Localize("Mods_SummaryDisabled", "Mods in the build: {0}, switched off: {1}",
                    enabled, mods.Count - enabled);

            InstalledModsSummary = summary;

            OnPropertyChanged(nameof(HasNoInstalledMods));

            RefreshPacks();
            ApplyModsFilter();
            ScheduleBuildCheck();
            OnPropertyChanged(nameof(CanUseSafeMode));
            RefreshBrowserInstallState();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_ReadMods", "Failed to read mods: {0}", ex.Message);
        }
    }

    public async Task ImportModpackAsync(string mrpackPath)
    {
        try
        {
            IsModsBusy = true;
            await MaybeBackupAsync(BackupTrigger.BeforeModChange);
            Status = Localize("Status_ImportingModpack", "Reading the modpack…");

            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Status = Localize("Status_ModpackFiles", "Modpack files {0}/{1}", p.Completed, p.Total);
            });

            var result = await _modpacks.InstallAsync(mrpackPath, InstanceDirectory, progress);
            AppendConsole(
                $"--- Modpack '{result.Plan.Name}' installed: " +
                $"{result.InstalledFiles} files, {result.FailedFiles} failed, {result.SkippedFiles} skipped ---");

            if (result.Plan.GameVersion is not null)
            {
                var version = _allVersions.FirstOrDefault(v => v.Id == result.Plan.GameVersion);
                if (version is not null)
                {
                    if (!ShowSnapshots && version.Type != "release")
                    {
                        ShowSnapshots = true;
                    }

                    SelectedVersion = version;
                }
            }

            if (result.Plan.Loader != LoaderKind.Vanilla)
            {
                SelectedLoader = result.Plan.Loader;
            }

            RefreshMods();
            Status = Localize("Status_ModpackInstalled", "Modpack \"{0}\" installed", result.Plan.Name);

            // Only label files that have no provenance yet; re-labelling would wipe the
            // Modrinth and catalog sources recorded earlier.
            foreach (var mod in InstalledMods)
            {
                var known = SelectedInstance?.InstalledMods.Any(m =>
                    string.Equals(m.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase)) == true;

                if (!known)
                {
                    RecordInstalledMod(new InstalledModRecord
                    {
                        FileName = mod.FileName,
                        Source = ModSource.Modpack,
                        Name = mod.DisplayName
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Modpack", "Modpack import failed: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsModsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void ToggleMod(InstalledModItem? mod)
    {
        if (mod is null)
        {
            return;
        }

        try
        {
            var wasEnabled = mod.Enabled;
            _mods.SetEnabled(mod.Path, !mod.Enabled);

            // The file is renamed on disk, so the record has to follow it.
            var record = SelectedInstance?.InstalledMods.FirstOrDefault(m =>
                string.Equals(m.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase));

            if (record is not null && SelectedInstance is not null)
            {
                record.FileName = wasEnabled
                    ? mod.FileName + ".disabled"
                    : mod.FileName[..^".disabled".Length];

                // Remembered rather than inferred from the file name: the catalog sync
                // has to know this was the player's decision, otherwise it reinstalls the
                // mod on the next start and deletes the disabled copy as outdated.
                record.DisabledByUser = wasEnabled;

                _instances.Save(SelectedInstance);
            }

            RefreshMods();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_ToggleMod", "Failed to toggle the mod: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task UninstallModAsync(InstalledModItem? mod)
    {
        if (mod is null)
        {
            return;
        }

        try
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange);
            _mods.Uninstall(mod.Path);

            if (SelectedInstance is not null)
            {
                SelectedInstance.InstalledMods.RemoveAll(m =>
                    string.Equals(m.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase));
                _instances.Save(SelectedInstance);
            }

            RefreshMods();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_RemoveMod", "Failed to remove the mod: {0}", ex.Message);
        }
    }
}
