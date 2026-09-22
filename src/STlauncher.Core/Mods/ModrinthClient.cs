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

/// <summary>A mod project page: long description plus gallery.</summary>
public sealed record ModProject(
    string Id,
    string Slug,
    string Title,
    string Description,
    string? Body,
    string? IconUrl,
    long Downloads,
    IReadOnlyList<string> Gallery)
{
    /// <summary>"mod", "resourcepack", "shader" - what folder the file belongs in.</summary>
    public string ProjectType { get; init; } = ProjectTypes.Mod;
}

/// <summary>Modrinth project types the launcher browses. Values are what the API uses.</summary>
public static class ProjectTypes
{
    public const string Mod = "mod";
    public const string ResourcePack = "resourcepack";
    public const string Shader = "shader";

    /// <summary>The folder inside the game directory for a project type.</summary>
    public static string FolderFor(string? projectType) => projectType switch
    {
        ResourcePack => "resourcepacks",
        Shader => "shaderpacks",
        _ => "mods"
    };

    /// <summary>Packs are loader-independent; a loader filter would hide all of them.</summary>
    public static bool UsesLoader(string? projectType) => projectType is null or Mod;
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
    string? Author)
{
    /// <summary>Modrinth category names ("optimization", "utility"); the loaders are filtered out.</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
}

/// <summary>Another project a version needs, or works with. Only "required" is acted on.</summary>
public sealed record ModDependency(string? ProjectId, string? VersionId, string Type)
{
    public bool IsRequired => string.Equals(Type, "required", StringComparison.OrdinalIgnoreCase);
}

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
    IReadOnlyList<ModFile> Files,
    string VersionType = "release")
{
    /// <summary>What this version depends on, as Modrinth lists it.</summary>
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = Array.Empty<ModDependency>();

    /// <summary>The project this version belongs to. Filled in by the hash lookups.</summary>
    public string? ProjectId { get; init; }

    public ModFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();

    /// <summary>
    /// Picks the file for a specific platform. One Modrinth version can carry several
    /// files - e.g. a Fabric jar next to a NeoForge or Paper one - so the loader and game
    /// version in the file name decide, and "primary" is only a hint.
    /// </summary>
    public ModFile? SelectFile(string? gameVersion, LoaderKind loader)
        => ModrinthClient.SelectFile(this, gameVersion, loader);
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
        CancellationToken cancellationToken = default,
        string projectType = ProjectTypes.Mod)
    {
        var facets = Uri.EscapeDataString(BuildFacets(gameVersion, loader, category, projectType));
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
                            h.Author)
                        {
                            // Modrinth puts the loaders into the same list as the categories.
                            Categories = (h.Categories ?? new List<string>())
                                .Where(c => c is not ("fabric" or "forge" or "neoforge" or "quilt" or "minecraft"))
                                .ToList()
                        })
                        .ToList()
                    ?? new List<ModSearchResult>();

        return new ModSearchPage(items, response?.TotalHits ?? items.Count);
    }

    /// <summary>Full project page: long description and gallery images.</summary>
    public async Task<ModProject?> GetProjectAsync(
        string idOrSlug,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _http
                .GetStringAsync($"{BaseUrl}/project/{Uri.EscapeDataString(idOrSlug)}", cancellationToken)
                .ConfigureAwait(false);

            var dto = JsonSerializer.Deserialize<ProjectDto>(json, Json.Options);

            if (dto is null)
            {
                return null;
            }

            var gallery = (dto.Gallery ?? new List<GalleryDto>())
                .Select(g => g.Url)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .ToList();

            return new ModProject(
                dto.Id ?? string.Empty,
                dto.Slug ?? string.Empty,
                dto.Title ?? string.Empty,
                dto.Description ?? string.Empty,
                dto.Body,
                dto.IconUrl,
                dto.Downloads,
                gallery)
            {
                ProjectType = string.IsNullOrWhiteSpace(dto.ProjectType) ? ProjectTypes.Mod : dto.ProjectType!
            };
        }
        catch (Exception)
        {
            return null;
        }
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

        return versions.Select(ToModVersion).ToList();
    }

    /// <summary>
    /// The versions that installed files are, looked up by their SHA-1. One request for
    /// the whole folder; files Modrinth does not know are simply absent from the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModVersion>> GetVersionsByHashesAsync(
        IEnumerable<string> sha1Hashes,
        CancellationToken cancellationToken = default)
    {
        var hashes = sha1Hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (hashes.Count == 0)
        {
            return new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);
        }

        var body = JsonSerializer.Serialize(new { hashes, algorithm = "sha1" });
        var json = await PostJsonAsync($"{BaseUrl}/version_files", body, cancellationToken).ConfigureAwait(false);
        var map = JsonSerializer.Deserialize<Dictionary<string, VersionDto>>(json, Json.Options) ?? new Dictionary<string, VersionDto>();

        return map.ToDictionary(p => p.Key, p => ToModVersion(p.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The newest version of each file's project for a game version and loader, by the
    /// file's SHA-1. This is how "is there an update" is asked without knowing what the
    /// file is: a jar dropped into the folder by hand has no record.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModVersion>> GetLatestVersionsAsync(
        IEnumerable<string> sha1Hashes,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        var hashes = sha1Hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (hashes.Count == 0)
        {
            return new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);
        }

        var loaders = ToModrinthLoader(loader) is { } name ? new[] { name } : Array.Empty<string>();
        var gameVersions = string.IsNullOrWhiteSpace(gameVersion) ? Array.Empty<string>() : new[] { gameVersion! };

        var body = JsonSerializer.Serialize(new
        {
            hashes,
            algorithm = "sha1",
            loaders,
            game_versions = gameVersions
        });

        var json = await PostJsonAsync($"{BaseUrl}/version_files/update", body, cancellationToken).ConfigureAwait(false);
        var map = JsonSerializer.Deserialize<Dictionary<string, VersionDto>>(json, Json.Options) ?? new Dictionary<string, VersionDto>();

        return map.ToDictionary(p => p.Key, p => ToModVersion(p.Value), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> PostJsonAsync(string url, string body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ModVersion ToModVersion(VersionDto v)
        => new(
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
            .ToList(),
            v.VersionType ?? "release")
        {
            Dependencies = (v.Dependencies ?? new List<DependencyDto>())
                .Select(d => new ModDependency(d.ProjectId, d.VersionId, d.DependencyType ?? "optional"))
                .ToList(),
            ProjectId = v.ProjectId
        };

    /// <summary>
    /// Picks the version a build should install. Releases win over betas and alphas, and
    /// within the same kind the API order (newest first) is preserved.
    /// </summary>
    public static ModVersion? SelectPreferred(IEnumerable<ModVersion> versions)
    {
        var list = versions.ToList();

        return list.Count == 0
            ? null
            : list.OrderBy(Rank).First();

        static int Rank(ModVersion version) => version.VersionType.ToLowerInvariant() switch
        {
            "release" => 0,
            "beta" => 1,
            "alpha" => 2,
            _ => 1
        };
    }

    /// <summary>
    /// Narrows versions that share the same number across game versions - "1.10.5" can
    /// exist for several Minecraft releases - to the one matching the request.
    /// </summary>
    public static IReadOnlyList<ModVersion> NarrowTo(
        IEnumerable<ModVersion> versions,
        string? gameVersion,
        LoaderKind loader)
    {
        var list = versions.ToList();

        if (list.Count <= 1)
        {
            return list;
        }

        var loaderName = ToModrinthLoader(loader);

        var matching = list
            .Where(v => string.IsNullOrEmpty(gameVersion) || v.GameVersions.Contains(gameVersion))
            .Where(v => loaderName is null ||
                        v.Loaders.Contains(loaderName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return matching.Count > 0 ? matching : list;
    }

    /// <summary>
    /// Chooses the file for a platform. A version may carry a Fabric jar alongside a
    /// NeoForge or Paper one, so the file name decides and "primary" is only a hint.
    /// </summary>
    public static ModFile? SelectFile(ModVersion version, string? gameVersion, LoaderKind loader)
    {
        var files = version.Files;

        if (files.Count == 0)
        {
            return null;
        }

        if (files.Count == 1)
        {
            return files[0];
        }

        var loaderName = ToModrinthLoader(loader);

        bool ByLoader(ModFile file) => loaderName is null ||
                                        file.FileName.Contains(loaderName, StringComparison.OrdinalIgnoreCase);

        bool ByVersion(ModFile file) => string.IsNullOrEmpty(gameVersion) ||
                                        file.FileName.Contains(gameVersion, StringComparison.OrdinalIgnoreCase);

        return files.FirstOrDefault(f => f.Primary && ByLoader(f) && ByVersion(f))
               ?? files.FirstOrDefault(f => ByLoader(f) && ByVersion(f))
               ?? files.FirstOrDefault(f => ByLoader(f))
               ?? files.FirstOrDefault(f => f.Primary)
               ?? files[0];
    }

    public static string BuildFacets(string? gameVersion, LoaderKind loader, string? category = null, string projectType = ProjectTypes.Mod)
    {
        var facets = new List<string> { $"[\"project_type:{projectType}\"]" };

        // A resource pack has no loader; asking for "fabric" packs returns none.
        var loaderName = ProjectTypes.UsesLoader(projectType) ? ToModrinthLoader(loader) : null;
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

        [JsonPropertyName("categories")]
        public List<string>? Categories { get; set; }
    }

    private sealed class DependencyDto
    {
        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("version_id")]
        public string? VersionId { get; set; }

        [JsonPropertyName("dependency_type")]
        public string? DependencyType { get; set; }
    }

    private sealed class VersionDto
    {
        [JsonPropertyName("dependencies")]
        public List<DependencyDto>? Dependencies { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

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

        [JsonPropertyName("version_type")]
        public string? VersionType { get; set; }
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

    private sealed class ProjectDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("project_type")]
        public string? ProjectType { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("downloads")]
        public long Downloads { get; set; }

        [JsonPropertyName("gallery")]
        public List<GalleryDto>? Gallery { get; set; }
    }

    private sealed class GalleryDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}