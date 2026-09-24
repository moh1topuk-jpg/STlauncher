using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;

namespace STlauncher.Core.Mods;

/// <param name="Folder">"mods", "resourcepacks" or "shaderpacks": where the file lives.</param>
public sealed record InstalledMod(string FileName, string DisplayName, string Path, bool Enabled, long Size, string Folder = ModManager.ModsFolderName);

public sealed class ModManager
{
    public const string ModsFolderName = "mods";
    private const string DisabledSuffix = ".disabled";

    private readonly DownloadClient _downloader;

    public ModManager(DownloadClient downloader)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    }

    public static string ModsDirectory(string gameDirectory)
        => Path.Combine(gameDirectory, ModsFolderName);

    public IReadOnlyList<InstalledMod> ListMods(string gameDirectory)
    {
        var directory = ModsDirectory(gameDirectory);

        if (!Directory.Exists(directory))
        {
            return Array.Empty<InstalledMod>();
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsModFile)
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var enabled = !fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                var display = enabled
                    ? Path.GetFileNameWithoutExtension(fileName)
                    : Path.GetFileNameWithoutExtension(fileName[..^DisabledSuffix.Length]);

                return new InstalledMod(fileName, display, path, enabled, new FileInfo(path).Length);
            })
            .OrderByDescending(m => m.Enabled)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Packs in resourcepacks/ or shaderpacks/. They are switched on inside the game, so
    /// there is no enabled flag to read; the launcher only lists and removes them.
    /// </summary>
    public IReadOnlyList<InstalledMod> ListPacks(string gameDirectory, string folder)
    {
        var directory = Path.Combine(gameDirectory, folder);

        if (!Directory.Exists(directory))
        {
            return Array.Empty<InstalledMod>();
        }

        return Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(p => !Path.GetFileName(p).StartsWith('.'))
            .Where(p => Directory.Exists(p) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var size = Directory.Exists(path) ? 0 : new FileInfo(path).Length;
                var display = Directory.Exists(path) ? fileName : Path.GetFileNameWithoutExtension(fileName);

                return new InstalledMod(fileName, display, path, true, size, folder);
            })
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool SetEnabled(string path, bool enabled)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var isDisabled = path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

        if (enabled && isDisabled)
        {
            File.Move(path, path[..^DisabledSuffix.Length], overwrite: false);
            return true;
        }

        if (!enabled && !isDisabled)
        {
            File.Move(path, path + DisabledSuffix, overwrite: false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// After a new file has landed in the mods folder: removes every other enabled jar that
    /// declares the same mod id, so a newer version replaces the older one instead of
    /// sitting next to it. Two jars with one id stop the game from starting at all.
    /// Disabled copies are left alone; the player switched those off on purpose.
    /// </summary>
    /// <returns>The file names that were removed, for the caller's records and log.</returns>
    public IReadOnlyList<string> RemoveOtherVersions(string gameDirectory, string newFileName)
    {
        var directory = ModsDirectory(gameDirectory);
        var newPath = Path.Combine(directory, newFileName);
        var ids = ModMetadataReader.Read(newPath).Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ids.Count == 0)
        {
            return Array.Empty<string>();
        }

        var removed = new List<string>();

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);

            if (!IsModFile(path) ||
                fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, newFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ModMetadataReader.Read(path).Any(m => ids.Contains(m.Id)))
            {
                File.Delete(path);
                removed.Add(fileName);
            }
        }

        return removed;
    }

    public void Uninstall(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            // An unpacked resource pack is a folder.
            Directory.Delete(path, recursive: true);
        }
    }

    public Task<string> InstallAsync(
        string gameDirectory,
        string fileName,
        string url,
        string? sha1,
        long size,
        CancellationToken cancellationToken = default)
        => InstallAsync(gameDirectory, ModsFolderName, fileName, url, sha1, size, cancellationToken);

    /// <summary>Downloads a file into one of the game's content folders.</summary>
    public async Task<string> InstallAsync(
        string gameDirectory,
        string folder,
        string fileName,
        string url,
        string? sha1,
        long size,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(gameDirectory, folder);
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, fileName);
        await _downloader
            .EnsureFileAsync(new DownloadItem(url, destination, sha1, size), cancellationToken)
            .ConfigureAwait(false);

        return destination;
    }

    /// <summary>SHA-1 of a file as Modrinth stores it, or null when the file cannot be read.</summary>
    public static string? TryComputeSha1(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var hash = System.Security.Cryptography.SHA1.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsModFile(string path)
    {
        var fileName = Path.GetFileName(path);

        if (fileName.StartsWith('.'))
        {
            return false;
        }

        if (fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^DisabledSuffix.Length];
        }

        return fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }
}