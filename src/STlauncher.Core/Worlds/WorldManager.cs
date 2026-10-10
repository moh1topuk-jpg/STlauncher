using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using STlauncher.Core.Nbt;

namespace STlauncher.Core.Worlds;

/// <summary>
/// The single-player worlds of one build: listing them and the things a player does to a
/// world - rename, copy, export, import, back up, delete. Three rules hold throughout:
/// nothing is ever overwritten (a copy, an import and a restored backup each get a folder
/// name that is free), nothing is hard-deleted on a click (a deleted world moves into the
/// launcher's trash inside the build), and a world the game is using is left alone.
/// </summary>
public sealed class WorldManager
{
    public const string SavesFolder = "saves";

    /// <summary>How long a deleted world can still be brought back.</summary>
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    /// <summary>The most an imported world may unpack into. Far above any real world, far below a full disk.</summary>
    public const long DefaultMaxImportBytes = 32L * 1024 * 1024 * 1024;

    /// <summary>A world of a hundred thousand chunks is a few thousand files; this is not a world.</summary>
    public const int MaxImportEntries = 200_000;

    public const string BackupPrefix = "world-";

    /// <summary>A folder in saves that an import or a copy is still filling; never listed as a world.</summary>
    private const string StagingPrefix = ".stl-incoming-";

    private const string StampFormat = "yyyyMMdd-HHmmss";

    private static readonly Regex BackupNamePattern = new(
        @"^world-(?<name>.+)-(?<stamp>\d{8}-\d{6})(?:-\d+)?\.zip$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrashNamePattern = new(
        @"^(?<name>.+)-(?<stamp>\d{8}-\d{6})(?:-\d+)?$",
        RegexOptions.Compiled);

    private readonly Func<string, bool> _isGameRunning;

    /// <param name="isGameRunning">
    /// Given a game directory, says whether that build's game is running. Everything that
    /// writes into saves asks it first. A delegate, so whoever knows about running games
    /// can answer without this class knowing them.
    /// </param>
    public WorldManager(Func<string, bool>? isGameRunning = null)
    {
        _isGameRunning = isGameRunning ?? (_ => false);
    }

    public static string SavesDirectory(string gameDirectory) => Path.Combine(gameDirectory, SavesFolder);

    public static string TrashDirectory(string gameDirectory)
        => Path.Combine(gameDirectory, ".stlauncher", "trash", "worlds");

    /// <summary>Where a build's world backups live: apart from whole-build backups, so neither list nor prune sees the other's files.</summary>
    public static string BackupsDirectoryFor(string backupsRoot, string instanceId)
        => Path.Combine(backupsRoot, "worlds", instanceId);

    // ===================== Listing =====================

    /// <summary>Every folder in saves that has a level.dat, most recently played first.</summary>
    public IReadOnlyList<WorldInfo> List(string gameDirectory)
    {
        var saves = SavesDirectory(gameDirectory);

        if (!Directory.Exists(saves))
        {
            return Array.Empty<WorldInfo>();
        }

        var worlds = new List<WorldInfo>();

        foreach (var directory in SafeDirectories(saves))
        {
            if (Path.GetFileName(directory).StartsWith(StagingPrefix, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(directory, WorldInfo.LevelFileName)))
            {
                continue;
            }

            worlds.Add(WorldInfo.Read(directory));
        }

        return worlds
            .OrderByDescending(w => w.LastPlayed ?? DateTimeOffset.MinValue)
            .ThenBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Bytes on disk. Walks the whole folder, so it is for a background thread.</summary>
    public static long MeasureSize(string worldDirectory, CancellationToken cancellationToken = default)
    {
        long total = 0;

        foreach (var file in RealFiles(worldDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                total += file.Info.Length;
            }
            catch (IOException)
            {
                // Gone between the listing and the question; it simply does not count.
            }
        }

        return total;
    }

    /// <summary>
    /// True when the game holds the world open. The game locks session.lock for as long
    /// as the world is loaded, whoever started that game - this launcher or another.
    /// </summary>
    public static bool IsSessionLocked(string worldDirectory)
    {
        var path = Path.Combine(worldDirectory, WorldInfo.SessionLockFileName);

        // Byte-range locks are not something .NET offers on macOS; there the game-running
        // check is the only guard.
        if (OperatingSystem.IsMacOS() || !File.Exists(path))
        {
            return false;
        }

        try
        {
            // Java leaves the file open for sharing and locks its bytes instead, so opening
            // succeeds either way and it is the lock attempt that tells.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var length = Math.Max(1, stream.Length);
            stream.Lock(0, length);
            stream.Unlock(0, length);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only file says nothing about a game; the operation itself will report it.
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    // ===================== Rename, duplicate =====================

    /// <summary>
    /// Changes the name the game shows. Only LevelName inside level.dat changes; the
    /// folder keeps its name, so backups, server-side references and the game's own
    /// "last world" pointer still find it.
    /// </summary>
    public WorldInfo Rename(string gameDirectory, WorldInfo world, string newName)
    {
        var name = CleanLevelName(newName);
        EnsureWritable(gameDirectory, world.Directory);

        SetLevelName(world.Directory, name);
        return WorldInfo.Read(world.Directory);
    }

    /// <summary>A full copy under a free folder name, called <paramref name="newName"/> in the game.</summary>
    public WorldInfo Duplicate(string gameDirectory, WorldInfo world, string newName, CancellationToken cancellationToken = default)
    {
        var name = CleanLevelName(newName);
        EnsureWritable(gameDirectory, world.Directory);

        var saves = SavesDirectory(gameDirectory);
        var staging = Path.Combine(saves, StagingPrefix + Guid.NewGuid().ToString("N"));

        try
        {
            CopyTree(world.Directory, staging, cancellationToken);

            if (world.IsReadable)
            {
                SetLevelName(staging, name);
            }

            var target = Path.Combine(saves, FreeFolderName(saves, name));
            Directory.Move(staging, target);
            return WorldInfo.Read(target);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    // ===================== Trash =====================

    /// <summary>
    /// Takes the world out of saves and into the launcher's trash inside the build. The
    /// game no longer sees it; the launcher can put it back until the retention runs out.
    /// </summary>
    public TrashedWorld MoveToTrash(string gameDirectory, WorldInfo world)
    {
        EnsureWritable(gameDirectory, world.Directory);

        var trash = TrashDirectory(gameDirectory);
        Directory.CreateDirectory(trash);

        var now = DateTime.Now;
        var baseName = $"{world.FolderName}-{now.ToString(StampFormat, CultureInfo.InvariantCulture)}";
        var target = Path.Combine(trash, baseName);

        for (var counter = 1; Directory.Exists(target); counter++)
        {
            target = Path.Combine(trash, $"{baseName}-{counter}");
        }

        MoveDirectory(world.Directory, target);

        // Truncated to the second, which is what the folder name carries and what a later listing reads back.
        var deletedAt = new DateTimeOffset(new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Local));
        return new TrashedWorld(target, world.FolderName, world.Name, deletedAt);
    }

    /// <summary>What is in the trash, most recently deleted first.</summary>
    public IReadOnlyList<TrashedWorld> ListTrash(string gameDirectory)
    {
        var trash = TrashDirectory(gameDirectory);

        if (!Directory.Exists(trash))
        {
            return Array.Empty<TrashedWorld>();
        }

        var result = new List<TrashedWorld>();

        foreach (var directory in SafeDirectories(trash))
        {
            var match = TrashNamePattern.Match(Path.GetFileName(directory));

            if (!match.Success || !TryParseStamp(match.Groups["stamp"].Value, out var deletedAt))
            {
                // Not something this class put there; it is not ours to list or to expire.
                continue;
            }

            var folder = match.Groups["name"].Value;
            var name = File.Exists(Path.Combine(directory, WorldInfo.LevelFileName))
                ? WorldInfo.Read(directory) is { IsReadable: true } info ? info.Name : folder
                : folder;

            result.Add(new TrashedWorld(directory, folder, name, deletedAt));
        }

        return result.OrderByDescending(t => t.DeletedAt).ToList();
    }

    /// <summary>Puts a deleted world back into saves, under its old folder name if that is still free.</summary>
    public WorldInfo RestoreFromTrash(string gameDirectory, TrashedWorld trashed)
    {
        EnsureWritable(gameDirectory, worldDirectory: null);

        if (!Directory.Exists(trashed.Directory))
        {
            throw new DirectoryNotFoundException($"The deleted world is no longer in the trash: {trashed.Directory}");
        }

        var saves = SavesDirectory(gameDirectory);
        Directory.CreateDirectory(saves);

        var target = Path.Combine(saves, FreeFolderName(saves, trashed.FolderName));
        MoveDirectory(trashed.Directory, target);
        return WorldInfo.Read(target);
    }

    /// <summary>
    /// Removes trashed worlds whose retention has run out. This is the one place a world
    /// is really deleted, and only one the player deleted themselves that long ago.
    /// </summary>
    public int PurgeExpiredTrash(string gameDirectory, DateTimeOffset now)
    {
        var removed = 0;

        foreach (var trashed in ListTrash(gameDirectory))
        {
            if (trashed.ExpiresAt <= now && TryDeleteDirectory(trashed.Directory))
            {
                removed++;
            }
        }

        return removed;
    }

    // ===================== Export, import =====================

    /// <summary>
    /// Writes the world as a zip with one folder inside, which is the shape every
    /// launcher, map site and the game's own players pass worlds around in.
    /// </summary>
    public void Export(WorldInfo world, string zipPath, CancellationToken cancellationToken = default)
    {
        EnsureNotOpen(world.Directory);

        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Built beside the target and moved into place when complete: a failure halfway
        // must not leave a truncated zip that looks like a world.
        var temp = zipPath + ".tmp";

        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in RealFiles(world.Directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // The lock belongs to whoever has the world open, not to the world.
                    if (string.Equals(file.Relative, WorldInfo.SessionLockFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var entry = archive.CreateEntry($"{world.FolderName}/{file.Relative}", CompressionLevel.Optimal);
                    entry.LastWriteTime = SafeEntryTime(file.Info);

                    using var source = new FileStream(file.Info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = entry.Open();
                    source.CopyTo(target);
                }
            }

            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    /// <summary>
    /// Unpacks a zipped world into saves under a free folder name. The archive is someone
    /// else's file: every entry is checked to land inside the new folder, the total is
    /// held under a ceiling, and no entry may inflate past the size it declared. Whatever
    /// goes wrong, only the folder this call created is removed.
    /// </summary>
    /// <param name="levelName">A new LevelName for the unpacked world; null keeps the archive's.</param>
    public WorldInfo Import(
        string gameDirectory,
        string zipPath,
        string? levelName = null,
        CancellationToken cancellationToken = default,
        long maxBytes = DefaultMaxImportBytes)
    {
        EnsureWritable(gameDirectory, worldDirectory: null);

        using var archive = ZipFile.OpenRead(zipPath);

        var entries = ReadEntries(archive);
        var prefix = FindWorldRoot(entries);

        var wanted = entries
            .Where(e => e.Name.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > prefix.Length)
            .ToList();

        long declared = 0;

        foreach (var entry in wanted)
        {
            declared += entry.Entry.Length;

            if (declared > maxBytes)
            {
                throw new WorldArchiveException(WorldArchiveProblem.TooLarge, "The archive unpacks into more than a world may take.");
            }
        }

        var saves = SavesDirectory(gameDirectory);
        Directory.CreateDirectory(saves);

        var folderBase = prefix.Length > 0
            ? prefix.TrimEnd('/')
            : Path.GetFileNameWithoutExtension(zipPath);

        var staging = Path.Combine(saves, StagingPrefix + Guid.NewGuid().ToString("N"));
        var root = Path.GetFullPath(staging);

        try
        {
            Directory.CreateDirectory(staging);
            long written = 0;
            var buffer = new byte[81920];

            foreach (var (entry, name) in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = name[prefix.Length..];

                if (relative.EndsWith('/') ||
                    string.Equals(relative, WorldInfo.SessionLockFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

                // The names were vetted above; this is the same question asked of the
                // path the operating system will actually use.
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorldArchiveException(WorldArchiveProblem.UnsafePath, $"Archive entry '{entry.FullName}' points outside the world folder.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                using var source = entry.Open();
                using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);

                long entryWritten = 0;
                int read;

                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryWritten += read;
                    written += read;

                    // The declared size is only what the archive says about itself. An
                    // entry that keeps inflating past it is a bomb, whatever its header claims.
                    if (entryWritten > entry.Length || written > maxBytes)
                    {
                        throw new WorldArchiveException(WorldArchiveProblem.TooLarge, $"Archive entry '{entry.FullName}' unpacks into more than it declares.");
                    }

                    target.Write(buffer, 0, read);
                }
            }

            if (!File.Exists(Path.Combine(staging, WorldInfo.LevelFileName)))
            {
                throw new WorldArchiveException(WorldArchiveProblem.NotAWorld, "The archive has no level.dat.");
            }

            if (levelName is not null && WorldInfo.Read(staging).IsReadable)
            {
                SetLevelName(staging, CleanLevelName(levelName));
            }

            var final = Path.Combine(saves, FreeFolderName(saves, folderBase));
            Directory.Move(staging, final);
            return WorldInfo.Read(final);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static List<(ZipArchiveEntry Entry, string Name)> ReadEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxImportEntries)
        {
            throw new WorldArchiveException(WorldArchiveProblem.TooLarge, "The archive has more files than a world does.");
        }

        var result = new List<(ZipArchiveEntry, string)>(archive.Entries.Count);

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');

            // One bad name condemns the archive: a zip that tries to climb out of its
            // folder was not made by exporting a world, and skipping the entry would
            // unpack the rest of whatever it is.
            if (name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(segment => segment == ".." || segment == "."))
            {
                throw new WorldArchiveException(WorldArchiveProblem.UnsafePath, $"Archive entry '{entry.FullName}' points outside the world folder.");
            }

            result.Add((entry, name));
        }

        return result;
    }

    /// <summary>"" when level.dat is at the top, "Folder/" when it is inside exactly one folder.</summary>
    private static string FindWorldRoot(List<(ZipArchiveEntry Entry, string Name)> entries)
    {
        if (entries.Any(e => string.Equals(e.Name, WorldInfo.LevelFileName, StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        var suffix = "/" + WorldInfo.LevelFileName;

        var roots = entries
            .Select(e => e.Name)
            .Where(n => n.EndsWith(suffix, StringComparison.Ordinal) && n.IndexOf('/') == n.Length - suffix.Length)
            .Select(n => n[..(n.Length - WorldInfo.LevelFileName.Length)])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Several worlds in one zip is a pack of maps or a whole saves folder; which one
        // was meant is not for the launcher to guess.
        return roots.Count == 1
            ? roots[0]
            : throw new WorldArchiveException(
                WorldArchiveProblem.NotAWorld,
                roots.Count == 0 ? "The archive has no level.dat at the top or inside one folder." : "The archive holds more than one world.");
    }

    // ===================== Backups =====================

    /// <summary>
    /// Zips the world into the build's backups area. Named after the world's folder and
    /// the moment, the way whole-build backups are named after the build.
    /// </summary>
    public WorldBackupInfo Backup(WorldInfo world, string backupsDirectory, CancellationToken cancellationToken = default)
    {
        EnsureNotOpen(world.Directory);
        Directory.CreateDirectory(backupsDirectory);

        var now = DateTime.Now;
        var baseName = $"{BackupPrefix}{world.FolderName}-{now.ToString(StampFormat, CultureInfo.InvariantCulture)}";
        var fileName = baseName + ".zip";
        var path = Path.Combine(backupsDirectory, fileName);

        // Two backups within one second must not overwrite each other.
        for (var counter = 1; File.Exists(path); counter++)
        {
            fileName = $"{baseName}-{counter}.zip";
            path = Path.Combine(backupsDirectory, fileName);
        }

        Export(world, path, cancellationToken);

        return new WorldBackupInfo(path, fileName, world.FolderName, now, new FileInfo(path).Length);
    }

    /// <summary>The backups in a folder, newest first; of one world when <paramref name="folderName"/> is given.</summary>
    public IReadOnlyList<WorldBackupInfo> ListBackups(string backupsDirectory, string? folderName = null)
    {
        if (!Directory.Exists(backupsDirectory))
        {
            return Array.Empty<WorldBackupInfo>();
        }

        var result = new List<WorldBackupInfo>();

        foreach (var file in new DirectoryInfo(backupsDirectory).EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly))
        {
            var match = BackupNamePattern.Match(file.Name);

            if (!match.Success)
            {
                continue;
            }

            var world = match.Groups["name"].Value;

            if (folderName is not null && !string.Equals(world, folderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The name is the record of when: a file's own dates change when it is copied to another disk.
            var createdAt = TryParseStamp(match.Groups["stamp"].Value, out var stamp) ? stamp : file.CreationTime;
            result.Add(new WorldBackupInfo(file.FullName, file.Name, world, createdAt, file.Length));
        }

        return result
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Brings a backup back as a new world beside the current one. Never over it: the
    /// player may want a single chest out of last week, not last week instead of today.
    /// </summary>
    public WorldInfo RestoreBackup(string gameDirectory, WorldBackupInfo backup, string? levelName = null, CancellationToken cancellationToken = default)
        => Import(gameDirectory, backup.Path, levelName, cancellationToken);

    /// <summary>
    /// Keeps at most <paramref name="maxCount"/> backups of one world and trims their
    /// total below <paramref name="maxTotalBytes"/>. The newest one always stays.
    /// </summary>
    public int PruneBackups(string backupsDirectory, string folderName, int maxCount, long maxTotalBytes)
    {
        var backups = ListBackups(backupsDirectory, folderName).ToList();
        var removed = 0;

        if (maxCount > 0)
        {
            while (backups.Count > maxCount)
            {
                removed += TryDeleteFile(backups[^1].Path) ? 1 : 0;
                backups.RemoveAt(backups.Count - 1);
            }
        }

        if (maxTotalBytes > 0)
        {
            var total = backups.Sum(b => b.Size);

            for (var i = backups.Count - 1; i >= 1 && total > maxTotalBytes; i--)
            {
                if (TryDeleteFile(backups[i].Path))
                {
                    total -= backups[i].Size;
                    removed++;
                }
            }
        }

        return removed;
    }

    // ===================== Guards and helpers =====================

    /// <summary>Before anything that writes into saves: not while the game runs, not into a world that is open.</summary>
    private void EnsureWritable(string gameDirectory, string? worldDirectory)
    {
        if (_isGameRunning(gameDirectory))
        {
            throw new WorldBusyException(WorldBusyReason.GameRunning);
        }

        if (worldDirectory is not null)
        {
            EnsureNotOpen(worldDirectory);
        }
    }

    /// <summary>Before anything that reads a whole world: a copy of a world mid-save is a broken copy.</summary>
    private static void EnsureNotOpen(string worldDirectory)
    {
        if (IsSessionLocked(worldDirectory))
        {
            throw new WorldBusyException(WorldBusyReason.WorldOpen);
        }
    }

    private static void SetLevelName(string worldDirectory, string name)
    {
        var path = Path.Combine(worldDirectory, WorldInfo.LevelFileName);
        var document = NbtFile.Read(path);

        var data = document.Root.GetCompound("Data")
                   ?? throw new InvalidDataException("level.dat has no Data tag.");

        // Set in place: every other tag, known to us or not, goes back exactly as read.
        data.Set("LevelName", new NbtString(name));
        NbtFile.Write(path, document);
    }

    private static string CleanLevelName(string name)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();

        if (clean.Length == 0)
        {
            throw new ArgumentException("A world needs a name.", nameof(name));
        }

        return clean.Length > 128 ? clean[..128].TrimEnd() : clean;
    }

    /// <summary>A folder name that does not exist in <paramref name="parent"/>: the wanted one, or it with " (2)", " (3)"…</summary>
    public static string FreeFolderName(string parent, string wanted)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(wanted.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (name.Length > 80)
        {
            name = name[..80].TrimEnd('.', ' ');
        }

        if (name.Length == 0 || name.StartsWith('.') || IsReservedName(name))
        {
            name = "World" + (name.Length == 0 ? string.Empty : " " + name.TrimStart('.'));
        }

        var candidate = name;

        for (var counter = 2; Directory.Exists(Path.Combine(parent, candidate)) || File.Exists(Path.Combine(parent, candidate)); counter++)
        {
            candidate = $"{name} ({counter})";
        }

        return candidate;
    }

    private static bool IsReservedName(string name)
    {
        var stem = name.Split('.')[0].Trim().ToUpperInvariant();

        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
               (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }

    private static bool TryParseStamp(string stamp, out DateTimeOffset value)
    {
        if (DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            value = new DateTimeOffset(parsed);
            return true;
        }

        value = default;
        return false;
    }

    private static DateTimeOffset SafeEntryTime(FileInfo info)
    {
        // The zip format cannot say anything earlier than 1980.
        var time = info.LastWriteTime;
        return time.Year < 1980 ? new DateTime(1980, 1, 1) : time;
    }

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The files under a folder, with paths relative to it using '/'. Links are not
    /// followed: a junction inside a world can point at a whole drive, and "the world"
    /// that got copied or zipped would then be that drive.
    /// </summary>
    private static IEnumerable<(FileInfo Info, string Relative)> RealFiles(string root)
    {
        var pending = new Stack<(DirectoryInfo Directory, string Prefix)>();
        pending.Push((new DirectoryInfo(root), string.Empty));

        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();
            List<FileSystemInfo> children;

            try
            {
                children = directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception) when (prefix.Length > 0)
            {
                // A subfolder that cannot be listed is skipped; the root failing is the caller's news.
                continue;
            }

            foreach (var child in children)
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0 && child.LinkTarget is not null)
                {
                    continue;
                }

                if (child is DirectoryInfo sub)
                {
                    pending.Push((sub, prefix + sub.Name + "/"));
                }
                else if (child is FileInfo file)
                {
                    yield return (file, prefix + file.Name);
                }
            }
        }
    }

    private static void CopyTree(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);

        foreach (var file in RealFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(file.Relative, WorldInfo.SessionLockFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(target, file.Relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            file.Info.CopyTo(destination, overwrite: false);
        }
    }

    /// <summary>
    /// A rename when both ends are on one volume, which is the usual case and is atomic.
    /// When saves is a link to another drive it becomes copy, then remove the original
    /// only once the copy is whole.
    /// </summary>
    private static void MoveDirectory(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
            return;
        }
        catch (IOException) when (!SameRoot(source, target))
        {
        }

        try
        {
            CopyTree(source, target, CancellationToken.None);
        }
        catch
        {
            TryDeleteDirectory(target);
            throw;
        }

        Directory.Delete(source, recursive: true);
    }

    private static bool SameRoot(string a, string b)
        => string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
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
}
