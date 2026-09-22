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

    /// <summary>
    /// How long one address may take. A blocked host often answers nothing at all, and
    /// the shared client's five minutes would keep the whole start waiting on it.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Head start the main address gets before the mirrors are asked as well.</summary>
    public TimeSpan MirrorDelay { get; set; } = TimeSpan.FromSeconds(3);

    public async Task<CatalogLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var error = default(string);

        if (IsHttpUrl(CatalogUrl))
        {
            // The main address and the mirrors race; the first live copy wins. Trying
            // them one after another meant a player behind a block sat through every
            // timeout in turn before seeing the launcher at all.
            var (catalog, failure) = await RaceAsync(cancellationToken).ConfigureAwait(false);

            if (catalog is not null)
            {
                return new CatalogLoadResult(catalog, CatalogOrigin.Remote, null);
            }

            error = failure;
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

    private async Task<(ContentCatalog? Catalog, string? Error)> RaceAsync(CancellationToken cancellationToken)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var attempts = new List<Task<(string Url, ContentCatalog? Catalog, Exception? Error)>>
        {
            FetchOneAsync(CatalogUrl!, TimeSpan.Zero, race.Token)
        };

        foreach (var url in FallbackUrls.Where(IsHttpUrl))
        {
            attempts.Add(FetchOneAsync(url, MirrorDelay, race.Token));
        }

        var errors = new List<string>();

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);

            var (url, catalog, exception) = await finished.ConfigureAwait(false);

            if (catalog is not null)
            {
                // The others are still in flight; nobody needs their answer now.
                race.Cancel();
                return (catalog, null);
            }

            if (exception is not null)
            {
                _logger?.LogWarning(exception, "Failed to load the catalog from {Url}.", url);
                errors.Add($"{new Uri(url).Host}: {Http.NetworkFailures.InnermostMessage(exception)}");
            }
        }

        return (null, string.Join("; ", errors));
    }

    private async Task<(string Url, ContentCatalog? Catalog, Exception? Error)> FetchOneAsync(
        string url,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var json = await _http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            var catalog = Parse(json);

            Directory.CreateDirectory(_paths.Meta);
            AtomicFile.WriteAllText(CachePath, json);

            _logger?.LogInformation("Loaded catalog '{Name}' from {Url}.", catalog.Name, url);
            return (url, catalog, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Another address won, or the caller gave up: not a failure of this one.
            return (url, null, null);
        }
        catch (Exception ex)
        {
            return (url, null, ex);
        }
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