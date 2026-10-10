using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Mods;

/// <summary>
/// What a build already has, for the one question an install asks of it: "is this
/// project here?". A mod is there when the launcher recorded installing it, and also when
/// a jar in mods/ says so itself - by its own id, by an alias, or by a mod nested in it.
/// The second half is what stops a second Fabric API arriving next to the one the
/// player dropped in by hand.
/// </summary>
public sealed class InstalledBuild
{
    public static readonly InstalledBuild Empty = new(Array.Empty<InstalledModRecord>(), Array.Empty<BuildJar>(), LoaderKind.Vanilla, null);

    private readonly List<InstalledModRecord> _records;
    private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A jar-declared id, in the shapes a project slug may take, to the file that declares it.</summary>
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

    public InstalledBuild(IEnumerable<InstalledModRecord> records, IEnumerable<BuildJar> jars, LoaderKind loader, string? gameVersion)
    {
        _records = records.ToList();

        foreach (var jar in jars)
        {
            _fileNames.Add(BareName(jar.FileName));

            // The section this loader reads; a jar for another loader is still that mod.
            var section = ModMetadataReader.SectionFor(jar.Sections, loader, gameVersion) ?? jar.Sections.FirstOrDefault();

            if (section is null)
            {
                continue;
            }

            foreach (var id in section.Provides.Append(section.Id))
            {
                foreach (var shape in Shapes(id))
                {
                    _ids.TryAdd(shape, jar.FileName);
                }
            }
        }
    }

    /// <summary>The file that makes the project present, or null when the build does not have it.</summary>
    public string? Find(ModProject project)
        => Find(project.Source, project.Id, project.Slug, project.ProjectType);

    public string? Find(ModSource source, string? projectId, string? slug, string? projectType = ProjectTypes.Mod)
    {
        foreach (var record in _records)
        {
            if (!string.IsNullOrEmpty(slug) && string.Equals(record.Id, slug, StringComparison.OrdinalIgnoreCase))
            {
                return record.FileName;
            }

            if (!string.IsNullOrEmpty(projectId) && record.Source == source &&
                (string.Equals(record.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(record.Id, projectId, StringComparison.OrdinalIgnoreCase)))
            {
                return record.FileName;
            }
        }

        // Only a mod has an id inside its file; a pack is known by its record alone.
        if (!ProjectTypes.UsesLoader(projectType) || string.IsNullOrEmpty(slug))
        {
            return null;
        }

        foreach (var shape in Shapes(slug))
        {
            if (_ids.TryGetValue(shape, out var fileName))
            {
                return fileName;
            }
        }

        return null;
    }

    /// <summary>True when a file of this name is in mods/, switched on or off.</summary>
    public bool HasFile(string? fileName)
        => !string.IsNullOrEmpty(fileName) && _fileNames.Contains(BareName(fileName));

    private static string BareName(string fileName)
        => fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? fileName[..^".disabled".Length] : fileName;

    /// <summary>
    /// A slug and a mod id are the same name written by two people: "ferrite-core" and
    /// "ferritecore", "architectury-api" and "architectury", "create-fabric" and "create".
    /// Compared without punctuation, and once more without the "api" or the loader's name
    /// on the end. A miss costs a download the mod list then switches off as a double; a
    /// false hit is caught by the build check - so this may be generous, not clever.
    /// </summary>
    private static IEnumerable<string> Shapes(string name)
    {
        var plain = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        if (plain.Length == 0)
        {
            yield break;
        }

        yield return plain;

        foreach (var suffix in new[] { "api", "fabric", "neoforge", "forge", "quilt" })
        {
            if (plain.Length >= suffix.Length + 3 && plain.EndsWith(suffix, StringComparison.Ordinal))
            {
                yield return plain[..^suffix.Length];
                yield break;
            }
        }
    }
}

public enum ModPlanState
{
    /// <summary>Will be downloaded.</summary>
    Install,

    /// <summary>The build already has it; nothing comes.</summary>
    Satisfied,

    /// <summary>Its author gives the file out on the mod's page only; the player fetches it by hand.</summary>
    Blocked,

    /// <summary>There is no file of it for this game version and loader.</summary>
    Unavailable,

    /// <summary>Goes well with the mod, is not in the build, and is not brought: only named.</summary>
    Optional
}

/// <summary>One mod of an install plan with the exact file picked for it.</summary>
public sealed record ModPlanItem(
    ModSource Source,
    string ProjectId,
    string Slug,
    string Title,
    string? IconUrl,
    string ProjectType,
    bool Required,
    ModPlanState State)
{
    public ModVersion? Version { get; init; }

    public ModFile? File { get; init; }

    /// <summary>For <see cref="ModPlanState.Satisfied"/>: the file in the build that makes it so.</summary>
    public string? SatisfiedBy { get; init; }

    /// <summary>The title of the mod that asked for this one.</summary>
    public string? RequiredBy { get; init; }

    /// <summary>Where a person takes the file by hand, when the source gives a page.</summary>
    public string? PageUrl { get; init; }

    /// <summary>Bytes this item adds to the download.</summary>
    public long DownloadSize => State == ModPlanState.Install ? File?.Size ?? 0 : 0;
}

/// <summary>Two mods a source says do not go together, one of them from the plan.</summary>
/// <param name="Title">The mod of the plan that declares it.</param>
/// <param name="With">The mod it does not go with.</param>
/// <param name="InstalledFile">The file of that mod in the build; null when it is another mod of the same plan.</param>
public sealed record ModPlanConflict(string Title, string With, string? InstalledFile);

/// <summary>
/// Everything one "Add" does, decided once: the file for the mod, every mod it requires
/// down to the last one with the file for each, and what was left out and why. The panel
/// shows this and the installer carries out this - there is no second resolution for the
/// two to disagree in.
/// </summary>
public sealed class ModInstallPlan
{
    public ModInstallPlan(
        ModPlanItem root,
        IReadOnlyList<ModPlanItem> required,
        IReadOnlyList<ModPlanItem> optional,
        IReadOnlyList<ModPlanConflict> conflicts,
        string? failure)
    {
        Root = root;
        Required = required;
        Optional = optional;
        Conflicts = conflicts;
        Failure = failure;
    }

    /// <summary>The mod that was asked for.</summary>
    public ModPlanItem Root { get; }

    /// <summary>Every mod it requires, however deep, each before the mods that need it.</summary>
    public IReadOnlyList<ModPlanItem> Required { get; }

    /// <summary>What the mod itself goes well with. Never installed by the plan.</summary>
    public IReadOnlyList<ModPlanItem> Optional { get; }

    public IReadOnlyList<ModPlanConflict> Conflicts { get; }

    /// <summary>
    /// Why the plan is not whole: a required mod could not be asked about. Null for a
    /// whole plan. A plan with a hole is shown, never installed.
    /// </summary>
    public string? Failure { get; }

    public bool IsComplete => Failure is null;

    /// <summary>The files that come, in the order they are installed: what is needed first, the mod itself last.</summary>
    public IReadOnlyList<ModPlanItem> Downloads
        => Required.Where(i => i.State == ModPlanState.Install)
            .Concat(Root.State == ModPlanState.Install ? new[] { Root } : Array.Empty<ModPlanItem>())
            .ToList();

    public IEnumerable<ModPlanItem> Satisfied => Required.Where(i => i.State == ModPlanState.Satisfied);

    /// <summary>Required mods with no file for this build: the mod cannot start without them.</summary>
    public IEnumerable<ModPlanItem> Missing => Required.Where(i => i.State == ModPlanState.Unavailable);

    public IEnumerable<ModPlanItem> Blocked => Required.Where(i => i.State == ModPlanState.Blocked);

    /// <summary>True when the install is more than the one mod that was named.</summary>
    public bool BringsOthers => Required.Any(i => i.State != ModPlanState.Satisfied);

    public long TotalBytes => Downloads.Sum(i => i.DownloadSize);
}

/// <param name="Slug">May be empty when the caller only has a file to go by.</param>
public sealed record ModInstallRequest(
    ModVersion Version,
    string Slug,
    string Title,
    string? IconUrl,
    string ProjectType,
    string? GameVersion,
    LoaderKind Loader);

/// <summary>
/// Draws the <see cref="ModInstallPlan"/> for one mod, for Modrinth and CurseForge alike
/// through <see cref="IModSource"/>. Asks the sources and reads nothing from disk: the
/// build's contents come in as an <see cref="InstalledBuild"/>.
/// </summary>
public sealed class ModInstallResolver
{
    /// <summary>Chains of "requires" are three or four mods long; this is where a loop of lookups stops.</summary>
    public const int MaxDepth = 8;

    /// <summary>More mods than any one mod brings. Past this the plan is called incomplete.</summary>
    public const int MaxItems = 64;

    private readonly Dictionary<ModSource, IModSource> _sources;
    private readonly Func<ModVersion, CancellationToken, Task<bool>>? _isBlocked;

    /// <param name="isBlocked">
    /// Whether a file can only be taken from its page by hand. CurseForge's own question;
    /// without it nothing is blocked.
    /// </param>
    public ModInstallResolver(IEnumerable<IModSource> sources, Func<ModVersion, CancellationToken, Task<bool>>? isBlocked = null)
    {
        _sources = sources.ToDictionary(s => s.Source);
        _isBlocked = isBlocked;
    }

    public static LoaderKind LoaderFor(string? projectType, LoaderKind loader)
        => ProjectTypes.UsesLoader(projectType) ? loader : LoaderKind.Vanilla;

    public async Task<ModInstallPlan> ResolveAsync(ModInstallRequest request, InstalledBuild build, CancellationToken cancellationToken = default)
    {
        var walk = new Walk(request, build);
        var version = request.Version;

        var rootFile = ModrinthClient.SelectFile(version, request.GameVersion, LoaderFor(request.ProjectType, request.Loader));
        var rootState = ModPlanState.Install;

        // A CurseForge file may come without an address and still be downloadable; the
        // blocked question settles that. For Modrinth no address means no file.
        if (rootFile is null || (version.Source != ModSource.CurseForge && string.IsNullOrEmpty(rootFile.Url)))
        {
            rootState = ModPlanState.Unavailable;
        }
        else if (await IsBlockedAsync(version, cancellationToken).ConfigureAwait(false))
        {
            rootState = ModPlanState.Blocked;
        }

        var root = new ModPlanItem(
            version.Source,
            version.ProjectId ?? string.Empty,
            request.Slug,
            request.Title,
            request.IconUrl,
            request.ProjectType,
            Required: true,
            rootState)
        {
            Version = version,
            File = rootFile,
            PageUrl = version.PageUrl
        };

        // The mod itself is on the path from the start: a dependency that requires it
        // back is a circle, not one more file.
        walk.Visit(version.Source, version.ProjectId, request.Slug);

        if (rootState != ModPlanState.Unavailable)
        {
            await CollectAsync(version, request.Title, 0, walk, cancellationToken).ConfigureAwait(false);
        }

        var requiredKeys = walk.Required.Select(i => Key(i.Source, i.ProjectId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // "Incompatible" with something that arrives in the same plan is a conflict too.
        foreach (var (declaredBy, source, projectId, title) in walk.Incompatible)
        {
            if (requiredKeys.Contains(Key(source, projectId)) ||
                string.Equals(Key(source, projectId), Key(root.Source, root.ProjectId), StringComparison.OrdinalIgnoreCase))
            {
                walk.Conflicts.Add(new ModPlanConflict(declaredBy, title, null));
            }
        }

        return new ModInstallPlan(
            root,
            walk.Required,
            walk.Optional.Where(o => !requiredKeys.Contains(Key(o.Source, o.ProjectId))).ToList(),
            walk.Conflicts
                .GroupBy(c => (c.Title, c.With), c => c)
                .Select(g => g.First())
                .ToList(),
            walk.Failure);
    }

    private async Task CollectAsync(ModVersion version, string title, int depth, Walk walk, CancellationToken cancellationToken)
    {
        if (!_sources.TryGetValue(version.Source, out var source))
        {
            return;
        }

        foreach (var dependency in version.Dependencies.Where(d => !string.IsNullOrEmpty(d.ProjectId)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var incompatible = string.Equals(dependency.Type, "incompatible", StringComparison.OrdinalIgnoreCase);

            // What a dependency merely goes well with is its own business, not this mod's;
            // an embedded relation is already inside the file.
            var optional = depth == 0 && string.Equals(dependency.Type, "optional", StringComparison.OrdinalIgnoreCase);

            if (!dependency.IsRequired && !optional && !incompatible)
            {
                continue;
            }

            if (dependency.IsRequired && walk.Seen(version.Source, dependency.ProjectId))
            {
                continue;
            }

            try
            {
                var project = await source.GetProjectAsync(dependency.ProjectId!, cancellationToken).ConfigureAwait(false);

                // A project the source no longer has: nothing to bring, nothing to name.
                if (project is null)
                {
                    continue;
                }

                var present = walk.Build.Find(project);

                if (incompatible)
                {
                    if (present is not null)
                    {
                        walk.Conflicts.Add(new ModPlanConflict(title, project.Title, present));
                    }

                    walk.Incompatible.Add((title, version.Source, project.Id, project.Title));
                    continue;
                }

                if (!dependency.IsRequired)
                {
                    if (!walk.Seen(version.Source, project.Id, project.Slug) &&
                        !walk.Optional.Any(o => string.Equals(o.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        walk.Optional.Add(Item(project, required: false, present is null ? ModPlanState.Optional : ModPlanState.Satisfied, title) with
                        {
                            SatisfiedBy = present
                        });
                    }

                    continue;
                }

                // The id in a dependency and the project's own may be spelled differently
                // (a slug, a number): both are marked, and a project met twice is one item.
                // A circle of requirements ends here, too: the mod that started it is marked.
                var known = walk.Seen(version.Source, project.Id, project.Slug);
                walk.Visit(version.Source, project.Id, project.Slug);
                walk.Visit(version.Source, dependency.ProjectId, null);

                if (known)
                {
                    continue;
                }

                if (present is not null)
                {
                    // What a mod already in the build needs is the build check's to say.
                    walk.Required.Add(Item(project, required: true, ModPlanState.Satisfied, title) with { SatisfiedBy = present });
                    continue;
                }

                if (walk.Required.Count >= MaxItems || depth >= MaxDepth)
                {
                    walk.Failure ??= $"{title}: the list of required mods does not end";
                    return;
                }

                var loader = LoaderFor(project.ProjectType, walk.Request.Loader);
                var candidates = await source.GetVersionsAsync(project.Id, walk.Request.GameVersion, loader, cancellationToken).ConfigureAwait(false);

                var pick = dependency.VersionId is { Length: > 0 } wanted
                    ? candidates.FirstOrDefault(v => v.Id == wanted) ?? ModrinthClient.SelectPreferred(candidates)
                    : ModrinthClient.SelectPreferred(candidates);

                var file = pick is null ? null : ModrinthClient.SelectFile(pick, walk.Request.GameVersion, loader);

                if (pick is null || file is null || (pick.Source != ModSource.CurseForge && string.IsNullOrEmpty(file.Url)))
                {
                    walk.Required.Add(Item(project, required: true, ModPlanState.Unavailable, title));
                    continue;
                }

                // The very file is in the folder under no record: a jar the player put there.
                if (ProjectTypes.UsesLoader(project.ProjectType) && walk.Build.HasFile(file.FileName))
                {
                    walk.Required.Add(Item(project, required: true, ModPlanState.Satisfied, title) with { SatisfiedBy = file.FileName });
                    continue;
                }

                var blocked = await IsBlockedAsync(pick, cancellationToken).ConfigureAwait(false);

                // What it needs comes before it: a failure half-way leaves the build
                // without the mod rather than with a mod that cannot start.
                await CollectAsync(pick, project.Title, depth + 1, walk, cancellationToken).ConfigureAwait(false);

                walk.Required.Add(Item(project, required: true, blocked ? ModPlanState.Blocked : ModPlanState.Install, title) with
                {
                    Version = pick,
                    File = file,
                    PageUrl = pick.PageUrl ?? project.PageUrl
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A required mod that could not be asked about: the install might need a
                // file this plan does not name, so the plan says it is not whole.
                if (dependency.IsRequired)
                {
                    walk.Failure ??= $"{title}: {ex.Message}";
                }
            }
        }
    }

    private async Task<bool> IsBlockedAsync(ModVersion version, CancellationToken cancellationToken)
        => _isBlocked is not null && await _isBlocked(version, cancellationToken).ConfigureAwait(false);

    private static ModPlanItem Item(ModProject project, bool required, ModPlanState state, string requiredBy)
        => new(project.Source, project.Id, project.Slug, project.Title, project.IconUrl, project.ProjectType, required, state)
        {
            RequiredBy = requiredBy,
            PageUrl = project.PageUrl
        };

    private static string Key(ModSource source, string? projectId) => $"{source}:{projectId}";

    /// <summary>The plan as it is being put together.</summary>
    private sealed class Walk
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public Walk(ModInstallRequest request, InstalledBuild build)
        {
            Request = request;
            Build = build;
        }

        public ModInstallRequest Request { get; }

        public InstalledBuild Build { get; }

        public List<ModPlanItem> Required { get; } = new();

        public List<ModPlanItem> Optional { get; } = new();

        public List<ModPlanConflict> Conflicts { get; } = new();

        public List<(string DeclaredBy, ModSource Source, string ProjectId, string Title)> Incompatible { get; } = new();

        public string? Failure { get; set; }

        public bool Seen(ModSource source, string? projectId, string? slug = null)
            => Keys(source, projectId, slug).Any(_seen.Contains);

        /// <summary>Marks the project as on the plan.</summary>
        public void Visit(ModSource source, string? projectId, string? slug)
        {
            foreach (var key in Keys(source, projectId, slug))
            {
                _seen.Add(key);
            }
        }

        private static IEnumerable<string> Keys(ModSource source, string? projectId, string? slug)
        {
            if (!string.IsNullOrEmpty(projectId))
            {
                yield return $"{source}:id:{projectId}";
            }

            if (!string.IsNullOrEmpty(slug))
            {
                yield return $"{source}:slug:{slug}";
            }
        }
    }
}
