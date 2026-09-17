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
using STlauncher.Core.Java;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Settings, nickname presets, builds and backups. Split out of the main file to keep
/// both readable.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly List<CatalogSection> _catalogSections = new();
    private ContentCatalog? _loadedCatalog;
    private DispatcherTimer? _backupTimer;

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
            Status = "Nickname must be 3-16 characters: A-Z, a-z, 0-9, underscore.";
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
                JavaChoices.Add(new JavaChoice(
                    $"Java {installation.MajorVersion} — {installation.ExecutablePath}",
                    installation.ExecutablePath));
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

    // ===================== Builds (catalog items) =====================

    public ObservableCollection<CatalogBuild> CatalogBuilds { get; } = new();

    public ObservableCollection<CatalogSectionView> CatalogViews { get; } = new();

    [ObservableProperty]
    private CatalogBuild? _selectedBuild;

    [ObservableProperty]
    private string _catalogFilter = string.Empty;

    [ObservableProperty]
    private bool _catalogOnlyRequired;

    partial void OnCatalogFilterChanged(string value) => RebuildCatalogViews();

    partial void OnCatalogOnlyRequiredChanged(bool value) => RebuildCatalogViews();

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

            if (!string.IsNullOrWhiteSpace(build.ServerAddress))
            {
                ServerAddress = build.ServerAddress!;
                ServerName = build.ServerName ?? ServerName;
            }

            SelectedInstance.EnabledCatalogItems = build.Items.ToList();
        }
        finally
        {
            _applyingInstance = false;
        }

        SyncInstance();
        RebuildCatalogViews();
        Status = $"Build '{build.Name}' applied. {build.Items.Count} item(s) will be installed on launch.";
    }

    [RelayCommand]
    private void ApplySelectedBuild() => ApplyBuild(SelectedBuild);

    private void OnCatalogItemToggled(CatalogItemView view)
    {
        if (SelectedInstance is null || _applyingInstance)
        {
            return;
        }

        var id = view.Item.Id;
        var items = SelectedInstance.EnabledCatalogItems;

        items.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));

        if (view.Enabled)
        {
            items.Add(id);
        }

        _instances.Save(SelectedInstance);
        Status = $"Build now has {items.Count} item(s); they install before launch.";
    }

    private void RebuildCatalogViews()
    {
        CatalogViews.Clear();

        var filter = (CatalogFilter ?? string.Empty).Trim();
        var enabledIds = SelectedInstance?.EnabledCatalogItems ?? new List<string>();

        foreach (var section in _catalogSections)
        {
            var items = section.Items
                .Where(i => !CatalogOnlyRequired || i.Required)
                .Where(i => filter.Length == 0 ||
                            i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            (i.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(i => new CatalogItemView(
                    i,
                    enabledIds.Contains(i.Id, StringComparer.OrdinalIgnoreCase),
                    OnCatalogItemToggled))
                .ToList();

            if (items.Count == 0)
            {
                continue;
            }

            CatalogViews.Add(new CatalogSectionView(section.Title, section.Description, items));
        }
    }

    private async Task EnsureBuildItemsInstalledAsync()
    {
        if (SelectedInstance is null || SelectedInstance.EnabledCatalogItems.Count == 0)
        {
            return;
        }

        var pending = SelectedInstance.EnabledCatalogItems
            .Select(id => _loadedCatalog?.FindItem(id))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        AppendConsole($"--- Ensuring {pending.Count} build item(s) ---");

        foreach (var item in pending)
        {
            var result = await _catalogInstaller.InstallAsync(
                item,
                InstanceDirectory,
                SelectedVersion?.Id,
                SelectedLoader);

            AppendConsole($"[build] {item.Name}: {result.Message}");
        }

        RefreshMods();
    }

    // ===================== Backups =====================

    public ObservableCollection<BackupInfo> Backups { get; } = new();

    [ObservableProperty]
    private bool _backupsEnabled;

    [ObservableProperty]
    private decimal _backupsIntervalMinutes = 30;

    [ObservableProperty]
    private decimal _backupsMaxCount = 10;

    [ObservableProperty]
    private decimal _backupsMaxTotalMb = 2048;

    [ObservableProperty]
    private string _backupStatus = string.Empty;

    partial void OnBackupsEnabledChanged(bool value) => RestartBackupTimer();

    partial void OnBackupsIntervalMinutesChanged(decimal value) => RestartBackupTimer();

    partial void OnBackupsMaxCountChanged(decimal value) => PersistSettings();

    partial void OnBackupsMaxTotalMbChanged(decimal value) => PersistSettings();

    public string BackupsDirectory => _backupDirectoryOverride is { Length: > 0 }
        ? _backupDirectoryOverride
        : System.IO.Path.Combine(_paths.Root, "backups");

    private string _backupDirectoryOverride = string.Empty;

    [RelayCommand]
    private void CreateBackupNow()
    {
        try
        {
            if (SelectedInstance is null)
            {
                return;
            }

            var backup = _backups.Create(InstanceDirectory, BackupsDirectory, SelectedInstance.Id);
            PruneBackups();
            RefreshBackups();

            BackupStatus = $"Backup created: {backup.FileName} ({backup.Size / 1024} KB)";
            AppendConsole($"[backup] {backup.Path}");
        }
        catch (Exception ex)
        {
            BackupStatus = "Backup failed: " + ex.Message;
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
            BackupStatus = "Failed to open the backups folder: " + ex.Message;
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

    private void RestartBackupTimer()
    {
        _backupTimer?.Stop();
        _backupTimer = null;

        if (!BackupsEnabled)
        {
            PersistSettings();
            return;
        }

        var minutes = Math.Max(1, (int)BackupsIntervalMinutes);

        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _backupTimer.Tick += (_, _) => CreateBackupNow();
        _backupTimer.Start();

        BackupStatus = $"Automatic backups every {minutes} min.";
        PersistSettings();
    }

    private static string Localize(string key, string fallback)
        => Application.Current?.Resources.TryGetResource(key, null, out var value) == true && value is string text
            ? text
            : fallback;
}

public sealed record JavaChoice(string Display, string? Path);

public sealed record AfterLaunchOption(AfterLaunchAction Action, string Display);

public sealed class CatalogSectionView
{
    public CatalogSectionView(string title, string? description, List<CatalogItemView> items)
    {
        Title = title;
        Description = description;
        Items = items;
    }

    public string Title { get; }

    public string? Description { get; }

    public List<CatalogItemView> Items { get; }
}

public partial class CatalogItemView : ObservableObject
{
    private readonly Action<CatalogItemView>? _onToggled;

    public CatalogItemView(CatalogItem item, bool enabled, Action<CatalogItemView>? onToggled = null)
    {
        Item = item;
        _enabled = enabled;
        _onToggled = onToggled;
    }

    public CatalogItem Item { get; }

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => _onToggled?.Invoke(this);
}