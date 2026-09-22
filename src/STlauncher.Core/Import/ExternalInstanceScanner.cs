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
        ("ATLauncher", ExternalLauncherKind.AtLauncher, null)
    };

    /// <summary>Places worth looking in, in the order they are offered.</summary>
    public static IReadOnlyList<(string Path, ExternalLauncherKind Kind)> DefaultRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var roots = new List<(string, ExternalLauncherKind)>
        {
            (Path.Combine(appData, ".minecraft"), ExternalLauncherKind.DotMinecraft),
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
        var tlauncherDirectory = ReadPropertiesValue(Path.Combine(appData, ".tlauncher", "tlauncher-2.0.properties"), "minecraft.gamedir");
        if (!string.IsNullOrWhiteSpace(tlauncherDirectory))
        {
            roots.Add((tlauncherDirectory!, ExternalLauncherKind.DotMinecraft));
        }

        // Prism and PolyMC let the player move the instance folder; the setting wins.
        AddConfiguredInstanceDirectory(roots, Path.Combine(appData, "PrismLauncher", "prismlauncher.cfg"), ExternalLauncherKind.Prism);
        AddConfiguredInstanceDirectory(roots, Path.Combine(appData, "PolyMC", "polymc.cfg"), ExternalLauncherKind.PolyMc);

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
        }

        return roots
            .GroupBy(r => r.Item1.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
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
    {
        var result = new List<ExternalInstance>();

        foreach (var (path, kind) in roots ?? DefaultRoots())
        {
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

        if (Directory.Exists(Path.Combine(path, "versions")))
        {
            return ScanDotMinecraft(path);
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

    private static IReadOnlyList<ExternalInstance> ScanDotMinecraft(string dotMinecraft)
    {
        var versions = Path.Combine(dotMinecraft, "versions");
        var result = new List<ExternalInstance>();

        if (Directory.Exists(versions))
        {
            var modCount = CountMods(Path.Combine(dotMinecraft, ModsFolder));

            foreach (var directory in SafeDirectories(versions))
            {
                var id = Path.GetFileName(directory);

                if (!string.IsNullOrEmpty(id))
                {
                    result.Add(InspectVersionFolder(directory, id, dotMinecraft, ExternalLauncherKind.DotMinecraft, modCount));
                }
            }
        }

        result.AddRange(ScanLauncherProfiles(dotMinecraft));

        return Sort(result);
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

            if (!File.Exists(path))
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

                    var versionId = StringOf(profile, "lastVersionId") ?? string.Empty;
                    var modCount = CountMods(Path.Combine(fullGameDirectory, ModsFolder));

                    // "latest-release" is not a version on disk; the player picks one after import.
                    var versionDirectory = Path.Combine(dotMinecraft, "versions", versionId);

                    var instance = versionId.Length > 0 &&
                                   !versionId.StartsWith("latest-", StringComparison.OrdinalIgnoreCase) &&
                                   Directory.Exists(versionDirectory)
                        ? InspectVersionFolder(versionDirectory, versionId, fullGameDirectory, ExternalLauncherKind.DotMinecraft, modCount)
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

        foreach (var directory in SafeDirectories(root))
        {
            var found = InspectInstanceFolder(directory, kind);

            if (found is not null)
            {
                result.Add(found);
            }
        }

        return Sort(result);
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

        var modCount = CountMods(Path.Combine(gameDirectory, ModsFolder));

        // Each launcher keeps its own description file. Whatever is there is read; the
        // kind only decides the label, never what is trusted.
        var described = ReadMmcPack(directory)
                        ?? ReadCurseForgePack(directory)
                        ?? ReadModrinthProfile(directory)
                        ?? ReadGdLauncherNext(directory)
                        ?? ReadInstanceJson(directory)
                        ?? ReadTechnicPack(directory);

        var name = ReadPropertiesValue(Path.Combine(directory, "instance.cfg"), "name") ?? described?.Name ?? folderName;

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
                LoaderVersion = described.LoaderVersion
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

        return new ExternalInstance(
            name,
            gameDirectory,
            verdict?.GameVersion ?? string.Empty,
            verdict?.Loader ?? LoaderKind.Vanilla,
            kind,
            VersionJsonPath: null,
            modCount)
        {
            VersionInferred = verdict is not null
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
        int modCount)
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

        return new ExternalInstance(id, gameDirectory, id, DetectLoader(json), kind, jsonPath, modCount)
        {
            GameVersion = DetectGameVersion(json, id),
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
    {
        if (!string.IsNullOrWhiteSpace(json.InheritsFrom))
        {
            return json.InheritsFrom;
        }

        foreach (var library in json.Libraries.Select(l => l.Name ?? string.Empty))
        {
            var parts = library.Split(':');

            if (parts.Length < 3)
            {
                continue;
            }

            // Fabric and Quilt both map the game through intermediary, named by version.
            if (parts[0] == "net.fabricmc" && parts[1] == "intermediary")
            {
                return parts[2];
            }

            // Forge: "1.20.1-47.2.0".
            if (parts[0] == "net.minecraftforge" && parts[1] == "forge")
            {
                return parts[2].Split('-')[0];
            }
        }

        // A profile that carries the client download is the game itself, and its id is
        // the version - whatever shape Mojang gives it ("1.21.1", "26.2", "26.3-snapshot-1").
        if (json.Downloads?.Client is not null && DetectLoader(json) == LoaderKind.Vanilla)
        {
            return id;
        }

        // Otherwise the version is somewhere in the name - unless someone renamed it
        // completely. Both the old "1.x" and the year-based "26.x" numbering count.
        var match = Regex.Match(id, @"(?<![\d.])(1\.\d{1,2}(\.\d{1,2})?|2\d\.\d{1,2}(\.\d{1,2})?)(?![\d.])");

        return match.Success ? match.Value : null;
    }

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

        if (!File.Exists(path))
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
                var componentVersion = StringOf(component, "version");

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

        if (!File.Exists(path))
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

        if (!File.Exists(path))
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

        if (!File.Exists(path))
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

        if (!File.Exists(path))
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

        if (!File.Exists(path))
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
            return Directory.Exists(modsDirectory)
                ? Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
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
