using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Content;

public enum CatalogOrigin
{
    None,

    /// <summary>Fetched from the configured http(s) URL.</summary>
    Remote,

    /// <summary>Read from the configured local path or file:// URI.</summary>
    LocalFile,

    /// <summary>Previously downloaded copy, used when the source is unavailable.</summary>
    Cache,

    /// <summary>catalog.json placed next to settings.json.</summary>
    DropIn
}

public sealed record CatalogLoadResult(ContentCatalog? Catalog, CatalogOrigin Origin, string? Error);

public sealed class ContentCatalogService
{
    public const int SupportedSchemaVersion = 1;
    public const string LocalFileName = "catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly System.Net.Http.HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly ILogger<ContentCatalogService>? _logger;

    public ContentCatalogService(
        System.Net.Http.HttpClient http,
        LauncherPaths paths,
        ILogger<ContentCatalogService>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
    }

    /// <summary>
    /// Where to read the catalog from. Accepts an http(s) URL, a file:// URI or a plain
    /// filesystem path. Empty falls back to the cache and the drop-in file.
    /// </summary>
    public string? CatalogUrl { get; set; }

    public string CachePath => Path.Combine(_paths.Meta, LocalFileName);

    /// <summary>A catalog placed next to settings.json, so hosting is optional.</summary>
    public string DropInPath => Path.Combine(_paths.Root, LocalFileName);

    /// <summary>
    /// Copies of the same catalog on other networks, tried in order when the main address
    /// fails. The catalog is how the launcher learns where its update mirror is, so a
    /// player whose provider blocks GitHub entirely could otherwise never find the mirror:
    /// the address would be delivered through the very network that is blocked.
    /// </summary>
    public IReadOnlyList<string> FallbackUrls { get; set; } = Array.Empty<string>();

    public async Task<CatalogLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var error = default(string);

        if (IsHttpUrl(CatalogUrl))
        {
            try
            {
                var json = await _http.GetStringAsync(CatalogUrl!, cancellationToken).ConfigureAwait(false);
                var catalog = Parse(json);

                Directory.CreateDirectory(_paths.Meta);
                AtomicFile.WriteAllText(CachePath, json);

                _logger?.LogInformation("Loaded catalog '{Name}' with {Count} items.", catalog.Name, catalog.ItemCount);
                return new CatalogLoadResult(catalog, CatalogOrigin.Remote, null);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load the catalog from {Url}.", CatalogUrl);
                error = ex.Message;
            }

            // Before settling for yesterday's copy, try the same catalog elsewhere: a
            // live catalog through a mirror beats a stale one from the cache.
            var mirrored = await TryFallbacksAsync(cancellationToken).ConfigureAwait(false);

            if (mirrored is not null)
            {
                return new CatalogLoadResult(mirrored, CatalogOrigin.Remote, null);
            }
        }
        else if (IsLocalPath(CatalogUrl))
        {
            try
            {
                var json = await File.ReadAllTextAsync(ResolveLocalFile(CatalogUrl!), cancellationToken).ConfigureAwait(false);
                return new CatalogLoadResult(Parse(json), CatalogOrigin.LocalFile, null);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read the catalog from {Path}.", CatalogUrl);
                error = ex.Message;
            }
        }

        var cached = LoadCached();
        if (cached is not null)
        {
            return new CatalogLoadResult(cached, CatalogOrigin.Cache, error);
        }

        var dropIn = TryReadFile(DropInPath);
        if (dropIn is not null)
        {
            return new CatalogLoadResult(dropIn, CatalogOrigin.DropIn, error);
        }

        return new CatalogLoadResult(null, CatalogOrigin.None, error);
    }

    public ContentCatalog? LoadCached() => TryReadFile(CachePath);

    private async Task<ContentCatalog?> TryFallbacksAsync(CancellationToken cancellationToken)
    {
        foreach (var url in FallbackUrls)
        {
            if (!IsHttpUrl(url))
            {
                continue;
            }

            try
            {
                var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var catalog = Parse(json);

                Directory.CreateDirectory(_paths.Meta);
                AtomicFile.WriteAllText(CachePath, json);

                _logger?.LogInformation("Loaded catalog '{Name}' through the mirror {Url}.", catalog.Name, url);
                return catalog;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Catalog mirror {Url} failed as well.", url);
            }
        }

        return null;
    }

    public ContentCatalog? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read the catalog at {Path}.", path);
            return null;
        }
    }

    public static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static bool IsLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var fileUri) && fileUri.IsFile;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.IsFile;
        }

        return true;
    }

    public static string ResolveLocalFile(string value)
        => value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
           Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           uri.IsFile
            ? uri.LocalPath
            : value;

    public static ContentCatalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<ContentCatalog>(json, JsonOptions)
                      ?? throw new InvalidDataException("Catalog is empty.");

        if (catalog.SchemaVersion > SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Catalog schema version {catalog.SchemaVersion} is newer than supported ({SupportedSchemaVersion}). " +
                "Update the launcher.");
        }

        foreach (var section in catalog.Sections)
        {
            foreach (var item in section.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    item.Id = $"{section.Id}/{item.Name}";
                }
            }
        }

        return catalog;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        options.Converters.Add(new TolerantEnumConverter<CatalogItemType>(CatalogItemType.Other));
        options.Converters.Add(new TolerantEnumConverter<CatalogSourceKind>(CatalogSourceKind.Unknown));
        options.Converters.Add(new TolerantEnumConverter<LoaderKind>(LoaderKind.Vanilla));
        return options;
    }
}