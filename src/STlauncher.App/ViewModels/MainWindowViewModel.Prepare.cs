using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;

namespace STlauncher.App.ViewModels;

/// <summary>
/// "Prepare the server's build in advance" (off unless the player turns it on). While the
/// launcher is open or in the tray, the files the catalog sync would fetch are downloaded
/// into a folder of their own, and the game, loader and Java files the build will need go
/// where every launch keeps them - so "Play" has nothing left to download. Nothing goes
/// into the build: catalog changes are still offered and taken as before, and taking them
/// finds the files ready. One file at a time, paused while a game runs or a launch is
/// under way, and never on a connection Windows calls metered.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>How often another look is taken while the launcher stays open.</summary>
    private static readonly TimeSpan PrepareInterval = TimeSpan.FromHours(2);

    /// <summary>How long a "metered or not" answer is trusted.</summary>
    private static readonly TimeSpan MeteredRecheck = TimeSpan.FromMinutes(1);

    [ObservableProperty]
    private bool _prepareAhead;

    /// <summary>What is prepared, or what is being done: the line under the switch.</summary>
    [ObservableProperty]
    private string _prepareStatus = string.Empty;

    [ObservableProperty]
    private bool _isPreparing;

    /// <summary>There are prepared files to clear.</summary>
    [ObservableProperty]
    private bool _hasPrepared;

    private CancellationTokenSource? _prepareCts;
    private Timer? _prepareTimer;
    private bool _gameFilesPrepared;
    private DateTime _meteredCheckedUtc = DateTime.MinValue;
    private bool _meteredNow;

    partial void OnPrepareAheadChanged(bool value)
    {
        // Read from the settings at start: the schedule starts after the startup sync.
        if (_settingsLoading)
        {
            return;
        }

        PersistSettings();

        if (value)
        {
            StartPrepareSchedule();
        }
        else
        {
            StopPreparing();
        }

        RefreshPrepareStatus();
    }

    /// <summary>After the startup sync: the first look, then one every couple of hours.</summary>
    private async Task StartupSyncThenPrepareAsync(Instance? recommended)
    {
        await StartBuildSyncAsync(recommended);

        if (PrepareAhead)
        {
            StartPrepareSchedule();
        }

        RefreshPrepareStatus();
    }

    private void StartPrepareSchedule()
    {
        _prepareTimer?.Dispose();
        _prepareTimer = new Timer(
            _ => Dispatcher.UIThread.Post(() => _ = PrepareAheadAsync()),
            null,
            TimeSpan.FromSeconds(5),
            PrepareInterval);
    }

    private void StopPreparing()
    {
        _prepareTimer?.Dispose();
        _prepareTimer = null;
        _prepareCts?.Cancel();
    }

    /// <summary>Wait while a game runs, a launch or a sync downloads, or the connection is metered.</summary>
    private bool ShouldPausePreparing()
    {
        if (IsGameRunning || IsBusy || IsBuildSyncBusy || IsBuildImportBusy)
        {
            return true;
        }

        if (DateTime.UtcNow - _meteredCheckedUtc > MeteredRecheck)
        {
            _meteredNow = MeteredConnection.IsMetered() == true;
            _meteredCheckedUtc = DateTime.UtcNow;
        }

        return _meteredNow;
    }

    /// <summary>One round: the catalog's pending files, then the game files of the version it will run.</summary>
    private async Task PrepareAheadAsync()
    {
        if (!PrepareAhead || IsPreparing || _loadedCatalog is null)
        {
            return;
        }

        // Asked here and not only between files: a round never starts on a metered line.
        if (MeteredConnection.IsMetered() == true)
        {
            _meteredNow = true;
            _meteredCheckedUtc = DateTime.UtcNow;
            PrepareStatus = Localize("Prepare_Metered", "The connection is metered: preparing waits for a regular one.");
            return;
        }

        var instance = _pendingSyncInstance
                       ?? (RecommendedBuild is { } recommended ? CatalogBuildSync.FindLinked(_allInstances, recommended) : null);

        if (instance is null)
        {
            PrepareStatus = Localize("Prepare_Nothing", "Nothing to prepare: the server's build and the game files are in place.");
            return;
        }

        // What the build will be once the player says yes: the catalog applied, on a copy.
        var build = ReferenceEquals(instance, _pendingSyncInstance) && _pendingCatalogBuild is not null
            ? _pendingCatalogBuild
            : _loadedCatalog.Builds.FirstOrDefault(b => string.Equals(b.Id, instance.CatalogBuildId, StringComparison.OrdinalIgnoreCase));
        var target = build is null ? instance : PreviewCatalogBuild(instance, build);

        if (!ReferenceEquals(target, instance))
        {
            target.InstalledMods = instance.InstalledMods.ToList();
            target.ExternalGameDirectory = instance.ExternalGameDirectory;
        }

        var plan = PlanBuildSync(target);
        var directory = _instances.GameDirectory(instance);

        _prepareCts?.Dispose();
        _prepareCts = new CancellationTokenSource();
        var token = _prepareCts.Token;
        IsPreparing = true;

        try
        {
            var outcome = await Task.Run(() => PrepareFilesAsync(plan.Pending, target, directory, token), token);
            _gameFilesPrepared = outcome;
            AppendConsole($"[prepare] round done: {plan.Pending.Count} catalog file(s) looked at, game files {(outcome ? "ready" : "not ready")}");
        }
        catch (OperationCanceledException)
        {
            AppendConsole("[prepare] stopped");
        }
        catch (Exception ex)
        {
            AppendConsole($"[prepare] failed: {ex.Message}");
            PrepareStatus = Localize("Prepare_Failed", "Could not prepare: {0}. Next try in two hours.", ex.Message);
            IsPreparing = false;
            return;
        }
        finally
        {
            IsPreparing = false;
        }

        RefreshPrepareStatus();
    }

    /// <returns>True when the game, loader and Java files are all in place now.</returns>
    private async Task<bool> PrepareFilesAsync(IReadOnlyList<CatalogItem> items, Instance target, string directory, CancellationToken token)
    {
        var wanted = new List<DownloadItem>();
        var resolvedAll = true;
        var index = 0;

        foreach (var item in items)
        {
            index++;
            await WaitWhilePausedAsync(token);

            var resolved = await _catalogInstaller.ResolveAsync(item, directory, target.VersionId, target.Loader, token);

            if (resolved.Download is not { } download)
            {
                resolvedAll = false;
                continue;
            }

            wanted.Add(download);

            // Already in the build, or another build has it in the shared store: nothing to fetch.
            if ((File.Exists(download.DestinationPath) && DownloadClient.Matches(download.DestinationPath, download)) ||
                (_downloader.SharedFiles is { Enabled: true } store && store.ObjectPath(download.Sha1) is { } shared && File.Exists(shared)))
            {
                continue;
            }

            var name = item.Name;
            var at = index;
            Dispatcher.UIThread.Post(() => PrepareStatus = Localize("Prepare_Busy", "Preparing: {0} ({1} of {2})…", name, at, items.Count));

            await _prepared.PrepareAsync(_downloader, download, token);

            // Gentle on the line: the player may be watching a video.
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        }

        // Only when every item said what it is: an item that failed to resolve keeps its file.
        if (resolvedAll)
        {
            _prepared.KeepOnly(wanted);
        }

        if (string.IsNullOrWhiteSpace(target.VersionId))
        {
            return false;
        }

        await WaitWhilePausedAsync(token);
        Dispatcher.UIThread.Post(() => PrepareStatus = Localize("Prepare_BusyGame", "Preparing the game files of Minecraft {0}…", target.VersionId));

        var versionId = target.VersionId!;

        // Fabric and Quilt are a profile and libraries: downloads. Forge and NeoForge run
        // their installer, which is work, not a download; that waits for "Play".
        if (target.Loader is LoaderKind.Fabric or LoaderKind.Quilt)
        {
            versionId = await _loaders.InstallAsync(target.Loader, target.VersionId!, target.LoaderVersion, cancellationToken: token);
        }

        await _launch.PrefetchAsync(versionId, ShouldPausePreparing, null, token);
        return true;
    }

    private async Task WaitWhilePausedAsync(CancellationToken token)
    {
        while (ShouldPausePreparing())
        {
            Dispatcher.UIThread.Post(() => PrepareStatus = _meteredNow
                ? Localize("Prepare_Metered", "The connection is metered: preparing waits for a regular one.")
                : Localize("Prepare_Paused", "Paused while the game runs or a launch downloads."));
            await Task.Delay(TimeSpan.FromSeconds(15), token);
        }
    }

    /// <summary>The line under the switch: what is ready.</summary>
    private void RefreshPrepareStatus()
    {
        var (files, bytes) = _prepared.Measure();
        HasPrepared = files > 0;

        if (!PrepareAhead || IsPreparing)
        {
            if (!PrepareAhead)
            {
                PrepareStatus = files > 0
                    ? Localize("Prepare_LeftOver", "Off. {0} prepared earlier ({1}) can be cleared.", PreparedFilesLabel(files), FormatSize(bytes))
                    : string.Empty;
            }

            return;
        }

        PrepareStatus = files > 0
            ? Localize("Prepare_Ready", "Ready: {0}, {1}", PreparedFilesLabel(files), FormatSize(bytes)) +
              (_gameFilesPrepared ? Localize("Prepare_GameReady", " · game, loader and Java in place") : string.Empty)
            : _gameFilesPrepared
                ? Localize("Prepare_Nothing", "Nothing to prepare: the server's build and the game files are in place.")
                : Localize("Prepare_Waiting", "On: the first look is taken a few seconds after the start.");
    }

    private static string PreparedFilesLabel(int files)
    {
        var key = (files % 10, files % 100) switch
        {
            (1, not 11) => "Prepare_FilesOne",
            (>= 2 and <= 4, < 12 or > 14) => "Prepare_FilesFew",
            _ => "Prepare_FilesMany"
        };

        return Localize(key, "{0} files", files);
    }

    /// <summary>Empties the prepared folder; the game files stay - every launch uses them.</summary>
    [RelayCommand]
    private void ClearPrepared()
    {
        _prepareCts?.Cancel();
        _prepared.Clear();
        RefreshPrepareStatus();
        Status = Localize("Prepare_Cleared", "Prepared files cleared.");
    }
}
