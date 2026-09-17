using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core.Backups;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Java;
using STlauncher.Core.Launch;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Settings, nickname presets, builds and backups. Split out of the main file to keep
/// both readable.
/// </summary>
public partial class MainWindowViewModel
{
    private ContentCatalog? _loadedCatalog;

    // ===================== Window lifecycle =====================

    /// <summary>Raised when the launcher should hide itself after the game starts.</summary>
    public event Action? RequestHideLauncher;

    /// <summary>Raised when the launcher should close after the game starts.</summary>
    public event Action? RequestCloseLauncher;

    // ===================== Launch behaviour =====================

    public ObservableCollection<AfterLaunchOption> AfterLaunchOptions { get; } = new();

    [ObservableProperty]
    private AfterLaunchOption? _selectedAfterLaunchOption;

    [ObservableProperty]
    private AfterLaunchAction _afterLaunch = AfterLaunchAction.Close;

    [ObservableProperty]
    private bool _forceUpdate;

    partial void OnAfterLaunchChanged(AfterLaunchAction value) => PersistSettings();

    partial void OnForceUpdateChanged(bool value) => PersistSettings();

    partial void OnSelectedAfterLaunchOptionChanged(AfterLaunchOption? value)
    {
        if (value is not null)
        {
            AfterLaunch = value.Action;
        }
    }

    /// <summary>Rebuilt when the language changes so the labels stay translated.</summary>
    private void LoadAfterLaunchOptions()
    {
        var current = SelectedAfterLaunchOption?.Action ?? AfterLaunch;

        AfterLaunchOptions.Clear();
        AfterLaunchOptions.Add(new AfterLaunchOption(AfterLaunchAction.Keep,
            Localize("AfterLaunch_Keep", "Keep the launcher open")));
        AfterLaunchOptions.Add(new AfterLaunchOption(AfterLaunchAction.Hide,
            Localize("AfterLaunch_Hide", "Minimize")));
        AfterLaunchOptions.Add(new AfterLaunchOption(AfterLaunchAction.Close,
            Localize("AfterLaunch_Close", "Close the launcher")));

        SelectedAfterLaunchOption = AfterLaunchOptions.FirstOrDefault(o => o.Action == current)
                                    ?? AfterLaunchOptions[^1];
    }

    [ObservableProperty]
    private string _extraGameArgs = string.Empty;

    /// <summary>Window width for the game. Zero means "use the game default".</summary>
    [ObservableProperty]
    private decimal _width;

    [ObservableProperty]
    private decimal _height;

    partial void OnExtraGameArgsChanged(string value) => SyncInstance();

    partial void OnWidthChanged(decimal value) => SyncInstance();

    partial void OnHeightChanged(decimal value) => SyncInstance();

    /// <summary>Splits a raw argument string, ignoring extra whitespace.</summary>
    public static IReadOnlyList<string> SplitArguments(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Runs the game and reacts once it is up. The process is never killed here, so the
    /// game keeps running even when the launcher closes itself.
    /// </summary>
    private async Task<int> LaunchAndReactAsync(LaunchCommand command, LaunchSettings settings)
    {
        void OnStarted()
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (AfterLaunch)
                {
                    case AfterLaunchAction.Hide:
                        AppendConsole("[launcher] game started - hiding.");
                        RequestHideLauncher?.Invoke();
                        break;

                    case AfterLaunchAction.Close:
                        AppendConsole("[launcher] game started - closing.");
                        RequestCloseLauncher?.Invoke();
                        break;

                    default:
                        AppendConsole("[launcher] game started.");
                        break;
                }
            });
        }

        _gameLauncher.GameStarted += OnStarted;

        try
        {
            return await _launch.LaunchAsync(command, settings.GameDirectory);
        }
        finally
        {
            _gameLauncher.GameStarted -= OnStarted;
        }
    }

    // ===================== Nickname presets =====================

    public ObservableCollection<string> Nicknames { get; } = new();

    [ObservableProperty]
    private string _newNickname = string.Empty;

    [RelayCommand]
    private void ApplyNickname(string? nickname)
    {
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            Username = nickname;
        }
    }

    [RelayCommand]
    private void SaveNickname()
    {
        var nickname = (NewNickname ?? string.Empty).Trim();

        if (nickname.Length == 0)
        {
            nickname = Username.Trim();
        }

        if (nickname.Length == 0)
        {
            return;
        }

        if (!Core.Auth.OfflineAuth.IsValidUsername(nickname))
        {
            Status = Localize("Status_InvalidNickname", "Nickname: 3-16 characters, letters, digits and underscore");
            return;
        }

        if (!Nicknames.Contains(nickname, StringComparer.OrdinalIgnoreCase))
        {
            Nicknames.Add(nickname);
        }

        Username = nickname;
        NewNickname = string.Empty;
        PersistSettings();
    }

    [RelayCommand]
    private void RemoveNickname(string? nickname)
    {
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            Nicknames.Remove(nickname);
            PersistSettings();
        }
    }

    // ===================== Version filters =====================

    [ObservableProperty]
    private bool _showOldReleases;

    [ObservableProperty]
    private bool _showBeta;

    [ObservableProperty]
    private bool _showAlpha;

    partial void OnShowOldReleasesChanged(bool value) => ApplyVersionFilter();

    partial void OnShowBetaChanged(bool value) => ApplyVersionFilter();

    partial void OnShowAlphaChanged(bool value) => ApplyVersionFilter();

    /// <summary>True for releases before 1.5.2, which the launcher hides by default.</summary>
    public static bool IsOldRelease(string versionId)
    {
        var parts = versionId.Split('.');

        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            return true;
        }

        if (major != 1)
        {
            return major < 1;
        }

        if (minor != 5)
        {
            return minor < 5;
        }

        var patch = parts.Length > 2 && int.TryParse(parts[2], out var value) ? value : 0;
        return patch < 2;
    }

    // ===================== Java =====================

    public ObservableCollection<JavaChoice> JavaChoices { get; } = new();

    [ObservableProperty]
    private JavaChoice? _selectedJavaChoice;

    partial void OnSelectedJavaChoiceChanged(JavaChoice? value) => PersistSettings();

    private void LoadJavaChoices()
    {
        var auto = Localize("Settings_JavaAuto", "Automatic");

        JavaChoices.Clear();
        JavaChoices.Add(new JavaChoice(auto, null));

        try
        {
            foreach (var installation in _java.DiscoverInstalled())
            {
                var display = string.IsNullOrWhiteSpace(installation.Vendor)
                    ? $"Java {installation.MajorVersion}"
                    : $"Java {installation.MajorVersion} � {installation.Vendor}";

                JavaChoices.Add(new JavaChoice(display, installation.ExecutablePath));
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[java] discovery failed: {ex.Message}");
        }

        SelectedJavaChoice = JavaChoices.FirstOrDefault(c =>
                                string.Equals(c.Path, _globalJavaPath, StringComparison.OrdinalIgnoreCase))
                            ?? JavaChoices[0];
    }

    // ===================== Recommended builds =====================

public ObservableCollection<CatalogBuild> CatalogBuilds { get; } = new();

    [ObservableProperty]
    private CatalogBuild? _selectedBuild;

    /// <summary>True while the recommended build is being fetched and installed.</summary>
    [ObservableProperty]
    private bool _isBuildImportBusy;

    /// <summary>
    /// The build the catalog marks as recommended, falling back to the first entry so an
    /// older catalog without the flag keeps working.
    /// </summary>
    public CatalogBuild? RecommendedBuild =>
        CatalogBuilds.FirstOrDefault(b => b.Recommended) ?? CatalogBuilds.FirstOrDefault();

    partial void OnSelectedBuildChanged(CatalogBuild? value) => ApplyBuild(value);


    private void ApplyBuild(CatalogBuild? build)
    {
        if (build is null || SelectedInstance is null)
        {
            return;
        }

        _applyingInstance = true;
        try
        {
            if (build.GameVersion is not null)
            {
                SelectedVersion = _allVersions.FirstOrDefault(v => v.Id == build.GameVersion) ?? SelectedVersion;
            }

            SelectedLoader = build.Loader;

            if (build.LoaderVersion is not null)
            {
                SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Version == build.LoaderVersion)
                                        ?? SelectedLoaderVersion;
            }

            if (build.MemoryMb is > 0)
            {
                MaxMemoryMb = build.MemoryMb.Value;
            }

            SelectedInstance.EnabledCatalogItems = build.Items.ToList();
        }
        finally
        {
            _applyingInstance = false;
        }

        SyncInstance();
        OnPropertyChanged(nameof(BuildModCount));
        Status = Localize("Status_BuildApplied", "Build \"{0}\" applied: {1} item(s)", build.Name, build.Items.Count);
    }

    [RelayCommand]
    private void ApplySelectedBuild() => ApplyBuild(SelectedBuild);

    /// <summary>
    /// Fetches the recommended build from the catalog and adds it to the build list as a
    /// new build, downloading its mods so it is ready to play.
    /// </summary>
    [RelayCommand]
    private async Task AddRecommendedBuildAsync()
    {
        if (IsBuildImportBusy)
        {
            return;
        }

        try
        {
            IsBuildImportBusy = true;
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

            var build = RecommendedBuild;

            if (build is null)
            {
                Status = Localize("Builds_ImportNone", "The catalog does not offer a build");
                return;
            }

            var instance = CreateInstanceFromBuild(build, uniqueName: true);

            _allInstances.Add(instance);
            ApplyBuildFilter();
            SelectedInstance = instance;

            var versionMissing = await ApplyBuildToEditorAsync(build);

            if (versionMissing)
            {
                // The build is in the list and usable, it just needs a version picked by hand.
                Status = Localize(
                    "Builds_ImportVersionMissing",
                    "Build \"{0}\" added, but version {1} is not available - choose one in Settings.",
                    instance.Name,
                    build.GameVersion);
                return;
            }

            Status = Localize("Builds_ImportInstalling", "Installing build mods…");
            await EnsureBuildItemsInstalledAsync();

            RefreshBackups();

            Status = Localize(
                "Builds_ImportDone",
                "Build \"{0}\" added ({1} item(s))",
                instance.Name,
                build.Items.Count);

            AppendConsole($"[builds] added '{instance.Name}' from the catalog");
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
    /// Makes the instance match the recommended build. The build file in the repository is
    /// the source of truth: adding a mod there installs it for everyone, removing one
    /// uninstalls it. Mods are not bumped to newer Modrinth releases by themselves - to
    /// update one, pin its version in the catalog.
    /// </summary>
    private async Task EnsureBuildItemsInstalledAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        await MaybeBackupAsync(BackupTrigger.BeforeModChange);

        var enabledIds = SelectedInstance.EnabledCatalogItems;

        // Catalog mods that are no longer part of the build are removed.
        var removed = SelectedInstance.InstalledMods
            .Where(m => m.Source == ModSource.Catalog &&
                        m.Id is not null &&
                        !enabledIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var record in removed)
        {
            DeleteInstalledFile(record);
        }

        var pending = new List<CatalogItem>();

        foreach (var id in enabledIds)
        {
            var item = _loadedCatalog?.FindItem(id);

            if (item is null)
            {
                continue;
            }

            var record = FindCatalogRecord(id);
            var installed = record is not null &&
                            System.IO.File.Exists(System.IO.Path.Combine(
                                ModManager.ModsDirectory(InstanceDirectory),
                                record.FileName));

            // Already present and not pinned: leave the file as it is.
            if (installed && string.IsNullOrWhiteSpace(item.Source.Version))
            {
                continue;
            }

            pending.Add(item);
        }

        if (pending.Count == 0)
        {
            RefreshMods();
            return;
        }

        AppendConsole($"--- Build: {pending.Count} mod(s) to install ---");

        foreach (var item in pending)
        {
            var result = await _catalogInstaller.InstallAsync(
                item,
                InstanceDirectory,
                SelectedVersion?.Id,
                SelectedLoader);

            AppendConsole($"[build] {item.Name}: {result.Message}");

            if (!result.Success || result.Path is null)
            {
                continue;
            }

            var fileName = System.IO.Path.GetFileName(result.Path);

            ReplaceInstalledFile(item.Id, fileName);
            RecordInstalledMod(new InstalledModRecord
            {
                FileName = fileName,
                Source = ModSource.Catalog,
                Id = item.Id,
                Name = item.Name,
                IconUrl = item.IconUrl,
                Required = item.Required
            });
        }

        RefreshMods();
    }

    private InstalledModRecord? FindCatalogRecord(string id)
        => SelectedInstance?.InstalledMods.FirstOrDefault(m =>
            m.Source == ModSource.Catalog &&
            string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Deletes a recorded file and forgets it.</summary>
    private void DeleteInstalledFile(InstalledModRecord record)
    {
        if (SelectedInstance is null)
        {
            return;
        }

        try
        {
            var path = System.IO.Path.Combine(ModManager.ModsDirectory(InstanceDirectory), record.FileName);

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

        SelectedInstance.InstalledMods.Remove(record);
        _instances.Save(SelectedInstance);
    }

    /// <summary>
    /// Removes the file previously installed for a project when a different one replaced it.
    /// </summary>
    private void ReplaceInstalledFile(string? projectId, string newFileName)
    {
        if (SelectedInstance is null || string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        var stale = SelectedInstance.InstalledMods
            .Where(m => string.Equals(m.Id, projectId, StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.Equals(m.FileName, newFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (stale.Count == 0)
        {
            return;
        }

        var directory = ModManager.ModsDirectory(InstanceDirectory);

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

                SelectedInstance.InstalledMods.Remove(record);
            }
            catch (Exception ex)
            {
                AppendConsole($"[build] could not remove {record.FileName}: {ex.Message}");
            }
        }

        _instances.Save(SelectedInstance);
    }

    // ===================== Backups =====================

    public ObservableCollection<BackupInfo> Backups { get; } = new();

    [ObservableProperty]
    private bool _backupsEnabled;

    [ObservableProperty]
    private bool _backupsBeforeLaunch;

    [ObservableProperty]
    private bool _backupsDaily = true;

    [ObservableProperty]
    private bool _backupsBeforeModChanges = true;

    [ObservableProperty]
    private decimal _backupsMaxCount = 10;

    [ObservableProperty]
    private decimal _backupsMaxTotalMb = 2048;

    [ObservableProperty]
    private string _backupStatus = string.Empty;

    partial void OnBackupsEnabledChanged(bool value) => PersistSettings();

    partial void OnBackupsBeforeLaunchChanged(bool value) => PersistSettings();

    partial void OnBackupsDailyChanged(bool value) => PersistSettings();

    partial void OnBackupsBeforeModChangesChanged(bool value) => PersistSettings();

    partial void OnBackupsMaxCountChanged(decimal value) => PersistSettings();

    partial void OnBackupsMaxTotalMbChanged(decimal value) => PersistSettings();

    public string BackupsDirectory => _backupDirectoryOverride is { Length: > 0 }
        ? _backupDirectoryOverride
        : System.IO.Path.Combine(_paths.Root, "backups");

    private string _backupDirectoryOverride = string.Empty;

[RelayCommand]
    private async Task CreateBackupNowAsync()
    {
        try
        {
            if (SelectedInstance is null)
            {
                return;
            }

            BackupStatus = Localize("Backup_InProgress", "Creating a backup�");
            AppendConsole($"[backup] creating a backup of {SelectedInstance.Name}");

            var backup = await _backups.CreateAsync(InstanceDirectory, BackupsDirectory, SelectedInstance.Id);
            PruneBackups();
            RefreshBackups();

            BackupStatus = Localize("Backup_Created", "Backup created: {0} ({1} KB)", backup.FileName, backup.Size / 1024);
            AppendConsole($"[backup] {backup.Path}");
        }
        catch (Exception ex)
        {
            BackupStatus = Localize("Backup_Failed", "Backup failed: {0}", ex.Message);
            AppendConsole($"[backup] failed: {ex}");
        }
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(BackupsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = BackupsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            BackupStatus = Localize("Backup_FailedFolder", "Failed to open the backups folder: {0}", ex.Message);
        }
    }

    public void RefreshBackups()
    {
        Backups.Clear();

        foreach (var backup in _backups.List(BackupsDirectory))
        {
            Backups.Add(backup);
        }
    }

    private void PruneBackups()
        => _backups.Prune(
            BackupsDirectory,
            (int)BackupsMaxCount,
            (long)BackupsMaxTotalMb * 1024 * 1024);

    /// <summary>
    /// Creates a backup when the configured trigger applies. There is no background
    /// timer: the "once a day" rule is evaluated when the launcher actually launches
    /// the game or touches mods.
    /// </summary>
public async Task<bool> MaybeBackupAsync(BackupTrigger trigger)
    {
        if (!BackupsEnabled || SelectedInstance is null)
        {
            return false;
        }

        var due = trigger switch
        {
            BackupTrigger.BeforeLaunch => BackupsBeforeLaunch || (BackupsDaily && IsDailyBackupDue()),
            BackupTrigger.BeforeModChange => BackupsBeforeModChanges,
            _ => false
        };

        if (!due)
        {
            return false;
        }

        await CreateBackupNowAsync();
        return true;
    }

    private bool IsDailyBackupDue()
    {
        var latest = _backups.List(BackupsDirectory, SelectedInstance?.Id).FirstOrDefault();

        return latest is null || DateTimeOffset.Now - latest.CreatedAt >= TimeSpan.FromDays(1);
    }

    /// <summary>
    /// Looks up a localized string. The fallback keeps the UI readable if a key is
    /// missing, and the UI resource tests fail the build in that case anyway.
    /// </summary>
    public static string Localize(string key, string fallback, params object?[] args)
    {
        var text = Application.Current?.Resources.TryGetResource(key, null, out var value) == true &&
                   value is string found
            ? found
            : fallback;

        return args.Length == 0 ? text : string.Format(text, args);
    }
}

/// <summary>One bar of the online chart: pixel height, tooltip and whether it has data.</summary>
public sealed class ServerHistoryBarView
{
    public ServerHistoryBarView(double height, string tooltip, bool isEmpty)
    {
        Height = height;
        Tooltip = tooltip;
        IsEmpty = isEmpty;
    }

    public double Height { get; }

    public string Tooltip { get; }

    public bool IsEmpty { get; }
}

public sealed record JavaChoice(string Display, string? Path);

public sealed record AfterLaunchOption(AfterLaunchAction Action, string Display);

/// <summary>What caused a backup check.</summary>
public enum BackupTrigger
{
    BeforeLaunch,
    BeforeModChange
}
