using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Mods;

public enum BuildIssueKind
{
    /// <summary>A mod asks for another mod that is not in the build, or is switched off.</summary>
    MissingDependency,

    /// <summary>Two enabled jars declare the same mod id; the game refuses to start.</summary>
    DuplicateMod,

    /// <summary>A jar built for another loader: a Forge mod in a Fabric build.</summary>
    WrongLoader,

    /// <summary>The mod says it was made for other game versions. Often still works; sometimes not.</summary>
    WrongGameVersion,

    /// <summary>
    /// A mod says it does not run with the version of another mod that is in the build
    /// (Fabric "breaks", Forge "incompatible"). The loader stops the game.
    /// </summary>
    Incompatible,

    /// <summary>The same, said softly (Fabric "conflicts", NeoForge "discouraged"): the loader only warns.</summary>
    Discouraged,

    /// <summary>
    /// Two mods that both replace the game's renderer (Sodium, Embeddium, Rubidium) are
    /// switched on. They rarely declare each other, so the loader lets them through and
    /// the game crashes while it starts.
    /// </summary>
    TwoRenderers
}

/// <summary>What is wrong, which file, and what would fix it.</summary>
/// <param name="Subject">The mod's display name.</param>
/// <param name="FileName">The jar the issue is about.</param>
/// <param name="Detail">The missing id, the other duplicate's file, the declared range, or the other mod's name.</param>
/// <param name="DisabledFileName">For a missing dependency: the file that would satisfy it if switched on.</param>
public sealed record BuildIssue(
    BuildIssueKind Kind,
    string Subject,
    string FileName,
    string? Detail,
    string? DisabledFileName = null)
{
    /// <summary>For a conflict: the versions of the other mod this one does not run with; null for all of them.</summary>
    public string? Range { get; init; }

    /// <summary>For a conflict: the version of the other mod that is in the build.</summary>
    public string? OtherVersion { get; init; }

    /// <summary>For a conflict: the jar the other mod came in.</summary>
    public string? OtherFileName { get; init; }

    /// <summary>True for issues the game will not start with. Version mismatches and soft conflicts are a warning.</summary>
    public bool IsBlocking => Kind is not (BuildIssueKind.WrongGameVersion or BuildIssueKind.Discouraged);

    /// <summary>
    /// True when flipping one file's switch is the obvious fix, safe to do for the player
    /// in bulk. A conflict is not: which of the two mods goes, or whether one of them is
    /// updated instead, is the player's call.
    /// </summary>
    public bool FixableBySwitch => Kind switch
    {
        BuildIssueKind.MissingDependency => DisabledFileName is not null,
        BuildIssueKind.Incompatible or BuildIssueKind.Discouraged or BuildIssueKind.TwoRenderers => false,
        _ => true
    };
}

/// <summary>One jar of a build with every loader section it carries.</summary>
public sealed record BuildJar(string FileName, bool Enabled, IReadOnlyList<ModMetadata> Sections);

/// <summary>
/// Reads every jar in mods/ and reports what would stop the game before it is started:
/// the same checks the crash analyser makes afterwards, only from the metadata instead
/// of the log. Nothing here touches the network.
/// </summary>
public static class BuildChecker
{
    /// <summary>
    /// The jars of a build's mods folder with what each says about itself. With a cache,
    /// only the files that changed since the last look are opened.
    /// </summary>
    public static IReadOnlyList<BuildJar> ReadJars(string gameDirectory, ModMetadataCache? cache = null)
    {
        var modsDirectory = ModManager.ModsDirectory(gameDirectory);

        if (!Directory.Exists(modsDirectory))
        {
            return Array.Empty<BuildJar>();
        }

        var jars = new List<BuildJar>();

        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!ModManager.IsModFile(path))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            var sections = cache is null ? ModMetadataReader.Read(path) : cache.Read(path);

            jars.Add(new BuildJar(fileName, !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase), sections));
        }

        cache?.Save(jars.Select(j => j.FileName));

        return jars;
    }

    /// <param name="loaderVersion">The build's loader version when known: what the loader brings by itself depends on it.</param>
    /// <param name="cache">Answers from earlier checks of this build; see <see cref="ModMetadataCache"/>.</param>
    public static IReadOnlyList<BuildIssue> Check(
        string gameDirectory,
        LoaderKind loader,
        string? gameVersion,
        string? loaderVersion = null,
        ModMetadataCache? cache = null)
    {
        if (loader == LoaderKind.Vanilla)
        {
            return Array.Empty<BuildIssue>();
        }

        return Check(ReadJars(gameDirectory, cache), loader, gameVersion, loaderVersion);
    }

    public static IReadOnlyList<BuildIssue> Check(
        IReadOnlyList<BuildJar> jars,
        LoaderKind loader,
        string? gameVersion,
        string? loaderVersion = null)
    {
        if (jars.Count == 0 || loader == LoaderKind.Vanilla)
        {
            return Array.Empty<BuildIssue>();
        }

        var issues = new List<BuildIssue>();

        // One record per jar: the section this loader reads. A jar built for several
        // loaders states its requirements once for each, and only the running loader's
        // list is the one that has to be met.
        var active = new List<ModMetadata>();

        foreach (var jar in jars.Where(j => j.Enabled && j.Sections.Count > 0))
        {
            if (ModMetadataReader.SectionFor(jar.Sections, loader, gameVersion) is { } section)
            {
                active.Add(section);
            }
            else
            {
                var first = jar.Sections[0];
                issues.Add(new BuildIssue(BuildIssueKind.WrongLoader, first.Name, first.FileName, first.Loader.ToString()));
            }
        }

        // Which ids the enabled jars provide, on this loader, and in which version.
        var provided = new Dictionary<string, Provider>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in active)
        {
            Offer(provided, mod.Id, new Provider(mod.Name, mod.Version, mod.FileName));

            foreach (var extra in mod.Provides)
            {
                var version = mod.Bundled.FirstOrDefault(b => b.Id.Equals(extra, StringComparison.OrdinalIgnoreCase))?.Version;
                Offer(provided, extra, new Provider(extra, version, mod.FileName));
            }
        }

        var disabledIds = jars
            .Where(j => !j.Enabled)
            .Select(j => ModMetadataReader.SectionFor(j.Sections, loader, gameVersion))
            .Where(m => m is not null)
            .SelectMany(m => m!.Provides.Append(m.Id).Select(id => (id, m.FileName)))
            .GroupBy(p => p.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FileName, StringComparer.OrdinalIgnoreCase);

        // Duplicates: same id from two different files.
        foreach (var group in active.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var files = group.Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (files.Count < 2)
            {
                continue;
            }

            // Name the older one as the file to remove; versions compare by their numeric core.
            var ordered = group.OrderBy(m => m.Version, Comparer<string>.Create(VersionRange.CompareVersions)).ToList();
            var older = ordered.First();
            var newer = ordered.Last();
            issues.Add(new BuildIssue(BuildIssueKind.DuplicateMod, newer.Name, older.FileName, newer.FileName));
        }

        foreach (var mod in active)
        {
            // Missing dependencies.
            foreach (var dependency in mod.Dependencies.Where(d => d.Required))
            {
                if (ModMetadataReader.IsLoaderProvided(dependency.Id, loader, loaderVersion) || provided.ContainsKey(dependency.Id))
                {
                    continue;
                }

                // Fabric API modules are "fabric-<name>-v<n>"; the umbrella jar counts even when
                // its nested list could not be read.
                if (dependency.Id.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase) && provided.ContainsKey("fabric-api"))
                {
                    continue;
                }

                disabledIds.TryGetValue(dependency.Id, out var disabledFile);
                issues.Add(new BuildIssue(BuildIssueKind.MissingDependency, mod.Name, mod.FileName, dependency.Id, disabledFile));
            }

            // Mods it says it does not run with - but only in the versions it names.
            foreach (var conflict in mod.Conflicts)
            {
                if (ModMetadataReader.BuiltIn.Contains(conflict.Id) ||
                    !provided.TryGetValue(conflict.Id, out var other) ||
                    string.Equals(other.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase) ||
                    !VersionRange.DefinitelyMatches(conflict.VersionRange, other.Version))
                {
                    continue;
                }

                issues.Add(new BuildIssue(
                    conflict.Breaks ? BuildIssueKind.Incompatible : BuildIssueKind.Discouraged,
                    mod.Name,
                    mod.FileName,
                    other.Name)
                {
                    Range = string.IsNullOrWhiteSpace(conflict.VersionRange) || conflict.VersionRange.Trim() == "*" ? null : conflict.VersionRange.Trim(),
                    OtherVersion = other.Version,
                    OtherFileName = other.FileName
                });
            }

            if (!string.IsNullOrEmpty(gameVersion) && mod.MinecraftRange is { Length: > 0 } range &&
                !VersionRange.Satisfies(range, gameVersion))
            {
                issues.Add(new BuildIssue(BuildIssueKind.WrongGameVersion, mod.Name, mod.FileName, range));
            }
        }

        AddTwoRenderers(issues, jars, loader, gameVersion);

        return issues
            .GroupBy(i => (i.Kind, i.FileName, i.Detail))
            .Select(g => g.First())
            .OrderBy(i => i.IsBlocking ? 0 : 1)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Two renderers switched on at once, said as that. What a renderer is, is asked of
    /// the same recogniser the "Ускорение" switch fills its slot by. Two files of one and
    /// the same mod are left to the duplicate check; a conflict the two mods declare
    /// against each other is dropped, because this line already says it, and plainer.
    /// </summary>
    private static void AddTwoRenderers(List<BuildIssue> issues, IReadOnlyList<BuildJar> jars, LoaderKind loader, string? gameVersion)
    {
        var renderers = jars
            .Where(j => j.Enabled && Boost.BoostRenderer.IsRenderer(j, loader, gameVersion))
            .OrderBy(j => j.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (renderers.Count < 2)
        {
            return;
        }

        ModMetadata? Section(BuildJar jar) => ModMetadataReader.SectionFor(jar.Sections, loader, gameVersion) ?? jar.Sections.FirstOrDefault();

        var first = renderers[0];
        var firstSection = Section(first);
        var found = false;

        foreach (var other in renderers.Skip(1))
        {
            var section = Section(other);

            if (section is not null && firstSection is not null &&
                string.Equals(section.Id, firstSection.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found = true;

            issues.Add(new BuildIssue(BuildIssueKind.TwoRenderers, section?.Name ?? other.FileName, other.FileName, firstSection?.Name ?? first.FileName)
            {
                OtherFileName = first.FileName
            });
        }

        if (!found)
        {
            return;
        }

        var files = renderers.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        issues.RemoveAll(i => i.Kind is BuildIssueKind.Incompatible or BuildIssueKind.Discouraged &&
                              files.Contains(i.FileName) &&
                              i.OtherFileName is not null && files.Contains(i.OtherFileName));
    }

    private sealed record Provider(string Name, string? Version, string FileName);

    /// <summary>The same id from several jars: the loader runs the newest, so that is the version to judge by.</summary>
    private static void Offer(Dictionary<string, Provider> provided, string id, Provider candidate)
    {
        if (!provided.TryGetValue(id, out var known) ||
            (candidate.Version is not null && (known.Version is null || VersionRange.CompareVersions(candidate.Version, known.Version) > 0)))
        {
            provided[id] = candidate;
        }
    }
}
