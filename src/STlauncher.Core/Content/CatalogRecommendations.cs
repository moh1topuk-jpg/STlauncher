using System;
using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Content;

/// <summary>Where a pack the server recommends stands in a build.</summary>
public enum RecommendationState
{
    /// <summary>Not in the build.</summary>
    Missing,

    /// <summary>In the build, the version the catalog pins (or the pin could not be compared).</summary>
    Installed,

    /// <summary>In the build, another version than the catalog pins: offered, never swapped by itself.</summary>
    OtherVersion
}

/// <param name="FileName">The file in the build's folder that is this pack; null when missing.</param>
/// <param name="InstalledVersion">What the launcher recorded for that file, when it did.</param>
/// <param name="Enabled">For a resource pack: in the game's pack list. For a shader: the one the loader uses.</param>
public sealed record RecommendationStatus(
    RecommendationState State,
    string? FileName,
    string? InstalledVersion,
    bool Enabled)
{
    public static readonly RecommendationStatus Missing = new(RecommendationState.Missing, null, null, false);
}

/// <summary>The catalog's pinned version of a pack as Modrinth describes it, once looked up.</summary>
public sealed record PinnedPack(string VersionNumber, string FileName, long Size);

/// <summary>
/// Resource packs and shaders the server suggests for a catalog build ("Рекомендует
/// сервер"). They are a suggestion and nothing more: nothing here installs, and the sync
/// never sees them - they are not in the build's item list, and what the player installs
/// from the panel is recorded as a Modrinth file, which the sync does not touch.
/// </summary>
public static class CatalogRecommendations
{
    /// <summary>
    /// The recommended items of one kind, in the order the build lists them. Only resource
    /// packs and shader packs are taken: a mod named here would be a mod outside the build,
    /// which is not what this list is for.
    /// </summary>
    public static IReadOnlyList<CatalogItem> For(ContentCatalog? catalog, CatalogBuild? build, CatalogItemType type)
    {
        if (catalog is null || build is null || type is not (CatalogItemType.ResourcePack or CatalogItemType.ShaderPack))
        {
            return Array.Empty<CatalogItem>();
        }

        var result = new List<CatalogItem>();

        foreach (var id in build.Recommends.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (catalog.FindItem(id) is { } item &&
                item.Type == type &&
                item.Source.Kind == CatalogSourceKind.Modrinth &&
                !string.IsNullOrWhiteSpace(item.Source.Project) &&
                // A build cannot both require and merely suggest the same thing.
                !build.Items.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>The folder the kind lives in: resourcepacks or shaderpacks.</summary>
    public static string FolderFor(CatalogItemType type) => CatalogPlacement.FolderFor(type);

    /// <summary>The Modrinth project type for a kind, for the install plan.</summary>
    public static string ProjectTypeFor(CatalogItemType type)
        => type == CatalogItemType.ShaderPack ? ProjectTypes.Shader : ProjectTypes.ResourcePack;

    /// <summary>
    /// Works out where a recommended pack stands. The pinned file on disk settles it; then
    /// a record of the same project in the same folder (the player may have installed it
    /// from the browser, in another version); otherwise it is missing.
    /// </summary>
    /// <param name="filesInFolder">File names in the item's folder, as the pack lists show them.</param>
    /// <param name="pinned">The pinned version looked up on Modrinth; null while it is not known.</param>
    /// <param name="enabled">Pack file names the game uses: the enabled resource packs, or the active shader.</param>
    public static RecommendationStatus Evaluate(
        CatalogItem item,
        IEnumerable<InstalledModRecord> records,
        IEnumerable<string> filesInFolder,
        PinnedPack? pinned,
        IEnumerable<string> enabled)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var folder = FolderFor(item.Type);
        var files = new HashSet<string>(filesInFolder, StringComparer.OrdinalIgnoreCase);
        var on = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);

        if (pinned is not null && files.Contains(pinned.FileName))
        {
            var record = RecordsFor(item, records, folder)
                .FirstOrDefault(r => string.Equals(r.FileName, pinned.FileName, StringComparison.OrdinalIgnoreCase));

            return new RecommendationStatus(RecommendationState.Installed, pinned.FileName, record?.Version ?? pinned.VersionNumber, on.Contains(pinned.FileName));
        }

        foreach (var record in RecordsFor(item, records, folder))
        {
            if (!files.Contains(record.FileName))
            {
                // A record whose file is gone is a pack the player deleted.
                continue;
            }

            var current = IsPinnedVersion(record.Version, item.Source.Version, pinned?.VersionNumber);

            return new RecommendationStatus(
                current ? RecommendationState.Installed : RecommendationState.OtherVersion,
                record.FileName,
                record.Version,
                on.Contains(record.FileName));
        }

        return RecommendationStatus.Missing;
    }

    /// <summary>
    /// The record's version is the pin: the pin as written (a version number or id), or the
    /// number Modrinth gave for it. No version on either side is taken as current - the
    /// panel offers a different version only when it can say it is different.
    /// </summary>
    public static bool IsPinnedVersion(string? recorded, string? pin, string? pinnedNumber)
    {
        if (string.IsNullOrWhiteSpace(recorded) || string.IsNullOrWhiteSpace(pin))
        {
            return true;
        }

        if (string.Equals(recorded, pin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A pin by version id is compared through the number it resolves to; while that is
        // not known, the two cannot be told apart, and the pack is not called outdated.
        return pinnedNumber is null
            ? !LooksLikeVersionNumber(pin!)
            : string.Equals(recorded, pinnedNumber, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pinned version among a project's versions: by id first, then by number, narrowed
    /// to the game version when the same number exists for several releases.
    /// </summary>
    public static ModVersion? PickPinned(IEnumerable<ModVersion> versions, string? pin, string? gameVersion)
    {
        var list = versions.ToList();

        if (string.IsNullOrWhiteSpace(pin))
        {
            var forGame = list.Where(v => string.IsNullOrEmpty(gameVersion) || v.GameVersions.Contains(gameVersion!)).ToList();
            return ModrinthClient.SelectPreferred(forGame);
        }

        var byId = list.FirstOrDefault(v => string.Equals(v.Id, pin, StringComparison.Ordinal));

        if (byId is not null)
        {
            return byId;
        }

        var byNumber = list.Where(v => string.Equals(v.VersionNumber, pin, StringComparison.OrdinalIgnoreCase)).ToList();

        return byNumber.FirstOrDefault(v => string.IsNullOrEmpty(gameVersion) || v.GameVersions.Contains(gameVersion!))
               ?? byNumber.FirstOrDefault();
    }

    /// <summary>Records of this item's project in this folder.</summary>
    private static IEnumerable<InstalledModRecord> RecordsFor(CatalogItem item, IEnumerable<InstalledModRecord> records, string folder)
        => records.Where(r =>
            string.Equals(r.Folder ?? ModManager.ModsFolderName, folder, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.Id, item.Source.Project, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.Id, item.Id, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.ProjectId, item.Source.Project, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Modrinth version ids are eight letters and digits; numbers have dots, dashes or a "+".</summary>
    private static bool LooksLikeVersionNumber(string pin)
        => pin.Length != 8 || pin.Any(c => !char.IsLetterOrDigit(c));
}
