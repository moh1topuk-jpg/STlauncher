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
using STlauncher.Core.Server;
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
    private readonly TranslationService _translations;
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
    private CancellationTokenSource? _loaderVersionsCts;


    public MainWindowViewModel(
        VersionService versions,
        LaunchService launch,
        LoaderService loaders,
        SettingsService settings,
        SkinService skins,
        RemoteImageService images,
        TranslationService translations,
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
        _translations = translations;
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

    /// <summary>Drives the notification banner: an update was found and the user has not dismissed it.</summary>
    [ObservableProperty]
    private bool _isUpdateBannerVisible;

    [ObservableProperty]
    private string _updateBannerText = string.Empty;

    [ObservableProperty]
    private string _updateBannerAction = string.Empty;

    /// <summary>Version offered by the banner, kept so the install button knows what it is applying.</summary>
    [ObservableProperty]
    private string _availableUpdateVersion = string.Empty;


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

    /// <summary>The launch progress block is shown while preparing or running.</summary>
    public bool ShowLaunchProgress => IsBusy || IsGameRunning;

    /// <summary>Reminder that the server button connects straight to the server.</summary>
    public string ServerJoinHint
        => Localize("Game_ServerJoinHint", "Play on server connects you to {0}", ServerAddress);

    [ObservableProperty]
    private Bitmap? _serverLogo;

    /// <summary>Icon reported by the server itself; falls back to the launcher artwork.</summary>
    [ObservableProperty]
    private Bitmap? _serverIcon;

    [ObservableProperty]
    private string _serverMotd = string.Empty;

    [ObservableProperty]
    private string _serverPlayers = string.Empty;

    [ObservableProperty]
    private bool _isServerStatusBusy;

    public ObservableCollection<ServerHistoryBarView> ServerHistoryBars { get; } = new();

    [ObservableProperty]
    private bool _hasServerHistory;

    [ObservableProperty]
    private string _serverAverage = string.Empty;

    [ObservableProperty]
    private string _serverPeak = string.Empty;

    [ObservableProperty]
    private string _serverMonitoringNote = string.Empty;

    private ServerHistoryStore _serverHistory = null!;
    private DispatcherTimer? _serverTimer;
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _consoleTimer;

    /// <summary>Game output arrives on background threads; the UI drains it in batches.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingConsoleLines = new();

    private const int MaxConsoleLines = 2000;

    /// <summary>Highest bar in the window; used to scale the chart.</summary>
    private const double ChartHeight = 80;

    [RelayCommand]
    private async Task RefreshServerStatusAsync()
    {
        if (IsServerStatusBusy)
        {
            return;
        }

        try
        {
            IsServerStatusBusy = true;

            var status = await ServerPinger.PingAsync(ServerAddress);

            if (status is null)
            {
                ServerMotd = Localize("Server_Offline", "Server did not respond");
                ServerPlayers = string.Empty;
                ServerIcon = ServerLogo;
                RefreshServerHistory();
                return;
            }

            ServerMotd = status.Motd;
            ServerPlayers = Localize("Server_Players", "{0} / {1} online", status.Online, status.Max);
            ServerIcon = status.Favicon is { Length: > 0 }
                ? CreateBitmap(status.Favicon) ?? ServerLogo
                : ServerLogo;

            _serverHistory.Add(status.Online);
            RefreshServerHistory();
        }
        catch (Exception ex)
        {
            AppendConsole($"[server] {ex.Message}");
        }
        finally
        {
            IsServerStatusBusy = false;
        }
    }

    private void RefreshServerHistory()
    {
        var window = TimeSpan.FromDays(3);
        var bars = _serverHistory.GetHourlyBars(window);
        var peak = bars.Count == 0 ? 0 : bars.Max(b => b.Peak);
        var hasSamples = bars.Any(b => b.Peak > 0);

        ServerHistoryBars.Clear();

        foreach (var bar in bars)
        {
            var hasData = bar.Peak > 0;
            var fraction = peak == 0 ? 0 : bar.Average / peak;

            ServerHistoryBars.Add(new ServerHistoryBarView(
                hasData ? Math.Max(4, fraction * ChartHeight) : 3,
                hasData
                    ? Localize(
                        "Server_BarTooltip",
                        "{0} - {1} (peak {2})",
                        bar.Label,
                        bar.Average.ToString("F0", System.Globalization.CultureInfo.CurrentCulture),
                        bar.Peak)
                    : string.Empty,
                !hasData));
        }

        HasServerHistory = hasSamples;

        var (average, top) = _serverHistory.GetSummary(window);
        ServerAverage = HasServerHistory
            ? Localize("Server_Average", "Average: {0:F0}", average)
            : string.Empty;
        ServerPeak = HasServerHistory
            ? Localize("Server_Peak", "Peak: {0}", top)
            : string.Empty;
        ServerMonitoringNote = HasServerHistory
            ? string.Empty
            : Localize("Server_NoData", "No data yet - samples appear as the launcher runs.");
    }

    private void StartServerTimer()
    {
        _serverTimer?.Stop();

        // One sample every ten minutes while the launcher is open.
        _serverTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _serverTimer.Tick += (_, _) => _ = RefreshServerStatusAsync();
        _serverTimer.Start();
    }

    private static Bitmap? CreateBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new System.IO.MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads Assets/server-logo.png when it exists. A missing file simply leaves the
    /// placeholder in place, so the build never depends on an artwork asset.
    /// </summary>
    private void LoadServerLogo()
    {
        try
        {
            var uri = new Uri("avares://STlauncher.App/Assets/server-logo.png");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            ServerLogo = new Bitmap(stream);
        }
        catch (Exception)
        {
            ServerLogo = null;
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowLaunchProgress));

    partial void OnIsGameRunningChanged(bool value) => OnPropertyChanged(nameof(ShowLaunchProgress));

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

        // The launcher exists for one server: its address is fixed, not per build or user.
        ServerName = ServerDefaults.Name;
        ServerAddress = ServerDefaults.Address;
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
        LoadServerLogo();
        RefreshBackups();

        _serverHistory = new ServerHistoryStore(
            System.IO.Path.Combine(_paths.Root, "server-history.json"));

        _ = RefreshServerStatusAsync();
        StartServerTimer();

        Status = Localize("Status_Ready", "Ready");
        CatalogStatus = Localize("Catalog_NotLoaded", "Catalog not loaded yet");
        UpdateStatus = _updates.IsSupported
            ? Localize("Update_Idle", "Current version: {0}", _updates.CurrentVersion)
            : Localize("Update_OnlyInstalled", "Updates are only available in the installed build");


        _gameLauncher.OutputReceived += line => AppendConsole(line);
        _gameLauncher.ErrorReceived += line => AppendConsole(line);
        StartConsoleFlusher();


        _allInstances.Clear();
        _allInstances.AddRange(_instances.List());

        // The catalog has to be read before the first instance is created: a fresh
        // installation should start from the recommended build, not from an empty profile.
        await LoadCatalogAsync();

        if (_allInstances.Count == 0)
        {
            if (_instances.HasAnyInstanceDirectory())
            {
                // A build folder is right there but its definition could not be read.
                // Creating a new profile would bury it - and its worlds and mods - forever.
                Status = Localize(
                    "Instance_Unreadable",
                    "A build could not be read and was left untouched: {0}",
                    string.Join(", ", _instances.UnreadableDefinitions.Select(System.IO.Path.GetFileName)));
            }
            else
            {
                _allInstances.Add(CreateDefaultInstance(settings));
            }
        }

        ApplyBuildFilter();

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
        await LoadCategoriesAsync();
        ScheduleBrowserReload();

        StartUpdateWatcher();
    }

    /// <summary>
    /// Looks for a new release shortly after startup and then every six hours, so a
    /// launcher left open for days still notices a release.
    /// </summary>
    private void StartUpdateWatcher()
    {
        if (!_updates.IsSupported)
        {
            return;
        }

        // Deliberately delayed: startup already saturates the network with catalog,
        // version manifest and mod icon requests, and the banner is not urgent.
        _updateTimer?.Stop();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _updateTimer.Tick += (_, _) =>
        {
            // After the first tick settle into the long interval.
            _updateTimer!.Interval = TimeSpan.FromHours(6);
            _ = CheckForUpdatesQuietlyAsync();
        };
        _updateTimer.Start();
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

    /// <summary>
    /// First run. When the catalog offers a recommended build the profile is created from
    /// it, so a new device starts with the right version, loader and mods instead of an
    /// empty instance the player would have to configure by hand.
    /// </summary>
    private Instance CreateDefaultInstance(AppSettings settings)
    {
        var build = CatalogBuilds.FirstOrDefault();

        if (build is null)
        {
            var legacy = _instances.Create("Default");
            legacy.MaxMemoryMb = settings.MaxMemoryMb;
            legacy.MinMemoryMb = settings.MinMemoryMb;
            legacy.Loader = settings.Loader;
            legacy.LoaderVersion = settings.LoaderVersion;
            legacy.VersionId = settings.SelectedVersionId;
            _instances.Save(legacy);

            return legacy;
        }

        var instance = _instances.Create(build.Name);
        instance.VersionId = build.GameVersion;
        instance.Loader = build.Loader;
        instance.LoaderVersion = build.LoaderVersion;
        instance.EnabledCatalogItems = build.Items.ToList();

        if (build.MemoryMb is > 0)
        {
            instance.MaxMemoryMb = build.MemoryMb.Value;
        }

        _instances.Save(instance);

        AppendConsole($"[setup] created '{instance.Name}' from the recommended build");
        return instance;
    }

    [RelayCommand]
    private void CreateInstance()
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(NewInstanceName) ? "New instance" : NewInstanceName.Trim();
            var instance = _instances.Create(name);

            _allInstances.Add(instance);
            ApplyBuildFilter();
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
            _allInstances.RemoveAll(i => i.Id == SelectedInstance.Id);
            ApplyBuildFilter();
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

            _allInstances.Add(copy);
            ApplyBuildFilter();
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
            RefreshBuildListItem(SelectedInstance);
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
                ServerListAddress = string.IsNullOrWhiteSpace(ServerAddress) ? null : ServerAddress,
                LanguageCode = Language == "en" ? "en_us" : "ru_ru"
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

            if (SelectedInstance is not null)
            {
                SelectedInstance.LastPlayedAt = DateTimeOffset.Now;
                _instances.Save(SelectedInstance);
                RefreshBuildListItem(SelectedInstance);
            }

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
    private void ClearConsole()
    {
        Console.Clear();

        // Drop anything still queued, otherwise a cleared console refills a moment later.
        while (_pendingConsoleLines.TryDequeue(out _))
        {
        }
    }


    [RelayCommand]
    private void OpenWebsite() => OpenUrl(ServerDefaults.Website);

    /// <summary>Opens an external link in the default browser.</summary>
    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenLink", "Failed to open the link: {0}", ex.Message);
        }
    }

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

            CatalogBuilds.Clear();

            if (result.Catalog is not null)
            {
                _loadedCatalog = result.Catalog;

                foreach (var build in result.Catalog.Builds)
                {
                    CatalogBuilds.Add(build);
                }
            }
            else
            {
                _loadedCatalog = null;
            }

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

    /// <summary>
    /// The manual check from Settings. Reports every outcome, including "you are up to date".
    /// </summary>
    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunUpdateCheckAsync(announce: true);

    /// <summary>
    /// The automatic check. Stays quiet unless an update is actually waiting, so a missing
    /// network connection or a portable build never produces a pointless popup.
    /// </summary>
    private Task CheckForUpdatesQuietlyAsync() => RunUpdateCheckAsync(announce: false);

    private async Task RunUpdateCheckAsync(bool announce)
    {
        if (IsUpdateBusy)
        {
            return;
        }

        try
        {
            IsUpdateBusy = true;

            if (announce)
            {
                UpdateStatus = Localize("Update_Checking", "Checking for updates…");
            }

            var status = await _updates.CheckAsync();

            if (!status.IsSupported)
            {
                if (announce)
                {
                    UpdateStatus = Localize(
                        "Update_OnlyInstalled",
                        "Updates are only available in the installed build");
                }

                return;
            }

            if (!status.IsUpdateAvailable)
            {
                IsUpdateBannerVisible = false;
                CanRestartToUpdate = false;

                if (announce)
                {
                    UpdateStatus = Localize("Update_UpToDate", "You are up to date ({0})", status.CurrentVersion);
                }

                return;
            }

            AvailableUpdateVersion = status.AvailableVersion ?? string.Empty;

            // Velopack may already have the package on disk from an earlier session, in
            // which case the button only has to restart.
            var ready = _updates.IsReadyToApply;

            UpdateBannerText = ready
                ? Localize("Update_ReadyBanner", "Update {0} is ready to install", AvailableUpdateVersion)
                : Localize("Update_AvailableBanner", "Version {0} is available", AvailableUpdateVersion);

            UpdateBannerAction = ready
                ? Localize("Update_InstallAndRestart", "Install and restart")
                : Localize("Update_InstallNow", "Update now");

            UpdateStatus = UpdateBannerText;
            CanRestartToUpdate = ready;
            IsUpdateBannerVisible = true;
        }
        catch (Exception ex)
        {
            if (announce)
            {
                UpdateStatus = Localize("Update_Failed", "Update check failed: {0}", ex.Message);
            }
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    /// <summary>
    /// The one button the user presses: downloads the package if it is not on disk yet,
    /// then applies it and relaunches. Progress goes straight into the banner text.
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (IsUpdateBusy)
        {
            return;
        }

        try
        {
            IsUpdateBusy = true;
            var version = AvailableUpdateVersion;

            if (!_updates.IsReadyToApply)
            {
                void Report(string text)
                {
                    UpdateBannerText = text;
                    UpdateStatus = text;
                }

                Report(Localize("Update_Downloading", "Downloading {0}…", version));

                var progress = new Progress<int>(percent => Report(
                    Localize("Update_DownloadingPercent", "Downloading {0}… {1}%", version, percent)));

                await _updates.DownloadAsync(progress);
            }

            UpdateBannerText = Localize("Update_Restarting", "Restarting to finish the update…");
            UpdateStatus = UpdateBannerText;

            // Hands control to Velopack, which replaces this process. Nothing below runs
            // unless there turned out to be nothing to apply.
            if (!_updates.ApplyAndRestart())
            {
                UpdateStatus = Localize("Update_NothingToApply", "Nothing to apply");
                UpdateBannerText = UpdateStatus;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = Localize("Update_Failed", "Update check failed: {0}", ex.Message);
            UpdateBannerText = Localize("Update_FailedBanner", "Update failed - try again later");
            CanRestartToUpdate = _updates.IsReadyToApply;
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    /// <summary>Hides the banner for this session. Settings still offers the update.</summary>
    [RelayCommand]
    private void DismissUpdateBanner() => IsUpdateBannerVisible = false;


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

    partial void OnServerAddressChanged(string value)
    {
        SyncInstance();
        OnPropertyChanged(nameof(ServerJoinHint));
    }

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
        OnPropertyChanged(nameof(ServerJoinHint));
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
        var previous = _avatarCts;
        var cts = new CancellationTokenSource();
        _avatarCts = cts;

        // This runs on every keystroke in the nickname box; leaving the old sources
        // undisposed leaks a timer registration each time.
        previous?.Cancel();
        previous?.Dispose();

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
        // Changing the version and the loader in quick succession starts two of these.
        // Without superseding, both clear and both append, producing a merged list.
        var previousCts = _loaderVersionsCts;
        var cts = new CancellationTokenSource();
        _loaderVersionsCts = cts;
        previousCts?.Cancel();
        previousCts?.Dispose();

        // Captured before the list is cleared - reading it afterwards always gave null,
        // so the pinned loader build was silently replaced on every refresh.
        var previous = SelectedLoaderVersion;

        LoaderVersions.Clear();
        SelectedLoaderVersion = null;

        if (SelectedLoader == LoaderKind.Vanilla || SelectedVersion is null)
        {
            return;
        }

        var loader = SelectedLoader;
        var versionId = SelectedVersion.Id;

        try
        {
            IsLoaderBusy = true;
            var versions = await _loaders.GetLoaderVersionsAsync(loader, versionId);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var version in versions.ToList())
            {
                LoaderVersions.Add(version);
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Version == previous?.Version)
                                    ?? LoaderVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                Status = Localize("Error_LoaderVersions", "Failed to load {0} versions: {1}", loader, ex.Message);
            }
        }
        finally
        {
            // Only the current run owns the busy flag.
            if (_loaderVersionsCts == cts)
            {
                IsLoaderBusy = false;
            }
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

    /// <summary>
    /// Reports a failure raised while the window was starting up. The app stays open so the
    /// message is readable instead of disappearing with the process.
    /// </summary>
    public void ReportStartupFailure(Exception ex)
    {
        Status = Localize("Error_Startup", "Startup failed: {0}", ex.Message);
        AppendConsole($"[startup] {ex}");
        FlushConsole();
    }

    /// <summary>Reports a failure from a view event handler that has no other channel.</summary>
    public void ReportUiFailure(Exception ex)
    {
        Status = Localize("Error_Ui", "Something went wrong: {0}", ex.Message);
        AppendConsole($"[ui] {ex}");
    }

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

        try
        {
            _settings.Save(settings);
        }
        catch (Exception ex)
        {
            // Settings are persisted from a dozen property handlers and from startup.
            // Letting an IOException escape an async void handler used to take the whole
            // app down with no diagnostic.
            Status = Localize("Error_SaveSettings", "Could not save settings: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Queues a console line. The actual list update is batched on a timer: Minecraft
    /// emits thousands of lines during startup, and posting one dispatcher work item per
    /// line makes the window unresponsive exactly when the user is watching it.
    /// </summary>
    private void AppendConsole(string line) => _pendingConsoleLines.Enqueue(line);

    private void StartConsoleFlusher()
    {
        _consoleTimer?.Stop();

        _consoleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _consoleTimer.Tick += (_, _) => FlushConsole();
        _consoleTimer.Start();
    }

    private void FlushConsole()
    {
        var appended = false;

        while (_pendingConsoleLines.TryDequeue(out var line))
        {
            Console.Add(line);
            appended = true;
        }

        if (!appended)
        {
            return;
        }

        while (Console.Count > MaxConsoleLines)
        {
            Console.RemoveAt(0);
        }
    }
}





