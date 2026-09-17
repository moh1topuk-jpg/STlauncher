using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace STlauncher.Core.Server;

public enum ServerStatsOrigin
{
    /// <summary>No collector configured, and nothing cached.</summary>
    None,

    Remote,

    /// <summary>Previously downloaded copy, used when the collector is unreachable.</summary>
    Cache
}

public sealed record ServerStatsResult(ServerStatsSnapshot? Snapshot, ServerStatsOrigin Origin, string? Error);

/// <summary>
/// Reads the JSON published by the monitoring collector. The address is configurable -
/// and can come from the catalog - so the collector can be moved or replaced without
/// releasing a new launcher.
/// </summary>
public sealed class ServerStatsClient
{
    public const string CacheFileName = "server-stats.json";

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly ILogger<ServerStatsClient>? _logger;

    public ServerStatsClient(HttpClient http, LauncherPaths paths, ILogger<ServerStatsClient>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
    }

    /// <summary>Collector address. Empty disables the remote source entirely.</summary>
    public string? StatsUrl { get; set; }

    public string CachePath => Path.Combine(_paths.Meta, CacheFileName);

    public bool IsConfigured => IsHttpUrl(StatsUrl);

    public async Task<ServerStatsResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new ServerStatsResult(null, ServerStatsOrigin.None, null);
        }

        string? error = null;

        try
        {
            // The launcher blocks nothing on this, but a collector that stops responding
            // must not leave the request hanging for the whole HttpClient timeout.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var json = await _http.GetStringAsync(StatsUrl!, timeout.Token).ConfigureAwait(false);
            var snapshot = ServerStatsSnapshot.Parse(json);

            Directory.CreateDirectory(_paths.Meta);
            AtomicFile.WriteAllText(CachePath, json);

            return new ServerStatsResult(snapshot, ServerStatsOrigin.Remote, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load server stats from {Url}.", StatsUrl);
            error = ex.Message;
        }

        // Offline, or the collector is down: yesterday's numbers beat an empty page, and
        // the caller can tell the difference from the origin.
        var cached = ReadCache();

        return cached is not null
            ? new ServerStatsResult(cached, ServerStatsOrigin.Cache, error)
            : new ServerStatsResult(null, ServerStatsOrigin.None, error);
    }

    public ServerStatsSnapshot? ReadCache()
    {
        try
        {
            return File.Exists(CachePath) ? ServerStatsSnapshot.Parse(File.ReadAllText(CachePath)) : null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read cached server stats.");
            return null;
        }
    }

    public static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
