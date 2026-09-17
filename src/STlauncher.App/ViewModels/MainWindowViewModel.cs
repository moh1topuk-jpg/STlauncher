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
using STlauncher.Core.Content;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
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
    private readonly ModrinthClient _modrinth;
    private readonly CurseForgeClient _curseForge;
    private readonly ModManager _mods;
    private readonly ModpackInstaller _modpacks;
    private readonly ContentCatalogService _catalog;
    private readonly CatalogInstaller _catalogInstaller;
    private readonly UpdateService _updates;
    private readonly InstanceManager _instances;
    private readonly LocalizationService _localization;
    private readonly LauncherPaths _paths;
    private readonly GameLauncher _gameLauncher;

    private List<VersionSummary> _allVersions = new();
    private bool _initialized;
    private bool _applyingInstance;
    private CancellationTokenSource? _avatarCts;

    public MainWindowViewModel(
        VersionService versions,
        LaunchService launch,
        LoaderService loaders,
        SettingsService settings,
        SkinService skins,
        ModrinthClient modrinth,
        CurseForgeClient curseForge,
        ModManager mods,
        ModpackInstaller modpacks,
        ContentCatalogService catalog,
        CatalogInstaller catalogInstaller,
        UpdateService updates,
        InstanceManager instances,
        LocalizationService localization,
        LauncherPaths paths,
        GameLauncher gameLauncher)
    {
        _versions = versions;
        _launch = launch;
        _loaders = loaders;
        _settings = settings;
        _skins = skins;
        _modrinth = modrinth;
        _curseForge = curseForge;
        _mods = mods;
        _modpacks = modpacks;
        _catalog = catalog;
        _catalogInstaller = catalogInstaller;
        _updates = updates;
        _instances = instances;
        _localization = localization;
        _paths = paths;
        _gameLauncher = gameLauncher;
    }

    public ObservableCollection<Instance> Instances { get; } = new();

    public ObservableCollection<VersionSummary> Versions { get; } = new();

    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = new();

    public ObservableCollection<string> Console { get; } = new();

    public ObservableCollection<ModSearchResult> ModSearchResults { get; } = new();

    public ObservableCollection<InstalledMod> InstalledMods { get; } = new();

    public ObservableCollection<CatalogSection> CatalogSections { get; } = new();

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
    private ModSearchResult? _selectedMod;

    [ObservableProperty]
    private bool _isModsBusy;

    [ObservableProperty]
    private string _updateStatus = "Updates are only available in the installed build.";

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
    private string _curseForgeApiKey = string.Empty;

    [ObservableProperty]
    private string _catalogUrl = string.Empty;

    [ObservableProperty]
    private string _catalogStatus = "Catalog not loaded yet.";

    [ObservableProperty]
    private bool _isCatalogBusy;

    [ObservableProperty]
    private ShellSection _section = ShellSection.Game;

    [ObservableProperty]
    private string _language = LocalizationService.DefaultLanguage;

    [ObservableProperty]
    private bool _showDeveloperConsole;

    public bool IsGameSection => Section == ShellSection.Game;
    public bool IsModsSection => Section == ShellSection.Mods;
    public bool IsContentSection => Section == ShellSection.Content;
    public bool IsServerSection => Section == ShellSection.Server;
    public bool IsConsoleSection => Section == ShellSection.Console;
    public bool IsSettingsSection => Section == ShellSection.Settings;

    public IReadOnlyList<string> Languages => _localization.AvailableLanguages;

    [ObservableProperty]
    private string _status = "Ready";

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
        CurseForgeApiKey = settings.CurseForgeApiKey ?? string.Empty;
        _curseForge.ApiKey = settings.CurseForgeApiKey;
        CatalogUrl = settings.CatalogUrl ?? string.Empty;
        _catalog.CatalogUrl = settings.CatalogUrl;
        Language = LocalizationService.Normalize(settings.Language);
        ShowDeveloperConsole = settings.ShowDeveloperConsole;

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
            Status = $"Instance '{instance.Name}' created.";
        }
        catch (Exception ex)
        {
            Status = "Failed to create instance: " + ex.Message;
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
            Status = $"Instance '{name}' deleted.";
        }
        catch (Exception ex)
        {
            Status = "Failed to delete instance: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task LoadVersionsAsync()
    {
        try
        {
            IsBusy = true;
            Status = "Loading version list...";

            var manifest = await _versions.GetManifestAsync();
            _allVersions = manifest.Versions;
            ApplyVersionFilter();

            Status = SelectedVersion is not null
                ? $"Loaded {Versions.Count} versions."
                : "No versions available.";
        }
        catch (Exception ex)
        {
            Status = "Failed to load versions: " + ex.Message;
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
            Status = "Select a version first.";
            return;
        }

        if (!OfflineAuth.IsValidUsername(Username))
        {
            Status = "Nickname must be 3-16 characters: A-Z, a-z, 0-9, underscore.";
            return;
        }

        try
        {
            IsBusy = true;
            Progress = 0;
            AppendConsole($"--- Launching {SelectedVersion.Id} as {Username} ---");

            var account = OfflineAuth.Login(Username);
            PersistSettings();

            var versionId = SelectedVersion.Id;

            if (SelectedLoader != LoaderKind.Vanilla)
            {
                Status = $"Installing {SelectedLoader}...";
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

            var settings = new LaunchSettings
            {
                GameDirectory = InstanceDirectory,
                MaxMemoryMb = (int)MaxMemoryMb,
                MinMemoryMb = (int)MinMemoryMb,
                ServerAddress = joinServer && !string.IsNullOrWhiteSpace(ServerAddress) ? ServerAddress : null,
                ServerListName = ServerName,
                ServerListAddress = string.IsNullOrWhiteSpace(ServerAddress) ? null : ServerAddress
            };

            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Status = p.Failed > 0
                    ? $"Files {p.Completed}/{p.Total} (failed: {p.Failed})"
                    : $"Files {p.Completed}/{p.Total}";
            });

            Status = "Preparing...";
            var command = await _launch.PrepareAsync(versionId, account, settings, progress);

            Status = "Starting Minecraft...";
            IsGameRunning = true;
            var exitCode = await _launch.LaunchAsync(command, settings.GameDirectory);
            Status = $"Game exited with code {exitCode}.";
            AppendConsole($"--- Game exited with code {exitCode} ---");
        }
        catch (Exception ex)
        {
            Status = "Launch failed: " + ex.Message;
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            IsGameRunning = false;
            Progress = 0;
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
            Status = "Failed to open the game folder: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SearchModsAsync()
    {
        if (SelectedVersion is null)
        {
            Status = "Select a version first.";
            return;
        }

        if (SelectedLoader == LoaderKind.Vanilla)
        {
            Status = "Select a mod loader before searching for mods.";
            return;
        }

        try
        {
            IsModsBusy = true;
            Status = "Searching Modrinth...";

            var results = await _modrinth.SearchAsync(ModSearchQuery, SelectedVersion.Id, SelectedLoader);
            ModSearchResults.Clear();

            foreach (var result in results)
            {
                ModSearchResults.Add(result);
            }

            Status = $"Found {ModSearchResults.Count} mods.";
        }
        catch (Exception ex)
        {
            Status = "Mod search failed: " + ex.Message;
        }
        finally
        {
            IsModsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallSelectedModAsync()
    {
        if (SelectedMod is null || SelectedVersion is null)
        {
            return;
        }

        try
        {
            IsModsBusy = true;
            Status = $"Resolving {SelectedMod.Title}...";

            var versions = await _modrinth.GetVersionsAsync(SelectedMod.ProjectId, SelectedVersion.Id, SelectedLoader);
            var file = versions.FirstOrDefault()?.PrimaryFile;

            if (file is null || string.IsNullOrEmpty(file.Url))
            {
                Status = "No compatible file found for this version and loader.";
                return;
            }

            Status = $"Installing {file.FileName}...";
            await _mods.InstallAsync(InstanceDirectory, file.FileName, file.Url, file.Sha1, file.Size);
            AppendConsole($"Installed mod: {file.FileName}");
            RefreshMods();
            Status = $"Installed {file.FileName}.";
        }
        catch (Exception ex)
        {
            Status = "Mod install failed: " + ex.Message;
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsModsBusy = false;
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
            CatalogStatus = "Loading catalog...";

            var result = await _catalog.LoadAsync();
            CatalogSections.Clear();

            if (result.Catalog is not null)
            {
                foreach (var section in result.Catalog.Sections)
                {
                    CatalogSections.Add(section);
                }
            }

            var summary = $"{result.Catalog?.Name ?? "Catalog"}: " +
                          $"{CatalogSections.Count} section(s), {result.Catalog?.ItemCount ?? 0} item(s).";

            if (result.Catalog is null)
            {
                CatalogStatus = string.IsNullOrWhiteSpace(CatalogUrl)
                    ? $"No catalog found. Put {ContentCatalogService.LocalFileName} next to settings.json " +
                      $"({_catalog.DropInPath}) or set a URL/path in Settings."
                    : $"Failed to load the catalog: {result.Error}";
                return;
            }

            CatalogStatus = result.Origin switch
            {
                CatalogOrigin.Remote => summary,
                CatalogOrigin.LocalFile => $"{summary} (local file)",
                CatalogOrigin.Cache => $"{summary} (cached copy: source unavailable)",
                CatalogOrigin.DropIn => $"{summary} (local file)",
                _ => summary
            };

            if (result.Error is not null && result.Origin is not CatalogOrigin.Remote)
            {
                CatalogStatus += $" — {result.Error}";
            }
        }
        catch (Exception ex)
        {
            CatalogStatus = "Failed to load the catalog: " + ex.Message;
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
            Status = $"Installing {item.Name}...";
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
                RefreshMods();
            }
        }
        catch (Exception ex)
        {
            Status = "Catalog install failed: " + ex.Message;
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
            InstalledMods.Clear();
            foreach (var mod in _mods.ListMods(InstanceDirectory))
            {
                InstalledMods.Add(mod);
            }
        }
        catch (Exception ex)
        {
            Status = "Failed to read mods: " + ex.Message;
        }
    }

    public async Task ImportModpackAsync(string mrpackPath)
    {
        try
        {
            IsModsBusy = true;
            Status = "Reading modpack...";

            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Status = $"Modpack files {p.Completed}/{p.Total}";
            });

            var result = await _modpacks.InstallAsync(mrpackPath, InstanceDirectory, progress);
            AppendConsole(
                $"--- Modpack '{result.Plan.Name}' ({result.Plan.Format}) installed: " +
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
            Status = $"Modpack '{result.Plan.Name}' installed.";
        }
        catch (Exception ex)
        {
            Status = "Modpack import failed: " + ex.Message;
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
            UpdateStatus = "Checking for updates...";

            var status = await _updates.CheckAsync();

            if (!status.IsInstalled)
            {
                UpdateStatus = "Updates are only available in the installed build.";
                CanRestartToUpdate = false;
                return;
            }

            if (!status.IsUpdateAvailable)
            {
                UpdateStatus = $"You are up to date ({status.CurrentVersion}).";
                CanRestartToUpdate = false;
                return;
            }

            UpdateStatus = $"Downloading {status.AvailableVersion}...";
            var progress = new Progress<int>(p => UpdateStatus = $"Downloading {status.AvailableVersion}... {p}%");
            await _updates.DownloadAsync(progress);
            UpdateStatus = $"Update {status.AvailableVersion} is ready.";
            CanRestartToUpdate = true;
        }
        catch (Exception ex)
        {
            UpdateStatus = "Update check failed: " + ex.Message;
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
            UpdateStatus = "Nothing to apply.";
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
            _mods.SetEnabled(mod.Path, !mod.Enabled);
            RefreshMods();
        }
        catch (Exception ex)
        {
            Status = "Failed to toggle mod: " + ex.Message;
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
            _mods.Uninstall(mod.Path);
            RefreshMods();
        }
        catch (Exception ex)
        {
            Status = "Failed to remove mod: " + ex.Message;
        }
    }

    partial void OnShowSnapshotsChanged(bool value) => ApplyVersionFilter();

    partial void OnSelectedLoaderChanged(LoaderKind value)
    {
        SyncInstance();
        if (!_applyingInstance) _ = LoadLoaderVersionsAsync();
    }

    partial void OnSelectedVersionChanged(VersionSummary? value)
    {
        SyncInstance();
        if (!_applyingInstance) _ = LoadLoaderVersionsAsync();
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
        }
        finally
        {
            _applyingInstance = false;
        }

        _ = LoadLoaderVersionsAsync();
        RefreshMods();
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

        try
        {
            _instances.Save(SelectedInstance);
        }
        catch (Exception ex)
        {
            Status = "Failed to save instance: " + ex.Message;
        }
    }

    partial void OnUsernameChanged(string value) => _ = UpdateAvatarAsync();

    partial void OnCurseForgeApiKeyChanged(string value)
        => _curseForge.ApiKey = string.IsNullOrWhiteSpace(value) ? null : value;

    partial void OnCatalogUrlChanged(string value) => _catalog.CatalogUrl = value;

    partial void OnSectionChanged(ShellSection value)
    {
        OnPropertyChanged(nameof(IsGameSection));
        OnPropertyChanged(nameof(IsModsSection));
        OnPropertyChanged(nameof(IsContentSection));
        OnPropertyChanged(nameof(IsServerSection));
        OnPropertyChanged(nameof(IsConsoleSection));
        OnPropertyChanged(nameof(IsSettingsSection));
    }

    partial void OnLanguageChanged(string value)
    {
        _localization.Apply(value);
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
            Status = $"Failed to load {SelectedLoader} versions: {ex.Message}";
        }
        finally
        {
            IsLoaderBusy = false;
        }
    }

    private void ApplyVersionFilter()
    {
        var selectedId = SelectedVersion?.Id;

        var filtered = ShowSnapshots
            ? _allVersions
            : _allVersions.Where(v => v.Type == "release").ToList();

        Versions.Clear();
        foreach (var version in filtered)
        {
            Versions.Add(version);
        }

        SelectedVersion = selectedId is not null
            ? Versions.FirstOrDefault(v => v.Id == selectedId) ?? Versions.FirstOrDefault()
            : Versions.FirstOrDefault();
    }

    private void PersistSettings()
    {
        var settings = new AppSettings
        {
            Username = Username,
            ShowSnapshots = ShowSnapshots,
            CurseForgeApiKey = string.IsNullOrWhiteSpace(CurseForgeApiKey) ? null : CurseForgeApiKey,
            CatalogUrl = string.IsNullOrWhiteSpace(CatalogUrl) ? null : CatalogUrl,
            Language = Language,
            ShowDeveloperConsole = ShowDeveloperConsole,
            SelectedInstanceId = SelectedInstance?.Id,

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