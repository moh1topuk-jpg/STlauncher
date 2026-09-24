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
    WrongGameVersion
}

/// <summary>What is wrong, which file, and what would fix it.</summary>
/// <param name="Subject">The mod's display name.</param>
/// <param name="FileName">The jar the issue is about.</param>
/// <param name="Detail">The missing id, the other duplicate's file, or the declared range.</param>
/// <param name="DisabledFileName">For a missing dependency: the file that would satisfy it if switched on.</param>
public sealed record BuildIssue(
    BuildIssueKind Kind,
    string Subject,
    string FileName,
    string? Detail,
    string? DisabledFileName = null)
{
    /// <summary>True for issues the game will not start with. Version mismatches are a warning.</summary>
    public bool IsBlocking => Kind != BuildIssueKind.WrongGameVersion;
}

/// <summary>
/// Reads every jar in mods/ and reports what would stop the game before it is started:
/// the same checks the crash analyser makes afterwards, only from the metadata instead
/// of the log. Nothing here touches the network.
/// </summary>
public static class BuildChecker
{
    public static IReadOnlyList<BuildIssue> Check(string gameDirectory, LoaderKind loader, string? gameVersion)
    {
        var modsDirectory = ModManager.ModsDirectory(gameDirectory);

        if (!Directory.Exists(modsDirectory) || loader == LoaderKind.Vanilla)
        {
            return Array.Empty<BuildIssue>();
        }

        var enabled = new List<ModMetadata>();
        var disabled = new List<ModMetadata>();
        var unknownEnabled = new List<string>();

        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!ModManager.IsModFile(path))
            {
                continue;
            }

            var isDisabled = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var metadata = ModMetadataReader.Read(path);

            if (metadata.Count == 0)
            {
                if (!isDisabled) unknownEnabled.Add(Path.GetFileName(path));
                continue;
            }

            (isDisabled ? disabled : enabled).AddRange(metadata);
        }

        var issues = new List<BuildIssue>();
        var family = LoaderFamily(loader);

        // Which ids the enabled jars provide, on this loader.
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledForLoader = enabled.Where(m => LoaderFamily(m.Loader) == family).ToList();

        foreach (var mod in enabledForLoader)
        {
            provided.Add(mod.Id);
            foreach (var extra in mod.Provides) provided.Add(extra);
        }

        var disabledIds = disabled.Where(m => LoaderFamily(m.Loader) == family)
            .SelectMany(m => m.Provides.Append(m.Id).Select(id => (id, m.FileName)))
            .GroupBy(p => p.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FileName, StringComparer.OrdinalIgnoreCase);

        // Wrong loader: a jar with sections only for the other family.
        var byFile = enabled.GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byFile)
        {
            if (group.All(m => LoaderFamily(m.Loader) != family))
            {
                var first = group.First();
                issues.Add(new BuildIssue(BuildIssueKind.WrongLoader, first.Name, first.FileName, first.Loader.ToString()));
            }
        }

        // Duplicates: same id from two different files.
        foreach (var group in enabledForLoader.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
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

        // Missing dependencies and game version.
        foreach (var mod in enabledForLoader)
        {
            foreach (var dependency in mod.Dependencies.Where(d => d.Required))
            {
                if (ModMetadataReader.BuiltIn.Contains(dependency.Id) || provided.Contains(dependency.Id))
                {
                    continue;
                }

                // Fabric API modules are "fabric-<name>-v<n>"; the umbrella jar counts even when
                // its nested list could not be read.
                if (dependency.Id.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase) && provided.Contains("fabric-api"))
                {
                    continue;
                }

                disabledIds.TryGetValue(dependency.Id, out var disabledFile);
                issues.Add(new BuildIssue(BuildIssueKind.MissingDependency, mod.Name, mod.FileName, dependency.Id, disabledFile));
            }

            if (!string.IsNullOrEmpty(gameVersion) && mod.MinecraftRange is { Length: > 0 } range &&
                !VersionRange.Satisfies(range, gameVersion))
            {
                issues.Add(new BuildIssue(BuildIssueKind.WrongGameVersion, mod.Name, mod.FileName, range));
            }
        }

        return issues
            .GroupBy(i => (i.Kind, i.FileName, i.Detail))
            .Select(g => g.First())
            .OrderBy(i => i.IsBlocking ? 0 : 1)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Fabric and Quilt load each other's mods; Forge and NeoForge do not, but share a family for the loader check.</summary>
    private static int LoaderFamily(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric or LoaderKind.Quilt => 1,
        LoaderKind.Forge => 2,
        LoaderKind.NeoForge => 3,
        _ => 0
    };
}
