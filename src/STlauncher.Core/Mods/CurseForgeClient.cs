using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Mods;

/// <summary>Whether the launcher may fetch a CurseForge file itself.</summary>
public enum CurseForgeFileState
{
    /// <summary>There is an address on CurseForge's CDN and a SHA-1 to check the download against.</summary>
    Ready,

    /// <summary>
    /// The author switched off downloads by third-party programs, so CurseForge gives no
    /// address. Not a failure: the file's page is where the player takes it by hand, and
    /// the screen should offer to open it.
    /// </summary>
    Blocked,

    /// <summary>No file at all, or an answer that cannot be trusted: no hash, a name with a path in it.</summary>
    Unavailable
}

/// <param name="File">The file in question; the one to download when <paramref name="State"/> is Ready.</param>
/// <param name="PageUrl">Where a person gets the file in a browser. Set whenever it is known, in every state.</param>
/// <param name="InstalledPath">Where the file landed, after <see cref="CurseForgeClient.InstallAsync"/>.</param>
public sealed record CurseForgeFileOutcome(
    CurseForgeFileState State,
    ModFile? File,
    string? PageUrl,
    string? InstalledPath = null);

/// <summary>
/// CurseForge as a second mod source. Its API wants a key on every call and a key cannot
/// ship in an open-source launcher, so every request goes to the owner's mirror, which
/// adds the key (workers/updates/worker.js, docs/curseforge.md). Until the mirror says
/// it has one, <see cref="IsAvailableAsync"/> is false and nothing should show this
/// source at all.
///
/// Answers come back in the records the Modrinth client uses, so the browser needs one
/// code path. What is different about CurseForge is kept here: a file can be closed to
/// third-party downloads, and files are only ever fetched from CurseForge's own CDN
/// and checked against the SHA-1 its API gave.
/// </summary>
public sealed class CurseForgeClient : IModSource, IModFingerprintLookup
{
    /// <summary>Minecraft. The mirror serves nothing else.</summary>
    public const int GameId = 432;

    public const int ModsClassId = 6;
    public const int ResourcePacksClassId = 12;
    public const int ShadersClassId = 6552;

    /// <summary>The API refuses larger pages.</summary>
    public const int MaxPageSize = 50;

    /// <summary>The API refuses to page past this many results.</summary>
    private const int MaxResultWindow = 10_000;

    /// <summary>What the mirror takes in one fingerprint request.</summary>
    private const int FingerprintBatch = 1000;

    /// <summary>The only hosts a file is downloaded from, whatever address an answer carries.</summary>
    private static readonly string[] CdnHosts = { "edge.forgecdn.net", "mediafilez.forgecdn.net" };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly DownloadClient _downloader;

    /// <summary>Mod id to its page on curseforge.com, learned from search and project answers.</summary>
    private readonly ConcurrentDictionary<string, string> _pages = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<int, IReadOnlyList<CategoryDto>> _categories = new();

    private readonly object _gate = new();
    private string _baseUrl = string.Empty;
    private bool? _available;

    public CurseForgeClient(HttpClient http, DownloadClient downloader)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    }

    public ModSource Source => ModSource.CurseForge;

    /// <summary>
    /// The mirror's CurseForge address, "https://…/cf". Empty switches the source off.
    /// Setting a different address forgets what the previous one answered.
    /// </summary>
    public string BaseUrl
    {
        get
        {
            lock (_gate)
            {
                return _baseUrl;
            }
        }
        set
        {
            var cleaned = (value ?? string.Empty).Trim().TrimEnd('/');

            lock (_gate)
            {
                if (!string.Equals(cleaned, _baseUrl, StringComparison.Ordinal))
                {
                    _baseUrl = cleaned;
                    _available = null;
                }
            }
        }
    }

    /// <summary>
    /// The address to use given what the catalog says: its own when it names one, the
    /// built-in mirror when it says nothing, and none when it says "" - which is how the
    /// owner switches CurseForge off for everyone without a release.
    /// </summary>
    public static string ResolveBaseUrl(string? fromCatalog, string builtIn)
        => fromCatalog is null ? builtIn : fromCatalog.Trim();

    /// <summary>
    /// True when the mirror has a working key: GET /ping answers 200. The answer is kept
    /// for the session, so this can be asked on every screen. A mirror that could not be
    /// reached is not remembered - the player may only be offline for a minute.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        string baseUrl;

        lock (_gate)
        {
            if (_baseUrl.Length == 0)
            {
                return false;
            }

            if (_available is { } known)
            {
                return known;
            }

            baseUrl = _baseUrl;
        }

        HttpStatusCode status;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = NewRequest(HttpMethod.Get, baseUrl + "/ping");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = response.StatusCode;
        }
        catch (Exception)
        {
            return false;
        }

        var answer = (int)status is >= 200 and < 300;

        // 503 is the mirror saying "no key here"; 404 is a mirror from before this route.
        // Anything else is a hiccup, and the next call asks again.
        if (answer || status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound)
        {
            lock (_gate)
            {
                if (string.Equals(baseUrl, _baseUrl, StringComparison.Ordinal))
                {
                    _available = answer;
                }
            }
        }

        return answer;
    }

    // ===================== The browser's four questions =====================

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
        var classId = ClassIdFor(projectType);
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);
        var index = Math.Max(0, offset);
        var empty = new ModSearchPage(Array.Empty<ModSearchResult>(), 0);

        if (index + pageSize > MaxResultWindow)
        {
            return empty;
        }

        var parts = new List<string> { $"gameId={GameId}", $"classId={classId}" };

        if (!string.IsNullOrWhiteSpace(category))
        {
            // A category this source does not have holds nothing here. Searching without
            // the filter instead would put every mod under a chip that says "Magic".
            if (await ResolveCategoryIdAsync(category!, classId, cancellationToken).ConfigureAwait(false) is not { } categoryId)
            {
                return empty;
            }

            parts.Add($"categoryId={categoryId}");
        }

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            parts.Add("gameVersion=" + Uri.EscapeDataString(gameVersion!));

            // The API takes a loader only together with a game version.
            if (ProjectTypes.UsesLoader(projectType) && ToLoaderType(loader) is { } loaderType)
            {
                parts.Add($"modLoaderType={loaderType}");
            }
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add("searchFilter=" + Uri.EscapeDataString(query.Trim()));
        }

        parts.Add($"sortField={SortField(sort)}");
        parts.Add("sortOrder=desc");
        parts.Add($"index={index}");
        parts.Add($"pageSize={pageSize}");

        var response = await GetAsync<Envelope<List<ModDto>>>("/v1/mods/search?" + string.Join("&", parts), cancellationToken)
            .ConfigureAwait(false);

        var items = (response?.Data ?? new List<ModDto>()).Select(ToSearchResult).ToList();
        var total = response?.Pagination?.TotalCount ?? items.Count;

        return new ModSearchPage(items, Math.Min(total, MaxResultWindow));
    }

    /// <summary>The project page. Takes CurseForge's numeric id, or a slug.</summary>
    public async Task<ModProject?> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
    {
        try
        {
            var mod = await FindModAsync(idOrSlug, cancellationToken).ConfigureAwait(false);

            if (mod is null)
            {
                return null;
            }

            string? body = null;

            try
            {
                // The long description is HTML; MarkdownText.ToPlainText already reads that.
                body = (await GetAsync<Envelope<string>>($"/v1/mods/{mod.Id}/description", cancellationToken).ConfigureAwait(false))?.Data;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The page is still worth showing with the short description alone.
            }

            var shots = (mod.Screenshots ?? new List<ImageDto>())
                .Where(s => !string.IsNullOrWhiteSpace(s.Url) || !string.IsNullOrWhiteSpace(s.ThumbnailUrl))
                .ToList();

            return new ModProject(
                Id(mod.Id),
                mod.Slug ?? string.Empty,
                mod.Name ?? string.Empty,
                mod.Summary ?? string.Empty,
                body,
                IconOf(mod),
                (long)mod.DownloadCount,
                shots.Select(s => string.IsNullOrWhiteSpace(s.ThumbnailUrl) ? s.Url! : s.ThumbnailUrl!).ToList())
            {
                GalleryFull = shots.Select(s => string.IsNullOrWhiteSpace(s.Url) ? s.ThumbnailUrl! : s.Url!).ToList(),
                ProjectType = ProjectTypeFor(mod.ClassId),
                Source = ModSource.CurseForge,
                PageUrl = ProjectPage(Id(mod.Id))
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The top-level categories of a project type. <see cref="ModCategory.Name"/> is the
    /// category's slug, which is what <see cref="SearchAsync"/> takes back.
    /// </summary>
    public async Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(
        string projectType = ProjectTypes.Mod,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var classId = ClassIdFor(projectType);
            var all = await LoadCategoriesAsync(classId, cancellationToken).ConfigureAwait(false);

            // The sub-categories ("Energy" under "Technology") would triple the row of chips.
            return all
                .Where(c => c.ParentCategoryId == classId && !string.IsNullOrWhiteSpace(c.Slug))
                .Select(c => new ModCategory(c.Slug!, "categories") { Title = c.Name })
                .OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<ModCategory>();
        }
    }

    /// <summary>
    /// A project's files for a game version and loader, newest first, each as one
    /// <see cref="ModVersion"/> holding one file. <see cref="ModVersion.Id"/> is the
    /// CurseForge file id. A file closed to third-party downloads is in the list with an
    /// empty <see cref="ModFile.Url"/>; ask <see cref="CheckDownload"/> what to do with it.
    /// </summary>
    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        var modId = await ResolveIdAsync(projectIdOrSlug, cancellationToken).ConfigureAwait(false);

        if (modId is null)
        {
            return Array.Empty<ModVersion>();
        }

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            parts.Add("gameVersion=" + Uri.EscapeDataString(gameVersion!));
        }

        if (ToLoaderType(loader) is { } loaderType)
        {
            parts.Add($"modLoaderType={loaderType}");
        }

        parts.Add($"pageSize={MaxPageSize}");

        var response = await GetAsync<Envelope<List<FileDto>>>($"/v1/mods/{modId}/files?" + string.Join("&", parts), cancellationToken)
            .ConfigureAwait(false);

        return (response?.Data ?? new List<FileDto>())
            .Where(f => f.IsAvailable != false && f.IsServerPack != true)
            .OrderByDescending(f => DateOf(f))
            .ThenByDescending(f => f.Id)
            .Select(ToModVersion)
            .ToList();
    }

    // ===================== One file =====================

    /// <summary>One file by the ids kept in an <see cref="InstalledModRecord"/>; null when it is gone.</summary>
    public async Task<ModVersion?> GetFileAsync(string projectId, string fileId, CancellationToken cancellationToken = default)
    {
        if (!IsNumber(projectId) || !IsNumber(fileId))
        {
            return null;
        }

        var response = await GetAsync<Envelope<FileDto>>($"/v1/mods/{projectId}/files/{fileId}", cancellationToken).ConfigureAwait(false);

        return response?.Data is { } file ? ToModVersion(file) : null;
    }

    /// <summary>
    /// The CDN address of a file, asked of CurseForge directly. Null when the author
    /// closed the file to third-party downloads, when the file is gone, and when the
    /// answer points anywhere but the CDN.
    /// </summary>
    public async Task<string?> GetDownloadUrlAsync(string projectId, string fileId, CancellationToken cancellationToken = default)
    {
        if (!IsNumber(projectId) || !IsNumber(fileId))
        {
            return null;
        }

        var response = await GetAsync<Envelope<string>>(
            $"/v1/mods/{projectId}/files/{fileId}/download-url",
            cancellationToken,
            forbiddenIsNull: true).ConfigureAwait(false);

        return IsCdnUrl(response?.Data) ? response!.Data : null;
    }

    /// <summary>
    /// What can be done with a version's file, from what is already known - no request.
    /// Ready only with a CDN address, a full SHA-1 and a plain file name.
    /// </summary>
    public static CurseForgeFileOutcome CheckDownload(ModVersion version)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        var file = version.PrimaryFile;

        if (file is null || !IsSafeFileName(file.FileName))
        {
            return new CurseForgeFileOutcome(CurseForgeFileState.Unavailable, null, version.PageUrl);
        }

        if (file.Url.Length == 0)
        {
            return new CurseForgeFileOutcome(CurseForgeFileState.Blocked, file, version.PageUrl);
        }

        return IsCdnUrl(file.Url) && IsSha1(file.Sha1)
            ? new CurseForgeFileOutcome(CurseForgeFileState.Ready, file, version.PageUrl)
            : new CurseForgeFileOutcome(CurseForgeFileState.Unavailable, file, version.PageUrl);
    }

    /// <summary>
    /// The same, with a second opinion for a file that came without an address: file
    /// lists sometimes lack one that the download-url call still gives. Blocked from
    /// here is final.
    /// </summary>
    public async Task<CurseForgeFileOutcome> ResolveDownloadAsync(ModVersion version, CancellationToken cancellationToken = default)
    {
        var outcome = CheckDownload(version);

        if (outcome.State != CurseForgeFileState.Blocked || string.IsNullOrEmpty(version.ProjectId))
        {
            return outcome;
        }

        var url = await GetDownloadUrlAsync(version.ProjectId!, version.Id, cancellationToken).ConfigureAwait(false);

        if (url is null)
        {
            return outcome;
        }

        var file = outcome.File! with { Url = url };

        return IsSha1(file.Sha1)
            ? new CurseForgeFileOutcome(CurseForgeFileState.Ready, file, outcome.PageUrl)
            : new CurseForgeFileOutcome(CurseForgeFileState.Unavailable, file, outcome.PageUrl);
    }

    /// <summary>
    /// Downloads a version's file into one of the build's content folders ("mods",
    /// "resourcepacks", "shaderpacks") and checks it against the SHA-1 from the API.
    /// A blocked or unusable file is not an exception: the outcome says so and nothing
    /// is written. A failed download or a wrong hash throws, as the Modrinth path does.
    /// </summary>
    public async Task<CurseForgeFileOutcome> InstallAsync(
        ModVersion version,
        string gameDirectory,
        string folder,
        CancellationToken cancellationToken = default)
    {
        var outcome = await ResolveDownloadAsync(version, cancellationToken).ConfigureAwait(false);

        if (outcome.State != CurseForgeFileState.Ready)
        {
            return outcome;
        }

        var file = outcome.File!;
        var directory = Path.Combine(gameDirectory, folder);
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, file.FileName);

        await _downloader
            .EnsureFileAsync(new DownloadItem(file.Url, destination, file.Sha1, file.Size), cancellationToken)
            .ConfigureAwait(false);

        return outcome with { InstalledPath = destination };
    }

    /// <summary>
    /// The record to keep for a file installed from here: the slug in Id, as for Modrinth,
    /// so "is it in the build" keeps working by slug, plus the two numbers that find the
    /// exact file again.
    /// </summary>
    public static InstalledModRecord RecordFor(ModVersion version, ModFile file, string slug, string title, string? iconUrl, string folder)
        => new()
        {
            FileName = file.FileName,
            Source = ModSource.CurseForge,
            Id = slug,
            Name = title,
            IconUrl = iconUrl,
            Version = version.VersionNumber,
            Folder = folder,
            ProjectId = version.ProjectId,
            FileId = version.Id
        };

    // ===================== Files already in the folder =====================

    /// <summary>
    /// Which CurseForge files a set of jars are, by <see cref="CurseForgeFingerprint"/>.
    /// Files CurseForge does not know are simply absent from the result. This is what
    /// recognises a jar that was dropped into the folder by hand.
    /// </summary>
    public async Task<IReadOnlyDictionary<uint, ModVersion>> MatchFingerprintsAsync(
        IEnumerable<uint> fingerprints,
        CancellationToken cancellationToken = default)
    {
        var wanted = fingerprints.Distinct().ToList();
        var result = new Dictionary<uint, ModVersion>();

        for (var start = 0; start < wanted.Count; start += FingerprintBatch)
        {
            var body = JsonSerializer.Serialize(new { fingerprints = wanted.Skip(start).Take(FingerprintBatch) });

            using var request = NewRequest(HttpMethod.Post, RequireBase() + $"/v1/fingerprints/{GameId}");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var matches = JsonSerializer.Deserialize<Envelope<FingerprintDto>>(json, JsonOptions)?.Data?.ExactMatches;

            foreach (var match in matches ?? new List<FingerprintMatchDto>())
            {
                if (match.File is { } file && file.FileFingerprint is >= 0 and <= uint.MaxValue)
                {
                    result[(uint)file.FileFingerprint] = ToModVersion(file);
                }
            }
        }

        return result;
    }

    // ===================== Rules =====================

    /// <summary>True for an https address on CurseForge's CDN - the only place files are downloaded from.</summary>
    public static bool IsCdnUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           CdnHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>CurseForge's number for a loader; null when there is nothing to filter by.</summary>
    public static int? ToLoaderType(LoaderKind loader) => loader switch
    {
        LoaderKind.Forge => 1,
        LoaderKind.Fabric => 4,
        LoaderKind.Quilt => 5,
        LoaderKind.NeoForge => 6,
        _ => null
    };

    public static int ClassIdFor(string? projectType) => projectType switch
    {
        ProjectTypes.ResourcePack => ResourcePacksClassId,
        ProjectTypes.Shader => ShadersClassId,
        _ => ModsClassId
    };

    private static string ProjectTypeFor(int? classId) => classId switch
    {
        ResourcePacksClassId => ProjectTypes.ResourcePack,
        ShadersClassId => ProjectTypes.Shader,
        _ => ProjectTypes.Mod
    };

    /// <summary>
    /// The browser's sort names in CurseForge's numbers. It has no "relevance"; with or
    /// without a query, popularity is the closest to what a player expects on top.
    /// </summary>
    private static int SortField(string? sort) => sort switch
    {
        "downloads" => 6,
        "newest" => 11,
        "updated" => 3,
        _ => 2
    };

    private static bool IsSha1(string? value)
        => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool IsNumber(string? value)
        => value is { Length: > 0 and <= 10 } && value.All(c => c is >= '0' and <= '9');

    /// <summary>The name is written under the build's folder as is, so it must be a name and nothing else.</summary>
    private static bool IsSafeFileName(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           name is not ("." or "..") &&
           name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
           name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Only curseforge.com pages are ever handed to the browser.</summary>
    private static bool IsCurseForgePage(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           (uri.Host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase));

    private static string Id(int value) => value.ToString(CultureInfo.InvariantCulture);

    // ===================== Mapping =====================

    private ModSearchResult ToSearchResult(ModDto mod)
    {
        Remember(mod);

        return new ModSearchResult(
            Id(mod.Id),
            mod.Slug ?? string.Empty,
            mod.Name ?? string.Empty,
            mod.Summary ?? string.Empty,
            IconOf(mod),
            (long)mod.DownloadCount,
            mod.Authors?.FirstOrDefault()?.Name)
        {
            // The class ("Mods") sits in the same list as the categories.
            Categories = (mod.Categories ?? new List<CategoryDto>())
                .Where(c => c.IsClass != true && !string.IsNullOrWhiteSpace(c.Slug))
                .Select(c => c.Slug!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Source = ModSource.CurseForge,
            PageUrl = ProjectPage(Id(mod.Id))
        };
    }

    private ModVersion ToModVersion(FileDto file)
    {
        var tags = file.GameVersions ?? new List<string>();
        var files = new List<ModFile>();

        if (IsSafeFileName(file.FileName))
        {
            files.Add(new ModFile(
                // An address anywhere but the CDN is treated as no address at all.
                IsCdnUrl(file.DownloadUrl) ? file.DownloadUrl! : string.Empty,
                file.FileName!,
                file.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value?.ToLowerInvariant(),
                null,
                file.FileLength,
                Primary: true));
        }

        var name = DisplayName(file);
        var projectId = Id(file.ModId);

        return new ModVersion(
            Id(file.Id),
            name,
            name,
            // "1.21.1" sits next to "Fabric", "Client" and "Java 21" in one list.
            tags.Where(t => t.Length > 0 && char.IsDigit(t[0])).ToList(),
            tags.Where(t => t is "Forge" or "Fabric" or "Quilt" or "NeoForge").Select(t => t.ToLowerInvariant()).ToList(),
            files,
            file.ReleaseType switch { 2 => "beta", 3 => "alpha", _ => "release" })
        {
            Dependencies = (file.Dependencies ?? new List<DependencyDto>())
                .Select(d => new ModDependency(Id(d.ModId), null, DependencyType(d.RelationType)))
                .ToList(),
            ProjectId = projectId,
            Source = ModSource.CurseForge,
            PageUrl = FilePage(projectId, Id(file.Id))
        };
    }

    /// <summary>CurseForge has no version number, only a display name, which is often the file name.</summary>
    private static string DisplayName(FileDto file)
    {
        var name = string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName ?? string.Empty : file.DisplayName!.Trim();

        return name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    /// <summary>Relation types in Modrinth's words; only 3, "required", is acted on.</summary>
    private static string DependencyType(int relationType) => relationType switch
    {
        3 => "required",
        5 => "incompatible",
        1 or 6 => "embedded",
        _ => "optional"
    };

    private static DateTimeOffset DateOf(FileDto file)
        => DateTimeOffset.TryParse(file.FileDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : DateTimeOffset.MinValue;

    private static string? IconOf(ModDto mod)
        => !string.IsNullOrWhiteSpace(mod.Logo?.ThumbnailUrl) ? mod.Logo!.ThumbnailUrl : mod.Logo?.Url;

    private void Remember(ModDto mod)
    {
        if (IsCurseForgePage(mod.Links?.WebsiteUrl))
        {
            _pages[Id(mod.Id)] = mod.Links!.WebsiteUrl!.TrimEnd('/');
        }
    }

    /// <summary>The project's page; the numeric address works for a mod this session has not seen.</summary>
    private string ProjectPage(string projectId)
        => _pages.TryGetValue(projectId, out var url) ? url : $"https://www.curseforge.com/projects/{projectId}";

    private string FilePage(string projectId, string fileId)
        => _pages.TryGetValue(projectId, out var url) ? $"{url}/files/{fileId}" : ProjectPage(projectId);

    // ===================== Requests =====================

    private async Task<ModDto?> FindModAsync(string idOrSlug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idOrSlug))
        {
            return null;
        }

        ModDto? mod = null;

        if (IsNumber(idOrSlug))
        {
            mod = (await GetAsync<Envelope<ModDto>>($"/v1/mods/{idOrSlug}", cancellationToken).ConfigureAwait(false))?.Data;
        }
        else
        {
            // A slug is unique within a class, and the mirror serves three of them.
            foreach (var classId in new[] { ModsClassId, ResourcePacksClassId, ShadersClassId })
            {
                var found = await GetAsync<Envelope<List<ModDto>>>(
                    $"/v1/mods/search?gameId={GameId}&classId={classId}&slug={Uri.EscapeDataString(idOrSlug.Trim())}&pageSize=1",
                    cancellationToken).ConfigureAwait(false);

                mod = found?.Data?.FirstOrDefault();

                if (mod is not null)
                {
                    break;
                }
            }
        }

        if (mod is not null)
        {
            Remember(mod);
        }

        return mod;
    }

    private async Task<string?> ResolveIdAsync(string idOrSlug, CancellationToken cancellationToken)
    {
        if (IsNumber(idOrSlug))
        {
            return idOrSlug;
        }

        return await FindModAsync(idOrSlug, cancellationToken).ConfigureAwait(false) is { } mod ? Id(mod.Id) : null;
    }

    private async Task<IReadOnlyList<CategoryDto>> LoadCategoriesAsync(int classId, CancellationToken cancellationToken)
    {
        if (_categories.TryGetValue(classId, out var cached))
        {
            return cached;
        }

        var response = await GetAsync<Envelope<List<CategoryDto>>>($"/v1/categories?gameId={GameId}&classId={classId}", cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<CategoryDto> all = response?.Data ?? new List<CategoryDto>();

        if (all.Count > 0)
        {
            _categories[classId] = all;
        }

        return all;
    }

    /// <summary>A category by its id, slug or name; null when this source has no such category.</summary>
    private async Task<int?> ResolveCategoryIdAsync(string category, int classId, CancellationToken cancellationToken)
    {
        if (IsNumber(category))
        {
            return int.Parse(category, CultureInfo.InvariantCulture);
        }

        var all = await LoadCategoriesAsync(classId, cancellationToken).ConfigureAwait(false);

        return all.FirstOrDefault(c =>
            string.Equals(c.Slug, category, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private string RequireBase()
    {
        var baseUrl = BaseUrl;

        return baseUrl.Length > 0
            ? baseUrl
            : throw new InvalidOperationException("CurseForge is switched off: there is no mirror address.");
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);

        // The mirror only answers callers that name themselves, the way /report does.
        request.Headers.TryAddWithoutValidation("X-STlauncher", "curseforge");

        return request;
    }

    /// <summary>Null for "no such thing" (404, and 403 where that means a closed file); throws for anything else that is not a success.</summary>
    private async Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken, bool forbiddenIsNull = false)
        where T : class
    {
        using var request = NewRequest(HttpMethod.Get, RequireBase() + pathAndQuery);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound ||
            (forbiddenIsNull && response.StatusCode == HttpStatusCode.Forbidden))
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    // ===================== The API's shapes =====================
    // CurseForge names its fields in camelCase, which the case-insensitive options map
    // onto these properties without an attribute on each.

    private sealed class Envelope<T>
    {
        public T? Data { get; set; }

        public PaginationDto? Pagination { get; set; }
    }

    private sealed class PaginationDto
    {
        public int Index { get; set; }

        public int PageSize { get; set; }

        public int ResultCount { get; set; }

        public int TotalCount { get; set; }
    }

    private sealed class ModDto
    {
        public int Id { get; set; }

        public int GameId { get; set; }

        public string? Name { get; set; }

        public string? Slug { get; set; }

        public LinksDto? Links { get; set; }

        public string? Summary { get; set; }

        /// <summary>A whole number that the API has been known to send with a decimal point.</summary>
        public double DownloadCount { get; set; }

        public int? ClassId { get; set; }

        public List<CategoryDto>? Categories { get; set; }

        public List<AuthorDto>? Authors { get; set; }

        public ImageDto? Logo { get; set; }

        public List<ImageDto>? Screenshots { get; set; }
    }

    private sealed class LinksDto
    {
        public string? WebsiteUrl { get; set; }
    }

    private sealed class AuthorDto
    {
        public string? Name { get; set; }
    }

    private sealed class ImageDto
    {
        public string? ThumbnailUrl { get; set; }

        public string? Url { get; set; }
    }

    private sealed class CategoryDto
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        public string? Slug { get; set; }

        public bool? IsClass { get; set; }

        public int? ClassId { get; set; }

        public int? ParentCategoryId { get; set; }
    }

    private sealed class FileDto
    {
        public int Id { get; set; }

        public int ModId { get; set; }

        public bool? IsAvailable { get; set; }

        public string? DisplayName { get; set; }

        public string? FileName { get; set; }

        /// <summary>1 release, 2 beta, 3 alpha.</summary>
        public int ReleaseType { get; set; }

        public List<HashDto>? Hashes { get; set; }

        public string? FileDate { get; set; }

        public long FileLength { get; set; }

        /// <summary>Null when the author closed the project to third-party downloads.</summary>
        public string? DownloadUrl { get; set; }

        public List<string>? GameVersions { get; set; }

        public List<DependencyDto>? Dependencies { get; set; }

        public bool? IsServerPack { get; set; }

        public long FileFingerprint { get; set; }
    }

    private sealed class HashDto
    {
        public string? Value { get; set; }

        /// <summary>1 is SHA-1, 2 is MD5.</summary>
        public int Algo { get; set; }
    }

    private sealed class DependencyDto
    {
        public int ModId { get; set; }

        /// <summary>1 embedded, 2 optional, 3 required, 4 tool, 5 incompatible, 6 include.</summary>
        public int RelationType { get; set; }
    }

    private sealed class FingerprintDto
    {
        public List<FingerprintMatchDto>? ExactMatches { get; set; }
    }

    private sealed class FingerprintMatchDto
    {
        public int Id { get; set; }

        public FileDto? File { get; set; }
    }
}

/// <summary>
/// CurseForge's name for a file: MurmurHash2 with seed 1 over the file's bytes with the
/// whitespace ones (tab, line feed, carriage return, space) left out. It is the only
/// thing its API matches files by; SHA-1 lookups do not exist there.
/// </summary>
public static class CurseForgeFingerprint
{
    public static uint Compute(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        const uint m = 0x5bd1e995;
        var buffer = new byte[1 << 16];
        var start = stream.Position;
        int read;

        // The hash starts from the length, and the length counts only what is hashed:
        // one pass to count, one to hash, so a large jar is never held in memory.
        uint length = 0;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (!IsWhitespace(buffer[i]))
                {
                    length++;
                }
            }
        }

        stream.Position = start;

        unchecked
        {
            var h = 1u ^ length;
            uint k = 0;
            var shift = 0;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var b = buffer[i];

                    if (IsWhitespace(b))
                    {
                        continue;
                    }

                    k |= (uint)b << shift;
                    shift += 8;

                    if (shift == 32)
                    {
                        k *= m;
                        k ^= k >> 24;
                        k *= m;
                        h *= m;
                        h ^= k;
                        k = 0;
                        shift = 0;
                    }
                }
            }

            if (shift > 0)
            {
                h ^= k;
                h *= m;
            }

            h ^= h >> 13;
            h *= m;
            h ^= h >> 15;

            return h;
        }
    }

    /// <summary>The fingerprint of a file on disk, or null when it cannot be read.</summary>
    public static uint? TryComputeFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Compute(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsWhitespace(byte b) => b is 9 or 10 or 13 or 32;
}
