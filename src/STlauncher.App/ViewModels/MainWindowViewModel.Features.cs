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

    /// <summary>Raised when the launcher should quit: the game it hid for has closed normally.</summary>
    public event Action? RequestCloseLauncher;

    /// <summary>Raised when the window should disappear entirely while the game runs.</summary>
    public event Action? RequestConcealLauncher;

    /// <summary>Raised when the window should come back: the game ended, or crashed.</summary>
    public event Action? RequestShowLauncher;

    /// <summary>Why the last game ended badly; empty when it did not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameCrashed))]
    private string _gameCrashNotice = string.Empty;

    public bool HasGameCrashed => !string.IsNullOrEmpty(GameCrashNotice);

    /// <summary>Log of the last run, for the "open log" button next to a crash notice.</summary>
    public string? LastGameLogPath { get; private set; }

    [RelayCommand]
    private void OpenLastGameLog()
    {
        if (string.IsNullOrWhiteSpace(LastGameLogPath) || !System.IO.File.Exists(LastGameLogPath))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LastGameLogPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }

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

    // ===================== Interface language =====================

    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();

    [ObservableProperty]
    private LanguageOption? _selectedLanguageOption;

    partial void OnSelectedLanguageOptionChanged(LanguageOption? value)
    {
        if (value is not null)
        {
            Language = value.Code;
        }
    }

    /// <summary>
    /// Languages are listed under their own names. The picker used to show the raw codes
    /// ("ru", "en"), which is the sort of thing only the person who wrote it can read.
    /// </summary>
    private void LoadLanguageOptions()
    {
        LanguageOptions.Clear();

        foreach (var code in _localization.AvailableLanguages)
        {
            LanguageOptions.Add(new LanguageOption(code, NativeLanguageName(code)));
        }

        SelectedLanguageOption = LanguageOptions.FirstOrDefault(o =>
                                     string.Equals(o.Code, Language, StringComparison.OrdinalIgnoreCase))
                                 ?? LanguageOptions.FirstOrDefault();
    }

    private static string NativeLanguageName(string code) => code.ToLowerInvariant() switch
    {
        "ru" => "Русский",
        "en" => "English",
        _ => code
    };

    [ObservableProperty]
    private string _extraGameArgs = string.Empty;

    /// <summary>Window width for the game. Zero means "use the game default".</summary>
    [ObservableProperty]
    private decimal _width;

    [ObservableProperty]
    private decimal _height;

    partial void OnExtraGameArgsChanged(string value) => SyncInstance();

    partial void OnWidthChanged(decimal value)
    {
        SyncInstance();
        OnPropertyChanged(nameof(UseDefaultResolution));
    }

    partial void OnHeightChanged(decimal value)
    {
        SyncInstance();
        OnPropertyChanged(nameof(UseDefaultResolution));
    }

    /// <summary>
    /// "Let the game decide" as a tick box, instead of the 0 × 0 that meant it before and
    /// read as a broken setting.
    /// </summary>
    public bool UseDefaultResolution
    {
        get => Width <= 0 && Height <= 0;
        set
        {
            if (value)
            {
                Width = 0;
                Height = 0;
            }
            else if (Width <= 0 || Height <= 0)
            {
                Width = 1280;
                Height = 720;
            }

            OnPropertyChanged();
        }
    }

    // ===================== Memory =====================

    /// <summary>Physical memory of this machine, so the slider ends where the RAM does.</summary>
    public int TotalMemoryMb { get; } = DetectTotalMemoryMb();

    /// <summary>Upper end of the memory slider: the RAM minus what Windows and the launcher need.</summary>
    public int MemorySliderMax => Math.Max(2048, TotalMemoryMb - 2048);

    /// <summary>
    /// A sensible allocation for this machine: a quarter of the RAM, kept between 2 and
    /// 8 GB. More than that does not make Minecraft faster - it makes garbage collection
    /// pauses longer.
    /// </summary>
    public int RecommendedMemoryMb => Math.Clamp(TotalMemoryMb / 4 / 512 * 512, 2048, 8192);

    public string MemoryHint => Localize(
        "Settings_MemoryHint",
        "{0} MB of {1} MB - recommended {2} MB",
        (int)MaxMemoryMb,
        TotalMemoryMb,
        RecommendedMemoryMb);

    [RelayCommand]
    private void UseRecommendedMemory() => MaxMemoryMb = RecommendedMemoryMb;

    private static int DetectTotalMemoryMb()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return bytes > 0 ? (int)(bytes / 1024 / 1024) : 8192;
        }
        catch (Exception)
        {
            return 8192;
        }
    }

    // ===================== Nickname editing =====================

    /// <summary>
    /// The name is shown as text and only becomes a field on request: the main screen
    /// is for starting the game, not for filling in forms.
    /// </summary>
    [ObservableProperty]
    private bool _isEditingNickname;

    [RelayCommand]
    private void EditNickname() => IsEditingNickname = true;

    [RelayCommand]
    private void FinishEditingNickname()
    {
        Username = Username.Trim();
        IsEditingNickname = false;
    }

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
                        // Out of sight rather than gone. Exiting here broke the game's
                        // output pipe, cut the log short, and left nobody to report a crash:
                        // to the player the launcher just vanished. It quits for real once
                        // the game has closed normally.
                        AppendConsole("[launcher] game started - hiding until it exits.");
                        RequestConcealLauncher?.Invoke();
                        break;

                    default:
                        AppendConsole("[launcher] game started.");
                        break;
                }
            });
        }

        _gameLauncher.GameStarted += OnStarted;

        int exitCode;

        try
        {
            exitCode = await _launch.LaunchAsync(command, settings.GameDirectory);
        }
        finally
        {
            _gameLauncher.GameStarted -= OnStarted;
        }

        LastGameLogPath = _gameLauncher.LogFilePath;

        // Exit code 0 is a normal quit. Anything else is a crash the player must hear
        // about - which is only possible because "close" no longer really closes.
        GameCrashNotice = exitCode == 0
            ? string.Empty
            : Localize(
                "Game_CrashNotice",
                "Minecraft closed with an error (code {0}). The game log says why.",
                exitCode);

        if (AfterLaunch == AfterLaunchAction.Close && exitCode == 0)
        {
            RequestCloseLauncher?.Invoke();
        }
        else if (AfterLaunch != AfterLaunchAction.Keep)
        {
            RequestShowLauncher?.Invoke();
        }

        return exitCode;
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

    /// <summary>
    /// Offers another generated name. The generated default is a suggestion, not a
    /// verdict - and rerolling is quicker than inventing one on the spot.
    /// </summary>
    [RelayCommand]
    private void RerollNickname()
    {
        Username = Core.Auth.NicknameGenerator.NextUnused(Nicknames.Append(Username));
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
                    : $"Java {installation.MajorVersion} · {installation.Vendor}";

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

            BackupStatus = Localize("Backup_InProgress", "Creating a backup…");
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

public sealed record JavaChoice(string Display, string? Path);

/// <summary>An interface language, shown under its own name rather than as a code.</summary>
public sealed record LanguageOption(string Code, string Display);

public sealed record AfterLaunchOption(AfterLaunchAction Action, string Display);

/// <summary>What caused a backup check.</summary>
public enum BackupTrigger
{
    BeforeLaunch,
    BeforeModChange
}
