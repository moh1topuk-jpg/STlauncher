using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core;
using STlauncher.Core.Auth;
using STlauncher.Core.Backups;
using STlauncher.Core.Content;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
using STlauncher.Core.Java;
using STlauncher.Core.Launch;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;
using STlauncher.Core.Mods;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Versions;

namespace STlauncher.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly VersionService _versions;
    private readonly LaunchService _launch;
    private readonly LoaderService _loaders;
    private readonly SettingsService _settings;
    private readonly SkinService _skins;
    private readonly RemoteImageService _images;
    private readonly ModrinthClient _modrinth;
    private readonly ModManager _mods;
    private readonly ModpackInstaller _modpacks;
    private readonly ContentCatalogService _catalog;
    private readonly CatalogInstaller _catalogInstaller;
    private readonly UpdateService _updates;
    private readonly InstanceManager _instances;
    private readonly LocalizationService _localization;
    private readonly JavaManager _java;
    private readonly InstanceBackupService _backups;
    private readonly LauncherPaths _paths;
    private readonly GameLauncher _gameLauncher;

    private List<VersionSummary> _allVersions = new();
    private bool _initialized;
    private bool _applyingInstance;
    private string _globalJavaPath = string.Empty;
    private CancellationTokenSource? _avatarCts;

    public MainWindowViewModel(
        VersionService versions,
        LaunchService launch,
        LoaderService loaders,
        SettingsService settings,
        SkinService skins,
        RemoteImageService images,
        ModrinthClient modrinth,
        ModManager mods,
        ModpackInstaller modpacks,
        ContentCatalogService catalog,
        CatalogInstaller catalogInstaller,
        UpdateService updates,
        InstanceManager instances,
        LocalizationService localization,
        JavaManager java,
        InstanceBackupService backups,
        LauncherPaths paths,
        GameLauncher gameLauncher)
    {
        _versions = versions;
        _launch = launch;
        _loaders = loaders;
        _settings = settings;
        _skins = skins;
        _images = images;
        _modrinth = modrinth;
        _mods = mods;
        _modpacks = modpacks;
        _catalog = catalog;
        _catalogInstaller = catalogInstaller;
        _updates = updates;
        _instances = instances;
        _localization = localization;
        _java = java;
        _backups = backups;
        _paths = paths;
        _gameLauncher = gameLauncher;
    }

    public ObservableCollection<Instance> Instances { get; } = new();

    public ObservableCollection<VersionSummary> Versions { get; } = new();

    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = new();

    public ObservableCollection<string> Console { get; } = new();

    public ObservableCollection<InstalledMod> InstalledMods { get; } = new();

    public IReadOnlyList<LoaderKind> LoaderKinds { get; } = Enum.GetValues<LoaderKind>();

    [ObservableProperty]
    private VersionSummary? _selectedVersion;

    [ObservableProperty]
    private LoaderKind _selectedLoader = LoaderKind.Vanilla;

    [ObservableProperty]
    private LoaderVersion? _selectedLoaderVersion;

    [ObservableProperty]
    private bool _isLoaderBusy;

    [ObservableProperty]
    private Bitmap? _avatar;

    [ObservableProperty]
    private string _modSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isModsBusy;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private bool _isUpdateBusy;

    [ObservableProperty]
    private bool _canRestartToUpdate;

    [ObservableProperty]
    private string _username = "Player";

    [ObservableProperty]
    private decimal _maxMemoryMb = 2048;

    [ObservableProperty]
    private decimal _minMemoryMb = 512;

    [ObservableProperty]
    private bool _showSnapshots;

    [ObservableProperty]
    private string _serverName = "My Server";

    [ObservableProperty]
    private string _serverAddress = string.Empty;


    [ObservableProperty]
    private string _catalogUrl = string.Empty;

    [ObservableProperty]
    private string _catalogStatus = string.Empty;

    [ObservableProperty]
    private bool _isCatalogBusy;

    [ObservableProperty]
    private ShellSection _section = ShellSection.Game;

    [ObservableProperty]
    private string _language = LocalizationService.DefaultLanguage;

    [ObservableProperty]
    private bool _showDeveloperConsole;

    public bool IsGameSection => Section == ShellSection.Game;
    public bool IsBuildsSection => Section == ShellSection.Builds;
    public bool IsServerSection => Section == ShellSection.Server;
    public bool IsConsoleSection => Section == ShellSection.Console;
    public bool IsSettingsSection => Section == ShellSection.Settings;

    public IReadOnlyList<string> Languages => _localization.AvailableLanguages;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private Instance? _selectedInstance;

    [ObservableProperty]
    private string _newInstanceName = string.Empty;

    [ObservableProperty]
    private string _instanceNameEdit = string.Empty;

    /// <summary>Number of catalog mods the current build installs before launch.</summary>
    public int BuildModCount => SelectedInstance?.EnabledCatalogItems.Count ?? 0;

    public string InstanceDirectory
        => SelectedInstance is null
            ? _paths.InstanceDirectory("default")
            : _instances.GameDirectory(SelectedInstance.Id);

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var settings = _settings.Load();
        Username = settings.Username;
        ShowSnapshots = settings.ShowSnapshots;
        CatalogUrl = ResolveCatalogUrl(settings.CatalogUrl);
        _catalog.CatalogUrl = CatalogUrl;
        Language = LocalizationService.Normalize(settings.Language);
        ShowDeveloperConsole = settings.ShowDeveloperConsole;

        ShowOldReleases = settings.ShowOldReleases;
        ShowBeta = settings.ShowBeta;
        ShowAlpha = settings.ShowAlpha;

        AfterLaunch = settings.AfterLaunch;
        ForceUpdate = settings.ForceUpdate;
        _globalJavaPath = settings.JavaPath ?? string.Empty;

        BackupsEnabled = settings.BackupsEnabled;
        BackupsBeforeLaunch = settings.BackupsBeforeLaunch;
        BackupsDaily = settings.BackupsDaily;
        BackupsBeforeModChanges = settings.BackupsBeforeModChanges;
        BackupsMaxCount = settings.BackupsMaxCount;
        BackupsMaxTotalMb = settings.BackupsMaxTotalMb;
        _backupDirectoryOverride = settings.BackupsDirectory ?? string.Empty;

        Nicknames.Clear();
        foreach (var nickname in settings.Nicknames)
        {
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                Nicknames.Add(nickname);
            }
        }

        LoadJavaChoices();
        LoadAfterLaunchOptions();
        RefreshBackups();

        Status = Localize("Status_Ready", "Ready");
        CatalogStatus = Localize("Catalog_NotLoaded", "Catalog not loaded yet");
        UpdateStatus = Localize("Update_OnlyInstalled", "Updates are only available in the installed build");

        _gameLauncher.OutputReceived += line => AppendConsole(line);
        _gameLauncher.ErrorReceived += line => AppendConsole(line);

        Instances.Clear();
        foreach (var existing in _instances.List())
        {
            Instances.Add(existing);
        }

        if (Instances.Count == 0)
        {
            Instances.Add(CreateMigratedInstance(settings));
        }

        await LoadVersionsAsync();

        SelectedInstance = Instances.FirstOrDefault(i => i.Id == settings.SelectedInstanceId)
                           ?? Instances.FirstOrDefault();

        await LoadLoaderVersionsAsync();

        if (SelectedInstance?.LoaderVersion is { Length: > 0 } loaderVersion)
        {
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Version == loaderVersion)
                                    ?? SelectedLoaderVersion;
        }

        RefreshMods();
        await LoadCatalogAsync();
        await LoadCategoriesAsync();
        ScheduleBrowserReload();
    }

    /// <summary>
    /// Upgrades installations that still hold the previous default catalog URL, while
    /// leaving a deliberately customised address untouched.
    /// </summary>
    private static string ResolveCatalogUrl(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) ||
            string.Equals(stored, AppSettings.LegacyCatalogUrl, StringComparison.OrdinalIgnoreCase))
        {
            return AppSettings.DefaultCatalogUrl;
        }

        return stored;
    }

    private Instance CreateMigratedInstance(AppSettings settings)
    {
        var instance = _instances.Create("Default");
        instance.MaxMemoryMb = settings.MaxMemoryMb;
        instance.MinMemoryMb = settings.MinMemoryMb;
        instance.ServerName = settings.ServerName;
        instance.ServerAddress = settings.ServerAddress;
        instance.Loader = settings.Loader;
        instance.LoaderVersion = settings.LoaderVersion;
        instance.VersionId = settings.SelectedVersionId;
        _instances.Save(instance);

        return instance;
    }

    [RelayCommand]
    private void CreateInstance()
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(NewInstanceName) ? "New instance" : NewInstanceName.Trim();
            var instance = _instances.Create(name);

            Instances.Add(instance);
            SelectedInstance = instance;
            NewInstanceName = string.Empty;
            Status = Localize("Status_BuildCreated", "Build \"{0}\" created", instance.Name);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_CreateBuild", "Failed to create the build: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteInstance()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        try
        {
            var name = SelectedInstance.Name;
            _instances.Delete(SelectedInstance.Id);
            Instances.Remove(SelectedInstance);
            SelectedInstance = Instances.FirstOrDefault();
            Status = Localize("Status_BuildDeleted", "Build \"{0}\" deleted", name);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_DeleteBuild", "Failed to delete the build: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void DuplicateInstance()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        try
        {
            var sourceName = SelectedInstance.Name;
            var copy = _instances.Duplicate(SelectedInstance.Id);

            Instances.Add(copy);
            SelectedInstance = copy;
            Status = Localize("Status_BuildDuplicated", "Build \"{0}\" created from \"{1}\"", copy.Name, sourceName);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_DuplicateBuild", "Failed to duplicate the build: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void RenameInstance()
    {
        if (SelectedInstance is null || string.IsNullOrWhiteSpace(InstanceNameEdit))
        {
            return;
        }

        try
        {
            SelectedInstance.Name = InstanceNameEdit.Trim();
            _instances.Save(SelectedInstance);

            // The collection holds the same object, so refresh the list item text.
            var index = Instances.IndexOf(SelectedInstance);
            if (index >= 0)
            {
                var current = SelectedInstance;
                Instances[index] = current;
                SelectedInstance = current;
            }

            Status = Localize("Status_BuildRenamed", "Build renamed to \"{0}\"", SelectedInstance.Name);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_RenameBuild", "Failed to rename the build: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task LoadVersionsAsync()
    {
        try
        {
            IsBusy = true;
            Status = Localize("Status_LoadingVersions", "Loading version list…");

            var manifest = await _versions.GetManifestAsync();
            _allVersions = manifest.Versions;
            ApplyVersionFilter();

            Status = SelectedVersion is not null
                ? Localize("Status_VersionsLoaded", "{0} versions loaded", Versions.Count)
                : Localize("Status_NoVersions", "No versions found");
        }
        catch (Exception ex)
        {
            Status = Localize("Error_LoadVersions", "Failed to load versions: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task PlayAsync() => StartAsync(joinServer: false);

    [RelayCommand]
    private Task PlayOnServerAsync() => StartAsync(joinServer: true);

    private async Task StartAsync(bool joinServer)
    {
        if (IsBusy)
        {
            return;
        }

        if (SelectedVersion is null)
        {
            Status = Localize("Status_SelectVersion", "Select a version first");
            return;
        }

        if (!OfflineAuth.IsValidUsername(Username))
        {
            Status = Localize("Status_InvalidNickname", "Nickname: 3-16 characters, letters, digits and underscore");
            return;
        }

        try
        {
            IsBusy = true;
            Progress = 0;
            AppendConsole($"--- Launching {SelectedVersion.Id} as {Username} ---");
            MaybeBackup(BackupTrigger.BeforeLaunch);

            var account = OfflineAuth.Login(Username);
            PersistSettings();

            var versionId = SelectedVersion.Id;

            if (SelectedLoader != LoaderKind.Vanilla)
            {
                Status = Localize("Status_InstallingLoader", "Installing {0}…", SelectedLoader);
                AppendConsole($"--- Installing {SelectedLoader} for {versionId} ---");

                var gameJava = (await _versions.ResolveAsync(versionId)).JavaVersion?.MajorVersion ?? 8;
                var installerLog = new Progress<string>(line => AppendConsole(line));

                versionId = await _loaders.InstallAsync(
                    SelectedLoader,
                    versionId,
                    SelectedLoaderVersion?.Version,
                    gameJava,
                    installerLog);

                AppendConsole($"--- Loader ready: {versionId} ---");
            }

            Status = Localize("Status_CheckingBuildMods", "Checking build mods…");
            await EnsureBuildItemsInstalledAsync();

            var settings = new LaunchSettings
            {
                GameDirectory = InstanceDirectory,
                MaxMemoryMb = (int)MaxMemoryMb,
                MinMemoryMb = (int)MinMemoryMb,
                Width = SelectedInstance?.Width,
                Height = SelectedInstance?.Height,
                ExtraGameArgs = SplitArguments(SelectedInstance?.ExtraGameArgs),
                JavaPath = SelectedJavaChoice?.Path,
                ForceUpdate = ForceUpdate,
                ServerAddress = joinServer && !string.IsNullOrWhiteSpace(ServerAddress) ? ServerAddress : null,
                ServerListName = ServerName,
                ServerListAddress = string.IsNullOrWhiteSpace(ServerAddress) ? null : ServerAddress
            };

            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Status = p.Failed > 0
                    ? Localize("Status_FilesFailed", "Files {0}/{1}, failed: {2}", p.Completed, p.Total, p.Failed)
                    : Localize("Status_Files", "Files {0}/{1}", p.Completed, p.Total);
            });

            Status = Localize("Status_Preparing", "Preparing…");
            var command = await _launch.PrepareAsync(versionId, account, settings, progress);

            Status = Localize("Status_StartingGame", "Starting Minecraft…");
            IsGameRunning = true;

            var exitCode = await LaunchAndReactAsync(command, settings);

            Status = Localize("Status_GameExited", "Game exited with code {0}", exitCode);
            AppendConsole($"--- Game exited with code {exitCode} ---");
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Launch", "Launch failed: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            IsGameRunning = false;
            Progress = 0;

            if (ForceUpdate)
            {
                ForceUpdate = false;
            }
        }
    }

    [RelayCommand]
    private void ClearConsole() => Console.Clear();

    [RelayCommand]
    private void OpenGameFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(InstanceDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = InstanceDirectory,
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

            _catalogSections.Clear();
            CatalogBuilds.Clear();

            if (result.Catalog is not null)
            {
                _loadedCatalog = result.Catalog;
                _catalogSections.AddRange(result.Catalog.Sections);

                foreach (var build in result.Catalog.Builds)
                {
                    CatalogBuilds.Add(build);
                }
            }
            else
            {
                _loadedCatalog = null;
            }

            RebuildCatalogViews();

            var summary = Localize(
                "Catalog_Summary",
                "{0}: {1} section(s), {2} item(s), {3} build(s)",
                result.Catalog?.Name ?? "Catalog",
                CatalogViews.Count,
                result.Catalog?.ItemCount ?? 0,
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
                CatalogStatus += $" — {result.Error}";
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
            MaybeBackup(BackupTrigger.BeforeModChange);
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
            foreach (var mod in _mods.ListMods(InstanceDirectory))
            {
                InstalledMods.Add(mod);
            }

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
            MaybeBackup(BackupTrigger.BeforeModChange);
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
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            IsUpdateBusy = true;
            UpdateStatus = Localize("Update_Checking", "Checking for updates…");

            var status = await _updates.CheckAsync();

            if (!status.IsInstalled)
            {
                UpdateStatus = Localize("Update_OnlyInstalled", "Updates are only available in the installed build");
                CanRestartToUpdate = false;
                return;
            }

            if (!status.IsUpdateAvailable)
            {
                UpdateStatus = Localize("Update_UpToDate", "You are up to date ({0})", status.CurrentVersion);
                CanRestartToUpdate = false;
                return;
            }

            UpdateStatus = Localize("Update_Downloading", "Downloading {0}…", status.AvailableVersion);
            var progress = new Progress<int>(p =>
                UpdateStatus = Localize("Update_DownloadingPercent", "Downloading {0}… {1}%", status.AvailableVersion, p));

            await _updates.DownloadAsync(progress);
            UpdateStatus = Localize("Update_Ready", "Update {0} is ready", status.AvailableVersion);
            CanRestartToUpdate = true;
        }
        catch (Exception ex)
        {
            UpdateStatus = Localize("Update_Failed", "Update check failed: {0}", ex.Message);
            CanRestartToUpdate = false;
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        if (!_updates.ApplyAndRestart())
        {
            UpdateStatus = Localize("Update_NothingToApply", "Nothing to apply");
        }
    }

    [RelayCommand]
    private void ToggleMod(InstalledMod? mod)
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
    private void UninstallMod(InstalledMod? mod)
    {
        if (mod is null)
        {
            return;
        }

        try
        {
            MaybeBackup(BackupTrigger.BeforeModChange);
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

    partial void OnShowSnapshotsChanged(bool value) => ApplyVersionFilter();

    partial void OnSelectedLoaderChanged(LoaderKind value)
    {
        SyncInstance();

        if (!_applyingInstance)
        {
            OnPropertyChanged(nameof(IsBuildConfigured));
            _ = LoadLoaderVersionsAsync();
            ScheduleBrowserReload();
        }
    }

    partial void OnSelectedVersionChanged(VersionSummary? value)
    {
        SyncInstance();

        if (!_applyingInstance)
        {
            OnPropertyChanged(nameof(IsBuildConfigured));
            _ = LoadLoaderVersionsAsync();
            ScheduleBrowserReload();
        }
    }

    partial void OnSelectedLoaderVersionChanged(LoaderVersion? value) => SyncInstance();

    partial void OnMaxMemoryMbChanged(decimal value) => SyncInstance();

    partial void OnMinMemoryMbChanged(decimal value) => SyncInstance();

    partial void OnServerNameChanged(string value) => SyncInstance();

    partial void OnServerAddressChanged(string value) => SyncInstance();

    partial void OnSelectedInstanceChanged(Instance? value)
    {
        if (value is null)
        {
            return;
        }

        _applyingInstance = true;
        try
        {
            MaxMemoryMb = value.MaxMemoryMb;
            MinMemoryMb = value.MinMemoryMb;
            ServerName = value.ServerName ?? "My Server";
            ServerAddress = value.ServerAddress ?? string.Empty;
            SelectedLoader = value.Loader;
            SelectedVersion = value.VersionId is null
                ? null
                : _allVersions.FirstOrDefault(v => v.Id == value.VersionId);
            ExtraGameArgs = value.ExtraGameArgs ?? string.Empty;
            Width = value.Width ?? 0;
            Height = value.Height ?? 0;
            InstanceNameEdit = value.Name;
        }
        finally
        {
            _applyingInstance = false;
        }

        OnPropertyChanged(nameof(BuildModCount));

        _ = LoadLoaderVersionsAsync();
        RefreshMods();
        RebuildCatalogViews();
        RefreshBrowserInstallState();
        OnPropertyChanged(nameof(IsBuildConfigured));
        ScheduleBrowserReload();
        PersistSettings();
    }

    /// <summary>Writes the current editing values back into the selected instance.</summary>
    private void SyncInstance()
    {
        if (_applyingInstance || SelectedInstance is null)
        {
            return;
        }

        SelectedInstance.VersionId = SelectedVersion?.Id;
        SelectedInstance.Loader = SelectedLoader;
        SelectedInstance.LoaderVersion = SelectedLoaderVersion?.Version;
        SelectedInstance.MaxMemoryMb = (int)MaxMemoryMb;
        SelectedInstance.MinMemoryMb = (int)MinMemoryMb;
        SelectedInstance.ServerName = ServerName;
        SelectedInstance.ServerAddress = string.IsNullOrWhiteSpace(ServerAddress) ? null : ServerAddress;
        SelectedInstance.ExtraGameArgs = string.IsNullOrWhiteSpace(ExtraGameArgs) ? null : ExtraGameArgs;
        SelectedInstance.Width = Width > 0 ? (int)Width : null;
        SelectedInstance.Height = Height > 0 ? (int)Height : null;

        try
        {
            _instances.Save(SelectedInstance);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_SaveBuild", "Failed to save the build: {0}", ex.Message);
        }
    }

    partial void OnUsernameChanged(string value) => _ = UpdateAvatarAsync();


    partial void OnCatalogUrlChanged(string value) => _catalog.CatalogUrl = value;

    partial void OnSectionChanged(ShellSection value)
    {
        OnPropertyChanged(nameof(IsGameSection));
        OnPropertyChanged(nameof(IsBuildsSection));
        OnPropertyChanged(nameof(IsServerSection));
        OnPropertyChanged(nameof(IsConsoleSection));
        OnPropertyChanged(nameof(IsSettingsSection));
    }

    partial void OnLanguageChanged(string value)
    {
        _localization.Apply(value);
        LoadAfterLaunchOptions();
        ReloadLocalizedBrowserOptions();
        PersistSettings();
    }

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (Enum.TryParse<ShellSection>(section, ignoreCase: true, out var parsed))
        {
            Section = parsed;
        }
    }

    private async Task UpdateAvatarAsync()
    {
        _avatarCts?.Cancel();
        var cts = new CancellationTokenSource();
        _avatarCts = cts;

        try
        {
            await Task.Delay(400, cts.Token);
            var bitmap = await _skins.GetAvatarAsync(Username, cts.Token);

            if (!cts.IsCancellationRequested)
            {
                Avatar = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadLoaderVersionsAsync()
    {
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;

        if (SelectedLoader == LoaderKind.Vanilla || SelectedVersion is null)
        {
            return;
        }

        try
        {
            IsLoaderBusy = true;
            var previous = SelectedLoaderVersion;
            var versions = await _loaders.GetLoaderVersionsAsync(SelectedLoader, SelectedVersion.Id);
            var list = versions.ToList();

            foreach (var version in list)
            {
                LoaderVersions.Add(version);
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Version == previous?.Version)
                                    ?? LoaderVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_LoaderVersions", "Failed to load {0} versions: {1}", SelectedLoader, ex.Message);
        }
        finally
        {
            IsLoaderBusy = false;
        }
    }

    private void ApplyVersionFilter()
    {
        var selectedId = SelectedVersion?.Id;

        var filtered = _allVersions.Where(IsVersionVisible).ToList();

        Versions.Clear();
        foreach (var version in filtered)
        {
            Versions.Add(version);
        }

        SelectedVersion = selectedId is not null
            ? Versions.FirstOrDefault(v => v.Id == selectedId) ?? Versions.FirstOrDefault()
            : Versions.FirstOrDefault();
    }

    private bool IsVersionVisible(VersionSummary version) => version.Type switch
    {
        "release" => !IsOldRelease(version.Id) || ShowOldReleases,
        "snapshot" => ShowSnapshots,
        "old_beta" => ShowBeta,
        "old_alpha" => ShowAlpha,
        _ => false
    };

    private void PersistSettings()
    {
        var settings = new AppSettings
        {
            Username = Username,
            ShowSnapshots = ShowSnapshots,
            CatalogUrl = string.IsNullOrWhiteSpace(CatalogUrl) ? null : CatalogUrl,
            Language = Language,
            ShowDeveloperConsole = ShowDeveloperConsole,
            SelectedInstanceId = SelectedInstance?.Id,

            Nicknames = Nicknames.ToList(),
            ShowOldReleases = ShowOldReleases,
            ShowBeta = ShowBeta,
            ShowAlpha = ShowAlpha,
            JavaPath = SelectedJavaChoice?.Path,
            AfterLaunch = AfterLaunch,
            ForceUpdate = ForceUpdate,
            BackupsEnabled = BackupsEnabled,
            BackupsBeforeLaunch = BackupsBeforeLaunch,
            BackupsDaily = BackupsDaily,
            BackupsBeforeModChanges = BackupsBeforeModChanges,
            BackupsMaxCount = (int)BackupsMaxCount,
            BackupsMaxTotalMb = (int)BackupsMaxTotalMb,
            BackupsDirectory = string.IsNullOrWhiteSpace(_backupDirectoryOverride) ? null : _backupDirectoryOverride,

            // Legacy global fields, kept so older settings files can be migrated into an instance.
            SelectedVersionId = SelectedVersion?.Id,
            Loader = SelectedLoader,
            LoaderVersion = SelectedLoaderVersion?.Version,
            MaxMemoryMb = (int)MaxMemoryMb,
            MinMemoryMb = (int)MinMemoryMb,
            ServerName = ServerName,
            ServerAddress = string.IsNullOrWhiteSpace(ServerAddress) ? null : ServerAddress
        };

        _settings.Save(settings);
    }

    private void AppendConsole(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Console.Add(line);
            while (Console.Count > 2000)
            {
                Console.RemoveAt(0);
            }
        });
    }
}




