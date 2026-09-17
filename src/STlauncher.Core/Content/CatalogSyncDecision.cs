using System;
using STlauncher.Core.Instances;

namespace STlauncher.Core.Content;

/// <summary>Why the launcher is about to touch a mod - or why it is leaving it alone.</summary>
public enum CatalogSyncAction
{
    /// <summary>Nothing to do: the right file is already there.</summary>
    Keep,

    /// <summary>Never installed, or the file is gone.</summary>
    Install,

    /// <summary>The catalog now pins a different version than the one on disk.</summary>
    Update,

    /// <summary>The player switched it off; their choice outranks the catalog.</summary>
    KeepDisabled
}

/// <summary>
/// Decides what a catalog item needs, given what is recorded and what is on disk.
/// </summary>
/// <remarks>
/// This used to be one line inside the view model: skip when the file exists *and the
/// item is not pinned*. Every item in a real catalog is pinned - that is the whole point
/// of pinning - so nothing was ever skipped, and every start re-resolved all of them
/// through the mod API. The files were not re-downloaded (the hash check caught that),
/// but the launcher still reported them as downloaded and undid any mod the player had
/// switched off.
/// </remarks>
public static class CatalogSyncDecision
{
    public static CatalogSyncAction Decide(CatalogItem item, InstalledModRecord? record, bool fileExists)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (record is null || !fileExists)
        {
            // A record with no file is a file the player deleted, or a failed install.
            return record?.DisabledByUser == true && !fileExists
                ? CatalogSyncAction.KeepDisabled
                : CatalogSyncAction.Install;
        }

        if (record.DisabledByUser)
        {
            return CatalogSyncAction.KeepDisabled;
        }

        var pinned = item.Source.Version;

        if (string.IsNullOrWhiteSpace(pinned))
        {
            // Unpinned: the build is reproducible precisely because it is not bumped on
            // its own. Whatever was installed first stays.
            return CatalogSyncAction.Keep;
        }

        return string.Equals(record.Version, pinned, StringComparison.OrdinalIgnoreCase)
            ? CatalogSyncAction.Keep
            : CatalogSyncAction.Update;
    }

    /// <summary>True when the action means the launcher has to fetch something.</summary>
    public static bool NeedsInstall(this CatalogSyncAction action)
        => action is CatalogSyncAction.Install or CatalogSyncAction.Update;
}
