using System;
using System.Collections.Generic;
using System.IO;

namespace STlauncher.Core.Import;

/// <summary>
/// Tells a real folder from a symbolic link or a junction. The import never walks through
/// one: a link inside somebody's build can point anywhere on the machine - the whole user
/// profile, another drive - and scanning or copying "the build" would then read or
/// duplicate files that were never part of it. The one exception is a launcher's root
/// folder that is itself a link, which <see cref="LinkedRootResolver"/> resolves and vets.
/// </summary>
public static class LinkGuard
{
    /// <summary>
    /// True for a symbolic link or a junction, whether it points at a folder or a file.
    /// Other reparse points are not links: a OneDrive placeholder is an ordinary file that
    /// happens to live in the cloud, and treating it as a link would hide every build
    /// kept under a synced Documents folder.
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

            if (!info.Exists)
            {
                return false;
            }

            return (info.Attributes & FileAttributes.ReparsePoint) != 0
                ? info.LinkTarget is not null
                : info.LinkTarget is not null && !OperatingSystem.IsWindows();
        }
        catch (Exception)
        {
            // A folder that cannot even be asked what it is does not get walked into.
            return true;
        }
    }

    /// <summary>Where a link points, for telling the player; null when it is not a link or cannot be read.</summary>
    public static string? TargetOf(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.LinkTarget;

            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            // The raw target of a junction comes with the NT prefix.
            target = target.StartsWith(@"\??\", StringComparison.Ordinal) ? target[4..] : target;

            return Path.IsPathRooted(target)
                ? target
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? string.Empty, target));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Subfolders that are real folders. Unreadable places yield nothing.</summary>
    public static IReadOnlyList<string> RealDirectories(string directory)
    {
        var result = new List<string>();

        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IsLink(child))
                {
                    result.Add(child);
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>Files that are real files. Unreadable places yield nothing.</summary>
    public static IReadOnlyList<string> RealFiles(string directory, string pattern = "*")
    {
        var result = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                if (!IsLink(file))
                {
                    result.Add(file);
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>True when the folder exists and is a real one, not a link to somewhere else.</summary>
    public static bool IsRealDirectory(string path) => Directory.Exists(path) && !IsLink(path);

    /// <summary>True when the file exists and is a real one.</summary>
    public static bool IsRealFile(string path) => File.Exists(path) && !IsLink(path);
}
