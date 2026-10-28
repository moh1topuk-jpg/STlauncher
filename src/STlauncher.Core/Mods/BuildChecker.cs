using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Mods;

public enum BuildIssueKind
{
    /// <summary>A mod requires another mod that is not in the build, or is switched off.</summary>
    MissingDependency,

    /// <summary>Two enabled jars declare the same mod id; the loader refuses to start.</summary>
    DuplicateMod,

    /// <summary>A jar built for another loader: a Forge mod in a Fabric build. The loader skips it.</summary>
    WrongLoader,

    /// <summary>The mod declares game versions this build is not.</summary>
    WrongGameVersion,

    /// <summary>
    /// A mod says it does not run with the version of another mod that is in the build
    /// (Fabric and Quilt "breaks", Forge and NeoForge "incompatible"). The loader stops the game.
    /// </summary>
    Incompatible,

    /// <summary>The same, said softly (Fabric "conflicts", NeoForge "discouraged"): the loader only warns.</summary>
    Discouraged,

    /// <summary>
    /// Two mods that both replace the game's renderer (Sodium, Embeddium, Rubidium) are
    /// switched on. The launcher's own knowledge, not something the jars declare.
    /// </summary>
    TwoRenderers,

    /// <summary>
    /// A required mod - or the loader itself - is in the build, but in a version outside
    /// the range the mod declares. The loader refuses to start.
    /// </summary>
    WrongDependencyVersion
}

/// <summary>How sure the launcher is, and what it says.</summary>
public enum BuildIssueSeverity
{
    /// <summary>
    /// "The game will not start": the loader itself refuses, by the jars' own declarations
    /// read the way the loader reads them. Nothing the launcher guesses lands here.
    /// </summary>
    WontStart,

    /// <summary>
    /// "May work incorrectly": the loader starts the game, and there is a real reason to
    /// look - a mod's own soft warning, a jar it will skip, two renderers at once.
    /// </summary>
    MayMisbehave
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
    /// <summary>For a conflict or a version requirement: the versions named; null for all of them.</summary>
    public string? Range { get; init; }

    /// <summary>For a conflict or a version requirement: the version of the other mod that is in the build.</summary>
    public string? OtherVersion { get; init; }

    /// <summary>For a conflict: the jar the other mod came in.</summary>
    public string? OtherFileName { get; init; }

    public BuildIssueSeverity Severity { get; init; } = BuildIssueSeverity.WontStart;

    /// <summary>True only for what the loader really refuses to start with.</summary>
    public bool IsBlocking => Severity == BuildIssueSeverity.WontStart;

    /// <summary>
    /// True when flipping one file's switch is the obvious fix, safe to do for the player
    /// in bulk ("fix everything", the repair). Only for what stops the game: a soft
    /// warning is never acted on by itself. A conflict is not either: which of the two
    /// mods goes, or whether one of them is updated instead, is the player's call.
    /// </summary>
    public bool FixableBySwitch => IsBlocking && Kind switch
    {
        BuildIssueKind.MissingDependency => DisabledFileName is not null,
        BuildIssueKind.Incompatible or BuildIssueKind.Discouraged or BuildIssueKind.TwoRenderers or BuildIssueKind.WrongDependencyVersion => false,
        _ => true
    };
}

/// <summary>One jar of a build with every loader section it carries.</summary>
public sealed record BuildJar(string FileName, bool Enabled, IReadOnlyList<ModMetadata> Sections);

/// <summary>
/// Reads every jar in mods/ and reports what would stop the game before it is started,
/// from the jars' own declarations read the way the loader reads them. Two levels:
/// "will not start" only for what the loader itself refuses (a missing or wrong-version
/// required mod, a "breaks", the same mod twice, a game version outside the mod's
/// required range); "may work incorrectly" for the loader's own soft warnings and the
/// few things the launcher knows for sure. Nothing here touches the network.
/// </summary>
/// <remarks>
/// The inventory, by loader:
/// Fabric - depends: missing or outside range stops the game (minecraft, fabricloader
/// included); breaks: stops it; recommends / suggests / conflicts: a line in the log.
/// A mod with "environment": "server" is not loaded on a client at all.
/// Quilt - depends without "optional": as Fabric's; breaks: stops it.
/// Forge / NeoForge - a mandatory / "required" dependency missing stops the game; a
/// dependency that is present, optional or not, outside its range stops it;
/// "incompatible": stops it; "discouraged": a warning screen with "proceed".
/// A bare Maven version ("1.20.1") is a recommendation, any version satisfies it.
/// </remarks>
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
        var dialect = loader is LoaderKind.Forge or LoaderKind.NeoForge ? RangeDialect.Maven : RangeDialect.Fabric;

        // One record per jar: the section this loader reads. A jar built for several
        // loaders states its requirements once for each, and only the running loader's
        // list is the one that has to be met.
        var active = new List<ModMetadata>();

        foreach (var jar in jars.Where(j => j.Enabled && j.Sections.Count > 0))
        {
            if (ModMetadataReader.SectionFor(jar.Sections, loader, gameVersion) is { } section)
            {
                // A server-only mod is skipped by the loader on a client: nothing it asks for counts.
                if (!section.ServerOnly)
                {
                    active.Add(section);
                }
            }
            else
            {
                // The loader does not read this jar at all: the mod will simply not be there.
                var first = jar.Sections[0];
                issues.Add(new BuildIssue(BuildIssueKind.WrongLoader, first.Name, first.FileName, first.Loader.ToString())
                {
                    Severity = BuildIssueSeverity.MayMisbehave
                });
            }
        }

        // Which ids the enabled jars provide, on this loader, and in which version: a jar's
        // own id, its "provides" aliases and every mod nested in it (jar-in-jar).
        // Several jars can bring the same id (Voxy carries its own Sodium inside). Which one
        // the loader picks is its business; a verdict is given only when every candidate
        // leads to the same answer.
        var provided = new Dictionary<string, List<Provider>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in active)
        {
            Offer(provided, mod.Id, new Provider(mod.Name, mod.Version, mod.FileName, Root: true));

            foreach (var extra in mod.Provides)
            {
                var version = mod.Bundled.FirstOrDefault(b => b.Id.Equals(extra, StringComparison.OrdinalIgnoreCase))?.Version;
                Offer(provided, extra, new Provider(extra, version, mod.FileName, Root: false));
            }
        }

        var disabledIds = jars
            .Where(j => !j.Enabled)
            .Select(j => ModMetadataReader.SectionFor(j.Sections, loader, gameVersion))
            .Where(m => m is not null)
            .SelectMany(m => m!.Provides.Append(m.Id).Select(id => (id, m.FileName)))
            .GroupBy(p => p.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FileName, StringComparer.OrdinalIgnoreCase);

        // Duplicates: the same declared id from two files in mods/. Every loader refuses.
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
            CheckDependencies(mod, loader, dialect, gameVersion, loaderVersion, provided, disabledIds, issues);
            CheckConflicts(mod, dialect, provided, issues);
        }

        AddTwoRenderers(issues, jars, loader, gameVersion);

        return issues
            .GroupBy(i => (i.Kind, i.FileName, i.Detail))
            .Select(g => g.OrderBy(i => i.Severity).First())
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CheckDependencies(
        ModMetadata mod,
        LoaderKind loader,
        RangeDialect dialect,
        string? gameVersion,
        string? loaderVersion,
        Dictionary<string, List<Provider>> provided,
        Dictionary<string, string> disabledIds,
        List<BuildIssue> issues)
    {
        // Forge and NeoForge hold a present mod to its range whether the dependency is
        // mandatory or optional; Fabric and Quilt only look at "depends".
        var rangesEnforcedWhenPresent = dialect == RangeDialect.Maven;

        foreach (var dependency in mod.Dependencies)
        {
            var id = dependency.Id;
            var enforced = dependency.Required || rangesEnforcedWhenPresent;

            // The game itself.
            if (id.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(gameVersion) || string.IsNullOrWhiteSpace(dependency.VersionRange) ||
                    !VersionRange.DefinitelyOutside(dependency.VersionRange, gameVersion, dialect))
                {
                    continue;
                }

                // "recommends minecraft" outside the range is the mod's own warning, not a refusal.
                issues.Add(new BuildIssue(BuildIssueKind.WrongGameVersion, mod.Name, mod.FileName, dependency.VersionRange!.Trim())
                {
                    Severity = enforced ? BuildIssueSeverity.WontStart : BuildIssueSeverity.MayMisbehave
                });
                continue;
            }

            // The loader itself: its version is known, so its range can be judged.
            if (LoaderIdFor(loader) is { } loaderId && id.Equals(loaderId, StringComparison.OrdinalIgnoreCase))
            {
                if (enforced && LoaderVersionCore(loaderVersion) is { } running &&
                    VersionRange.DefinitelyOutside(dependency.VersionRange, running, dialect))
                {
                    issues.Add(new BuildIssue(BuildIssueKind.WrongDependencyVersion, mod.Name, mod.FileName, LoaderName(loader))
                    {
                        Range = dependency.VersionRange?.Trim(),
                        OtherVersion = running
                    });
                }

                continue;
            }

            if (ModMetadataReader.IsLoaderProvided(id, loader, loaderVersion))
            {
                continue;
            }

            if (!provided.TryGetValue(id, out var candidates))
            {
                // Fabric API modules are "fabric-<name>-v<n>"; the umbrella jar counts even when
                // its nested list could not be read.
                if (id.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase) && provided.ContainsKey("fabric-api"))
                {
                    continue;
                }

                if (dependency.Required)
                {
                    disabledIds.TryGetValue(id, out var disabledFile);
                    issues.Add(new BuildIssue(BuildIssueKind.MissingDependency, mod.Name, mod.FileName, id, disabledFile));
                }

                continue;
            }

            var others = candidates.Where(c => !string.Equals(c.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (enforced && others.Count > 0 && others.Count == candidates.Count &&
                others.All(c => VersionRange.DefinitelyOutside(dependency.VersionRange, c.Version, dialect)))
            {
                var present = Primary(others);

                issues.Add(new BuildIssue(BuildIssueKind.WrongDependencyVersion, mod.Name, mod.FileName, present.Name)
                {
                    Range = dependency.VersionRange?.Trim(),
                    OtherVersion = present.Version,
                    OtherFileName = present.FileName
                });
            }
        }
    }

    /// <summary>Mods it says it does not run with - but only in the versions it names, and only when that is certain.</summary>
    private static void CheckConflicts(ModMetadata mod, RangeDialect dialect, Dictionary<string, List<Provider>> provided, List<BuildIssue> issues)
    {
        foreach (var conflict in mod.Conflicts)
        {
            if (ModMetadataReader.BuiltIn.Contains(conflict.Id) || !provided.TryGetValue(conflict.Id, out var candidates))
            {
                continue;
            }

            var others = candidates.Where(c => !string.Equals(c.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (others.Count == 0 || others.Count != candidates.Count ||
                !others.All(c => VersionRange.DefinitelyInside(conflict.VersionRange, c.Version, dialect)))
            {
                continue;
            }

            var other = Primary(others);

            issues.Add(new BuildIssue(
                conflict.Breaks ? BuildIssueKind.Incompatible : BuildIssueKind.Discouraged,
                mod.Name,
                mod.FileName,
                other.Name)
            {
                Range = string.IsNullOrWhiteSpace(conflict.VersionRange) || conflict.VersionRange.Trim() == "*" ? null : conflict.VersionRange.Trim(),
                OtherVersion = other.Version,
                OtherFileName = other.FileName,
                Severity = conflict.Breaks ? BuildIssueSeverity.WontStart : BuildIssueSeverity.MayMisbehave
            });
        }
    }

    /// <summary>The id a mod names the running loader by.</summary>
    private static string? LoaderIdFor(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabricloader",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",

        // Quilt answers to "fabricloader" in a version of its own that the build does not record.
        _ => null
    };

    private static string LoaderName(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric Loader",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => loader.ToString()
    };

    /// <summary>"1.20.1-47.2.0" is Forge 47.2.0; a plain number is taken as it is.</summary>
    private static string? LoaderVersionCore(string? loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            return null;
        }

        var text = loaderVersion.Trim();

        // "1.20.1-47.2.0": a game version in front. "0.16.0-beta.1" keeps its tag.
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^1\.\d+(?:\.\d+)?-(?<loader>\d+\.\d+.*)$");
        return match.Success ? match.Groups["loader"].Value : text;
    }

    /// <summary>
    /// Two renderers switched on at once, said as that. What a renderer is, is asked of
    /// the same recogniser the "Ускорение" switch fills its slot by. Two files of one and
    /// the same mod are left to the duplicate check; a conflict the two mods declare
    /// against each other is dropped, because this line already says it, and plainer.
    /// This is the launcher's own knowledge, so it is never more than a warning.
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
                OtherFileName = first.FileName,
                Severity = BuildIssueSeverity.MayMisbehave
            });
        }

        if (!found)
        {
            return;
        }

        // A "breaks" between the two still stops the game: that line stays, and the
        // softer renderer line beside it is enough said about the pair.
        var files = renderers.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        issues.RemoveAll(i => i.Kind is BuildIssueKind.Discouraged &&
                              files.Contains(i.FileName) &&
                              i.OtherFileName is not null && files.Contains(i.OtherFileName));

        var declared = issues
            .Where(i => i.Kind == BuildIssueKind.Incompatible && files.Contains(i.FileName) && i.OtherFileName is not null && files.Contains(i.OtherFileName))
            .SelectMany(i => new[] { i.FileName, i.OtherFileName! })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        issues.RemoveAll(i => i.Kind == BuildIssueKind.TwoRenderers &&
                              declared.Contains(i.FileName) && i.OtherFileName is not null && declared.Contains(i.OtherFileName));
    }

    /// <param name="Root">True for a jar of its own in mods/, false for an alias or a jar nested in another.</param>
    private sealed record Provider(string Name, string? Version, string FileName, bool Root);

    private static void Offer(Dictionary<string, List<Provider>> provided, string id, Provider candidate)
    {
        if (!provided.TryGetValue(id, out var list))
        {
            provided[id] = list = new List<Provider>();
        }

        list.Add(candidate);
    }

    /// <summary>The candidate to name in a line: a jar of its own before a nested copy, then the newest.</summary>
    private static Provider Primary(IReadOnlyList<Provider> candidates)
        => candidates
            .OrderByDescending(c => c.Root)
            .ThenByDescending(c => c.Version ?? string.Empty, Comparer<string>.Create(VersionRange.CompareVersions))
            .First();
}
