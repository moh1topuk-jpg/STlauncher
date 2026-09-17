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
}
