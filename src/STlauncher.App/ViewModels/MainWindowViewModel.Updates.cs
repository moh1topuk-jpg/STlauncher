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

    partial void OnHasUpdateChanged(bool value) => RefreshUpdateActionLabel();

    partial void OnIsUpdateBusyChanged(bool value) => RefreshUpdateActionLabel();

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
        RefreshUpdateActionLabel();

        if (!_updates.IsSupported)
        {
            return;
        }

        // Delayed by a few seconds rather than fired immediately: startup already
        // saturates the network with the catalog, the version manifest and mod icons,
        // and an update notice a moment later costs the player nothing.
        _updateTimer?.Stop();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _updateTimer.Tick += (_, _) =>
        {
            // After the first tick settle into the long interval.
            _updateTimer!.Interval = TimeSpan.FromHours(6);
            _ = CheckForUpdatesQuietlyAsync();
        };
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
            RefreshUpdateActionLabel();
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
}
