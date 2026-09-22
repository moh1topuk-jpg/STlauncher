using System;
using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Content;

/// <summary>A before/after of the catalog-owned parts of a build.</summary>
public sealed record BuildSnapshot(string? VersionId, LoaderKind Loader, string? LoaderVersion, IReadOnlyList<string> Items)
{
    public static BuildSnapshot Of(Instance instance)
        => new(instance.VersionId, instance.Loader, instance.LoaderVersion, instance.EnabledCatalogItems.ToList());
}

/// <summary>What changed, as names rather than ids, ready to be worded for the player.</summary>
/// <param name="Added">Display names of items that are new in the build.</param>
/// <param name="Removed">Display names of items that left it.</param>
public sealed record BuildChangeNotice(
    string? OldVersion,
    string? NewVersion,
    string? OldLoader,
    string? NewLoader,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    public bool VersionChanged => OldVersion is not null && NewVersion is not null && OldVersion != NewVersion;

    public bool LoaderChanged => OldLoader is not null && NewLoader is not null && OldLoader != NewLoader;

    public bool Any => VersionChanged || LoaderChanged || Added.Count > 0 || Removed.Count > 0;

    /// <summary>
    /// The difference between two snapshots. The server owner edits the catalog; the
    /// player sees "+ Sodium, Lithium; − Xyz; 1.21.4 → 1.21.5" instead of silently
    /// receiving files.
    /// </summary>
    public static BuildChangeNotice Between(BuildSnapshot before, BuildSnapshot after, Func<string, string> displayName)
    {
        if (before is null)
        {
            throw new ArgumentNullException(nameof(before));
        }

        if (after is null)
        {
            throw new ArgumentNullException(nameof(after));
        }

        var oldItems = before.Items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newItems = after.Items.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = after.Items.Where(i => !oldItems.Contains(i)).Select(displayName).ToList();
        var removed = before.Items.Where(i => !newItems.Contains(i)).Select(displayName).ToList();

        return new BuildChangeNotice(
            before.VersionId,
            after.VersionId,
            LoaderLabel(before.Loader, before.LoaderVersion),
            LoaderLabel(after.Loader, after.LoaderVersion),
            added,
            removed);
    }

    private static string LoaderLabel(LoaderKind loader, string? version)
        => string.IsNullOrWhiteSpace(version) ? loader.ToString() : $"{loader} {version}";
}
