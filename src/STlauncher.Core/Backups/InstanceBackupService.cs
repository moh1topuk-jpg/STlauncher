using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Backups;

public sealed record BackupInfo(string Path, string FileName, DateTimeOffset CreatedAt, long Size);

/// <summary>
/// Creates and prunes zip backups of an instance. Only player-relevant data is
/// included, so backups stay small.
/// </summary>
public sealed class InstanceBackupService
{
    public static readonly string[] IncludedEntries =
    {
        "saves",
        "config",
        "mods",
        "resourcepacks",
        "shaderpacks",
        "options.txt",
        "servers.dat"
    };

    public const string FilePrefix = "backup-";

    /// <summary>
    /// Compresses the instance on a background thread. Zipping a world takes long enough
    /// that doing it inline froze the window for the whole duration.
    /// </summary>
    public Task<BackupInfo> CreateAsync(
        string instanceDirectory,
        string backupsDirectory,
        string instanceId,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => Create(instanceDirectory, backupsDirectory, instanceId, cancellationToken),
            cancellationToken);

    public BackupInfo Create(
        string instanceDirectory,
        string backupsDirectory,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(instanceDirectory))
        {
            throw new DirectoryNotFoundException($"Instance directory not found: {instanceDirectory}");
        }

        Directory.CreateDirectory(backupsDirectory);

        var baseName = $"{FilePrefix}{instanceId}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var fileName = baseName + ".zip";
        var path = Path.Combine(backupsDirectory, fileName);

        // Several backups within one second must not overwrite each other.
        for (var counter = 1; File.Exists(path); counter++)
        {
            fileName = $"{baseName}-{counter}.zip";
            path = Path.Combine(backupsDirectory, fileName);
        }

        // Build the archive beside the target and move it into place only once it is
        // complete. Writing straight to the .zip left a truncated file that List() then
        // reported as a valid backup whenever a single entry failed (a world file held
        // open by a running game, for instance).
        var tempPath = path + ".tmp";

        try
        {
            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var entry in IncludedEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var source = Path.Combine(instanceDirectory, entry);

                    if (File.Exists(source))
                    {
                        archive.CreateEntryFromFile(source, entry, CompressionLevel.Optimal);
                    }
                    else if (Directory.Exists(source))
                    {
                        AddDirectory(archive, source, entry, cancellationToken);
                    }
                }
            }

            File.Move(tempPath, path);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        var info = new FileInfo(path);

        return new BackupInfo(path, fileName, DateTimeOffset.Now, info.Length);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort - the original exception is the one that matters.
        }
    }

    public IReadOnlyList<BackupInfo> List(string backupsDirectory, string? instanceId = null)
    {
        if (!Directory.Exists(backupsDirectory))
        {
            return Array.Empty<BackupInfo>();
        }

        var prefix = instanceId is null ? FilePrefix : $"{FilePrefix}{instanceId}-";

        return Directory.EnumerateFiles(backupsDirectory, "*.zip", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p))
            .Select(f => new BackupInfo(f.FullName, f.Name, f.CreationTime, f.Length))
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Keeps at most <paramref name="maxCount"/> backups and trims the total size
    /// below <paramref name="maxTotalBytes"/>. Never deletes the newest backup.
    /// </summary>
    public int Prune(string backupsDirectory, int maxCount, long maxTotalBytes)
    {
        var backups = List(backupsDirectory);
        var removed = 0;

        var keep = maxCount <= 0 ? backups.Count : Math.Min(maxCount, backups.Count);

        for (var i = keep; i < backups.Count; i++)
        {
            removed += Delete(backups[i].Path) ? 1 : 0;
        }

        if (maxTotalBytes <= 0)
        {
            return removed;
        }

        var remaining = List(backupsDirectory);

        // Keep the newest one regardless of the size limit.
        var total = remaining.Sum(b => b.Size);

        for (var i = remaining.Count - 1; i >= 1 && total > maxTotalBytes; i--)
        {
            var size = remaining[i].Size;

            if (Delete(remaining[i].Path))
            {
                total -= size;
                removed++;
            }
        }

        return removed;
    }

    private static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void AddDirectory(
        ZipArchive archive,
        string sourceDirectory,
        string entryPrefix,
        CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{entryPrefix}/{relative}", CompressionLevel.Optimal);
        }
    }
}
