using System;

namespace STlauncher.Core.Content;

public static class CatalogPlacement
{
    public const string ModsFolder = "mods";
    public const string ResourcePacksFolder = "resourcepacks";
    public const string ShaderPacksFolder = "shaderpacks";
    public const string ConfigFolder = "config";

    /// <summary>Default folder for an item kind. Empty means "cannot be placed by default".</summary>
    public static string FolderFor(CatalogItemType type) => type switch
    {
        CatalogItemType.Mod => ModsFolder,
        CatalogItemType.ResourcePack => ResourcePacksFolder,
        CatalogItemType.ShaderPack => ShaderPacksFolder,
        CatalogItemType.Config => ConfigFolder,
        _ => string.Empty
    };

    /// <summary>
    /// Destination relative to the instance directory. An explicit <c>targetPath</c> always
    /// wins, which is what allows new item kinds to be added to the catalog without a code change.
    /// Returns null when the item cannot be placed safely.
    /// </summary>
    public static string? ResolveRelativePath(CatalogItem item, string fileName)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (!string.IsNullOrWhiteSpace(item.TargetPath))
        {
            return RelativePath.IsSafe(item.TargetPath) ? item.TargetPath : null;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var folder = FolderFor(item.Type);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var relative = $"{folder}/{fileName}";
        return RelativePath.IsSafe(relative) ? relative : null;
    }
}