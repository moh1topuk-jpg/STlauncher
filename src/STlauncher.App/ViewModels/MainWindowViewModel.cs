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
    private readonly STlauncher.Core.Server.ServerStatsClient _stats;
    private readonly CatalogInstaller _catalogInstaller;
    private readonly UpdateService _updates;
    private readonly InstanceManager _instances;
    private readonly LocalizationService _localization;
    private readonly JavaManager _java;
    private readonly InstanceBackupService _backups;
    private readonly STlauncher.Core.Import.InstanceImporter _importer;
    private readonly LauncherPaths _paths;
    private readonly GameLauncher _gameLauncher;

    private List<VersionSummary> _allVersions = new();
    private bool _initialized;
    private bool _applyingInstance;
    private string _globalJavaPath = string.Empty;
    private CancellationTokenSource? _avatarCts;
    private CancellationTokenSource? _loaderVersionsCts;

    /// <summary>Catalog builds the player deleted, so the sync stops recreating them.</summary>
    private readonly HashSet<string> _dismissedBuildIds = new(StringComparer.OrdinalIgnoreCase);


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
        STlauncher.Core.Server.ServerStatsClient stats,
        CatalogInstaller catalogInstaller,
        UpdateService updates,
        InstanceManager instances,
        LocalizationService localization,
        JavaManager java,
        InstanceBackupService backups,
        STlauncher.Core.Import.InstanceImporter importer,
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
        _stats = stats;
        _catalogInstaller = catalogInstaller;
        _updates = updates;
        _instances = instances;
        _localization = localization;
        _java = java;
        _backups = backups;
        _importer = importer;
        _paths = paths;
        _gameLauncher = gameLauncher;
    }

    public ObservableCollection<Instance> Instances { get; } = new();

    public ObservableCollection<VersionSummary> Versions { get; } = new();

    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = new();

    public IReadOnlyList<LoaderKind> LoaderKinds { get; } = Enum.GetValues<LoaderKind>();

    [ObservableProperty]
    private VersionSummary? _selectedVersion;

    [ObservableProperty]
    private LoaderKind _selectedLoader = LoaderKind.Vanilla;

    [ObservableProperty]
    private LoaderVersion? _selectedLoaderVersion;

    [ObservableProperty]
    private bool _isLoaderBusy;

    /// <summary>The player's skin texture; the face and the 3D model are both drawn from it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkinSourceLabel))]
    private Services.PlayerSkin? _playerSkin;

    /// <summary>
    /// Where the skin came from, under the model. A player whose skin does not show
    /// deserves to know why: the name is not at Mojang, TLauncher or ely.by.
    /// </summary>
    public string SkinSourceLabel => PlayerSkin switch
    {
        null => string.Empty,
        { IsDefault: true } when SelectedSkinSource != Services.SkinSource.Auto
            => Localize("Skin_NoneAt", "{0} has no skin for this name", SkinSourceName(SelectedSkinSource)),
        { IsDefault: true } => Localize("Skin_NotFound", "No skin found for this name at Mojang, TLauncher or ely.by"),
        { Source: "cache" } => Localize("Skin_FromCache", "Skin from the last successful check"),
        { Source: { Length: > 0 } source } => Localize("Skin_From", "Skin from {0}", source),
        _ => string.Empty
    };

    // ===================== Skin system =====================
    // A player may have a skin in several systems - one on TLauncher, another on
    // ely.by. The row under the model lets them look at each and keep the one they mean.

    [ObservableProperty]
    private Services.SkinSource _selectedSkinSource = Services.SkinSource.Auto;

    public bool IsSkinSourceAuto => SelectedSkinSource == Services.SkinSource.Auto;
    public bool IsSkinSourceMojang => SelectedSkinSource == Services.SkinSource.Mojang;
    public bool IsSkinSourceTLauncher => SelectedSkinSource == Services.SkinSource.TLauncher;
    public bool IsSkinSourceElyBy => SelectedSkinSource == Services.SkinSource.ElyBy;

    partial void OnSelectedSkinSourceChanged(Services.SkinSource value)
    {
        RefreshSkinSourceFlags();
        PersistSettings();
        _ = UpdateAvatarAsync();
    }

    private void RefreshSkinSourceFlags()
    {
        OnPropertyChanged(nameof(IsSkinSourceAuto));
        OnPropertyChanged(nameof(IsSkinSourceMojang));
        OnPropertyChanged(nameof(IsSkinSourceTLauncher));
        OnPropertyChanged(nameof(IsSkinSourceElyBy));
    }

    [RelayCommand]
    private void SelectSkinSource(string? source)
    {
        if (Enum.TryParse<Services.SkinSource>(source, ignoreCase: true, out var parsed))
        {
            SelectedSkinSource = parsed;
        }
    }

    private static string SkinSourceName(Services.SkinSource source) => source switch
    {
        Services.SkinSource.Mojang => "Mojang",
        Services.SkinSource.TLauncher => "TLauncher",
        Services.SkinSource.ElyBy => "ely.by",
        _ => "Auto"
    };


    [ObservableProperty]
    private string _username = "Player";

    [ObservableProperty]
    private decimal _maxMemoryMb = 2048;

    [ObservableProperty]
    private decimal _minMemoryMb = 512;

    [ObservableProperty]
    private bool _showSnapshots;

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

    private DispatcherTimer? _updateTimer;


    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowLaunchProgress));

    partial void OnIsGameRunningChanged(bool value) => OnPropertyChanged(nameof(ShowLaunchProgress));

    [ObservableProperty]
    private string _status = string.Empty;

    private DispatcherTimer? _statusExpiry;

    /// <summary>
    /// A status line is news, and news goes stale: whatever was said clears itself after
    /// a while, unless something is still running. Before this, the last message of a
    /// session - often "Searching Modrinth…" - sat in the status bar until the launcher
    /// was closed.
    /// </summary>
    partial void OnStatusChanged(string value)
    {
        _statusExpiry?.Stop();

        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        _statusExpiry ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _statusExpiry.Tick -= OnStatusExpired;
        _statusExpiry.Tick += OnStatusExpired;
        _statusExpiry.Start();
    }

    private void OnStatusExpired(object? sender, EventArgs e)
    {
        _statusExpiry?.Stop();

        if (!IsBusy && !IsGameRunning && !IsModsBusy && !IsBrowserBusy && !IsBuildImportBusy)
        {
            Status = string.Empty;
        }
    }

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
    public int BuildModCount => SelectedInstance?.ModCount ?? 0;

    /// <summary>
    /// The selected build starts from its own profile, brought over from another launcher.
    /// Its version and loader are what that profile says; changing them here would do
    /// nothing, so the settings show them read-only.
    /// </summary>
    public bool IsProfileBuild => !string.IsNullOrWhiteSpace(SelectedInstance?.ProfileVersionId);

    public bool IsNotProfileBuild => !IsProfileBuild;

    public string ProfileBuildHint => IsProfileBuild
        ? Localize(
            "Game_ProfileBuild",
            "Started by its own profile \"{0}\": Minecraft {1}, {2} {3}. The version and loader are set by that profile.",
            SelectedInstance!.ProfileVersionId!,
            SelectedInstance.VersionId ?? "?",
            SelectedInstance.Loader,
            SelectedInstance.LoaderVersion ?? string.Empty)
        : string.Empty;

    public string InstanceDirectory
        => SelectedInstance is null
            ? _paths.InstanceDirectory("default")
            : _instances.GameDirectory(SelectedInstance);

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        // Read before anything below saves: the first save is what ends "first run".
        var isFirstRun = !_settings.Exists;
        var settings = _settings.Load();

        // A shared "Player" default means several people on the server end up with one
        // skin and one offline UUID, so a fresh installation gets a name of its own.
        Username = NicknameGenerator.IsPlaceholder(settings.Username)
            ? NicknameGenerator.NextUnused(settings.Nicknames)
            : settings.Username;

        ShowSnapshots = settings.ShowSnapshots;
        // The change handler configures the service - address and mirror together.
        CatalogUrl = ResolveCatalogUrl(settings.CatalogUrl);
        ServerStatsUrl = settings.ServerStatsUrl ?? string.Empty;
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

        ImportSuggestionDismissed = settings.ImportSuggestionDismissed;
        AnimatedBackground = settings.AnimatedBackground;
        LoadWhatsNew(settings.LastSeenVersion);

        _dismissedBuildIds.Clear();
        foreach (var dismissed in settings.DismissedBuildIds)
        {
            if (!string.IsNullOrWhiteSpace(dismissed))
            {
                _dismissedBuildIds.Add(dismissed);
            }
        }

        Nicknames.Clear();
        foreach (var nickname in settings.Nicknames)
        {
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                Nicknames.Add(nickname);
            }
        }

        if (Enum.TryParse<Services.SkinSource>(settings.SkinSource, ignoreCase: true, out var skinSource))
        {
            // Through the property: its change handler refreshes the buttons. The avatar
            // request it also starts is superseded by the one the username triggers.
            SelectedSkinSource = skinSource;
        }

        LoadJavaChoices();
        LoadAfterLaunchOptions();
        LoadLanguageOptions();
        RefreshBackups();

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

        // Right after the catalog, which is what points the check at the mirror, and
        // before anything else that could fail and leave the check never started.
        StartUpdateWatcher();

        if (_allInstances.Count == 0 && _instances.HasAnyInstanceDirectory())
        {
            // A build folder is right there but its definition could not be read.
            // Creating a new profile would bury it - and its worlds and mods - forever.
            Status = Localize(
                "Instance_Unreadable",
                "A build could not be read and was left untouched: {0}",
                string.Join(", ", _instances.UnreadableDefinitions.Select(System.IO.Path.GetFileName)));
        }

        // Creates the recommended build on a fresh installation and re-applies whatever
        // changed in the catalog to the build that came from it. Runs on every start, so
        // the launcher is in step with the repository before the player touches anything.
        var recommended = SyncRecommendedBuild(Snapshot(settings));

        ApplyBuildFilter();

        await LoadVersionsAsync();

        SelectedInstance = Instances.FirstOrDefault(i => i.Id == settings.SelectedInstanceId)
                           ?? Instances.FirstOrDefault(i =>
                               recommended is not null &&
                               string.Equals(i.Id, recommended.Id, StringComparison.OrdinalIgnoreCase))
                           ?? Instances.FirstOrDefault();

        await LoadLoaderVersionsAsync();

        if (SelectedInstance?.LoaderVersion is { Length: > 0 } loaderVersion)
        {
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Version == loaderVersion)
                                    ?? SelectedLoaderVersion;
        }

        StartServerMonitoring();

        RefreshMods();
        await LoadCategoriesAsync();
        ScheduleBrowserReload();

        // A nickname generated a moment ago only becomes this installation's identity
        // once it is on disk; without this it would be regenerated on the next start.
        PersistSettings();

        // The tour, for a brand-new installation only. It sits over the window while the
        // build syncs and the versions load underneath, so nothing waits for it.
        StartOnboardingIfFirstRun(!isFirstRun);

        // Deliberately not awaited: the window is already usable, and the missing mods
        // download in the background while the player reads the page.
        _ = StartBuildSyncAsync(recommended);

        // Also in the background: is there anything to bring over from another launcher?
        _ = LookForImportableBuildsAsync();
    }

    /// <summary>The settings the build sync needs, captured before anything edits them.</summary>
    private AppSettingsSnapshot Snapshot(AppSettings settings)
        => new(
            settings.MaxMemoryMb,
            settings.MinMemoryMb,
            settings.Loader,
            settings.LoaderVersion,
            settings.SelectedVersionId,
            _dismissedBuildIds.ToList());

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

            // Remember that this catalog build was removed on purpose, otherwise the
            // startup sync would put it straight back on the next launch.
            if (!string.IsNullOrWhiteSpace(SelectedInstance.CatalogBuildId))
            {
                _dismissedBuildIds.Add(SelectedInstance.CatalogBuildId!);
            }

            _instances.Delete(SelectedInstance.Id);
            _allInstances.RemoveAll(i => i.Id == SelectedInstance.Id);
            ApplyBuildFilter();
            SelectedInstance = Instances.FirstOrDefault();

            // Deleting the last build leaves nothing to select, and the selection handler
            // is what normally saves settings - including the dismissal recorded above.
            PersistSettings();

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
            // The copy is deliberately not linked to the catalog build: it is the
            // player's own from here on, and the sync must leave it alone.
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

        // An imported build starts from its own profile and needs no version picked here.
        var profileId = SelectedInstance?.ProfileVersionId;

        if (SelectedVersion is null && string.IsNullOrWhiteSpace(profileId))
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
            AppendConsole($"--- Launching {(string.IsNullOrWhiteSpace(profileId) ? SelectedVersion!.Id : profileId)} as {Username} ---");
            await MaybeBackupAsync(BackupTrigger.BeforeLaunch);

            var account = OfflineAuth.Login(Username);
            PersistSettings();

            var versionId = string.IsNullOrWhiteSpace(profileId) ? SelectedVersion!.Id : profileId!;

            // A profile build already carries its loader; installing one over it would
            // launch a different build from the one that was imported.
            if (SelectedLoader != LoaderKind.Vanilla && string.IsNullOrWhiteSpace(profileId))
            {
                Status = Localize("Status_InstallingLoader", "Installing {0}…", SelectedLoader);
                AppendConsole($"--- Installing {SelectedLoader} for {versionId} ---");

                var gameJava = (await _versions.ResolveAsync(versionId)).RequiredJavaMajor;
                var installerLog = new Progress<string>(line => AppendConsole(line));

                versionId = await _loaders.InstallAsync(
                    SelectedLoader,
                    versionId,
                    SelectedLoaderVersion?.Version,
                    gameJava,
                    installerLog);

                AppendConsole($"--- Loader ready: {versionId} ---");
            }

            // A startup sync may still be downloading into the same mods folder.
            await WaitForBuildSyncAsync();

            Status = Localize("Status_CheckingBuildMods", "Checking build mods…");

            if (SelectedInstance is not null)
            {
                await EnsureBuildItemsInstalledAsync(SelectedInstance);
                RefreshMods();
            }

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
                // Not written into a linked build's folder: that servers.dat belongs to the
                // other launcher, and quietly adding a server to it is exactly the kind of
                // pushiness the launcher should not have. Joining still works without it.
                ServerListAddress = string.IsNullOrWhiteSpace(ServerAddress) ||
                                    !string.IsNullOrWhiteSpace(SelectedInstance?.ExternalGameDirectory)
                    ? null
                    : ServerAddress,
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

            GameCrashNotice = string.Empty;
            var exitCode = await LaunchAndReactAsync(command, settings);

            Status = exitCode == 0
                ? Localize("Status_GameClosed", "Minecraft closed")
                : GameCrashNotice;
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

    partial void OnMaxMemoryMbChanged(decimal value)
    {
        SyncInstance();
        OnPropertyChanged(nameof(MemoryHint));
    }

    partial void OnMinMemoryMbChanged(decimal value) => SyncInstance();

    partial void OnSelectedInstanceChanged(Instance? value)
    {
        if (value is null || _refreshingListItem)
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
        OnPropertyChanged(nameof(IsProfileBuild));
        OnPropertyChanged(nameof(IsNotProfileBuild));
        OnPropertyChanged(nameof(ProfileBuildHint));
        OnPropertyChanged(nameof(IsCatalogInstance));
        OnPropertyChanged(nameof(BuildStateLabel));
        OnPropertyChanged(nameof(LastPlayedLabel));

        ApplyServerFromInstance();

        _ = LoadLoaderVersionsAsync();
        ForgetModUpdates();
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

        // Only real choices are written back. The pickers go empty on their own - a version
        // missing from the manifest, a loader list still loading - and writing that emptiness
        // into the build erased its version the moment it was selected.
        if (SelectedVersion is not null && !IsProfileBuild)
        {
            SelectedInstance.VersionId = SelectedVersion.Id;
        }

        if (!IsProfileBuild)
        {
            SelectedInstance.Loader = SelectedLoader;
        }

        if (SelectedLoaderVersion is not null && !IsProfileBuild)
        {
            SelectedInstance.LoaderVersion = SelectedLoaderVersion.Version;
        }
        else if (SelectedLoader == LoaderKind.Vanilla && !IsProfileBuild)
        {
            SelectedInstance.LoaderVersion = null;
        }
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


    partial void OnCatalogUrlChanged(string value)
    {
        _catalog.CatalogUrl = value;

        // Only the catalog the launcher ships with has a known mirror. Falling back to it
        // for a hand-configured catalog would quietly load a different one than was asked for.
        _catalog.FallbackUrls = string.Equals(value, AppSettings.DefaultCatalogUrl, StringComparison.OrdinalIgnoreCase)
            ? new[] { AppSettings.CatalogMirrorUrl }
            : Array.Empty<string>();
    }

    partial void OnSectionChanged(ShellSection value)
    {
        OnPropertyChanged(nameof(IsGameSection));
        OnPropertyChanged(nameof(IsBuildsSection));
        OnPropertyChanged(nameof(IsServerSection));
        OnPropertyChanged(nameof(IsConsoleSection));
        OnPropertyChanged(nameof(IsSettingsSection));

        // Opening the page should show the current online count, not whatever the last
        // five-minute tick left behind.
        if (value == ShellSection.Server)
        {
            _ = RefreshServerStatusAsync();
        }
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

        // Something is on screen at once; the real skin replaces it when it arrives.
        PlayerSkin ??= _skins.Default;

        try
        {
            await Task.Delay(400, cts.Token);
            var skin = await _skins.GetSkinAsync(Username, SelectedSkinSource, cts.Token);

            if (!cts.IsCancellationRequested)
            {
                PlayerSkin = skin;
                AppendConsole(skin.IsDefault
                    ? $"[skin] {Username}: not found at Mojang, TLauncher or ely.by - showing Steve"
                    : $"[skin] {Username}: {skin.Source}, {(skin.IsSlim ? "slim" : "classic")}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendConsole($"[skin] {ex.Message}");
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
            ServerStatsUrl = string.IsNullOrWhiteSpace(ServerStatsUrl) ? null : ServerStatsUrl,
            Language = Language,
            ShowDeveloperConsole = ShowDeveloperConsole,
            SelectedInstanceId = SelectedInstance?.Id,

            Nicknames = Nicknames.ToList(),
            DismissedBuildIds = _dismissedBuildIds.ToList(),
            ImportSuggestionDismissed = ImportSuggestionDismissed,
            AnimatedBackground = AnimatedBackground,
            LastSeenVersion = _lastSeenVersion,
            SkinSource = SelectedSkinSource.ToString(),
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
}
