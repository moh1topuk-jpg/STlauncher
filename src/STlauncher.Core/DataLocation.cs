using System;
using System.IO;

namespace STlauncher.Core;

/// <summary>
/// Where the launcher keeps its data. The default is under AppData; a player who wants
/// the gigabytes of builds on another disk points the launcher there, and that pointer
/// is the one file that always stays in AppData, so the launcher can find its way back.
/// </summary>
public static class DataLocation
{
    public const string PointerFileName = "data-root.txt";

    /// <summary>The folder the launcher used before a custom location existed.</summary>
    public static string DefaultRoot
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "STlauncher");

    public static string PointerPath => Path.Combine(DefaultRoot, PointerFileName);

    /// <summary>
    /// The configured root, or the default when there is no pointer or it points at a
    /// folder that is gone: a removed external drive must not leave the launcher unable
    /// to start.
    /// </summary>
    public static string Resolve() => Resolve(PointerPath, DefaultRoot);

    public static string Resolve(string pointerPath, string defaultRoot)
    {
        try
        {
            if (!File.Exists(pointerPath))
            {
                return defaultRoot;
            }

            var custom = File.ReadAllText(pointerPath).Trim();

            if (custom.Length == 0 || !Path.IsPathRooted(custom) || !Directory.Exists(custom))
            {
                return defaultRoot;
            }

            return Path.GetFullPath(custom);
        }
        catch (Exception)
        {
            return defaultRoot;
        }
    }

    /// <summary>Points the launcher at a folder from the next start on. Null goes back to the default.</summary>
    public static void Write(string? customRoot) => Write(PointerPath, customRoot);

    public static void Write(string pointerPath, string? customRoot)
    {
        var directory = Path.GetDirectoryName(pointerPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (string.IsNullOrWhiteSpace(customRoot))
        {
            if (File.Exists(pointerPath))
            {
                File.Delete(pointerPath);
            }

            return;
        }

        AtomicFile.WriteAllText(pointerPath, Path.GetFullPath(customRoot));
    }

    /// <summary>True when the two paths name the same folder.</summary>
    public static bool IsSame(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>A target inside the current root would be copied into itself.</summary>
    public static bool IsInside(string candidate, string root)
    {
        var child = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
}
