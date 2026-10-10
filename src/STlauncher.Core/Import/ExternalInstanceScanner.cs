using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;

namespace STlauncher.Core.Import;

/// <summary>
/// Finds builds that already exist on the machine, in other launchers or assembled by
/// hand, so moving to this launcher does not mean rebuilding everything.
/// </summary>
/// <remarks>
/// Every candidate is inspected rather than trusted. A real .minecraft folder is full of
/// half-finished version folders - a jar with no profile JSON, a leftover archive, a
/// directory someone renamed - and a build that silently fails to appear is worse than
/// one listed with the reason it cannot be used.
///
/// Each launcher describes its builds differently, and several keep them wherever the
/// player chose to install. So the scanner reads the launchers' own settings for the
/// instance folder, looks in the usual places for portable installs, and understands
/// every description format it can - falling back to the mod files themselves when a
/// launcher wrote nothing readable.
/// </remarks>
public static class ExternalInstanceScanner
{
    private const string ModsFolder = "mods";

    /// <summary>Where a launcher that keeps one folder per build puts the game files inside it.</summary>
    private static readonly string[] GameSubfolders = { ".minecraft", "minecraft", "instance" };

    /// <summary>Folder names of launchers that install wherever the player unpacked them.</summary>
    private static readonly (string Folder, ExternalLauncherKind Kind, string? Config)[] PortableLaunchers =
    {
        ("PrismLauncher", ExternalLauncherKind.Prism, "prismlauncher.cfg"),
        ("Prism Launcher", ExternalLauncherKind.Prism, "prismlauncher.cfg"),
        ("PolyMC", ExternalLauncherKind.PolyMc, "polymc.cfg"),
        ("MultiMC", ExternalLauncherKind.MultiMc, "multimc.cfg"),
        ("PineconeMC", ExternalLauncherKind.Prism, "elyprismlauncher.cfg"),
        ("ElyPrismLauncher", ExternalLauncherKind.Prism, "elyprismlauncher.cfg"),
        ("FjordLauncher", ExternalLauncherKind.Prism, "fjordlauncher.cfg"),
        ("PollyMC", ExternalLauncherKind.PolyMc, "pollymc.cfg"),
        ("ATLauncher", ExternalLauncherKind.AtLauncher, null)
    };

    /// <summary>
    /// Legacy Launcher keeps a game folder of each version under home/ when its
    /// "subfolders" setting is on: home/1.21.11, home/Fabric 1.21.11, home/Forge-1.12.
    /// </summary>
    private const string LegacyHomeFolder = "home";

    /// <summary>Places worth looking in, in the order they are offered.</summary>
    public static IReadOnlyList<(string Path, ExternalLauncherKind Kind)> DefaultRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // Where launchers keep their data: %APPDATA% on Windows, ~/.local/share on Linux,
        // ~/Library/Application Support on macOS. The vanilla folder is the odd one out:
        // a dot-folder in the home directory on Linux, "minecraft" without the dot on macOS.
        var appData = LauncherDataRoot(profile);
        var dotMinecraft = OperatingSystem.IsLinux() ? Path.Combine(profile, ".minecraft")
            : OperatingSystem.IsMacOS() ? Path.Combine(appData, "minecraft")
            : Path.Combine(appData, ".minecraft");
        var tlauncherHome = OperatingSystem.IsWindows() ? appData : profile;

        var roots = new List<(string, ExternalLauncherKind)>
        {
            (dotMinecraft, ExternalLauncherKind.DotMinecraft),
            (Path.Combine(appData, "PrismLauncher", "instances"), ExternalLauncherKind.Prism),
            (Path.Combine(appData, "PolyMC", "instances"), ExternalLauncherKind.PolyMc),
            (Path.Combine(profile, "curseforge", "minecraft", "Instances"), ExternalLauncherKind.CurseForge),
            (Path.Combine(documents, "Curse", "Minecraft", "Instances"), ExternalLauncherKind.CurseForge),
            (Path.Combine(appData, "ModrinthApp", "profiles"), ExternalLauncherKind.Modrinth),
            (Path.Combine(appData, "com.modrinth.theseus", "profiles"), ExternalLauncherKind.Modrinth),
            (Path.Combine(appData, "gdlauncher_carbon", "data", "instances"), ExternalLauncherKind.GdLauncher),
            (Path.Combine(appData, "gdlauncher_next", "instances"), ExternalLauncherKind.GdLauncher),
            (Path.Combine(local, ".ftba", "instances"), ExternalLauncherKind.Ftb),
            (Path.Combine(appData, ".technic", "modpacks"), ExternalLauncherKind.Technic),
            (Path.Combine(profile, ".xmcl", "instances"), ExternalLauncherKind.Xmcl)
        };

        // TLauncher can be pointed at a game folder other than .minecraft.
        var tlauncherDirectory = ReadPropertiesValue(Path.Combine(tlauncherHome, ".tlauncher", "tlauncher-2.0.properties"), "minecraft.gamedir");
        if (!string.IsNullOrWhiteSpace(tlauncherDirectory))
        {
            roots.Add((tlauncherDirectory!, ExternalLauncherKind.DotMinecraft));
        }

        // Prism and PolyMC let the player move the instance folder; the setting wins.
        AddConfiguredInstanceDirectory(roots, Path.Combine(appData, "PrismLauncher", "prismlauncher.cfg"), ExternalLauncherKind.Prism);
        AddConfiguredInstanceDirectory(roots, Path.Combine(appData, "PolyMC", "polymc.cfg"), ExternalLauncherKind.PolyMc);

        // Legacy Launcher: its settings live in several places depending on the version,
        // all under .tlauncher, and each may point the game at a folder of its own.
        foreach (var directory in ConfiguredGameDirectories(Path.Combine(tlauncherHome, ".tlauncher")))
        {
            roots.Add((directory, ExternalLauncherKind.DotMinecraft));
        }

        // Every fork of Prism or MultiMC under the data root, whatever it is called:
        // PineconeMC, Fjord, PollyMC and the next one. Told by the files, not the name.
        foreach (var found in DiscoverMultiMcFamily(appData))
        {
            roots.Add(found);
        }

        // Portable installs: MultiMC only ever ships that way, Prism and ATLauncher often do.
        foreach (var basePath in PortableBases(profile, local, documents))
        {
            foreach (var (folder, kind, config) in PortableLaunchers)
            {
                var directory = Path.Combine(basePath, folder);
                roots.Add((Path.Combine(directory, "instances"), kind));

                if (config is not null)
                {
                    AddConfiguredInstanceDirectory(roots, Path.Combine(directory, config), kind);
                }
            }

            // A portable zip unpacks as "PrismLauncher-Windows-MSVC-Portable-9.4" or whatever
            // the player renamed it to; the instances folder inside is what gives it away.
            foreach (var found in DiscoverMultiMcFamily(basePath))
            {
                roots.Add(found);
            }
        }

        return roots
            .GroupBy(r => r.Item1.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string LauncherDataRoot(string profile)
    {
        if (OperatingSystem.IsLinux())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrWhiteSpace(xdg) ? Path.Combine(profile, ".local", "share") : xdg;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(profile, "Library", "Application Support");
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    private static IEnumerable<string> PortableBases(string profile, string local, string documents)
    {
        yield return profile;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.Combine(profile, "Downloads");
        yield return documents;
        yield return Path.Combine(profile, "Games");
        yield return Path.Combine(local, "Programs");

        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
            {
                yield return drive.RootDirectory.FullName;
                yield return Path.Combine(drive.RootDirectory.FullName, "Games");
            }
        }
    }

    /// <summary>
    /// Folders directly under <paramref name="baseDirectory"/> that hold a MultiMC-style
    /// launcher: an instances folder with at least one instance described by mmc-pack.json
    /// or instance.cfg, or a launcher .cfg that names the instance folder. The kind comes
    /// from the config file's name when it is a known one; a fork nobody has heard of is
    /// still found, listed under the name of its folder.
    /// </summary>
    public static IReadOnlyList<(string Path, ExternalLauncherKind Kind)> DiscoverMultiMcFamily(string baseDirectory)
    {
        var result = new List<(string, ExternalLauncherKind)>();

        foreach (var directory in SafeDirectories(baseDirectory))
        {
            var kind = ExternalLauncherKind.Unknown;
            var configured = false;

            foreach (var config in SafeFiles(directory, "*.cfg"))
            {
                var name = Path.GetFileName(config).ToLowerInvariant();
                var configKind = name switch
                {
                    "prismlauncher.cfg" => ExternalLauncherKind.Prism,
                    "polymc.cfg" or "pollymc.cfg" => ExternalLauncherKind.PolyMc,
                    "multimc.cfg" => ExternalLauncherKind.MultiMc,
                    _ => ExternalLauncherKind.Unknown
                };

                if (ReadPropertiesValue(config, "InstanceDir") is { Length: > 0 })
                {
                    var before = result.Count;
                    AddConfiguredInstanceDirectory(result, config, configKind == ExternalLauncherKind.Unknown ? ExternalLauncherKind.Prism : configKind);
                    configured |= result.Count > before;
                }

                if (configKind != ExternalLauncherKind.Unknown)
                {
                    kind = configKind;
                }
            }

            var instances = Path.Combine(directory, "instances");

            // A launcher whose instance folder is a link to another drive: it goes on the
            // list so the scan can say why it was not opened, instead of looking empty.
            if (Directory.Exists(instances) && LinkGuard.IsLink(instances))
            {
                if (kind != ExternalLauncherKind.Unknown || SafeFiles(directory, "*.cfg").Any())
                {
                    result.Add((instances, kind == ExternalLauncherKind.Unknown ? ExternalLauncherKind.Prism : kind));
                }

                continue;
            }

            if (!Directory.Exists(instances) || !LooksLikeMultiMcInstances(instances))
            {
                continue;
            }

            // Any .cfg beside an instances folder of that shape is a MultiMC descendant;
            // Prism is the one people mean today.
            if (kind == ExternalLauncherKind.Unknown && (configured || SafeFiles(directory, "*.cfg").Any()))
            {
                kind = ExternalLauncherKind.Prism;
            }

            result.Add((instances, kind));
        }

        return result;
    }

    private static bool LooksLikeMultiMcInstances(string instances)
        => SafeDirectories(instances).Any(d =>
            LinkGuard.IsRealFile(Path.Combine(d, "mmc-pack.json")) || LinkGuard.IsRealFile(Path.Combine(d, "instance.cfg")));

    /// <summary>
    /// Game folders named in any properties file under a launcher's settings folder, as
    /// TLauncher and Legacy Launcher keep them: "minecraft.gamedir" in tlauncher-2.0.properties,
    /// legacy.properties or legacy/Minecraft/tl.properties. Any key ending in "gamedir" counts,
    /// so a renamed setting in the next version still works.
    /// </summary>
    public static IReadOnlyList<string> ConfiguredGameDirectories(string settingsDirectory)
    {
        var result = new List<string>();

        if (!Directory.Exists(settingsDirectory))
        {
            return result;
        }

        foreach (var file in SafeFiles(settingsDirectory, "*.properties", depth: 3))
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var separator = line.IndexOf('=');

                    if (separator <= 0)
                    {
                        continue;
                    }

                    var key = line[..separator].Trim();

                    if (!key.EndsWith("gamedir", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = line[(separator + 1)..].Trim().Trim('"').Replace("\\:", ":").Replace("\\\\", "\\");

                    if (value.Length > 0 && Directory.Exists(value) &&
                        !result.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(value);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        return result;
    }

    private static IEnumerable<string> SafeFiles(string directory, string pattern, int depth = 1)
    {
        var result = new List<string>();

        try
        {
            if (!LinkGuard.IsRealDirectory(directory))
            {
                return result;
            }

            result.AddRange(LinkGuard.RealFiles(directory, pattern));

            if (depth > 1)
            {
                foreach (var child in LinkGuard.RealDirectories(directory))
                {
                    result.AddRange(SafeFiles(child, pattern, depth - 1));
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>MultiMC-style "InstanceDir=" from an ini file; relative to the file when relative.</summary>
    public static void AddConfiguredInstanceDirectory(
        List<(string, ExternalLauncherKind)> roots,
        string configPath,
        ExternalLauncherKind kind)
    {
        var value = ReadPropertiesValue(configPath, "InstanceDir");

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            var directory = Path.IsPathRooted(value)
                ? value!
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, value!));

            roots.Add((directory, kind));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>One "key=value" from a properties/ini file. Java escapes ("C\:\\Users") are undone.</summary>
    public static string? ReadPropertiesValue(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var line in File.ReadLines(path))
            {
                var separator = line.IndexOf('=');

                if (separator <= 0 || !string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim().Trim('"');
                return value.Replace("\\:", ":").Replace("\\\\", "\\");
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    /// <summary>Scans every known location. Missing ones are simply skipped.</summary>
    public static IReadOnlyList<ExternalInstance> ScanAll(IEnumerable<(string Path, ExternalLauncherKind Kind)>? roots = null)
        => ScanAll(roots, null);

    /// <summary>
    /// The same, telling the caller which locations actually existed. "Found nothing" with
    /// no trail is what every complaint about the import looks like from the other side.
    /// </summary>
    public static IReadOnlyList<ExternalInstance> ScanAll(
        IEnumerable<(string Path, ExternalLauncherKind Kind)>? roots,
        List<string>? scannedRoots)
    {
        var result = new List<ExternalInstance>();

        foreach (var (path, kind) in roots ?? DefaultRoots())
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            scannedRoots?.Add(LinkGuard.IsLink(path) ? $"{path} ({kind}, a link - not opened)" : $"{path} ({kind})");
            result.AddRange(Scan(path, kind));
        }

        return Sort(Deduplicate(result));
    }

    /// <summary>
    /// Scans one location. <paramref name="kind"/> decides the layout: a .minecraft folder
    /// keeps its builds in versions/, the instance-per-folder launchers keep one game
    /// directory each.
    /// </summary>
    public static IReadOnlyList<ExternalInstance> Scan(string root, ExternalLauncherKind kind)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Array.Empty<ExternalInstance>();
        }

        if (LinkGuard.IsLink(root))
        {
            return new[] { RefusedLink(root, kind) };
        }

        return kind == ExternalLauncherKind.DotMinecraft
            ? ScanDotMinecraft(root)
            : ScanInstanceFolders(root, kind);
    }

    /// <summary>
    /// Looks at a folder the player pointed at, working out what it is: a .minecraft-style
    /// folder, a launcher's root, its instance folder, or a single build.
    /// </summary>
    public static IReadOnlyList<ExternalInstance> ScanUnknownFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return Array.Empty<ExternalInstance>();
        }

        if (LinkGuard.IsLink(path))
        {
            return new[] { RefusedLink(path, ExternalLauncherKind.Unknown) };
        }

        if (Directory.Exists(Path.Combine(path, "versions")) || Directory.Exists(Path.Combine(path, LegacyHomeFolder)))
        {
            return ScanDotMinecraft(path);
        }

        // The root of a MultiMC-family launcher, or a folder of several such launchers.
        var family = DiscoverMultiMcFamily(path);

        if (family.Count > 0)
        {
            var fromFamily = family.SelectMany(f => ScanInstanceFolders(f.Path, f.Kind)).ToList();

            if (fromFamily.Count > 0)
            {
                return Sort(Deduplicate(fromFamily));
            }
        }

        // The root of a launcher rather than its instance folder.
        foreach (var (subfolder, kind) in new[]
                 {
                     ("instances", ExternalLauncherKind.Unknown),
                     ("profiles", ExternalLauncherKind.Modrinth),
                     ("modpacks", ExternalLauncherKind.Technic)
                 })
        {
            var inner = Path.Combine(path, subfolder);

            if (Directory.Exists(inner))
            {
                var found = ScanInstanceFolders(inner, kind);

                if (found.Count > 0)
                {
                    return found;
                }
            }
        }

        // A single build with a description of its own wins over its subfolders, which
        // would otherwise turn up as builds of their own (".minecraft" inside it, say).
        var single = InspectInstanceFolder(path, ExternalLauncherKind.Unknown);

        if (single is { VersionInferred: false, IsUsable: true } && single.HasKnownVersion)
        {
            return new[] { single };
        }

        var asInstances = ScanInstanceFolders(path, ExternalLauncherKind.Unknown);

        if (asInstances.Count > 0)
        {
            return asInstances;
        }

        return single is null ? Array.Empty<ExternalInstance>() : new[] { single };
    }

    /// <summary>
    /// A folder that is a symbolic link or a junction, listed so the player sees why it
    /// was left alone and where it leads. Following it is their call: the real folder
    /// can be pointed at by hand.
    /// </summary>
    private static ExternalInstance RefusedLink(string path, ExternalLauncherKind kind, string? name = null)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return new ExternalInstance(
            string.IsNullOrWhiteSpace(name) ? Path.GetFileName(trimmed) is { Length: > 0 } folder ? folder : trimmed : name!,
            path,
            string.Empty,
            LoaderKind.Vanilla,
            kind,
            VersionJsonPath: null,
            ModCount: 0,
            ExternalInstanceProblem.SourceIsLink)
        {
            LinkTarget = LinkGuard.TargetOf(path)
        };
    }

    private static IReadOnlyList<ExternalInstance> ScanDotMinecraft(string dotMinecraft)
    {
        var versions = Path.Combine(dotMinecraft, "versions");
        var result = new List<ExternalInstance>();

        if (LinkGuard.IsRealDirectory(versions))
        {
            var modCount = CountMods(Path.Combine(dotMinecraft, ModsFolder));

            foreach (var directory in AllDirectories(versions))
            {
                var id = Path.GetFileName(directory);

                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                result.Add(LinkGuard.IsLink(directory)
                    ? RefusedLink(directory, ExternalLauncherKind.DotMinecraft)
                    : InspectVersionFolder(directory, id, dotMinecraft, ExternalLauncherKind.DotMinecraft, modCount));
            }
        }

        result.AddRange(ScanLauncherProfiles(dotMinecraft));
        result.AddRange(ScanLegacyHomeFolders(dotMinecraft));

        return Sort(result);
    }

    /// <summary>
    /// Legacy Launcher with subfolders on: each version plays in home/&lt;name&gt;, and the
    /// shared mods folder stays empty. The name is the version ("1.21.11", "Fabric 1.21.11")
    /// or a family ("Forge-1.12"); the matching profile in versions/ is used when there is
    /// one, otherwise the mods say what the folder is for.
    /// </summary>
    private static IEnumerable<ExternalInstance> ScanLegacyHomeFolders(string dotMinecraft)
    {
        var home = Path.Combine(dotMinecraft, LegacyHomeFolder);
        var result = new List<ExternalInstance>();

        if (!LinkGuard.IsRealDirectory(home))
        {
            return result;
        }

        var versions = Path.Combine(dotMinecraft, "versions");
        var versionIds = SafeDirectories(versions).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();

        foreach (var directory in SafeDirectories(home))
        {
            var name = Path.GetFileName(directory);

            if (string.IsNullOrEmpty(name) || !IsGameFolder(directory))
            {
                continue;
            }

            var modCount = CountMods(Path.Combine(directory, ModsFolder));

            // "Fabric 1.21.11" and "Fabric-1.21.11" both mean the profile named so, or the
            // one whose id carries both words.
            var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            var versionId = versionIds.FirstOrDefault(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase))
                            ?? versionIds.FirstOrDefault(id => words.Length > 1 && words.All(w => id.Contains(w, StringComparison.OrdinalIgnoreCase)))
                            ?? (words.Length == 1 ? versionIds.FirstOrDefault(id => string.Equals(id, words[0], StringComparison.OrdinalIgnoreCase)) : null);

            ExternalInstance instance;

            if (versionId is not null)
            {
                instance = InspectVersionFolder(Path.Combine(versions, versionId), versionId, directory, ExternalLauncherKind.DotMinecraft, modCount, gameDirectoryIsOwn: true);
            }
            else
            {
                // No profile to ask: the mods say what they are for, then the game's own
                // log, and the folder's name only when it is nothing but a version.
                var verdict = modCount > 0 ? ModFolderInspector.Inspect(Path.Combine(directory, ModsFolder)) : null;
                var log = verdict?.GameVersion is null ? GameVersionDetector.FromLog(directory) : null;
                var guessed = verdict?.GameVersion ?? log?.GameVersion ?? GameVersionDetector.FromName(name);

                instance = new ExternalInstance(name, directory, string.Empty, verdict?.Loader ?? log?.Loader ?? LoaderFromName(name), ExternalLauncherKind.DotMinecraft, null, modCount)
                {
                    GameVersion = string.IsNullOrEmpty(guessed) ? null : guessed,
                    VersionInferred = verdict is not null || log is not null
                };
            }

            result.Add(instance with { Name = name, GameDirectory = directory, ModCount = modCount, HasOwnFolder = true });
        }

        return result;
    }

    /// <summary>
    /// Profiles of the official launcher (and TLauncher, which keeps the same file) that
    /// point at a game folder of their own. The folder holds the worlds and mods; the
    /// version comes from the shared versions/ folder.
    /// </summary>
    private static IEnumerable<ExternalInstance> ScanLauncherProfiles(string dotMinecraft)
    {
        var result = new List<ExternalInstance>();
        var root = Path.GetFullPath(dotMinecraft).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var file in new[] { "launcher_profiles.json", "TlauncherProfiles.json" })
        {
            var path = Path.Combine(dotMinecraft, file);

            if (!LinkGuard.IsRealFile(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));

                if (!document.RootElement.TryGetProperty("profiles", out var profiles) ||
                    profiles.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var entry in profiles.EnumerateObject())
                {
                    var profile = entry.Value;
                    var gameDirectory = StringOf(profile, "gameDir");

                    if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
                    {
                        continue;
                    }

                    var fullGameDirectory = Path.GetFullPath(gameDirectory!).TrimEnd(Path.DirectorySeparatorChar);

                    if (string.Equals(fullGameDirectory, root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = StringOf(profile, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = Path.GetFileName(fullGameDirectory);
                    }

                    if (LinkGuard.IsLink(fullGameDirectory))
                    {
                        result.Add(RefusedLink(fullGameDirectory, ExternalLauncherKind.DotMinecraft, name) with { HasOwnFolder = true });
                        continue;
                    }

                    var versionId = StringOf(profile, "lastVersionId") ?? string.Empty;
                    var modCount = CountMods(Path.Combine(fullGameDirectory, ModsFolder));

                    // "latest-release" is not a version on disk; the player picks one after import.
                    var versionDirectory = Path.Combine(dotMinecraft, "versions", versionId);

                    var instance = versionId.Length > 0 &&
                                   !versionId.StartsWith("latest-", StringComparison.OrdinalIgnoreCase) &&
                                   versionId.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
                                   LinkGuard.IsRealDirectory(versionDirectory)
                        ? InspectVersionFolder(versionDirectory, versionId, fullGameDirectory, ExternalLauncherKind.DotMinecraft, modCount, gameDirectoryIsOwn: true)
                        : new ExternalInstance(name!, fullGameDirectory, string.Empty, LoaderKind.Vanilla, ExternalLauncherKind.DotMinecraft, null, modCount);

                    result.Add(instance with { Name = name!, GameDirectory = fullGameDirectory, ModCount = modCount, HasOwnFolder = true });
                }
            }
            catch (Exception)
            {
                // A profiles file the launcher cannot read is not a reason to hide the versions.
            }
        }

        return result;
    }

    private static IReadOnlyList<ExternalInstance> ScanInstanceFolders(string root, ExternalLauncherKind kind)
    {
        var result = new List<ExternalInstance>();
        var launcherName = ForkName(root, kind);

        foreach (var directory in AllDirectories(root))
        {
            if (LinkGuard.IsLink(directory))
            {
                // In a launcher's own instance folder a link is a build kept elsewhere,
                // and it is said so. In a folder nobody described it is just a link.
                if (kind != ExternalLauncherKind.Unknown)
                {
                    var refused = RefusedLink(directory, kind);
                    result.Add(launcherName is null ? refused : refused with { LauncherName = launcherName });
                }

                continue;
            }

            var found = InspectInstanceFolder(directory, kind);

            if (found is not null)
            {
                result.Add(launcherName is null ? found : found with { LauncherName = launcherName });
            }
        }

        return Sort(result);
    }

    /// <summary>
    /// The name of the folder an instances folder sits in, when that folder is a launcher
    /// the kind does not already name: "PineconeMC" next to Prism's files. A portable
    /// "PrismLauncher-Windows-MSVC-Portable-9.4" is still just Prism.
    /// </summary>
    private static string? ForkName(string root, ExternalLauncherKind kind)
    {
        if (kind is not (ExternalLauncherKind.Prism or ExternalLauncherKind.PolyMc or ExternalLauncherKind.MultiMc or ExternalLauncherKind.Unknown))
        {
            return null;
        }

        try
        {
            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!string.Equals(Path.GetFileName(trimmed), "instances", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var parent = Path.GetDirectoryName(trimmed);
            var name = Path.GetFileName(parent ?? string.Empty);

            // An instance folder moved elsewhere sits in a folder that is not the launcher;
            // the launcher's own folder is the one with its .cfg in it.
            if (string.IsNullOrWhiteSpace(name) || parent is null || !SafeFiles(parent, "*.cfg").Any())
            {
                return null;
            }

            foreach (var known in new[] { "PrismLauncher", "Prism Launcher", "PolyMC", "MultiMC" })
            {
                if (name.StartsWith(known, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One instance of a launcher that keeps a folder per build. The game directory is a
    /// known subfolder; which one depends on the launcher, so all the usual names are tried.
    /// </summary>
    private static ExternalInstance? InspectInstanceFolder(string directory, ExternalLauncherKind kind)
    {
        var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));

        if (string.IsNullOrEmpty(folderName))
        {
            return null;
        }

        var gameDirectory = GameSubfolders
                                .Select(n => Path.Combine(directory, n))
                                .FirstOrDefault(Directory.Exists)
                            ?? directory;

        var instanceName = ReadPropertiesValue(Path.Combine(directory, "instance.cfg"), "name");

        // The build's files are behind a link: the description may be right here, but
        // what would be copied or played is somewhere else entirely.
        if (LinkGuard.IsLink(gameDirectory))
        {
            return RefusedLink(gameDirectory, kind, instanceName ?? folderName);
        }

        var modCount = CountMods(Path.Combine(gameDirectory, ModsFolder));
        var settings = SourceInstanceSettings.Read(directory);

        // Each launcher keeps its own description file. Whatever is there is read; the
        // kind only decides the label, never what is trusted.
        var described = ReadMmcPack(directory)
                        ?? ReadCurseForgePack(directory)
                        ?? ReadModrinthProfile(directory)
                        ?? ReadGdLauncherNext(directory)
                        ?? ReadInstanceJson(directory)
                        ?? ReadTechnicPack(directory);

        var name = instanceName ?? described?.Name ?? folderName;

        if (described is not null)
        {
            return new ExternalInstance(
                name,
                gameDirectory,
                described.GameVersion,
                described.Loader,
                described.Kind ?? kind,
                VersionJsonPath: null,
                modCount)
            {
                LoaderVersion = described.LoaderVersion,
                Settings = settings
            };
        }

        // Nothing described it: only worth offering when there is actually a game folder.
        var hasSaves = Directory.Exists(Path.Combine(gameDirectory, "saves"));

        if (!hasSaves && modCount == 0)
        {
            return null;
        }

        // The mods themselves say what they are for - this is all the Modrinth App and a
        // hand-made folder leave behind.
        var verdict = modCount > 0 ? ModFolderInspector.Inspect(Path.Combine(gameDirectory, ModsFolder)) : null;

        // And when the mods do not agree on a version - or there are none - the game's
        // own log of its last start in this folder does. The folder's name is never
        // asked here: a pack is called whatever its author liked.
        var log = verdict?.GameVersion is null ? GameVersionDetector.FromLog(gameDirectory) : null;

        return new ExternalInstance(
            name,
            gameDirectory,
            verdict?.GameVersion ?? log?.GameVersion ?? string.Empty,
            verdict?.Loader ?? log?.Loader ?? LoaderKind.Vanilla,
            kind,
            VersionJsonPath: null,
            modCount)
        {
            VersionInferred = verdict is not null || log is not null,
            Settings = settings
        };
    }

    /// <summary>
    /// A single versions/&lt;id&gt; folder. This is where a .minecraft folder gets messy:
    /// half of them are leftovers.
    /// </summary>
    public static ExternalInstance InspectVersionFolder(
        string directory,
        string id,
        string gameDirectory,
        ExternalLauncherKind kind,
        int modCount,
        bool gameDirectoryIsOwn = false)
    {
        var jsonPath = Path.Combine(directory, id + ".json");
        var ownFolder = false;

        // TLauncher and the official launcher can give each version its own game folder,
        // and the usual place for it is the version folder itself: mods, saves and configs
        // sit next to the profile. The shared .minecraft is then not this build's folder
        // at all - it has none of its mods.
        if (IsGameFolder(directory))
        {
            gameDirectory = directory;
            modCount = CountMods(Path.Combine(directory, ModsFolder));
            ownFolder = true;
        }

        if (!File.Exists(jsonPath))
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, null, modCount,
                ExternalInstanceProblem.MissingVersionJson) { HasOwnFolder = ownFolder };
        }

        if (LinkGuard.IsLink(jsonPath))
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, null, modCount,
                ExternalInstanceProblem.SourceIsLink) { HasOwnFolder = ownFolder, LinkTarget = LinkGuard.TargetOf(jsonPath) };
        }

        VersionJson? json;

        try
        {
            json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath), MetadataJson.Options);
        }
        catch (Exception)
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, jsonPath, modCount,
                ExternalInstanceProblem.BrokenVersionJson) { HasOwnFolder = ownFolder };
        }

        if (json is null)
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, jsonPath, modCount,
                ExternalInstanceProblem.BrokenVersionJson) { HasOwnFolder = ownFolder };
        }

        if (string.IsNullOrWhiteSpace(json.MainClass) && string.IsNullOrWhiteSpace(json.InheritsFrom))
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, jsonPath, modCount,
                ExternalInstanceProblem.IncompleteProfile) { HasOwnFolder = ownFolder };
        }

        // What the profile is built on, then - in a folder only this build plays in -
        // what the game logged, and the name last, under a strict reading.
        var gameVersion = GameVersionDetector.FromProfile(json, Path.GetDirectoryName(directory))
                          ?? OwnIdAsVersion(json, id)
                          ?? (ownFolder || gameDirectoryIsOwn ? GameVersionDetector.FromLog(gameDirectory)?.GameVersion : null)
                          ?? GameVersionDetector.FromName(id);

        return new ExternalInstance(id, gameDirectory, id, DetectLoader(json), kind, jsonPath, modCount)
        {
            GameVersion = gameVersion,
            LoaderVersion = DetectLoaderVersion(json),
            HasOwnFolder = ownFolder
        };
    }

    /// <summary>A folder the game has actually been run in, as opposed to a bare profile.</summary>
    private static bool IsGameFolder(string directory)
        => Directory.Exists(Path.Combine(directory, ModsFolder)) ||
           Directory.Exists(Path.Combine(directory, "saves")) ||
           Directory.Exists(Path.Combine(directory, "config")) ||
           File.Exists(Path.Combine(directory, "options.txt"));

    /// <summary>
    /// The Minecraft version behind a profile. The profile's id is a free-form name, so
    /// the version is read from what the profile is built on, most reliable first.
    /// </summary>
    public static string? DetectGameVersion(VersionJson json, string id)
        => GameVersionDetector.FromProfile(json)
           ?? OwnIdAsVersion(json, id)
           ?? GameVersionDetector.FromName(id);

    /// <summary>
    /// A profile that carries the client download is the game itself, and its id is the
    /// version - whatever shape Mojang gives it ("1.21.1", "26.2", "26.3-snapshot-1").
    /// Unless someone named it: TLauncher's "OptiFine 1.16.5" carries the download too,
    /// and there the version is inside the name, not the name.
    /// </summary>
    private static string? OwnIdAsVersion(VersionJson json, string id)
        => json.Downloads?.Client is not null && DetectLoader(json) == LoaderKind.Vanilla && GameVersionDetector.IsGameId(id)
            ? id
            : null;

    public static string? DetectLoaderVersion(VersionJson json)
    {
        foreach (var library in json.Libraries.Select(l => l.Name ?? string.Empty))
        {
            var parts = library.Split(':');

            if (parts.Length >= 3 &&
                ((parts[0] == "net.fabricmc" && parts[1] == "fabric-loader") ||
                 (parts[0] == "org.quiltmc" && parts[1] == "quilt-loader")))
            {
                return parts[2];
            }
        }

        return null;
    }

    /// <summary>
    /// Works out the loader from what the profile actually launches. Used to decide which
    /// mods the catalog offers for the build - not to launch it, which the profile does
    /// on its own.
    /// </summary>
    public static LoaderKind DetectLoader(VersionJson json)
    {
        var mainClass = json.MainClass ?? string.Empty;
        var libraries = json.Libraries.Select(l => l.Name ?? string.Empty).ToList();

        if (mainClass.Contains("quiltmc", StringComparison.OrdinalIgnoreCase) ||
            libraries.Any(l => l.StartsWith("org.quiltmc:", StringComparison.OrdinalIgnoreCase)))
        {
            return LoaderKind.Quilt;
        }

        if (mainClass.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) ||
            libraries.Any(l => l.StartsWith("net.fabricmc:fabric-loader", StringComparison.OrdinalIgnoreCase)))
        {
            return LoaderKind.Fabric;
        }

        if (libraries.Any(l => l.StartsWith("net.neoforged:", StringComparison.OrdinalIgnoreCase)))
        {
            return LoaderKind.NeoForge;
        }

        if (mainClass.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ||
            mainClass.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase) ||
            libraries.Any(l => l.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase)))
        {
            return LoaderKind.Forge;
        }

        // OptiFine and other launchwrapper profiles run vanilla with a tweaker: no loader
        // as far as the mod catalog is concerned.
        return LoaderKind.Vanilla;
    }

    // ===================== Per-launcher description files =====================

    /// <summary>What a launcher's own description of a build boils down to.</summary>
    private sealed record Described(
        string GameVersion,
        LoaderKind Loader,
        string? LoaderVersion = null,
        string? Name = null,
        ExternalLauncherKind? Kind = null);

    /// <summary>Prism, PolyMC and MultiMC: components list the loader next to the game version.</summary>
    private static Described? ReadMmcPack(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "mmc-pack.json");

        if (!LinkGuard.IsRealFile(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("components", out var components))
            {
                return null;
            }

            var version = string.Empty;
            var loader = LoaderKind.Vanilla;
            string? loaderVersion = null;

            foreach (var component in components.EnumerateArray())
            {
                var uid = StringOf(component, "uid") ?? string.Empty;
                // "version" is what the player pinned; without a pin the launcher still
                // records the one it resolved, which is the build that was actually played.
                var componentVersion = StringOf(component, "version") ?? StringOf(component, "cachedVersion");

                switch (uid)
                {
                    case "net.minecraft":
                        version = componentVersion ?? string.Empty;
                        break;
                    case "net.fabricmc.fabric-loader":
                        (loader, loaderVersion) = (LoaderKind.Fabric, componentVersion);
                        break;
                    case "org.quiltmc.quilt-loader":
                        (loader, loaderVersion) = (LoaderKind.Quilt, componentVersion);
                        break;
                    case "net.minecraftforge":
                        (loader, loaderVersion) = (LoaderKind.Forge, componentVersion);
                        break;
                    case "net.neoforged":
                        (loader, loaderVersion) = (LoaderKind.NeoForge, componentVersion);
                        break;
                }
            }

            return string.IsNullOrEmpty(version) ? null : new Described(version, loader, loaderVersion);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// CurseForge: baseModLoader names the loader, gameVersion the game. A vanilla
    /// instance has baseModLoader set to null, which used to end the parse - and the
    /// instance with it.
    /// </summary>
    private static Described? ReadCurseForgePack(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "minecraftinstance.json");

        if (!LinkGuard.IsRealFile(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var version = StringOf(root, "gameVersion");
            var loader = LoaderKind.Vanilla;
            string? loaderVersion = null;

            if (root.TryGetProperty("baseModLoader", out var loaderNode) && loaderNode.ValueKind == JsonValueKind.Object)
            {
                version ??= StringOf(loaderNode, "minecraftVersion");
                var loaderName = StringOf(loaderNode, "name") ?? string.Empty;
                loader = LoaderFromName(loaderName);

                // "forge-47.2.0", "fabric-0.15.11", "neoforge-21.1.0"
                var dash = loaderName.IndexOf('-');
                loaderVersion = dash > 0 ? loaderName[(dash + 1)..] : StringOf(loaderNode, "forgeVersion");
            }

            return string.IsNullOrEmpty(version)
                ? null
                : new Described(version!, loader, loaderVersion, StringOf(root, "name"), ExternalLauncherKind.CurseForge);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The Modrinth App up to 2024 wrote profile.json; later versions keep the profile
    /// in a database and are handled by looking at the mods instead.
    /// </summary>
    private static Described? ReadModrinthProfile(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "profile.json");

        if (!LinkGuard.IsRealFile(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("metadata", out var metadata))
            {
                return null;
            }

            var version = StringOf(metadata, "game_version");
            var loader = StringOf(metadata, "loader") ?? string.Empty;

            return string.IsNullOrEmpty(version)
                ? null
                : new Described(version!, LoaderFromName(loader), StringOf(metadata, "loader_version"), StringOf(metadata, "name"), ExternalLauncherKind.Modrinth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>GDLauncher (the Electron one): config.json with a "loader" block.</summary>
    private static Described? ReadGdLauncherNext(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "config.json");

        if (!LinkGuard.IsRealFile(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var version = StringOf(loader, "mcVersion");

            return string.IsNullOrEmpty(version)
                ? null
                : new Described(version!, LoaderFromName(StringOf(loader, "loaderType") ?? string.Empty), StringOf(loader, "loaderVersion"), Kind: ExternalLauncherKind.GdLauncher);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// instance.json is the file name four launchers chose, each with its own shape:
    /// ATLauncher, the FTB App, XMCL and GDLauncher Carbon. Told apart by the keys.
    /// </summary>
    private static Described? ReadInstanceJson(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "instance.json");

        if (!LinkGuard.IsRealFile(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            // ATLauncher: { "id": "1.20.1", "launcher": { "name": ..., "loaderVersion": { "type": "Fabric", "version": ... } } }
            if (root.TryGetProperty("launcher", out var launcher) && launcher.ValueKind == JsonValueKind.Object)
            {
                var version = StringOf(root, "id");
                var loader = LoaderKind.Vanilla;
                string? loaderVersion = null;

                if (launcher.TryGetProperty("loaderVersion", out var lv) && lv.ValueKind == JsonValueKind.Object)
                {
                    loader = LoaderFromName(StringOf(lv, "type") ?? string.Empty);
                    loaderVersion = StringOf(lv, "version");
                }

                return string.IsNullOrEmpty(version)
                    ? null
                    : new Described(version!, loader, loaderVersion, StringOf(launcher, "name"), ExternalLauncherKind.AtLauncher);
            }

            // FTB App: { "mcVersion": "1.20.1", "modLoader": "forge-47.2.0", "name": ... }
            if (StringOf(root, "mcVersion") is { Length: > 0 } mcVersion)
            {
                var modLoader = StringOf(root, "modLoader") ?? string.Empty;
                var dash = modLoader.IndexOf('-');

                return new Described(
                    mcVersion,
                    LoaderFromName(modLoader),
                    dash > 0 ? modLoader[(dash + 1)..] : null,
                    StringOf(root, "name"),
                    ExternalLauncherKind.Ftb);
            }

            // XMCL: { "runtime": { "minecraft": "1.20.1", "fabricLoader": "0.15.0", ... } }
            if (root.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object &&
                StringOf(runtime, "minecraft") is { Length: > 0 } xmclVersion)
            {
                var loader = LoaderKind.Vanilla;
                string? loaderVersion = null;

                foreach (var (key, kind) in new[]
                         {
                             ("fabricLoader", LoaderKind.Fabric),
                             ("quiltLoader", LoaderKind.Quilt),
                             ("neoForged", LoaderKind.NeoForge),
                             ("forge", LoaderKind.Forge)
                         })
                {
                    if (StringOf(runtime, key) is { Length: > 0 } found)
                    {
                        (loader, loaderVersion) = (kind, found);
                        break;
                    }
                }

                return new Described(xmclVersion, loader, loaderVersion, StringOf(root, "name"), ExternalLauncherKind.Xmcl);
            }

            // GDLauncher Carbon: { "game_configuration": { "version": { "Standard": { "release": "1.20.1", "modloaders": [ { "type_": "fabric", "version": ... } ] } } } }
            if (root.TryGetProperty("game_configuration", out var game) &&
                game.TryGetProperty("version", out var versionNode) &&
                versionNode.TryGetProperty("Standard", out var standard) &&
                StringOf(standard, "release") is { Length: > 0 } release)
            {
                var loader = LoaderKind.Vanilla;
                string? loaderVersion = null;

                if (standard.TryGetProperty("modloaders", out var modloaders) && modloaders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in modloaders.EnumerateArray())
                    {
                        loader = LoaderFromName(StringOf(entry, "type_") ?? string.Empty);
                        loaderVersion = StringOf(entry, "version");
                        break;
                    }
                }

                return new Described(release, loader, loaderVersion, StringOf(root, "name"), ExternalLauncherKind.GdLauncher);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Technic: the pack's bin/version.json is a full launch profile.</summary>
    private static Described? ReadTechnicPack(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "bin", "version.json");

        if (!LinkGuard.IsRealDirectory(Path.Combine(instanceDirectory, "bin")) || !LinkGuard.IsRealFile(path))
        {
            return null;
        }

        try
        {
            var json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(path), MetadataJson.Options);

            if (json is null)
            {
                return null;
            }

            var version = DetectGameVersion(json, json.Id ?? string.Empty);

            return version is null
                ? null
                : new Described(version, DetectLoader(json), DetectLoaderVersion(json), Kind: ExternalLauncherKind.Technic);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static LoaderKind LoaderFromName(string name)
    {
        if (name.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return LoaderKind.NeoForge;
        if (name.Contains("forge", StringComparison.OrdinalIgnoreCase)) return LoaderKind.Forge;
        if (name.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return LoaderKind.Fabric;
        if (name.Contains("quilt", StringComparison.OrdinalIgnoreCase)) return LoaderKind.Quilt;
        return LoaderKind.Vanilla;
    }

    private static int CountMods(string modsDirectory)
    {
        try
        {
            return LinkGuard.IsRealDirectory(modsDirectory)
                ? Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Real subfolders of a real folder: links are neither entered nor returned.</summary>
    private static IEnumerable<string> SafeDirectories(string root)
        => LinkGuard.IsRealDirectory(root) ? LinkGuard.RealDirectories(root) : Array.Empty<string>();

    /// <summary>Subfolders of a real folder, links included, for places where a link is worth reporting.</summary>
    private static IEnumerable<string> AllDirectories(string root)
    {
        try
        {
            return LinkGuard.IsRealDirectory(root) ? Directory.EnumerateDirectories(root).ToList() : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<ExternalInstance> Sort(IEnumerable<ExternalInstance> found)
        => found
            .OrderByDescending(i => i.IsUsable)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The same folder can be reached through two launchers; offer it once.</summary>
    private static IReadOnlyList<ExternalInstance> Deduplicate(IEnumerable<ExternalInstance> found)
        => found
            .GroupBy(i => (i.GameDirectory.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant(),
                           i.VersionId.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
}
