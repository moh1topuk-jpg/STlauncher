using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Boost;

/// <summary>
/// One place in a build an accelerator mod takes. A place, not a mod, because for the
/// renderer there are three mods that do the same job and two of them together crash
/// the game: the place is either empty or it is not.
/// </summary>
/// <param name="Slugs">Modrinth projects that can fill it, the preferred one first.</param>
/// <param name="Renderer">The renderer's place: filled by any of Sodium, Embeddium or Rubidium.</param>
/// <param name="SkippedWithOptiFine">Left empty in a build with OptiFine, which does that work itself and breaks with the mod.</param>
public sealed record BoostSlot(string Title, IReadOnlyList<string> Slugs, bool Renderer = false, bool SkippedWithOptiFine = false);

public enum BoostModState
{
    /// <summary>Will be downloaded, with what it requires.</summary>
    Install,

    /// <summary>The switch brought it once and switched it off; it is switched back on.</summary>
    Reenable,

    /// <summary>The build has it already, in whatever form. Nothing comes.</summary>
    Present,

    /// <summary>The build has OptiFine; this mod does not go with it.</summary>
    SkippedForOptiFine,

    /// <summary>No file for this game version and loader, or one that cannot be installed whole.</summary>
    Unavailable,

    /// <summary>Marked incompatible with a mod that is in the build.</summary>
    Conflict
}

/// <summary>What the switch decided for one slot.</summary>
public sealed record BoostModItem(BoostSlot Slot, BoostModState State)
{
    public string Title { get; init; } = Slot.Title;

    /// <summary>For <see cref="BoostModState.Install"/>: the plan the installer carries out as it is.</summary>
    public ModInstallPlan? Plan { get; init; }

    /// <summary>For present, re-enabled and conflicting mods: the file in the build.</summary>
    public string? FileName { get; init; }

    /// <summary>Why not, for the states that are not an install.</summary>
    public string? Detail { get; init; }
}

/// <summary>Everything the switch would do to the build's mods, decided before anything is touched.</summary>
public sealed class BoostModPlan
{
    public BoostModPlan(IReadOnlyList<BoostModItem> items, IReadOnlyList<BoostJarRecord> reenable, bool hasOptiFine)
    {
        Items = items;
        Reenable = reenable;
        HasOptiFine = hasOptiFine;
    }

    public IReadOnlyList<BoostModItem> Items { get; }

    /// <summary>Every parked jar that is switched back on: the mods of the slots and what they required.</summary>
    public IReadOnlyList<BoostJarRecord> Reenable { get; }

    public bool HasOptiFine { get; }

    /// <summary>
    /// The files that come, each once: two mods that both require Fabric API bring one
    /// Fabric API. Dependencies stand before the mod that needs them.
    /// </summary>
    public IReadOnlyList<ModPlanItem> Downloads
        => Items.Where(i => i.Plan is not null)
            .SelectMany(i => i.Plan!.Downloads)
            .GroupBy(d => d.ProjectId.Length > 0 ? d.ProjectId : d.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    public long TotalBytes => Downloads.Sum(d => d.DownloadSize);

    public bool ChangesAnything => Reenable.Count > 0 || Items.Any(i => i.State == BoostModState.Install);
}

/// <summary>
/// Which accelerator mods a build takes, and whether it has them already. The list is
/// fixed here; that each of them exists for this game version and loader is asked of
/// Modrinth every time, never assumed.
/// </summary>
public static class BoostMods
{
    /// <summary>Mods that do the renderer's job. Any one fills the slot; two crash the game.</summary>
    public static readonly IReadOnlyList<string> RendererFamily = new[] { "sodium", "embeddium", "rubidium" };

    private static readonly string[] OptiFineFamily = { "optifine", "optifabric", "preview-optifine" };

    private static readonly Regex ReleaseVersion = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    // What follows a mod's name in a file name: its version or the loader it is for.
    // "sodium-extra" is another mod; "sodium-fabric-0.5.8" is Sodium.
    private static readonly HashSet<string> NameTails = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt", "mc", "jar", "universal", "all", "hd"
    };

    /// <summary>The slots of a build, in the order they are filled. Empty when the switch brings no mods to such a build.</summary>
    public static IReadOnlyList<BoostSlot> SlotsFor(LoaderKind loader, string? gameVersion)
    {
        var ferrite = new BoostSlot("FerriteCore", new[] { "ferrite-core" });
        var culling = new BoostSlot("Entity Culling", new[] { "entityculling" });
        var immediate = new BoostSlot("ImmediatelyFast", new[] { "immediatelyfast" }, SkippedWithOptiFine: true);
        var modernFix = new BoostSlot("ModernFix", new[] { "modernfix" });
        var dynamicFps = new BoostSlot("Dynamic FPS", new[] { "dynamic-fps" });

        switch (loader)
        {
            case LoaderKind.Fabric:
            case LoaderKind.Quilt:
                return new[]
                {
                    new BoostSlot("Sodium", new[] { "sodium" }, Renderer: true, SkippedWithOptiFine: true),
                    new BoostSlot("Lithium", new[] { "lithium" }),
                    ferrite, culling, immediate, modernFix, dynamicFps,
                    new BoostSlot("More Culling", new[] { "moreculling" }, SkippedWithOptiFine: true)
                };

            case LoaderKind.Forge:
                // Older Forge has none of these mods; a version that is not a plain
                // release number is not something to promise them for either.
                if (gameVersion is null || !ReleaseVersion.IsMatch(gameVersion) || VersionRange.CompareVersions(gameVersion, "1.16") < 0)
                {
                    return Array.Empty<BoostSlot>();
                }

                return new[]
                {
                    new BoostSlot("Embeddium", new[] { "embeddium" }, Renderer: true, SkippedWithOptiFine: true),
                    ferrite, culling, immediate, modernFix, dynamicFps
                };

            case LoaderKind.NeoForge:
                return new[]
                {
                    new BoostSlot("Sodium", new[] { "sodium", "embeddium" }, Renderer: true, SkippedWithOptiFine: true),
                    ferrite, culling, immediate, modernFix, dynamicFps
                };

            default:
                return Array.Empty<BoostSlot>();
        }
    }

    /// <summary>
    /// The file that makes a mod present in the build, or null. Three ways to be there:
    /// the launcher recorded installing the project, a jar declares the mod's id, or a
    /// jar is simply named after it - the last one for the jar that says nothing about
    /// itself, which OptiFine's is.
    /// </summary>
    public static string? FindPresent(InstalledBuild build, IEnumerable<string> fileNames, string slug, string? projectId = null)
        => build.Find(ModSource.Modrinth, projectId, slug) ?? fileNames.FirstOrDefault(f => IsNamedAfter(f, slug));

    public static bool HasOptiFine(InstalledBuild build, IReadOnlyCollection<string> fileNames)
        => OptiFineFamily.Any(slug => FindPresent(build, fileNames, slug) is not null);

    /// <summary>True when the file name starts with the mod's name and goes on with a version or a loader.</summary>
    public static bool IsNamedAfter(string fileName, string slug)
    {
        var wanted = new string(slug.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var tokens = Regex.Split(fileName.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0).ToArray();
        var name = string.Empty;

        for (var i = 0; i < tokens.Length && wanted.Length > 0; i++)
        {
            name += tokens[i];

            if (name == wanted)
            {
                var tail = i + 1 < tokens.Length ? tokens[i + 1] : "jar";

                return char.IsDigit(tail[0]) ||
                       NameTails.Contains(tail) ||
                       Regex.IsMatch(tail, @"^(v|mc|r|b)\d");
            }

            if (!wanted.StartsWith(name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Release first, else beta, never alpha; within a channel the newest, which is the order the source gives.</summary>
    public static ModVersion? Pick(IEnumerable<ModVersion> versions)
    {
        var list = versions.ToList();

        return list.FirstOrDefault(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(v => string.Equals(v.VersionType, "beta", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Decides every slot of the build. Reads nothing from disk and installs nothing.
    /// </summary>
    /// <param name="jars">Every jar in mods/, the switched-off ones too.</param>
    /// <param name="parked">Jars the switch itself switched off earlier and that are still there unchanged.</param>
    public static async Task<BoostModPlan> PlanAsync(
        IModSource modrinth,
        ModInstallResolver resolver,
        IReadOnlyList<InstalledModRecord> records,
        IReadOnlyList<BuildJar> jars,
        IReadOnlyList<BoostJarRecord> parked,
        LoaderKind loader,
        string? gameVersion,
        CancellationToken cancellationToken = default)
    {
        var parkedNames = parked.Select(p => p.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsParked(string fileName) => parkedNames.Contains(Bare(fileName));

        // The build as it is without what the switch parked: a parked Sodium must not
        // read as "Sodium is here" - it is ours to switch back on.
        var outsideJars = jars.Where(j => !IsParked(j.FileName)).ToList();
        var outsideNames = outsideJars.Select(j => j.FileName).ToList();
        var outside = new InstalledBuild(records.Where(r => !IsParked(r.FileName)), outsideJars, loader, gameVersion);

        // The resolver sees everything: a parked Fabric API comes back on, so a mod that
        // requires it has it.
        var whole = new InstalledBuild(records, jars, loader, gameVersion);

        var optiFine = HasOptiFine(outside, outsideNames);
        var items = new List<BoostModItem>();
        var reenable = new List<BoostJarRecord>();

        foreach (var slot in SlotsFor(loader, gameVersion))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (optiFine && slot.SkippedWithOptiFine)
            {
                items.Add(new BoostModItem(slot, BoostModState.SkippedForOptiFine));
                continue;
            }

            var family = slot.Renderer ? RendererFamily.Concat(slot.Slugs).Distinct().ToList() : slot.Slugs;
            var present = family.Select(slug => FindPresent(outside, outsideNames, slug)).FirstOrDefault(f => f is not null);

            if (present is not null)
            {
                items.Add(new BoostModItem(slot, BoostModState.Present) { FileName = present });
                continue;
            }

            if (parked.FirstOrDefault(p => !p.Dependency && family.Contains(p.Slug ?? string.Empty, StringComparer.OrdinalIgnoreCase)) is { } ours)
            {
                reenable.Add(ours);
                items.Add(new BoostModItem(slot, BoostModState.Reenable) { Title = ours.Title ?? slot.Title, FileName = ours.FileName });
                continue;
            }

            items.Add(await ResolveSlotAsync(modrinth, resolver, slot, outside, whole, loader, gameVersion, cancellationToken).ConfigureAwait(false));
        }

        // What the parked mods required comes back with them - unless the build has
        // gained the same mod since: two jars of one id stop the game.
        if (reenable.Count > 0)
        {
            var taken = outsideJars.Where(j => j.Enabled).SelectMany(j => Ids(j, loader, gameVersion)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var dependency in parked.Where(p => p.Dependency))
            {
                var jar = jars.FirstOrDefault(j => string.Equals(Bare(j.FileName), dependency.FileName, StringComparison.OrdinalIgnoreCase));

                if (jar is null || !Ids(jar, loader, gameVersion).Any(taken.Contains))
                {
                    reenable.Add(dependency);
                }
            }
        }

        return new BoostModPlan(items, reenable, optiFine);
    }

    private static async Task<BoostModItem> ResolveSlotAsync(
        IModSource modrinth,
        ModInstallResolver resolver,
        BoostSlot slot,
        InstalledBuild outside,
        InstalledBuild whole,
        LoaderKind loader,
        string? gameVersion,
        CancellationToken cancellationToken)
    {
        var outcome = new BoostModItem(slot, BoostModState.Unavailable);

        foreach (var slug in slot.Slugs)
        {
            try
            {
                var project = await modrinth.GetProjectAsync(slug, cancellationToken).ConfigureAwait(false);

                if (project is null)
                {
                    continue;
                }

                // Under the project's own id this time: a record may know it by that.
                if (outside.Find(project) is { } present)
                {
                    return new BoostModItem(slot, BoostModState.Present) { Title = project.Title, FileName = present };
                }

                var (version, versionLoader) = await PickVersionAsync(modrinth, project.Id, gameVersion, loader, cancellationToken).ConfigureAwait(false);

                if (version is null)
                {
                    continue;
                }

                var plan = await resolver
                    .ResolveAsync(new ModInstallRequest(version, project.Slug, project.Title, project.IconUrl, ProjectTypes.Mod, gameVersion, versionLoader), whole, cancellationToken)
                    .ConfigureAwait(false);

                if (plan.Root.File is { } file && whole.HasFile(file.FileName))
                {
                    return new BoostModItem(slot, BoostModState.Present) { Title = project.Title, FileName = file.FileName };
                }

                if (plan.Conflicts.FirstOrDefault(c => c.InstalledFile is not null) is { } conflict)
                {
                    outcome = new BoostModItem(slot, BoostModState.Conflict) { Title = project.Title, FileName = conflict.InstalledFile, Detail = conflict.With };
                    continue;
                }

                // Half a set of files is not installed: the missing half is what crashes.
                if (!plan.IsComplete || plan.Root.State != ModPlanState.Install || plan.Missing.Any() || plan.Blocked.Any())
                {
                    outcome = new BoostModItem(slot, BoostModState.Unavailable)
                    {
                        Title = project.Title,
                        Detail = plan.Failure ?? plan.Missing.Concat(plan.Blocked).FirstOrDefault()?.Title
                    };
                    continue;
                }

                return new BoostModItem(slot, BoostModState.Install) { Title = project.Title, Plan = plan };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = new BoostModItem(slot, BoostModState.Unavailable) { Detail = ex.Message };
            }
        }

        return outcome;
    }

    /// <summary>A Quilt build runs Fabric mods: when a mod has no file marked for Quilt, its Fabric one is taken.</summary>
    private static async Task<(ModVersion? Version, LoaderKind Loader)> PickVersionAsync(
        IModSource modrinth,
        string projectId,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken)
    {
        var pick = Pick(await modrinth.GetVersionsAsync(projectId, gameVersion, loader, cancellationToken).ConfigureAwait(false));

        if (pick is null && loader == LoaderKind.Quilt)
        {
            pick = Pick(await modrinth.GetVersionsAsync(projectId, gameVersion, LoaderKind.Fabric, cancellationToken).ConfigureAwait(false));
            return (pick, LoaderKind.Fabric);
        }

        return (pick, loader);
    }

    /// <summary>Every mod id a jar answers to under this loader.</summary>
    internal static IEnumerable<string> Ids(BuildJar jar, LoaderKind loader, string? gameVersion)
    {
        var section = ModMetadataReader.SectionFor(jar.Sections, loader, gameVersion) ?? jar.Sections.FirstOrDefault();

        return section is null ? Array.Empty<string>() : section.Provides.Append(section.Id);
    }

    internal static string Bare(string fileName)
        => fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? fileName[..^".disabled".Length] : fileName;
}
