using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace STlauncher.Core.Import;

/// <summary>A folder the sweep believes holds builds, and what kind of place it is.</summary>
public sealed record SweepHit(string Path, ExternalLauncherKind Kind);

/// <summary>What a sweep came back with, and whether it saw everything it meant to.</summary>
/// <param name="TimedOut">True when the time ran out with folders still unvisited.</param>
/// <param name="HitLimitReached">True when it stopped because enough was found.</param>
public sealed record SweepResult(IReadOnlyList<SweepHit> Hits, bool TimedOut, bool HitLimitReached, int FoldersVisited);

/// <summary>
/// Looks over the other drives for game folders nobody could guess: a launcher unpacked
/// into D:\Soft\Minecraft\, a .minecraft moved to a second disk.
/// </summary>
/// <remarks>
/// The usual places are looked at on every start, and that is cheap. This is not: it
/// walks drives, so it runs only when the player asks, for a bounded time, three levels
/// deep, and it only reports folders - what is in them is read later, and nothing is
/// imported until the player picks it. Links are never followed and the system's own
/// trees are not entered.
/// </remarks>
public static class DriveSweep
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(12);

    public const int DefaultDepth = 3;

    public const int DefaultMaxHits = 40;

    /// <summary>
    /// Trees that are large, never hold a game folder, and would eat the whole budget.
    /// </summary>
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData",
        "System Volume Information", "Recovery", "PerfLogs", "MSOCache", "Config.Msi",
        "WindowsApps", "WpSystem", "WUDownloadCache", "OneDriveTemp", "Documents and Settings",
        "node_modules", ".git", ".svn", "steamapps", "SteamLibrary", "Epic Games", "XboxGames",
        "Riot Games", "Battle.net", "Origin Games", "GOG Games", "Ubisoft Game Launcher",
        "lost+found", "proc", "sys", "dev", "System", "Library", "Applications", ".Trash", ".Trashes",
        "Android"
    };

    /// <summary>Launcher folders known by name, and the subfolder that holds their builds.</summary>
    private static readonly (string Name, string BuildsFolder, ExternalLauncherKind Kind)[] KnownLaunchers =
    {
        ("ModrinthApp", "profiles", ExternalLauncherKind.Modrinth),
        ("com.modrinth.theseus", "profiles", ExternalLauncherKind.Modrinth),
        (".technic", "modpacks", ExternalLauncherKind.Technic),
        ("ATLauncher", "instances", ExternalLauncherKind.AtLauncher),
        (".xmcl", "instances", ExternalLauncherKind.Xmcl),
        ("gdlauncher_next", "instances", ExternalLauncherKind.GdLauncher),
        ("gdlauncher_carbon", "data", ExternalLauncherKind.GdLauncher),
        (".ftba", "instances", ExternalLauncherKind.Ftb)
    };

    /// <summary>Description files that mark a folder under instances/ as somebody's build.</summary>
    private static readonly string[] InstanceMarkers =
    {
        "mmc-pack.json", "instance.cfg", "minecraftinstance.json", "instance.json", "profile.json"
    };

    /// <summary>
    /// Drives worth sweeping: fixed and removable ones that are ready, except the one the
    /// system lives on - its usual places are covered by the ordinary scan, and the rest
    /// of it is the system.
    /// </summary>
    public static IReadOnlyList<string> OtherDriveRoots()
    {
        var result = new List<string>();

        try
        {
            var system = SystemDriveRoot();

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;

                if (string.Equals(root, system, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Away from Windows a "drive" is any mount, most of them the system's own.
                if (!OperatingSystem.IsWindows() &&
                    !new[] { "/mnt/", "/media/", "/run/media/", "/Volumes/" }.Any(p => root.StartsWith(p, StringComparison.Ordinal)))
                {
                    continue;
                }

                result.Add(root);
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    private static string SystemDriveRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "/";
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.GetPathRoot(string.IsNullOrEmpty(system) ? Environment.SystemDirectory : system) ?? string.Empty;
    }

    /// <summary>
    /// Walks <paramref name="roots"/> breadth-first, so that with the time running out the
    /// folders near the top of every drive have been seen rather than one drive in depth.
    /// </summary>
    public static SweepResult Sweep(
        IEnumerable<string> roots,
        TimeSpan? budget = null,
        int maxDepth = DefaultDepth,
        int maxHits = DefaultMaxHits,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var limit = budget ?? DefaultBudget;
        var hits = new List<SweepHit>();
        var queue = new Queue<(string Path, int Depth)>();
        var visited = 0;
        string? lastReported = null;

        foreach (var root in roots ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(root) && LinkGuard.IsRealDirectory(root))
            {
                queue.Enqueue((root, 0));
            }
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (clock.Elapsed >= limit)
            {
                return new SweepResult(hits, TimedOut: true, HitLimitReached: false, visited);
            }

            var (directory, depth) = queue.Dequeue();
            visited++;

            // The drive's own root is where the sweep starts, never a find in itself.
            if (depth > 0 && Classify(directory) is { } kind)
            {
                hits.Add(new SweepHit(directory, kind));

                if (hits.Count >= maxHits)
                {
                    return new SweepResult(hits, TimedOut: false, HitLimitReached: true, visited);
                }

                // Whatever is inside belongs to this find.
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            if (depth <= 1 && !string.Equals(directory, lastReported, StringComparison.Ordinal))
            {
                lastReported = directory;
                progress?.Report(directory);
            }

            foreach (var child in Children(directory))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        return new SweepResult(hits, TimedOut: false, HitLimitReached: false, visited);
    }

    /// <summary>
    /// Says what a folder is by what is in it, the name only helping: a .minecraft-style
    /// folder has versions/ beside the game's own folders, a launcher has instances/ with
    /// described builds in it.
    /// </summary>
    public static ExternalLauncherKind? Classify(string directory)
    {
        try
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

            if (LinkGuard.IsRealDirectory(Path.Combine(directory, "versions")) &&
                (File.Exists(Path.Combine(directory, "launcher_profiles.json")) ||
                 File.Exists(Path.Combine(directory, "TlauncherProfiles.json")) ||
                 Directory.Exists(Path.Combine(directory, "saves")) ||
                 Directory.Exists(Path.Combine(directory, "mods")) ||
                 Directory.Exists(Path.Combine(directory, "libraries")) ||
                 Directory.Exists(Path.Combine(directory, "assets"))))
            {
                return ExternalLauncherKind.DotMinecraft;
            }

            foreach (var (known, builds, kind) in KnownLaunchers)
            {
                if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase) &&
                    LinkGuard.IsRealDirectory(Path.Combine(directory, builds)))
                {
                    return kind;
                }
            }

            var instances = Path.Combine(directory, "instances");

            if (LinkGuard.IsRealDirectory(instances) &&
                LinkGuard.RealDirectories(instances).Take(50).Any(i => InstanceMarkers.Any(m => File.Exists(Path.Combine(i, m)))))
            {
                // Named only when a file says which launcher it is; otherwise it is "a launcher".
                return File.Exists(Path.Combine(directory, "multimc.cfg")) ? ExternalLauncherKind.MultiMc
                    : File.Exists(Path.Combine(directory, "prismlauncher.cfg")) ? ExternalLauncherKind.Prism
                    : File.Exists(Path.Combine(directory, "polymc.cfg")) ? ExternalLauncherKind.PolyMc
                    : LinkGuard.RealDirectories(instances).Take(50).Any(i => File.Exists(Path.Combine(i, "minecraftinstance.json"))) ? ExternalLauncherKind.CurseForge
                    : ExternalLauncherKind.Unknown;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static IEnumerable<string> Children(string directory)
    {
        var result = new List<string>();

        try
        {
            foreach (var child in new DirectoryInfo(directory).EnumerateDirectories())
            {
                var name = child.Name;

                // "$Recycle.Bin", "$WinREAgent" and their kind; system folders the shell hides.
                // A folder with a custom icon is "system" too, so both marks are asked for.
                const FileAttributes Hidden = FileAttributes.System | FileAttributes.Hidden;

                if (name.Length == 0 || name[0] == '$' || SkippedNames.Contains(name) ||
                    (child.Attributes & Hidden) == Hidden)
                {
                    continue;
                }

                if (LinkGuard.IsLink(child.FullName))
                {
                    continue;
                }

                result.Add(child.FullName);
            }
        }
        catch (Exception)
        {
            // No access, a drive pulled out mid-walk: that branch is simply not seen.
        }

        return result;
    }
}
