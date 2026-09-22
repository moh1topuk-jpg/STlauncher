using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;

namespace STlauncher.Core.Backups;

/// <param name="InstanceId">The build the backup was taken from, read back from the file name.</param>
public sealed record BackupInfo(string Path, string FileName, DateTimeOffset CreatedAt, long Size, string? InstanceId = null);

/// <summary>What is inside a backup, from its table of contents alone.</summary>
/// <param name="Definition">The build's settings, when the backup carries them.</param>
public sealed record BackupContents(Instance? Definition, int Worlds, int Mods, bool HasConfig, bool HasOptions)
{
    public bool HasWorlds => Worlds > 0;
}

/// <summary>How much of a backup to put back.</summary>
public enum RestoreScope
{
    /// <summary>Only the worlds. Mods, configs and options stay as they are.</summary>
    Worlds,

    /// <summary>Everything the backup holds: worlds, mods, configs, packs, options.</summary>
    Everything
}

/// <summary>
/// Creates, reads back and prunes zip backups of a build. A backup holds the player's
/// data - worlds, mods, configs, packs, options - and the build's definition, so it can
/// be put back into the build it came from or turned into a new one.
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

    /// <summary>The build's definition, stored at the root of the archive.</summary>
    public const string DefinitionEntry = InstanceManager.DefinitionFileName;

    private static readonly Regex FileNamePattern = new(
        @"^backup-(?<id>.+)-(?<date>\d{8})-(?<time>\d{6})(?:-\d+)?\.zip$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Compresses the instance on a background thread. Zipping a world takes long enough
    /// that doing it inline froze the window for the whole duration.
    /// </summary>
    public Task<BackupInfo> CreateAsync(
        string instanceDirectory,
        string backupsDirectory,
        string instanceId,
        Instance? definition = null,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => Create(instanceDirectory, backupsDirectory, instanceId, definition, cancellationToken),
            cancellationToken);

    public BackupInfo Create(
        string instanceDirectory,
        string backupsDirectory,
        string instanceId,
        Instance? definition = null,
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
                // The definition first: with it, a backup of a deleted build still says
                // what version and loader the worlds were played on.
                if (definition is not null)
                {
                    var entry = archive.CreateEntry(DefinitionEntry, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(JsonSerializer.Serialize(definition, JsonOptions));
                }

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

        return new BackupInfo(path, fileName, DateTimeOffset.Now, info.Length, instanceId);
    }

    /// <summary>
    /// Reads the table of contents: how many worlds and mods, and the build definition
    /// if one is inside. Only the central directory is read, so this is cheap even for
    /// a multi-gigabyte archive.
    /// </summary>
    public BackupContents Inspect(string backupPath)
    {
        using var archive = ZipFile.OpenRead(backupPath);

        var worlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = 0;
        var hasConfig = false;
        var hasOptions = false;
        Instance? definition = null;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');

            if (string.Equals(name, DefinitionEntry, StringComparison.OrdinalIgnoreCase))
            {
                definition = ReadDefinition(entry);
                continue;
            }

            if (name.StartsWith("saves/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = name["saves/".Length..];
                var slash = rest.IndexOf('/');

                if (slash > 0)
                {
                    worlds.Add(rest[..slash]);
                }
            }
            else if (name.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                     name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                mods++;
            }
            else if (name.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            {
                hasConfig = true;
            }
            else if (string.Equals(name, "options.txt", StringComparison.OrdinalIgnoreCase))
            {
                hasOptions = true;
            }
        }

        return new BackupContents(definition, worlds.Count, mods, hasConfig, hasOptions);
    }

    /// <summary>
    /// Puts a backup back into a game folder. What the archive holds for an entry
    /// replaces what is there: a world restored on top of a newer copy of itself would
    /// otherwise keep region files the backup never had.
    /// </summary>
    public Task RestoreAsync(
        string backupPath,
        string gameDirectory,
        RestoreScope scope,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Restore(backupPath, gameDirectory, scope, cancellationToken), cancellationToken);

    public void Restore(string backupPath, string gameDirectory, RestoreScope scope, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup not found.", backupPath);
        }

        Directory.CreateDirectory(gameDirectory);
        var root = Path.GetFullPath(gameDirectory);

        using var archive = ZipFile.OpenRead(backupPath);

        var wanted = scope == RestoreScope.Worlds
            ? new[] { "saves" }
            : IncludedEntries;

        // Which top-level entries the archive actually has, so only those are replaced.
        var present = archive.Entries
            .Select(e => TopLevel(e.FullName))
            .Where(t => t is not null && wanted.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Select(t => t!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var top in present)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = Path.Combine(root, top);

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var top = TopLevel(entry.FullName);

            if (top is null || !present.Contains(top))
            {
                continue;
            }

            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');

            if (relative.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

            // An entry named "../x" must never land outside the game folder.
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup entry '{entry.FullName}' points outside the game folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>The build definition stored in a backup, or null when there is none.</summary>
    public Instance? ReadDefinition(string backupPath)
    {
        using var archive = ZipFile.OpenRead(backupPath);
        var entry = archive.GetEntry(DefinitionEntry);

        return entry is null ? null : ReadDefinition(entry);
    }

    private static Instance? ReadDefinition(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = new StreamReader(entry.Open());
            return JsonSerializer.Deserialize<Instance>(reader.ReadToEnd(), JsonOptions);
        }
        catch (Exception)
        {
            // A damaged definition does not make the worlds inside any less restorable.
            return null;
        }
    }

    private static string? TopLevel(string entryName)
    {
        var name = entryName.Replace('\\', '/').TrimStart('/');
        var slash = name.IndexOf('/');
        var top = slash < 0 ? name : name[..slash];

        return top.Length == 0 || top == "." || top == ".." ? null : top;
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
            .Select(f => new BackupInfo(f.FullName, f.Name, f.CreationTime, f.Length, InstanceIdFromName(f.Name)))
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"backup-showtime-20260922-101112.zip" → "showtime".</summary>
    public static string? InstanceIdFromName(string fileName)
    {
        var match = FileNamePattern.Match(fileName);
        return match.Success ? match.Groups["id"].Value : null;
    }

    public bool Delete(string backupPath)
    {
        return Delete(backupPath, out _);
    }

    private static bool Delete(string path, out Exception? error)
    {
        try
        {
            File.Delete(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
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
            removed += Delete(backups[i].Path, out _) ? 1 : 0;
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

            if (Delete(remaining[i].Path, out _))
            {
                total -= size;
                removed++;
            }
        }

        return removed;
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
