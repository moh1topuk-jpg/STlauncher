using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Modpacks;

public static class ModpackReader
{
    public const string ModrinthIndexFileName = "modrinth.index.json";
    public const string CurseForgeManifestFileName = "manifest.json";

    public static ModpackPlan Read(string modpackPath)
    {
        using var archive = ZipFile.OpenRead(modpackPath);
        return Read(archive);
    }

    public static ModpackPlan Read(ZipArchive archive)
    {
        if (archive.GetEntry(ModrinthIndexFileName) is not null)
        {
            return ReadModrinth(archive);
        }

        if (archive.GetEntry(CurseForgeManifestFileName) is not null)
        {
            return ReadCurseForge(archive);
        }

        throw new InvalidDataException(
            "Unsupported modpack: neither modrinth.index.json nor manifest.json was found.");
    }

    public static ModpackFormat DetectFormat(ZipArchive archive)
    {
        if (archive.GetEntry(ModrinthIndexFileName) is not null)
        {
            return ModpackFormat.Modrinth;
        }

        if (archive.GetEntry(CurseForgeManifestFileName) is not null)
        {
            return ModpackFormat.CurseForge;
        }

        throw new InvalidDataException("Unsupported modpack format.");
    }

    public static ModpackPlan ReadModrinth(ZipArchive archive)
    {
        var index = Deserialize<ModpackIndex>(archive, ModrinthIndexFileName);

        var (loader, loaderVersion) = DetectLoader(index.Dependencies);
        index.Dependencies.TryGetValue("minecraft", out var gameVersion);

        var files = new List<ModpackFilePlan>();

        foreach (var file in index.Files)
        {
            if (string.Equals(file.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsSafeRelativePath(file.Path))
            {
                throw new InvalidDataException($"Modpack file path escapes the instance directory: {file.Path}");
            }

            var url = file.Downloads.FirstOrDefault();
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            file.Hashes.TryGetValue("sha1", out var sha1);
            file.Hashes.TryGetValue("sha512", out var sha512);
            files.Add(new ModpackFilePlan(file.Path, url, sha1, sha512, file.FileSize));
        }

        var hasOverrides = HasAnyPrefix(archive, OverridePrefixes);

        return new ModpackPlan(
            ModpackFormat.Modrinth,
            index.Name ?? "Modpack",
            index.VersionId ?? "1.0.0",
            gameVersion,
            loader,
            loaderVersion,
            files,
            hasOverrides);
    }

    public static ModpackPlan ReadCurseForge(ZipArchive archive)
    {
        var manifest = Deserialize<CurseForgeManifest>(archive, CurseForgeManifestFileName);

        var (loader, loaderVersion) = DetectCurseForgeLoader(manifest.Minecraft?.ModLoaders);
        var overridesFolder = string.IsNullOrWhiteSpace(manifest.Overrides) ? "overrides" : manifest.Overrides!;
        var prefix = overridesFolder.TrimEnd('/') + "/";

        var files = new List<ModpackFilePlan>();

        foreach (var file in manifest.Files)
        {
            if (file.FileId <= 0)
            {
                continue;
            }

            files.Add(new ModpackFilePlan(
                string.Empty,
                null,
                null,
                null,
                0,
                file.ProjectId,
                file.FileId,
                file.Required));
        }

        return new ModpackPlan(
            ModpackFormat.CurseForge,
            manifest.Name ?? "Modpack",
            manifest.Version ?? "1.0.0",
            manifest.Minecraft?.Version,
            loader,
            loaderVersion,
            files,
            HasAnyPrefix(archive, new[] { prefix }),
            overridesFolder);
    }

    public static (LoaderKind Loader, string? Version) DetectLoader(
        IReadOnlyDictionary<string, string> dependencies)
    {
        if (dependencies.TryGetValue("fabric-loader", out var fabric)) return (LoaderKind.Fabric, fabric);
        if (dependencies.TryGetValue("quilt-loader", out var quilt)) return (LoaderKind.Quilt, quilt);
        if (dependencies.TryGetValue("neoforge", out var neo)) return (LoaderKind.NeoForge, neo);
        if (dependencies.TryGetValue("forge", out var forge)) return (LoaderKind.Forge, forge);

        return (LoaderKind.Vanilla, null);
    }

    /// <summary>
    /// CurseForge stores loaders as "forge-47.2.0", "fabric-0.15.0", "neoforge-20.4.237", "quilt-0.23.0".
    /// </summary>
    public static (LoaderKind Loader, string? Version) DetectCurseForgeLoader(
        IReadOnlyList<ModLoaderRef>? modLoaders)
    {
        if (modLoaders is null || modLoaders.Count == 0)
        {
            return (LoaderKind.Vanilla, null);
        }

        var primary = modLoaders.FirstOrDefault(l => l.Primary) ?? modLoaders[0];
        var id = primary.Id;

        if (string.IsNullOrWhiteSpace(id))
        {
            return (LoaderKind.Vanilla, null);
        }

        var separator = id.IndexOf('-');
        var name = separator > 0 ? id[..separator] : id;
        var version = separator > 0 ? id[(separator + 1)..] : null;

        var loader = name.ToLowerInvariant() switch
        {
            "fabric" => LoaderKind.Fabric,
            "quilt" => LoaderKind.Quilt,
            "neoforge" => LoaderKind.NeoForge,
            "forge" => LoaderKind.Forge,
            _ => LoaderKind.Vanilla
        };

        return (loader, version);
    }

    public static bool IsSafeRelativePath(string path) => RelativePath.IsSafe(path);

    private static readonly string[] OverridePrefixes = { "overrides/", "client-overrides/" };

    public static string[] OverrideFolderPrefixes(ModpackPlan plan)
        => plan.Format == ModpackFormat.Modrinth
            ? OverridePrefixes
            : new[] { plan.OverridesFolder.TrimEnd('/') + "/" };

    private static bool HasAnyPrefix(ZipArchive archive, IEnumerable<string> prefixes)
    {
        var list = prefixes.ToList();
        return archive.Entries.Any(e =>
            list.Any(p => e.FullName.StartsWith(p, StringComparison.Ordinal)));
    }

    private static T Deserialize<T>(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
                    ?? throw new InvalidDataException($"'{entryName}' was not found in the modpack.");

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, Json.Options)
               ?? throw new InvalidDataException($"{entryName} is empty.");
    }

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    }

    private sealed class ModpackIndex
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("versionId")]
        public string? VersionId { get; set; }

        [JsonPropertyName("files")]
        public List<ModpackIndexFile> Files { get; set; } = new();

        [JsonPropertyName("dependencies")]
        public Dictionary<string, string> Dependencies { get; set; } = new();
    }

    private sealed class ModpackIndexFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("hashes")]
        public Dictionary<string, string> Hashes { get; set; } = new();

        [JsonPropertyName("env")]
        public ModpackEnvironment? Env { get; set; }

        [JsonPropertyName("downloads")]
        public List<string> Downloads { get; set; } = new();

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }
    }

    private sealed class ModpackEnvironment
    {
        [JsonPropertyName("client")]
        public string? Client { get; set; }

        [JsonPropertyName("server")]
        public string? Server { get; set; }
    }

    private sealed class CurseForgeManifest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("manifestType")]
        public string? ManifestType { get; set; }

        [JsonPropertyName("minecraft")]
        public CurseForgeMinecraft? Minecraft { get; set; }

        [JsonPropertyName("files")]
        public List<CurseForgeManifestFile> Files { get; set; } = new();

        [JsonPropertyName("overrides")]
        public string? Overrides { get; set; }
    }

    private sealed class CurseForgeMinecraft
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("modLoaders")]
        public List<ModLoaderRef>? ModLoaders { get; set; }
    }

    private sealed class CurseForgeManifestFile
    {
        [JsonPropertyName("projectID")]
        public int ProjectId { get; set; }

        [JsonPropertyName("fileID")]
        public int FileId { get; set; }

        [JsonPropertyName("required")]
        public bool Required { get; set; } = true;
    }
}

public sealed class ModLoaderRef
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}