using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Http;
using Velopack;
using Velopack.Sources;

namespace STlauncher.App.Services;

/// <summary>How an attempt to fetch the installed version's package ended.</summary>
public enum RepairResult
{
    /// <summary>The full package of the installed version is on disk, checked, ready to apply.</summary>
    Ready,

    /// <summary>Not an installed build: there is nothing Velopack could put back.</summary>
    Unsupported,

    /// <summary>The sources answered, but none of them still carries this version.</summary>
    VersionNotOffered,

    /// <summary>No source could be reached, or the download did not survive the checksum.</summary>
    Failed
}

/// <summary>
/// Reinstalling the version that is already installed. Velopack 1.2 has no "repair"
/// call: CheckForUpdates returns nothing when the newest release is the installed one.
/// But its updater applies whatever full package it is pointed at without comparing
/// versions, so the same result is reached by hand: find the installed version in a
/// source's release feed, bring its full package into the packages folder, check it
/// against the feed's checksum, and apply it.
/// </summary>
public sealed partial class UpdateService
{
    private RepairManager? _repairManager;
    private VelopackAsset? _repairAsset;

    /// <summary>
    /// Fetches and checks the installed version's full package, mirrors first, GitHub
    /// last. A copy already in the packages folder is kept only if its checksum matches
    /// the feed; anything else is downloaded again. Applies nothing.
    /// </summary>
    public async Task<RepairResult> PrepareRepairAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_manager is null || !_manager.IsInstalled)
        {
            return RepairResult.Unsupported;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var reached = false;
            LastError = null;

            foreach (var (source, feedUrl) in Candidates())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var manager = new RepairManager(feedUrl is null
                        ? new GithubSource(RepositoryUrl, null, false)
                        : new SimpleWebSource(feedUrl));

                    var asset = await manager.FindInstalledAsync()
                        .WaitAsync(SourceTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    reached = true;

                    if (asset is null)
                    {
                        _logger?.LogInformation("Repair: {Source} does not list the installed version.", source);
                        continue;
                    }

                    await manager.FetchAsync(asset, progress, cancellationToken).ConfigureAwait(false);

                    _repairManager = manager;
                    _repairAsset = asset;
                    _logger?.LogInformation("Repair: package {File} ready from {Source}.", asset.FileName, source);
                    return RepairResult.Ready;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LastError = NetworkFailures.Classify(ex);
                    _logger?.LogWarning(ex, "Repair through {Source} failed ({Kind}).", source, LastError.Kind);
                }
            }

            return reached && LastError is null ? RepairResult.VersionNotOffered : RepairResult.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Puts the prepared package in place of the installed files and starts the launcher
    /// again. False when nothing was prepared; on success the process ends here.
    /// </summary>
    public bool ApplyRepairAndRestart()
    {
        if (_repairManager is null || _repairAsset is null)
        {
            return false;
        }

        _logger?.LogInformation("Reinstalling {Version} and restarting.", _repairAsset.Version);
        _repairManager.ApplyUpdatesAndRestart(_repairAsset);
        return true;
    }

    /// <summary>The parts of Velopack's manager that are only open to a subclass.</summary>
    private sealed class RepairManager : UpdateManager
    {
        public RepairManager(IUpdateSource source) : base(source)
        {
        }

        /// <summary>The full package of the installed version as this source lists it, or null.</summary>
        public async Task<VelopackAsset?> FindInstalledAsync()
        {
            var installed = CurrentVersion;

            if (installed is null)
            {
                return null;
            }

            var feed = await Source.GetReleaseFeed(Log, AppId, Channel, null, null).ConfigureAwait(false);

            return feed?.Assets?.FirstOrDefault(a =>
                a.Type == VelopackAssetType.Full && a.Version is not null && a.Version.Equals(installed));
        }

        public async Task FetchAsync(VelopackAsset asset, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            var directory = Locator.PackagesDir
                            ?? throw new InvalidOperationException("Velopack has no packages folder.");

            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, asset.FileName);

            if (File.Exists(target) && await MatchesAsync(target, asset, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(100);
                return;
            }

            // Next to the target, so the final move is a rename and not a copy.
            var partial = target + ".repair";

            try
            {
                await Source
                    .DownloadReleaseEntry(Log, asset, partial, percent => progress?.Report(percent), cancellationToken)
                    .ConfigureAwait(false);

                if (!await MatchesAsync(partial, asset, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException($"The downloaded package {asset.FileName} does not match its checksum.");
                }

                File.Move(partial, target, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(partial))
                    {
                        File.Delete(partial);
                    }
                }
                catch (Exception)
                {
                    // A leftover partial file is the launcher's own and is overwritten next time.
                }
            }
        }

        private static async Task<bool> MatchesAsync(string path, VelopackAsset asset, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            if (asset.Size > 0 && stream.Length != asset.Size)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(asset.SHA256))
            {
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return string.Equals(Convert.ToHexString(hash), asset.SHA256, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(asset.SHA1))
            {
                var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return string.Equals(Convert.ToHexString(hash), asset.SHA1, StringComparison.OrdinalIgnoreCase);
            }

            // A feed without checksums proves nothing about the file.
            return false;
        }
    }
}
