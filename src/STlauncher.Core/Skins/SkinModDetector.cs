using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Skins;

/// <summary>A mod in the build that already decides what skin the player has.</summary>
/// <param name="IsCustomSkinLoader">True for the very mod the launcher would add: it can be handed the skin as it is.</param>
public sealed record SkinMod(string Name, string FileName, bool IsCustomSkinLoader);

/// <summary>
/// Looks through a build's enabled mods for one that replaces skins. Two such mods patch
/// the same place in the game and the result is a crash or whichever loaded last, so the
/// launcher never adds its own beside another: it says which one is there and steps back.
/// </summary>
public static class SkinModDetector
{
    private sealed record Known(string Name, bool IsCustomSkinLoader, string[] Ids, string[] FileTokens);

    // Ids as the mods declare them; file tokens for the old Forge jars that declare
    // nothing a launcher can read (a coremod with mcmod.info, or with no metadata at all).
    private static readonly Known[] KnownMods =
    {
        new("CustomSkinLoader", true, new[] { "customskinloader" }, new[] { "customskinloader" }),
        new("SkinRestorer", false, new[] { "skinrestorer" }, new[] { "skinrestorer" }),
        new("SkinsRestorer", false, new[] { "skinsrestorer" }, new[] { "skinsrestorer" }),
        new("Fabric Tailor", false, new[] { "fabrictailor" }, new[] { "fabrictailor" }),
        new("OfflineSkins", false, new[] { "offlineskins", "offline-skins", "offline_skins" }, new[] { "offlineskins", "offline-skins" }),
        new("HD Skins", false, new[] { "hdskins" }, new[] { "hdskins" }),
        new("TLSkinCape", false, new[] { "tlskincape", "tlauncher_custom_cape_skin" }, new[] { "tlskincape" }),
        new("SkinShuffle", false, new[] { "skinshuffle" }, new[] { "skinshuffle" })
    };

    /// <summary>
    /// The skin mod that is switched on in the build, or null. A jar the launcher itself
    /// installed is passed as <paramref name="exceptFileName"/> and not counted. When
    /// there are several, one that is not CustomSkinLoader is reported first: that is
    /// the one the launcher cannot work with.
    /// </summary>
    public static SkinMod? Find(string gameDirectory, string? exceptFileName = null)
    {
        var directory = ModManager.ModsDirectory(gameDirectory);

        if (!Directory.Exists(directory))
        {
            return null;
        }

        SkinMod? own = null;

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(path);

                // "*.jar" also matches "x.jar.disabled" on Windows; a switched-off mod decides nothing.
                if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith('.') ||
                    string.Equals(fileName, exceptFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Identify(path) is not { } known)
                {
                    continue;
                }

                var found = new SkinMod(known.Name, fileName, known.IsCustomSkinLoader);

                if (!known.IsCustomSkinLoader)
                {
                    return found;
                }

                own ??= found;
            }
        }
        catch (IOException)
        {
            // A folder that cannot be listed right now is reported by whatever reads it next.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return own;
    }

    /// <summary>Which known skin mod a jar is: by the ids it declares, then by its file name.</summary>
    private static Known? Identify(string jarPath)
    {
        var ids = ModMetadataReader.Read(jarPath)
            .SelectMany(m => m.Provides.Append(m.Id))
            .ToList();

        foreach (var known in KnownMods)
        {
            if (ids.Any(id => known.Ids.Any(k => Matches(id, k))))
            {
                return known;
            }
        }

        // Only a jar that says nothing about itself is judged by its name: a mod with
        // its own id and "skinrestorer" somewhere in the file name is some other mod.
        if (ids.Count > 0)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(jarPath).Replace("_", string.Empty, StringComparison.Ordinal);

        return KnownMods.FirstOrDefault(known =>
            known.FileTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// An id is the known one, or the known one with a suffix: the universal
    /// CustomSkinLoader jar calls itself "customskinloader-bootstrap".
    /// </summary>
    private static bool Matches(string id, string known)
        => id.Equals(known, StringComparison.OrdinalIgnoreCase) ||
           id.StartsWith(known + "-", StringComparison.OrdinalIgnoreCase) ||
           id.StartsWith(known + "_", StringComparison.OrdinalIgnoreCase);
}
