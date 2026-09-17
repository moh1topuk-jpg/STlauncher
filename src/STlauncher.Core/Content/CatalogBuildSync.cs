using System;
using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Instances;

namespace STlauncher.Core.Content;

/// <summary>What changed when a catalog build was applied to an instance.</summary>
public sealed record CatalogBuildChanges(
    bool VersionChanged,
    bool LoaderChanged,
    bool ItemsChanged,
    bool ServerChanged)
{
    public static readonly CatalogBuildChanges None = new(false, false, false, false);

    public bool Any => VersionChanged || LoaderChanged || ItemsChanged || ServerChanged;
}

/// <summary>
/// Keeps the instance created from a catalog build in step with the catalog.
/// </summary>
/// <remarks>
/// The catalog file in the repository is the source of truth for what the build *is*:
/// game version, loader and the list of mods. It is deliberately not the source of truth
/// for how the player runs it - memory, resolution and extra arguments are applied once,
/// when the instance is created, and never overwritten afterwards. Re-applying them on
/// every start would silently undo the memory slider on every launcher restart.
/// </remarks>
public static class CatalogBuildSync
{
    /// <summary>
    /// The build the catalog offers by name, falling back to the first entry so a catalog
    /// written before the flag existed keeps working.
    /// </summary>
    public static CatalogBuild? Recommended(ContentCatalog? catalog)
        => catalog?.Builds.FirstOrDefault(b => b.Recommended) ?? catalog?.Builds.FirstOrDefault();

    /// <summary>
    /// Finds the instance that belongs to a build. Instances created before the link
    /// existed are adopted by name, so upgrading the launcher does not leave a duplicate
    /// of the recommended build next to the original.
    /// </summary>
    public static Instance? FindLinked(IEnumerable<Instance> instances, CatalogBuild build)
    {
        if (build is null)
        {
            throw new ArgumentNullException(nameof(build));
        }

        var all = instances as IReadOnlyList<Instance> ?? instances.ToList();

        var linked = all.FirstOrDefault(i =>
            !string.IsNullOrWhiteSpace(i.CatalogBuildId) &&
            string.Equals(i.CatalogBuildId, build.Id, StringComparison.OrdinalIgnoreCase));

        if (linked is not null)
        {
            return linked;
        }

        // Only an instance that is not already claimed by another build can be adopted.
        var unclaimed = all.Where(i => string.IsNullOrWhiteSpace(i.CatalogBuildId)).ToList();

        return unclaimed.FirstOrDefault(i =>
                   string.Equals(i.Name, build.Name, StringComparison.OrdinalIgnoreCase))
               ?? unclaimed.FirstOrDefault(i => IsSameBuild(i, build));
    }

    /// <summary>
    /// The instance is this build under a different name: same game version, same loader
    /// and exactly the same catalog items. Players rename their builds, so matching on
    /// the name alone would hand them a duplicate of what they already have.
    /// </summary>
    private static bool IsSameBuild(Instance instance, CatalogBuild build)
    {
        if (instance.EnabledCatalogItems.Count == 0 || build.Items.Count == 0)
        {
            return false;
        }

        if (instance.Loader != build.Loader)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(build.GameVersion) &&
            !string.Equals(instance.VersionId, build.GameVersion, StringComparison.Ordinal))
        {
            return false;
        }

        return instance.EnabledCatalogItems
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(build.Items);
    }

    /// <summary>
    /// Applies everything the catalog owns. <paramref name="isNew"/> additionally applies
    /// the one-off settings (memory) that a later sync must leave alone.
    /// </summary>
    public static CatalogBuildChanges Apply(Instance instance, CatalogBuild build, bool isNew = false)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        if (build is null)
        {
            throw new ArgumentNullException(nameof(build));
        }

        var versionChanged = false;
        var loaderChanged = false;
        var serverChanged = false;

        instance.CatalogBuildId = build.Id;

        if (!string.IsNullOrWhiteSpace(build.GameVersion) &&
            !string.Equals(instance.VersionId, build.GameVersion, StringComparison.Ordinal))
        {
            instance.VersionId = build.GameVersion;
            versionChanged = true;
        }

        if (instance.Loader != build.Loader)
        {
            instance.Loader = build.Loader;
            loaderChanged = true;
        }

        if (!string.IsNullOrWhiteSpace(build.LoaderVersion) &&
            !string.Equals(instance.LoaderVersion, build.LoaderVersion, StringComparison.Ordinal))
        {
            instance.LoaderVersion = build.LoaderVersion;
            loaderChanged = true;
        }

        if (!string.IsNullOrWhiteSpace(build.ServerName) &&
            !string.Equals(instance.ServerName, build.ServerName, StringComparison.Ordinal))
        {
            instance.ServerName = build.ServerName;
            serverChanged = true;
        }

        if (!string.IsNullOrWhiteSpace(build.ServerAddress) &&
            !string.Equals(instance.ServerAddress, build.ServerAddress, StringComparison.OrdinalIgnoreCase))
        {
            instance.ServerAddress = build.ServerAddress;
            serverChanged = true;
        }

        var itemsChanged = !instance.EnabledCatalogItems.SequenceEqual(build.Items, StringComparer.OrdinalIgnoreCase);

        if (itemsChanged)
        {
            instance.EnabledCatalogItems = build.Items.ToList();
        }

        if (isNew && build.MemoryMb is > 0)
        {
            instance.MaxMemoryMb = build.MemoryMb.Value;
        }

        return new CatalogBuildChanges(versionChanged, loaderChanged, itemsChanged, serverChanged);
    }
}
