using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace STlauncher.Core.Metadata;

public sealed class MetadataClient
{
    public const string VersionManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly ILogger<MetadataClient>? _logger;

    public MetadataClient(HttpClient http, LauncherPaths paths, ILogger<MetadataClient>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
    }

    /// <summary>A manifest younger than this is served from disk; new game versions can wait.</summary>
    private static readonly TimeSpan ManifestFreshFor = TimeSpan.FromHours(12);

    /// <summary>
    /// The cached copy first: the list changes a few times a month, and fetching it on
    /// every start put a Mojang round trip in front of the main screen. A stale or
    /// missing copy is fetched; a fetch that fails falls back to any copy at all.
    /// </summary>
    public async Task<VersionManifest> GetVersionManifestAsync(CancellationToken cancellationToken = default)
    {
        var cachePath = Path.Combine(_paths.Meta, "version_manifest_v2.json");

        if (TryReadCachedManifest(cachePath, ManifestFreshFor) is { } fresh)
        {
            return fresh;
        }

        string json;

        try
        {
            json = await _http.GetStringAsync(VersionManifestUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (TryReadCachedManifest(cachePath, TimeSpan.MaxValue) is { } stale)
        {
            _logger?.LogWarning("Version manifest fetch failed; using the cached copy.");
            return stale;
        }

        Directory.CreateDirectory(_paths.Meta);

        // Atomic like every other file the launcher owns: a torn write here leaves a
        // manifest that parses as "no versions at all" on the next start.
        AtomicFile.WriteAllText(Path.Combine(_paths.Meta, "version_manifest_v2.json"), json);

        var manifest = JsonSerializer.Deserialize<VersionManifest>(json, MetadataJson.Options)
                       ?? throw new InvalidDataException("Version manifest is empty.");

        _logger?.LogInformation("Loaded {Count} versions from the manifest.", manifest.Versions.Count);
        return manifest;
    }

    private static VersionManifest? TryReadCachedManifest(string path, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > maxAge)
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<VersionManifest>(File.ReadAllText(path), MetadataJson.Options);
            return manifest is { Versions.Count: > 0 } ? manifest : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<VersionJson> GetVersionJsonAsync(string url, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<VersionJson>(json, MetadataJson.Options)
               ?? throw new InvalidDataException($"Version JSON at {url} is empty.");
    }

    public async Task<string> DownloadVersionJsonAsync(
        string versionId,
        string url,
        CancellationToken cancellationToken = default)
    {
        var directory = _paths.VersionDirectory(versionId);
        Directory.CreateDirectory(directory);

        var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var path = _paths.VersionJsonPath(versionId);
        AtomicFile.WriteAllText(path, json);

        return path;
    }

    public VersionJson ParseVersionJson(string json)
        => JsonSerializer.Deserialize<VersionJson>(json, MetadataJson.Options)
           ?? throw new InvalidDataException("Version JSON is empty.");

    public VersionJson ReadVersionJsonFile(string path)
        => ParseVersionJson(File.ReadAllText(path));
}