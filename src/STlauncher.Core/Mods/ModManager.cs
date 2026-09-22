using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;

namespace STlauncher.Core.Mods;

public sealed record InstalledMod(string FileName, string DisplayName, string Path, bool Enabled, long Size);

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

    public void Uninstall(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async Task<string> InstallAsync(
        string gameDirectory,
        string fileName,
        string url,
        string? sha1,
        long size,
        CancellationToken cancellationToken = default)
    {
        var directory = ModsDirectory(gameDirectory);
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