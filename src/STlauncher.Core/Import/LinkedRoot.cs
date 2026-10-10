using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace STlauncher.Core.Import;

/// <summary>Why a root folder that is a link is not read through.</summary>
public enum LinkedRootProblem
{
    None,

    /// <summary>The link points at a folder that is not there: an unplugged drive, a folder since moved.</summary>
    Dangling,

    /// <summary>The links lead back to one another and never arrive at a folder.</summary>
    Loop,

    /// <summary>The folder is on another computer.</summary>
    Network,

    /// <summary>The folder is the launcher's own data, or inside it.</summary>
    OwnData,

    /// <summary>The folder belongs to the system: Windows, Program Files, a whole drive, a whole user profile.</summary>
    System,

    /// <summary>The link points at a file.</summary>
    NotAFolder
}

/// <summary>
/// Where a linked root really is. <see cref="Target"/> is the folder that would be read,
/// or - when there is a <see cref="Problem"/> - as far as the link could be followed.
/// </summary>
public sealed record LinkedRoot(string Link, string? Target, LinkedRootProblem Problem)
{
    public bool IsUsable => Problem == LinkedRootProblem.None && Target is not null;
}

/// <summary>
/// The one link the import does follow: the root a launcher keeps its game in. Moving
/// .minecraft to a bigger drive and leaving a junction where it was is how players are
/// told to free their system drive, and a launcher that then finds "no builds" is no use
/// to them. So the root - and only the root - is resolved once, all the way to a real
/// folder, and everything is read from there as from any folder; links met inside it
/// stay unfollowed as <see cref="LinkGuard"/> has it.
/// </summary>
/// <remarks>
/// A link can lead anywhere, so where it arrived is checked before anything is read:
/// an ordinary folder on this computer, not the launcher's own data and not a place that
/// belongs to the system. The real path travels with every build found, for the import
/// screen to show: the player agrees to read that folder, not the name the link wears.
/// </remarks>
public static class LinkedRootResolver
{
    /// <summary>More hops than any honest setup has; a chain this long is a loop that changes its spelling.</summary>
    private const int MaxHops = 32;

    public static LinkedRoot Resolve(string link) => Resolve(link, OwnDataRoots());

    /// <param name="launcherDataRoots">Folders the launcher keeps its own data in; a link into one is refused.</param>
    public static LinkedRoot Resolve(string link, IEnumerable<string> launcherDataRoots)
    {
        string current;

        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(link));
        }
        catch (Exception)
        {
            return new LinkedRoot(link, null, LinkedRootProblem.Dangling);
        }

        var seen = new HashSet<string>(PathComparer) { current };

        // Every part of the path is looked at, not only the last: a junction to
        // D:\Games\.minecraft where D:\Games is itself a link to a share has not arrived
        // on this computer yet.
        for (var hop = 0; hop < MaxHops; hop++)
        {
            // Before the path is touched at all: asking a share about a folder can hang
            // for as long as the other computer takes to not answer.
            if (IsNetworkPath(current))
            {
                return new LinkedRoot(link, current, LinkedRootProblem.Network);
            }

            var linked = FirstLinkedPart(current);

            if (linked is null)
            {
                return Judge(link, current, launcherDataRoots);
            }

            var target = LinkGuard.TargetOf(linked);

            if (target is null)
            {
                return new LinkedRoot(link, null, LinkedRootProblem.Dangling);
            }

            string next;

            try
            {
                next = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target + current[linked.Length..]));
            }
            catch (Exception)
            {
                return new LinkedRoot(link, target, LinkedRootProblem.Dangling);
            }

            if (!seen.Add(next))
            {
                return new LinkedRoot(link, null, LinkedRootProblem.Loop);
            }

            current = next;
        }

        return new LinkedRoot(link, null, LinkedRootProblem.Loop);
    }

    /// <summary>The shortest leading part of the path that is a link; null when none is, or the path stops existing first.</summary>
    private static string? FirstLinkedPart(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var parts = path[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var prefix = root;

        foreach (var part in parts)
        {
            prefix = prefix.Length == 0 ? part : Path.Combine(prefix, part);

            if (LinkGuard.IsLink(prefix))
            {
                return prefix;
            }

            if (!Directory.Exists(prefix) && !File.Exists(prefix))
            {
                return null;
            }
        }

        return null;
    }

    private static LinkedRoot Judge(string link, string target, IEnumerable<string> launcherDataRoots)
    {
        if (File.Exists(target))
        {
            return new LinkedRoot(link, target, LinkedRootProblem.NotAFolder);
        }

        if (!Directory.Exists(target))
        {
            return new LinkedRoot(link, target, LinkedRootProblem.Dangling);
        }

        if (launcherDataRoots.Any(root => IsSameOrInside(target, root)))
        {
            return new LinkedRoot(link, target, LinkedRootProblem.OwnData);
        }

        if (IsSystemPlace(target))
        {
            return new LinkedRoot(link, target, LinkedRootProblem.System);
        }

        return new LinkedRoot(link, target, LinkedRootProblem.None);
    }

    /// <summary>A UNC path, or a drive letter or mount that is a share.</summary>
    public static bool IsNetworkPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // "\\?\C:\..." is a long spelling of a local path; "\\?\UNC\server\share" is not.
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                path = path[4..];
            }
            else if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }
        }

        try
        {
            var best = DriveInfo.GetDrives()
                .Where(d => IsSameOrInside(path, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();

            return best?.DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A whole drive, a whole user profile, or anything under the folders the system and
    /// its programs live in. None of them is a game folder, and "importing" one would
    /// read through somebody's entire disk.
    /// </summary>
    public static bool IsSystemPlace(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full);

        if (root is not null && PathComparer.Equals(full, Path.TrimEndingDirectorySeparator(root)))
        {
            return true;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (profile.Length > 0 &&
            (PathComparer.Equals(full, Path.TrimEndingDirectorySeparator(profile)) ||
             PathComparer.Equals(full, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profile)))))
        {
            return true;
        }

        return SystemFolders().Any(folder => IsSameOrInside(full, folder));
    }

    private static IEnumerable<string> SystemFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.Windows,
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86
                     })
            {
                var path = Environment.GetFolderPath(folder);

                if (path.Length > 0)
                {
                    yield return path;
                }
            }

            yield break;
        }

        foreach (var path in new[] { "/bin", "/boot", "/dev", "/etc", "/lib", "/proc", "/sbin", "/sys", "/usr", "/System" })
        {
            yield return path;
        }
    }

    private static IReadOnlyList<string> OwnDataRoots()
    {
        var roots = new List<string>();

        try
        {
            roots.Add(DataLocation.DefaultRoot);
            roots.Add(DataLocation.Resolve());
        }
        catch (Exception)
        {
            // Without a data root to compare with, the other checks still stand.
        }

        return roots;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            var inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

            // A drive's root keeps its separator through the trim; a folder does not.
            var prefix = Path.EndsInDirectorySeparator(outer) ? outer : outer + Path.DirectorySeparatorChar;

            return PathComparer.Equals(inner, outer) || inner.StartsWith(prefix, PathComparison);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static StringComparer PathComparer
        => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
