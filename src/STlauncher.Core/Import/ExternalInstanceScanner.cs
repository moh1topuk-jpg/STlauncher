using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// </remarks>
public static class ExternalInstanceScanner
{
    private const string ModsFolder = "mods";

    /// <summary>Places worth looking in, in the order they are offered.</summary>
    public static IReadOnlyList<(string Path, ExternalLauncherKind Kind)> DefaultRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new[]
        {
            (Path.Combine(appData, ".minecraft"), ExternalLauncherKind.DotMinecraft),
            (Path.Combine(appData, "PrismLauncher", "instances"), ExternalLauncherKind.Prism),
            (Path.Combine(profile, "MultiMC", "instances"), ExternalLauncherKind.MultiMc),
            (Path.Combine(profile, "curseforge", "minecraft", "Instances"), ExternalLauncherKind.CurseForge),
            (Path.Combine(appData, "com.modrinth.theseus", "profiles"), ExternalLauncherKind.Modrinth),
            (Path.Combine(appData, "gdlauncher_next", "instances"), ExternalLauncherKind.GdLauncher),
            (Path.Combine(profile, "ATLauncher", "instances"), ExternalLauncherKind.AtLauncher)
        };
    }

    /// <summary>Scans every known location. Missing ones are simply skipped.</summary>
    public static IReadOnlyList<ExternalInstance> ScanAll(IEnumerable<(string Path, ExternalLauncherKind Kind)>? roots = null)
    {
        var result = new List<ExternalInstance>();

        foreach (var (path, kind) in roots ?? DefaultRoots())
        {
            result.AddRange(Scan(path, kind));
        }

        return Deduplicate(result);
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
    /// Looks at a folder the player pointed at, working out what it is. Accepts both a
    /// .minecraft-style folder and a single instance directory.
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

        // A folder of instances, or one instance: try both and keep whichever found more.
        var asInstances = ScanInstanceFolders(path, ExternalLauncherKind.Unknown);
        var asSingle = InspectInstanceFolder(path, ExternalLauncherKind.Unknown);

        if (asInstances.Count > 0)
        {
            return asInstances;
        }

        return asSingle is null ? Array.Empty<ExternalInstance>() : new[] { asSingle };
    }

    private static IReadOnlyList<ExternalInstance> ScanDotMinecraft(string dotMinecraft)
    {
        var versions = Path.Combine(dotMinecraft, "versions");

        if (!Directory.Exists(versions))
        {
            return Array.Empty<ExternalInstance>();
        }

        var modCount = CountMods(Path.Combine(dotMinecraft, ModsFolder));
        var result = new List<ExternalInstance>();

        foreach (var directory in SafeDirectories(versions))
        {
            var id = Path.GetFileName(directory);

            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            result.Add(InspectVersionFolder(directory, id, dotMinecraft, ExternalLauncherKind.DotMinecraft, modCount));
        }

        return result
            .OrderByDescending(i => i.IsUsable)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
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

        return result
            .OrderByDescending(i => i.IsUsable)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One instance of a launcher that keeps a folder per build. The game directory is a
    /// known subfolder; which one depends on the launcher, so all the usual names are tried.
    /// </summary>
    private static ExternalInstance? InspectInstanceFolder(string directory, ExternalLauncherKind kind)
    {
        var name = Path.GetFileName(directory);

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var gameDirectory = new[] { ".minecraft", "minecraft", "profile" }
                                .Select(n => Path.Combine(directory, n))
                                .FirstOrDefault(Directory.Exists)
                            ?? directory;

        var modCount = CountMods(Path.Combine(gameDirectory, ModsFolder));

        // Prism and MultiMC describe the build in mmc-pack.json; the others keep their own
        // file. Whatever it is, the version folder inside the instance is the fallback.
        var described = ReadMmcPack(directory) ?? ReadCurseForgePack(directory) ?? ReadModrinthProfile(directory);

        if (described is not null)
        {
            return new ExternalInstance(
                name,
                gameDirectory,
                described.Value.VersionId,
                described.Value.Loader,
                kind,
                VersionJsonPath: null,
                modCount);
        }

        // Nothing described it: only worth offering when there is actually a game folder.
        if (!Directory.Exists(Path.Combine(gameDirectory, "saves")) && modCount == 0)
        {
            return null;
        }

        return new ExternalInstance(
            name,
            gameDirectory,
            VersionId: string.Empty,
            LoaderKind.Vanilla,
            kind,
            VersionJsonPath: null,
            modCount,
            ExternalInstanceProblem.IncompleteProfile);
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

        if (!File.Exists(jsonPath))
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, null, modCount,
                ExternalInstanceProblem.MissingVersionJson);
        }

        VersionJson? json;

        try
        {
            json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath), MetadataJson.Options);
        }
        catch (Exception)
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, jsonPath, modCount,
                ExternalInstanceProblem.BrokenVersionJson);
        }

        if (json is null)
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, jsonPath, modCount,
                ExternalInstanceProblem.BrokenVersionJson);
        }

        if (string.IsNullOrWhiteSpace(json.MainClass) && string.IsNullOrWhiteSpace(json.InheritsFrom))
        {
            return new ExternalInstance(id, gameDirectory, id, LoaderKind.Vanilla, kind, jsonPath, modCount,
                ExternalInstanceProblem.IncompleteProfile);
        }

        return new ExternalInstance(id, gameDirectory, id, DetectLoader(json), kind, jsonPath, modCount);
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

    /// <summary>Prism and MultiMC: components list the loader next to the game version.</summary>
    private static (string VersionId, LoaderKind Loader)? ReadMmcPack(string instanceDirectory)
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

            foreach (var component in components.EnumerateArray())
            {
                var uid = component.TryGetProperty("uid", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                var componentVersion = component.TryGetProperty("version", out var v) ? v.GetString() : null;

                switch (uid)
                {
                    case "net.minecraft":
                        version = componentVersion ?? string.Empty;
                        break;
                    case "net.fabricmc.fabric-loader":
                        loader = LoaderKind.Fabric;
                        break;
                    case "org.quiltmc.quilt-loader":
                        loader = LoaderKind.Quilt;
                        break;
                    case "net.minecraftforge":
                        loader = LoaderKind.Forge;
                        break;
                    case "net.neoforged":
                        loader = LoaderKind.NeoForge;
                        break;
                }
            }

            return string.IsNullOrEmpty(version) ? null : (version, loader);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string VersionId, LoaderKind Loader)? ReadCurseForgePack(string instanceDirectory)
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

            if (!root.TryGetProperty("baseModLoader", out var loaderNode))
            {
                return null;
            }

            var version = loaderNode.TryGetProperty("minecraftVersion", out var v) ? v.GetString() : null;
            var name = loaderNode.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;

            return string.IsNullOrEmpty(version) ? null : (version!, LoaderFromName(name));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string VersionId, LoaderKind Loader)? ReadModrinthProfile(string instanceDirectory)
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

            var version = metadata.TryGetProperty("game_version", out var v) ? v.GetString() : null;
            var loader = metadata.TryGetProperty("loader", out var l) ? l.GetString() ?? string.Empty : string.Empty;

            return string.IsNullOrEmpty(version) ? null : (version!, LoaderFromName(loader));
        }
        catch (Exception)
        {
            return null;
        }
    }

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

    /// <summary>The same folder can be reached through two launchers; offer it once.</summary>
    private static IReadOnlyList<ExternalInstance> Deduplicate(IEnumerable<ExternalInstance> found)
        => found
            .GroupBy(i => (i.GameDirectory.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant(),
                           i.VersionId.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
}
