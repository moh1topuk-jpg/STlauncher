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

    public async Task<VersionManifest> GetVersionManifestAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetStringAsync(VersionManifestUrl, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(_paths.Meta);
        var cachePath = Path.Combine(_paths.Meta, "version_manifest_v2.json");
        await File.WriteAllTextAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<VersionManifest>(json, MetadataJson.Options)
                       ?? throw new InvalidDataException("Version manifest is empty.");

        _logger?.LogInformation("Loaded {Count} versions from the manifest.", manifest.Versions.Count);
        return manifest;
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
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);

        return path;
    }

    public VersionJson ParseVersionJson(string json)
        => JsonSerializer.Deserialize<VersionJson>(json, MetadataJson.Options)
           ?? throw new InvalidDataException("Version JSON is empty.");

    public VersionJson ReadVersionJsonFile(string path)
        => ParseVersionJson(File.ReadAllText(path));
}