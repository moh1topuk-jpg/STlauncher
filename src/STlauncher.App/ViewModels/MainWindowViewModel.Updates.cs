using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Self-update: the periodic check, the notification banner and the one button that
/// downloads, applies and relaunches.
/// </summary>
public partial class MainWindowViewModel
{
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

    /// <summary>
    /// An update is waiting. Drives the permanent control in the sidebar, which is the
    /// one place the state is always visible - the banner can be dismissed, and Settings
    /// is three clicks away.
    /// </summary>
    [ObservableProperty]
    private bool _hasUpdate;

    /// <summary>Label of that control: what pressing it will do right now.</summary>
    [ObservableProperty]
    private string _updateActionLabel = string.Empty;

    /// <summary>The running version, shown next to the control.</summary>
    [ObservableProperty]
    private string _currentVersionLabel = string.Empty;

    /// <summary>
    /// The running version and what the last check said about it. The automatic check
    /// must leave a visible trace: a check that stays silent when all is well looks
    /// exactly like one that never ran.
    /// </summary>
    [ObservableProperty]
    private string _updateStateLabel = string.Empty;

    private void SetUpdateState(string key, string fallback)
        => UpdateStateLabel = $"{CurrentVersionLabel} · {Localize(key, fallback)}";

    /// <summary>
    /// The pill at the foot of the rail has room for one short word: the version while
    /// there is nothing to do, the new version when there is.
    /// </summary>
    public string RailVersionLabel => IsUpdateBusy
        ? "…"
        : HasUpdate && !string.IsNullOrEmpty(AvailableUpdateVersion)
            ? AvailableUpdateVersion
            : CurrentVersionLabel;

    partial void OnCurrentVersionLabelChanged(string value) => OnPropertyChanged(nameof(RailVersionLabel));

    partial void OnAvailableUpdateVersionChanged(string value) => OnPropertyChanged(nameof(RailVersionLabel));

    /// <summary>
    /// The check failed in a way the player can do something about: offer the download
    /// page, since the launcher cannot fetch the build itself.
    /// </summary>
    [ObservableProperty]
    private bool _canDownloadManually;

    /// <summary>
    /// The installer by hand, through the mirror: the one address that works when
    /// GitHub does not. The GitHub page is for everyone else.
    /// </summary>
    [RelayCommand]
    private void OpenReleasesPage() => OpenUrl(Services.AppSettings.InstallerMirrorUrl);

    [RelayCommand]
    private void OpenGithubReleases() => OpenUrl(Services.UpdateService.ReleasesUrl);

    partial void OnHasUpdateChanged(bool value)
    {
        RefreshUpdateActionLabel();
        OnPropertyChanged(nameof(RailVersionLabel));
    }

    partial void OnIsUpdateBusyChanged(bool value)
    {
        RefreshUpdateActionLabel();
        OnPropertyChanged(nameof(RailVersionLabel));
    }

    /// <summary>
    /// One button, three states. Separate "check" and "update" buttons would mean one of
    /// them is always the wrong one to press.
    /// </summary>
    private void RefreshUpdateActionLabel()
    {
        UpdateActionLabel = IsUpdateBusy
            ? Localize("Update_Working", "Working…")
            : HasUpdate
                ? Localize("Update_ToVersion", "Update to {0}", AvailableUpdateVersion)
                : Localize("Update_Check", "Check for updates");
    }

    /// <summary>Checks, or installs when something is already waiting.</summary>
    [RelayCommand]
    private Task UpdateAction() => HasUpdate ? InstallUpdateAsync() : RunUpdateCheckAsync(announce: true);


    /// <summary>
    /// Checks on every start and then every six hours, so a launcher left open for days
    /// still notices a release.
    /// </summary>
    private void StartUpdateWatcher()
    {
        CurrentVersionLabel = _updates.CurrentVersion ?? Localize("Update_DevBuild", "dev build");
        UpdateStateLabel = CurrentVersionLabel;
        RefreshUpdateActionLabel();

        if (!_updates.IsSupported)
        {
            return;
        }

        SetUpdateState("Update_StateChecking", "checking…");

        // The first check runs right away: it is one small request, and it is the thing
        // the player is waiting to see. Then every six hours, so a launcher left open for
        // days still notices a release.
        _ = CheckForUpdatesQuietlyAsync();

        _updateTimer?.Stop();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesQuietlyAsync();
        _updateTimer.Start();
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

            foreach (var (source, failure) in _updates.LastFailures)
            {
                AppendConsole($"[update] {source}: {failure.Kind}: {failure.Detail}");
            }

            if (status.Source is { Length: > 0 } via)
            {
                AppendConsole($"[update] checked through {via}: {(status.IsUpdateAvailable ? status.AvailableVersion : "up to date")}");
            }

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
                HasUpdate = false;
                CanDownloadManually = false;
                SetUpdateState("Update_StateLatest", "latest version");

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
            HasUpdate = true;
            IsUpdateBannerVisible = true;
            UpdateStateLabel = $"{CurrentVersionLabel} → {AvailableUpdateVersion}";
            RefreshUpdateActionLabel();
        }
        catch (Exception ex)
        {
            ReportFailure(ex, announce);
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    /// <summary>
    /// Says what actually went wrong. "The SSL connection could not be established, see
    /// inner exception" is what the player used to be shown: it names no cause, suggests
    /// no action, and hides the one message that would have explained it.
    /// </summary>
    private void ReportFailure(Exception exception, bool announce)
    {
        var error = STlauncher.Core.Http.NetworkFailures.Classify(exception);

        CanDownloadManually = true;

        if (!HasUpdate)
        {
            SetUpdateState("Update_StateFailed", "could not check");
        }

        var headline = error.Kind switch
        {
            STlauncher.Core.Http.NetworkFailureKind.Blocked => Localize(
                "Update_Blocked",
                "Could not reach the update server - a provider or security software is likely blocking it."),
            STlauncher.Core.Http.NetworkFailureKind.NoConnection => Localize(
                "Update_NoConnection",
                "No connection to the update server."),
            _ => Localize("Update_Failed", "Update check failed: {0}", error.Detail)
        };

        // The detail goes to the log regardless: the next report should arrive with a
        // cause rather than with the wrapper message.
        foreach (var (source, failure) in _updates.LastFailures)
        {
            AppendConsole($"[update] {source}: {failure.Kind}: {failure.Detail}");
        }

        AppendConsole($"[update] all sources failed - {error.Kind}: {error.Detail}");

        if (announce || error.Kind != STlauncher.Core.Http.NetworkFailureKind.Unknown)
        {
            UpdateStatus = headline;
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
            ReportFailure(ex, announce: true);
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
}
