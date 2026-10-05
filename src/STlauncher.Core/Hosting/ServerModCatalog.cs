using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Hosting;

/// <summary>One file an install would fetch.</summary>
/// <param name="IsDependency">True for a mod the chosen one needs; false for the chosen one itself.</param>
public sealed record ServerModDownload(
    string ProjectId,
    string Title,
    string VersionId,
    string VersionNumber,
    string FileName,
    string Url,
    string Sha1,
    long Size,
    bool IsDependency)
{
    /// <summary>The host the file comes from, for the player to see before agreeing.</summary>
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
}

public enum ServerModProblemKind
{
    /// <summary>The mod has no version for this game version and loader.</summary>
    NoVersion,

    /// <summary>The version has no file that can be checked after the download.</summary>
    NoFile
}

/// <summary>Why an install cannot be offered. <paramref name="Title"/> is the mod it is about.</summary>
public sealed record ServerModProblem(ServerModProblemKind Kind, string Title);

/// <summary>
/// Everything adding one mod to a server would do, worked out before anything is
/// fetched: the files with their sizes, what the server already has, and what stands in
/// the way.
/// </summary>
/// <param name="Downloads">The mods it needs first, then the mod itself.</param>
/// <param name="AlreadyThere">Titles of needed mods the server already has.</param>
public sealed record ServerModPlan(
    string ProjectId,
    string Title,
    IReadOnlyList<ServerModDownload> Downloads,
    IReadOnlyList<string> AlreadyThere,
    IReadOnlyList<ServerModProblem> Problems)
{
    public long TotalBytes => Downloads.Sum(d => d.Size);

    public bool CanInstall => Problems.Count == 0 && Downloads.Count > 0;
}

/// <param name="Installed">File names now in the server's mods folder.</param>
/// <param name="SwitchedOff">Older files of the same mods, renamed to ".disabled" so two versions do not load together.</param>
public sealed record ServerModInstallResult(IReadOnlyList<string> Installed, IReadOnlyList<string> SwitchedOff);

/// <summary>What the server already has, in the terms a search hit can be compared by.</summary>
public sealed class ServerModPresence
{
    private readonly Dictionary<string, string> _projects;
    private readonly HashSet<string> _names;

    internal ServerModPresence(Dictionary<string, string> projects, HashSet<string> names)
    {
        _projects = projects;
        _names = names;
    }

    /// <summary>Modrinth project id to the title of the server's file of it.</summary>
    public IReadOnlyDictionary<string, string> Projects => _projects;

    public bool HasProject(string? projectId) => !string.IsNullOrEmpty(projectId) && _projects.ContainsKey(projectId!);

    /// <summary>
    /// True when the server has a mod of this project, or one whose id or title is the
    /// hit's slug or title. The second is for a jar Modrinth does not know by its hash:
    /// a mod built by hand is still that mod.
    /// </summary>
    public bool Has(string? projectId, string? slug, string? title)
        => HasProject(projectId) ||
           _names.Contains(ServerConfigFiles.Normalize(slug)) ||
           _names.Contains(ServerConfigFiles.Normalize(title));
}

/// <summary>
/// Mods for a server from Modrinth: the search that leaves client-only mods out, the plan
/// of what one mod brings with it, and the install itself. The plan is separate from the
/// install on purpose: the page shows it, and nothing is fetched until the player agrees.
/// </summary>
public sealed class ServerModCatalog
{
    /// <summary>A mod that needs a mod that needs a mod: deeper than this is a loop or a mistake.</summary>
    private const int MaxDepth = 6;

    private readonly ModrinthClient _modrinth;
    private readonly ModManager _mods;

    public ServerModCatalog(ModrinthClient modrinth, ModManager mods)
    {
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
        _mods = mods ?? throw new ArgumentNullException(nameof(mods));
    }

    /// <summary>
    /// The search query for a server: mods for its game version and loader that Modrinth
    /// marks as running on a server - required there or optional, not "unsupported". The
    /// inner list is an "or", the outer one an "and", as Modrinth reads facets.
    /// </summary>
    public static string BuildFacets(string? gameVersion, LoaderKind loader)
    {
        var facets = new List<string> { $"[\"project_type:{ProjectTypes.Mod}\"]" };

        if (ModrinthClient.ToModrinthLoader(loader) is { } loaderName)
        {
            facets.Add($"[\"categories:{loaderName}\"]");
        }

        if (!string.IsNullOrEmpty(gameVersion))
        {
            facets.Add($"[\"versions:{gameVersion}\"]");
        }

        facets.Add("[\"server_side:required\",\"server_side:optional\"]");

        return "[" + string.Join(",", facets) + "]";
    }

    /// <summary>True for the servers mods can be added to at all: a vanilla one loads none.</summary>
    public static bool SupportsMods(LoaderKind loader) => ModrinthClient.ToModrinthLoader(loader) is not null;

    /// <summary>Mods that run on this server. An empty query lists the most downloaded ones.</summary>
    public Task<ModSearchPage> SearchAsync(
        string? query,
        string? gameVersion,
        LoaderKind loader,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var text = query?.Trim() ?? string.Empty;

        return _modrinth.SearchByFacetsAsync(
            text,
            BuildFacets(gameVersion, loader),
            text.Length == 0 ? "downloads" : "relevance",
            limit,
            offset,
            cancellationToken);
    }

    /// <summary>
    /// What the server has, for marking search hits and for not fetching a needed mod
    /// twice. The files Modrinth knows by their hash are asked about in one request; when
    /// that fails the record and the jars' own ids still answer.
    /// </summary>
    public async Task<ServerModPresence> PresenceAsync(
        string serverDirectory,
        IReadOnlyList<ServerModEntry>? mods = null,
        CancellationToken cancellationToken = default)
    {
        mods ??= await Task.Run(() => ServerMods.List(serverDirectory), cancellationToken).ConfigureAwait(false);

        var projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new Dictionary<string, ServerModEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            foreach (var key in mod.ModIds.Append(mod.Title).Select(ServerConfigFiles.Normalize).Where(k => k.Length > 1))
            {
                names.Add(key);
            }

            if (!string.IsNullOrEmpty(mod.ProjectId))
            {
                projects[mod.ProjectId!] = mod.Title;
            }
            else if (await Task.Run(() => ModManager.TryComputeSha1(mod.Path), cancellationToken).ConfigureAwait(false) is { } sha1)
            {
                unknown[sha1] = mod;
            }
        }

        if (unknown.Count > 0)
        {
            try
            {
                var versions = await _modrinth.GetVersionsByHashesAsync(unknown.Keys, cancellationToken).ConfigureAwait(false);

                foreach (var (sha1, version) in versions)
                {
                    if (!string.IsNullOrEmpty(version.ProjectId) && unknown.TryGetValue(sha1, out var mod))
                    {
                        projects[version.ProjectId!] = mod.Title;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Offline or refused: the names above are what is left to go by.
            }
        }

        return new ServerModPresence(projects, names);
    }

    /// <summary>
    /// Works out what adding a mod would fetch: its newest version for the server's game
    /// version and loader, and every mod that version requires which the server does not
    /// have, down the chain. Nothing is downloaded and nothing on disk is changed.
    /// </summary>
    public async Task<ServerModPlan> PlanAsync(
        string projectId,
        string title,
        string? gameVersion,
        LoaderKind loader,
        ServerModPresence presence,
        CancellationToken cancellationToken = default)
    {
        var downloads = new List<ServerModDownload>();
        var already = new List<string>();
        var problems = new List<ServerModProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectId };

        await AddAsync(projectId, title, isDependency: false, depth: 0).ConfigureAwait(false);

        return new ServerModPlan(projectId, title, downloads, already, problems);

        async Task AddAsync(string id, string name, bool isDependency, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var versions = await _modrinth.GetVersionsAsync(id, gameVersion, loader, cancellationToken).ConfigureAwait(false);
            var version = ModrinthClient.SelectPreferred(ModrinthClient.NarrowTo(versions, gameVersion, loader));

            if (version is null)
            {
                problems.Add(new ServerModProblem(ServerModProblemKind.NoVersion, name));
                return;
            }

            // What a mod needs goes in before the mod, the order an install then follows:
            // a failure half way leaves the server without the mod, not with a mod that
            // cannot start.
            if (depth < MaxDepth)
            {
                foreach (var dependency in version.Dependencies.Where(d => d.IsRequired && !string.IsNullOrEmpty(d.ProjectId)))
                {
                    var dependencyId = dependency.ProjectId!;

                    if (!seen.Add(dependencyId))
                    {
                        continue;
                    }

                    if (presence.Projects.TryGetValue(dependencyId, out var have))
                    {
                        already.Add(have);
                        continue;
                    }

                    var project = await _modrinth.GetProjectAsync(dependencyId, cancellationToken).ConfigureAwait(false);
                    var dependencyTitle = string.IsNullOrWhiteSpace(project?.Title) ? dependencyId : project!.Title;

                    if (presence.Has(dependencyId, project?.Slug, project?.Title))
                    {
                        already.Add(dependencyTitle);
                        continue;
                    }

                    await AddAsync(dependencyId, dependencyTitle, isDependency: true, depth + 1).ConfigureAwait(false);
                }
            }

            var file = version.SelectFile(gameVersion, loader);

            // A file that cannot be checked after the download is not offered at all, and
            // its name is used as a name only: it comes from the network.
            if (file is null ||
                string.IsNullOrWhiteSpace(file.Sha1) ||
                !ServerMods.IsPlainModName(file.FileName) ||
                file.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                problems.Add(new ServerModProblem(ServerModProblemKind.NoFile, name));
                return;
            }

            downloads.Add(new ServerModDownload(
                id,
                name,
                version.Id,
                version.VersionNumber,
                file.FileName,
                file.Url,
                file.Sha1!,
                file.Size,
                isDependency));
        }
    }

    /// <summary>
    /// Carries a plan out: each file is fetched into the server's mods folder and checked
    /// against the hash Modrinth gave for it, then noted in the server's record. An older
    /// file of the same mod is switched off, not deleted. Called only after the player
    /// has seen the plan and agreed.
    /// </summary>
    public async Task<ServerModInstallResult> InstallAsync(
        ServerModPlan plan,
        string serverDirectory,
        IProgress<ServerModDownload>? started = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.CanInstall)
        {
            throw new InvalidOperationException("The plan has nothing that can be installed.");
        }

        var installed = new List<string>();
        var switchedOff = new List<string>();

        foreach (var download in plan.Downloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            started?.Report(download);

            await _mods
                .InstallAsync(serverDirectory, ModManager.ModsFolderName, download.FileName, download.Url, download.Sha1, download.Size, cancellationToken)
                .ConfigureAwait(false);

            ServerMods.Record(serverDirectory, new ServerModRecord
            {
                FileName = download.FileName,
                Origin = ServerModOrigin.Modrinth,
                ProjectId = download.ProjectId,
                VersionId = download.VersionId,
                Title = download.Title,
                AddedAt = DateTimeOffset.UtcNow
            });

            installed.Add(download.FileName);
            switchedOff.AddRange(SwitchOffOtherVersions(serverDirectory, download.FileName));
        }

        return new ServerModInstallResult(installed, switchedOff);
    }

    /// <summary>
    /// Two jars with one mod id stop a server from starting. The older one is renamed to
    /// ".disabled"; when a switched-off file of that name is already there it is left as
    /// it is and the older jar goes to <c>.removed</c> instead, so nothing is overwritten.
    /// </summary>
    private static IReadOnlyList<string> SwitchOffOtherVersions(string serverDirectory, string newFileName)
    {
        var directory = ModManager.ModsDirectory(serverDirectory);
        var ids = ModMetadataReader.Read(Path.Combine(directory, newFileName))
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ids.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly).ToList())
        {
            var fileName = Path.GetFileName(path);

            if (string.Equals(fileName, newFileName, StringComparison.OrdinalIgnoreCase) ||
                !ModManager.IsModFile(path) ||
                !ModMetadataReader.Read(path).Any(m => ids.Contains(m.Id)))
            {
                continue;
            }

            if (ServerMods.SetEnabled(serverDirectory, fileName, enabled: false) is null)
            {
                ServerMods.Remove(serverDirectory, fileName);
            }

            result.Add(fileName);
        }

        return result;
    }
}
