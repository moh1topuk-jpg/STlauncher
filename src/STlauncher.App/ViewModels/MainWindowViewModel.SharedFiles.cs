using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Storage;

namespace STlauncher.App.ViewModels;

/// <summary>
/// One copy on disk for the mods and packs several builds have in common: the switch, the
/// line that says what it spares, and the clean-up the player starts by hand. The work
/// itself is <see cref="SharedFileStore"/>; downloads reach it through the downloader.
/// </summary>
public partial class MainWindowViewModel
{
    private CancellationTokenSource? _sharedFilesCancel;
    private bool _sharedFilesMeasured;
    private string _sharedFilesSummary = string.Empty;

    /// <summary>On: a new download that another build already has becomes a link to it.</summary>
    [ObservableProperty]
    private bool _shareFiles = true;

    [ObservableProperty]
    private bool _isSharedFilesBusy;

    [ObservableProperty]
    private string _sharedFilesStatus = string.Empty;

    partial void OnShareFilesChanged(bool value)
    {
        if (_mods.SharedFiles is { } store)
        {
            store.Enabled = value;
        }

        PersistSettings();
    }

    /// <summary>
    /// "Shared files: 42, 310 MB on disk. Saves 1.2 GB." Counted the first time the
    /// settings page asks, off the UI thread, and again after every clean-up.
    /// </summary>
    public string SharedFilesSummary
    {
        get
        {
            if (!_sharedFilesMeasured)
            {
                _sharedFilesMeasured = true;
                _ = MeasureSharedFilesAsync();
            }

            return _sharedFilesSummary;
        }
    }

    private async Task MeasureSharedFilesAsync()
    {
        if (_mods.SharedFiles is not { } store)
        {
            return;
        }

        try
        {
            var usage = await Task.Run(() => store.Measure());

            _sharedFilesSummary = usage.Objects == 0
                ? Localize("SharedFiles_Empty", "No shared files yet.")
                : usage.SavedBytes is { } saved
                    ? Localize("SharedFiles_Summary", "Shared files: {0}, {1} on disk. Saves {2}.", usage.Objects, FormatSize(usage.StoreBytes), FormatSize(saved))
                    : Localize("SharedFiles_SummaryPlain", "Shared files: {0}, {1} on disk.", usage.Objects, FormatSize(usage.StoreBytes));

            if (usage.UnusedBytes > 0)
            {
                _sharedFilesSummary += " " + Localize("SharedFiles_Unused", "No build needs {0} of that.", FormatSize(usage.UnusedBytes));
            }
        }
        catch (Exception ex)
        {
            // The line is a courtesy; without it the button still works.
            AppendConsole($"[shared] measure failed: {ex.Message}");
            _sharedFilesSummary = string.Empty;
        }

        OnPropertyChanged(nameof(SharedFilesSummary));
    }

    /// <summary>
    /// Links identical files across builds and removes store objects no build uses. Only
    /// on the player's click: it rewrites directory entries in every build.
    /// </summary>
    [RelayCommand]
    private async Task FreeSharedSpaceAsync()
    {
        if (IsSharedFilesBusy || _mods.SharedFiles is not { } store)
        {
            return;
        }

        // The running game holds its jars open, and an install in flight is adding them.
        if (IsGameRunning || IsBusy || IsBuildSyncBusy || IsModsBusy || IsBrowserBusy || IsMovingData)
        {
            SharedFilesStatus = Localize("DataFolder_Busy", "Wait for the game and downloads to finish first");
            return;
        }

        using var cancel = new CancellationTokenSource();
        _sharedFilesCancel = cancel;

        try
        {
            IsSharedFilesBusy = true;
            SharedFilesStatus = Localize("SharedFiles_Scanning", "Looking through the builds…");

            var progress = new Progress<SharedStoreProgress>(p => SharedFilesStatus = p.Stage switch
            {
                SharedStoreStage.Comparing => Localize("SharedFiles_Comparing", "Comparing files: {0} of {1}", p.Done + 1, p.Total),
                SharedStoreStage.Linking => Localize("SharedFiles_Linking", "Joining identical files: {0} of {1}", p.Done + 1, p.Total),
                SharedStoreStage.Removing => Localize("SharedFiles_Removing", "Removing what no build uses: {0} of {1}", p.Done + 1, p.Total),
                _ => Localize("SharedFiles_Scanning", "Looking through the builds…")
            });

            var result = await Task.Run(() => store.Optimize(progress, cancel.Token), cancel.Token);

            AppendConsole($"[shared] linked {result.LinkedFiles} file(s), {result.LinkedBytes} bytes; removed {result.RemovedObjects} object(s), {result.RemovedBytes} bytes; skipped {result.SkippedFiles}");

            // Progress callbacks queued before the end would otherwise overwrite the result.
            await Task.Yield();

            var freed = result.LinkedBytes + result.RemovedBytes;

            SharedFilesStatus = result.LinkedFiles == 0 && result.RemovedObjects == 0
                ? Localize("SharedFiles_Nothing", "Nothing to free: the builds have no identical files kept apart.")
                : Localize("SharedFiles_Done", "Freed {0}. Identical files joined: {1}; removed from the store: {2}.", FormatSize(freed), result.LinkedFiles, result.RemovedObjects);

            if (result.SkippedFiles > 0)
            {
                SharedFilesStatus += " " + Localize("SharedFiles_Skipped", "Could not join {0}: the file is in use or this disk cannot share files.", result.SkippedFiles);
            }
        }
        catch (OperationCanceledException)
        {
            await Task.Yield();
            SharedFilesStatus = Localize("SharedFiles_Cancelled", "Stopped. What was done stays; the builds are intact.");
        }
        catch (Exception ex)
        {
            SharedFilesStatus = Localize("SharedFiles_Failed", "Could not finish: {0}", ex.Message);
            AppendConsole($"[shared] clean-up failed: {ex}");
        }
        finally
        {
            _sharedFilesCancel = null;
            IsSharedFilesBusy = false;
        }

        await MeasureSharedFilesAsync();
    }

    [RelayCommand]
    private void CancelFreeSharedSpace() => _sharedFilesCancel?.Cancel();
}
