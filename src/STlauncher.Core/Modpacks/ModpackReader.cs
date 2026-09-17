using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Modpacks;

/// <summary>
/// Reads Modrinth modpacks (.mrpack). The index already contains direct download URLs,
/// so no third-party API or key is involved.
/// </summary>
public static class ModpackReader
{
    public const string IndexFileName = "modrinth.index.json";

    private static readonly string[] OverridePrefixes = { "overrides/", "client-overrides/" };

    public static ModpackPlan Read(string modpackPath)
    {
        using var archive = ZipFile.OpenRead(modpackPath);
        return Read(archive);
    }

    public static ModpackPlan Read(ZipArchive archive)
    {
        var entry = archive.GetEntry(IndexFileName)
                    ?? throw new InvalidDataException(
                        $"'{IndexFileName}' was not found. Only Modrinth modpacks are supported.");

        using var stream = entry.Open();
        var index = JsonSerializer.Deserialize<ModpackIndex>(stream, Json.Options)
                    ?? throw new InvalidDataException("Modpack index is empty.");

        var (loader, loaderVersion) = DetectLoader(index.Dependencies);
        index.Dependencies.TryGetValue("minecraft", out var gameVersion);

        var files = new List<ModpackFilePlan>();

        foreach (var file in index.Files)
        {
            if (string.Equals(file.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!RelativePath.IsSafe(file.Path))
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

        var hasOverrides = archive.Entries.Any(e =>
            OverridePrefixes.Any(p => e.FullName.StartsWith(p, StringComparison.Ordinal)));

        return new ModpackPlan(
            index.Name ?? "Modpack",
            index.VersionId ?? "1.0.0",
            gameVersion,
            loader,
            loaderVersion,
            files,
            hasOverrides);
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

    public static IReadOnlyList<string> OverrideFolderPrefixes() => OverridePrefixes;

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
}