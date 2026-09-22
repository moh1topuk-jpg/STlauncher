using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Http;
using Velopack;
using Velopack.Sources;

namespace STlauncher.App.Services;

/// <summary>The outcome of an update check.</summary>
/// <param name="IsSupported">
/// False for a portable or development build: those have no Velopack metadata and
/// therefore cannot update themselves in place.
/// </param>
/// <param name="Source">Where the answer came from: a mirror address, or "GitHub".</param>
public sealed record UpdateStatus(
    bool IsSupported,
    bool IsUpdateAvailable,
    string? CurrentVersion,
    string? AvailableVersion,
    string? Source = null)
{
    public static UpdateStatus Unsupported(string? currentVersion) =>
        new(false, false, currentVersion, null);
}

/// <summary>
/// Wraps Velopack so the rest of the app never has to care whether it is running as an
/// installed build. Releases are published to GitHub by the tag-driven CI workflow; the
/// check asks the mirrors first and GitHub last, each with a short deadline, and keeps
/// whichever answered for the download. One blocked host used to be the end of it.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/moh1topuk-jpg/STlauncher";

    /// <summary>Where a player is sent when the launcher cannot update itself.</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases/latest";

    /// <summary>The label for the GitHub source in logs and status lines.</summary>
    public const string GithubLabel = "GitHub";

    /// <summary>How long one source may take to answer before the next is asked.</summary>
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<UpdateService>? _logger;

    // Guards the pending-update fields. The automatic background check and the manual
    // "check now" button can run at the same time, and without this they would overwrite
    // each other's state - which is how you end up applying an update you never downloaded.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Mirror addresses in the order they are tried. GitHub always comes after them.</summary>
    private IReadOnlyList<string> _mirrors = Array.Empty<string>();

    /// <summary>The manager that answered last time; downloads go through it.</summary>
    private UpdateManager? _manager;
    private string? _managerSource;
    private UpdateInfo? _pending;
    private bool _isDownloaded;

    public UpdateService(ILogger<UpdateService>? logger = null)
    {
        _logger = logger;
        _manager = CreateManager(null);
        _managerSource = GithubLabel;
    }

    /// <summary>True only for a build installed through the Velopack installer.</summary>
    public bool IsSupported => _manager?.IsInstalled ?? false;

    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>What went wrong last time, so the UI can say something useful.</summary>
    public NetworkFailure? LastError { get; private set; }

    /// <summary>Every source that failed in the last check, for the log.</summary>
    public IReadOnlyList<(string Source, NetworkFailure Failure)> LastFailures { get; private set; }
        = Array.Empty<(string, NetworkFailure)>();

    /// <summary>
    /// An update is already unpacked on disk and needs nothing but a restart. This can be
    /// true straight after startup when the download happened in an earlier session.
    /// </summary>
    public bool IsReadyToApply =>
        _isDownloaded || _manager?.UpdatePendingRestart is not null;

    /// <summary>
    /// The mirrors to try before GitHub, in order. The catalog publishes them, and the
    /// launcher ships with one built in, so a player whose provider blocks GitHub is
    /// routed around it even when the catalog itself could not be fetched.
    /// </summary>
    public void UseFeeds(IEnumerable<string?> feedUrls)
    {
        var mirrors = feedUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mirrors.SequenceEqual(_mirrors, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _mirrors = mirrors;
        _pending = null;
        _isDownloaded = false;
    }

    /// <summary>One mirror, kept for callers that have a single address.</summary>
    public void UseFeed(string? feedUrl) => UseFeeds(new[] { feedUrl });

    private UpdateManager? CreateManager(string? feedUrl)
    {
        try
        {
            IUpdateSource source = feedUrl is null
                ? new GithubSource(RepositoryUrl, null, false)
                : new SimpleWebSource(feedUrl);

            return new UpdateManager(source);
        }
        catch (Exception ex)
        {
            // Not an error worth showing: it simply means this build cannot self-update.
            _logger?.LogInformation(ex, "Velopack is unavailable, self-update is disabled.");
            return null;
        }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null || !_manager.IsInstalled)
        {
            return UpdateStatus.Unsupported(CurrentVersion);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            LastError = null;
            LastFailures = Array.Empty<(string, NetworkFailure)>();

            // An update downloaded in a previous session is already waiting - offering the
            // restart straight away is both faster and avoids downloading it twice.
            var prepared = _manager.UpdatePendingRestart;

            if (prepared is not null)
            {
                _isDownloaded = true;
                return new UpdateStatus(true, true, CurrentVersion, prepared.Version?.ToString(), _managerSource);
            }

            var failures = new List<(string, NetworkFailure)>();
            Exception? last = null;

            foreach (var (source, feedUrl) in Candidates())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var manager = feedUrl is null && _managerSource == GithubLabel ? _manager : CreateManager(feedUrl);

                if (manager is null)
                {
                    continue;
                }

                try
                {
                    // Velopack 1.2 has no cancellable overload of CheckForUpdatesAsync, so the
                    // deadline only stops the wait; a hung request finishes on its own later.
                    var update = await manager.CheckForUpdatesAsync()
                        .WaitAsync(SourceTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    _manager = manager;
                    _managerSource = source;
                    _pending = update;
                    _isDownloaded = false;

                    if (failures.Count > 0)
                    {
                        _logger?.LogInformation("Update check answered by {Source} after {Failed} source(s) failed.", source, failures.Count);
                    }

                    LastFailures = failures;

                    return new UpdateStatus(
                        true,
                        update is not null,
                        CurrentVersion,
                        update?.TargetFullRelease?.Version?.ToString(),
                        source);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    var failure = NetworkFailures.Classify(ex);
                    failures.Add((source, failure));
                    _logger?.LogWarning(ex, "Update check through {Source} failed ({Kind}).", source, failure.Kind);
                }
            }

            LastFailures = failures;
            LastError = last is null ? new NetworkFailure(NetworkFailureKind.Unknown, "no update source") : NetworkFailures.Classify(last);
            throw last ?? new InvalidOperationException("No update source is configured.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Mirrors first - they are what a blocked player can reach - and GitHub last.</summary>
    private IEnumerable<(string Source, string? FeedUrl)> Candidates()
    {
        foreach (var mirror in _mirrors)
        {
            yield return (mirror, mirror);
        }

        yield return (GithubLabel, null);
    }

    public async Task DownloadAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_manager is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Already on disk, or there is nothing to fetch.
            if (_pending is null || _isDownloaded)
            {
                return;
            }

            await _manager
                .DownloadUpdatesAsync(_pending, percent => progress?.Report(percent), cancellationToken)
                .ConfigureAwait(false);

            _isDownloaded = true;
        }
        catch (Exception ex)
        {
            LastError = NetworkFailures.Classify(ex);
            _logger?.LogWarning(ex, "Update download through {Source} failed ({Kind}).", _managerSource, LastError.Kind);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Swaps in the new version and relaunches. Returns false when there is nothing to
    /// apply; on success the process is replaced and this never returns.
    /// </summary>
    public bool ApplyAndRestart()
    {
        if (_manager is null)
        {
            return false;
        }

        var asset = _manager.UpdatePendingRestart ?? _pending?.TargetFullRelease;

        if (asset is null)
        {
            return false;
        }

        _logger?.LogInformation("Applying update {Version} and restarting.", asset.Version);
        _manager.ApplyUpdatesAndRestart(asset);
        return true;
    }
}
