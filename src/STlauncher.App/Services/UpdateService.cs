using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace STlauncher.App.Services;

public sealed record UpdateStatus(
    bool IsInstalled,
    bool IsUpdateAvailable,
    string? CurrentVersion,
    string? AvailableVersion);

public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/moh1topuk-jpg/STlauncher";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
        }
        catch (Exception)
        {
            _manager = null;
        }
    }

    public bool IsInstalled => _manager?.IsInstalled ?? false;

    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null || !_manager.IsInstalled)
        {
            return new UpdateStatus(false, false, CurrentVersion, null);
        }

        var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        _pending = update;

        return new UpdateStatus(
            true,
            update is not null,
            _manager.CurrentVersion?.ToString(),
            update?.TargetFullRelease?.Version?.ToString());
    }

    public async Task DownloadAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_manager is null || _pending is null)
        {
            return;
        }

        await _manager
            .DownloadUpdatesAsync(_pending, percent => progress?.Report(percent), cancellationToken)
            .ConfigureAwait(false);
    }

    public bool ApplyAndRestart()
    {
        if (_manager is null || _pending is null)
        {
            return false;
        }

        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        return true;
    }
}