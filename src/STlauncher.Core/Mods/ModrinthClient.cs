using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Mods;

/// <summary>
/// Modrinth returns header="categories" for every mod category, so the display name is
/// derived from the machine name instead.
/// </summary>
public sealed record ModCategory(string Name, string Header)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Header : Humanize(Name);

    private static string Humanize(string name)
    {
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 0
            ? name
            : string.Join(' ', parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}

/// <summary>One page of search results together with the total match count.</summary>
public sealed record ModSearchPage(IReadOnlyList<ModSearchResult> Items, int TotalHits);

public sealed record ModSearchResult(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string? IconUrl,
    long Downloads,
    string? Author);

public sealed record ModFile(
    string Url,
    string FileName,
    string? Sha1,
    string? Sha512,
    long Size,
    bool Primary);

public sealed record ModVersion(
    string Id,
    string Name,
    string VersionNumber,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<ModFile> Files)
{
    public ModFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public sealed class ModrinthClient
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    private readonly HttpClient _http;

    public ModrinthClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<ModSearchPage> SearchAsync(
        string query,
        string? gameVersion,
        LoaderKind loader,
        string? category = null,
        string sort = "relevance",
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var facets = Uri.EscapeDataString(BuildFacets(gameVersion, loader, category));
        var index = string.IsNullOrWhiteSpace(sort) ? "relevance" : sort;

        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query ?? string.Empty)}" +
                  $"&limit={limit}&offset={offset}&index={index}&facets={facets}";

        var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<SearchResponse>(json, Json.Options);

        var items = response?.Hits
                        .Select(h => new ModSearchResult(
                            h.ProjectId ?? string.Empty,
                            h.Slug ?? string.Empty,
                            h.Title ?? string.Empty,
                            h.Description ?? string.Empty,
                            h.IconUrl,
                            h.Downloads,
                            h.Author))
                        .ToList()
                    ?? new List<ModSearchResult>();

        return new ModSearchPage(items, response?.TotalHits ?? items.Count);
    }

    /// <summary>Available categories for a project type, used by the browser filters.</summary>
    public async Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(
        string projectType = "mod",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/tag/category", cancellationToken).ConfigureAwait(false);
            var all = JsonSerializer.Deserialize<List<CategoryDto>>(json, Json.Options) ?? new List<CategoryDto>();

            return all
                .Where(c => string.Equals(c.ProjectType, projectType, StringComparison.OrdinalIgnoreCase))
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new ModCategory(c.Name!, c.Header ?? c.Name!))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<ModCategory>();
        }
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        var filters = new List<string>();

        if (!string.IsNullOrEmpty(gameVersion))
        {
            filters.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
        }

        var loaderName = ToModrinthLoader(loader);
        if (loaderName is not null)
        {
            filters.Add($"loaders={Uri.EscapeDataString($"[\"{loaderName}\"]")}");
        }

        var suffix = filters.Count > 0 ? "?" + string.Join("&", filters) : string.Empty;
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version{suffix}";

        var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var versions = JsonSerializer.Deserialize<List<VersionDto>>(json, Json.Options) ?? new List<VersionDto>();

        return versions.Select(v => new ModVersion(
                v.Id ?? string.Empty,
                v.Name ?? string.Empty,
                v.VersionNumber ?? string.Empty,
                v.GameVersions ?? new List<string>(),
                v.Loaders ?? new List<string>(),
                (v.Files ?? new List<FileDto>())
                .Select(f => new ModFile(
                    f.Url ?? string.Empty,
                    f.Filename ?? string.Empty,
                    f.Hashes?.Sha1,
                    f.Hashes?.Sha512,
                    f.Size,
                    f.Primary))
                .ToList()))
            .ToList();
    }

    public static string BuildFacets(string? gameVersion, LoaderKind loader, string? category = null)
    {
        var facets = new List<string> { "[\"project_type:mod\"]" };

        var loaderName = ToModrinthLoader(loader);
        if (loaderName is not null)
        {
            facets.Add($"[\"categories:{loaderName}\"]");
        }

        if (!string.IsNullOrEmpty(gameVersion))
        {
            facets.Add($"[\"versions:{gameVersion}\"]");
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            facets.Add($"[\"categories:{category}\"]");
        }

        return "[" + string.Join(",", facets) + "]";
    }

    public static string? ToModrinthLoader(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null
    };

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("hits")]
        public List<SearchHit> Hits { get; set; } = new();

        [JsonPropertyName("total_hits")]
        public int TotalHits { get; set; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("downloads")]
        public long Downloads { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }
    }

    private sealed class VersionDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version_number")]
        public string? VersionNumber { get; set; }

        [JsonPropertyName("game_versions")]
        public List<string>? GameVersions { get; set; }

        [JsonPropertyName("loaders")]
        public List<string>? Loaders { get; set; }

        [JsonPropertyName("files")]
        public List<FileDto>? Files { get; set; }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("primary")]
        public bool Primary { get; set; }

        [JsonPropertyName("hashes")]
        public HashesDto? Hashes { get; set; }
    }

    private sealed class HashesDto
    {
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        [JsonPropertyName("sha512")]
        public string? Sha512 { get; set; }
    }

    private sealed class CategoryDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("header")]
        public string? Header { get; set; }

        [JsonPropertyName("project_type")]
        public string? ProjectType { get; set; }
    }
}