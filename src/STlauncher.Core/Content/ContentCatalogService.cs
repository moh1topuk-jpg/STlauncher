using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace STlauncher.Core.Content;

public sealed record CatalogLoadResult(ContentCatalog? Catalog, bool FromRemote, string? Error);

public sealed class ContentCatalogService
{
    public const int SupportedSchemaVersion = 1;

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

    /// <summary>Remote catalog address. Empty means "use the cached copy only".</summary>
    public string? CatalogUrl { get; set; }

    public string CachePath => Path.Combine(_paths.Meta, "catalog.json");

    public async Task<CatalogLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(CatalogUrl))
        {
            return new CatalogLoadResult(LoadCached(), false, null);
        }

        try
        {
            var json = await _http.GetStringAsync(CatalogUrl, cancellationToken).ConfigureAwait(false);
            var catalog = Parse(json);

            Directory.CreateDirectory(_paths.Meta);
            await File.WriteAllTextAsync(CachePath, json, cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation("Loaded catalog '{Name}' with {Count} items.", catalog.Name, catalog.ItemCount);
            return new CatalogLoadResult(catalog, true, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load the catalog from {Url}.", CatalogUrl);

            var cached = LoadCached();
            return new CatalogLoadResult(cached, false, ex.Message);
        }
    }

    public ContentCatalog? LoadCached()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            return Parse(File.ReadAllText(CachePath));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read the cached catalog.");
            return null;
        }
    }

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
        return options;
    }
}