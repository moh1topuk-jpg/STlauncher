using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Modpacks;

/// <summary>A file the pack names by its download address rather than carrying.</summary>
public sealed record ModpackExportFile(string Path, string Url, string Sha1, string Sha512, long Size);

/// <summary>A file the pack carries inside itself, under overrides/.</summary>
public sealed record ModpackOverride(string Path, string SourcePath);

/// <summary>
/// Writes a Modrinth modpack (.mrpack): an index of files with their download addresses
/// and hashes, plus an overrides folder for everything that has no address. The same
/// format the importer reads, so a build leaves this launcher the way it came in.
/// </summary>
public static class ModpackWriter
{
    /// <summary>
    /// The only hosts the format lets an index point at. CurseForge's CDN is not one of
    /// them, and other launchers refuse a pack that names it - so a file installed from
    /// CurseForge and not found on Modrinth travels inside the pack, under overrides.
    /// </summary>
    private static readonly string[] AllowedHosts =
    {
        "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"
    };

    public static bool IsAllowedDownload(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    public static void Write(
        string targetPath,
        string name,
        string versionId,
        string gameVersion,
        LoaderKind loader,
        string? loaderVersion,
        IReadOnlyList<ModpackExportFile> files,
        IReadOnlyList<ModpackOverride> overrides)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("A target path is required.", nameof(targetPath));
        }

        var dependencies = new Dictionary<string, string> { ["minecraft"] = gameVersion };
        var loaderKey = loader switch
        {
            LoaderKind.Fabric => "fabric-loader",
            LoaderKind.Quilt => "quilt-loader",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            _ => null
        };

        if (loaderKey is not null && !string.IsNullOrWhiteSpace(loaderVersion))
        {
            dependencies[loaderKey] = loaderVersion!;
        }

        var index = new Dictionary<string, object?>
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = versionId,
            ["name"] = name,
            ["files"] = files.Select(f => new Dictionary<string, object?>
            {
                ["path"] = f.Path.Replace('\\', '/'),
                ["hashes"] = new Dictionary<string, string> { ["sha1"] = f.Sha1, ["sha512"] = f.Sha512 },
                ["env"] = new Dictionary<string, string> { ["client"] = "required", ["server"] = "optional" },
                ["downloads"] = new[] { f.Url },
                ["fileSize"] = f.Size
            }).ToList(),
            ["dependencies"] = dependencies
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written next to the target and moved into place, so a failed export never
        // leaves half a pack under the name the player chose.
        var temporary = targetPath + ".tmp";

        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }

        using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(ModpackReader.IndexFileName, CompressionLevel.Optimal);

            using (var stream = entry.Open())
            {
                JsonSerializer.Serialize(stream, index, new JsonSerializerOptions { WriteIndented = true });
            }

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in overrides)
            {
                var path = "overrides/" + item.Path.Replace('\\', '/').TrimStart('/');

                if (!File.Exists(item.SourcePath) || !written.Add(path))
                {
                    continue;
                }

                try
                {
                    zip.CreateEntryFromFile(item.SourcePath, path, CompressionLevel.Optimal);
                }
                catch (IOException)
                {
                    // A file the game holds open is left out rather than failing the pack.
                }
            }
        }

        File.Move(temporary, targetPath, overwrite: true);
    }
}
