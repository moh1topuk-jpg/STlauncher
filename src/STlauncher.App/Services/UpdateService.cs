using System;
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
public sealed record UpdateStatus(
    bool IsSupported,
    bool IsUpdateAvailable,
    string? CurrentVersion,
    string? AvailableVersion)
{
    public static UpdateStatus Unsupported(string? currentVersion) =>
        new(false, false, currentVersion, null);
}

/// <summary>
/// Wraps Velopack so the rest of the app never has to care whether it is running as an
/// installed build. Releases are published to GitHub by the tag-driven CI workflow, and
/// the manager reads that same release feed - unless a mirror is configured, for players
/// who cannot reach GitHub at all.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/moh1topuk-jpg/STlauncher";

    /// <summary>Where a player is sent when the launcher cannot update itself.</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases/latest";

    private readonly ILogger<UpdateService>? _logger;

    // Guards the pending-update fields. The automatic background check and the manual
    // "check now" button can run at the same time, and without this they would overwrite
    // each other's state - which is how you end up applying an update you never downloaded.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UpdateManager? _manager;
    private string? _feedUrl;
    private UpdateInfo? _pending;
    private bool _isDownloaded;

    public UpdateService(ILogger<UpdateService>? logger = null)
    {
        _logger = logger;
        _manager = CreateManager(null);
    }

    /// <summary>True only for a build installed through the Velopack installer.</summary>
    public bool IsSupported => _manager?.IsInstalled ?? false;

    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>What went wrong last time, so the UI can say something useful.</summary>
    public NetworkFailure? LastError { get; private set; }

    /// <summary>
    /// An update is already unpacked on disk and needs nothing but a restart. This can be
    /// true straight after startup when the download happened in an earlier session.
    /// </summary>
    public bool IsReadyToApply =>
        _isDownloaded || _manager?.UpdatePendingRestart is not null;

    /// <summary>
    /// Points the updater at a mirror instead of GitHub. Published through the catalog, so
    /// a player whose provider blocks GitHub can be routed around it without shipping a
    /// new build - which would be impossible anyway, since the new build lives on GitHub.
    /// </summary>
    public void UseFeed(string? feedUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(feedUrl) ? null : feedUrl.Trim();

        if (string.Equals(normalized, _feedUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _feedUrl = normalized;
        _manager = CreateManager(normalized);
        _pending = null;
        _isDownloaded = false;
    }

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

            // An update downloaded in a previous session is already waiting - offering the
            // restart straight away is both faster and avoids downloading it twice.
            var prepared = _manager.UpdatePendingRestart;

            if (prepared is not null)
            {
                _isDownloaded = true;
                return new UpdateStatus(true, true, CurrentVersion, prepared.Version?.ToString());
            }

            // Velopack 1.2 has no cancellable overload of CheckForUpdatesAsync, so the token
            // only stops us waiting - the request itself runs to completion in the background.
            var update = await _manager.CheckForUpdatesAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            _pending = update;
            _isDownloaded = false;

            return new UpdateStatus(
                true,
                update is not null,
                CurrentVersion,
                update?.TargetFullRelease?.Version?.ToString());
        }
        catch (Exception ex)
        {
            LastError = NetworkFailures.Classify(ex);
            _logger?.LogWarning(ex, "Update check failed ({Kind}).", LastError.Kind);
            throw;
        }
        finally
        {
            _gate.Release();
        }
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
            _logger?.LogWarning(ex, "Update download failed ({Kind}).", LastError.Kind);
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
