using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core;

/// <summary>What a move did, for the status line.</summary>
public sealed record DataMoveResult(int Files, long Bytes, IReadOnlyList<string> NotRemoved);

/// <summary>
/// Moves the launcher's data to another folder. Everything is copied first and the
/// source is deleted only after the last file is in place: a move that dies halfway
/// through - the disk fills up, the cable comes out - must leave the old folder whole.
/// </summary>
/// <remarks>
/// Mods and packs that several builds share are hard links to one file (see
/// <see cref="Storage.SharedFileStore"/>). A plain copy would turn every link into a full
/// file and the data would arrive several times its size, so a file with more than one
/// name is copied once and linked again on the other side. Where that cannot be done -
/// the new disk cannot link, or the system does not say which names belong together -
/// each name becomes a full copy, which is only bigger, and the clean-up in Settings
/// links them again later.
/// </remarks>
public static class DataDirectoryMover
{
    /// <summary>Files that belong to the location, not the data, and stay behind.</summary>
    private static readonly string[] StaysBehind = { DataLocation.PointerFileName };

    /// <summary>Scratch that is not worth carrying across.</summary>
    private static readonly string[] Skipped = { "logs", "crash.log" };

    public static Task<DataMoveResult> MoveAsync(
        string from,
        string to,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Move(from, to, progress, cancellationToken), cancellationToken);

    public static DataMoveResult Move(string from, string to, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(from))
        {
            throw new DirectoryNotFoundException($"Data folder not found: {from}");
        }

        if (DataLocation.IsSame(from, to))
        {
            throw new InvalidOperationException("The new folder is the current one.");
        }

        if (DataLocation.IsInside(to, from))
        {
            throw new InvalidOperationException("The new folder is inside the current data folder.");
        }

        Directory.CreateDirectory(to);

        // A folder with something already in it is somebody else's: refusing beats
        // merging two launchers' files into one.
        if (Directory.EnumerateFileSystemEntries(to).Any())
        {
            throw new InvalidOperationException("The new folder must be empty.");
        }

        var entries = Directory.EnumerateFileSystemEntries(from)
            .Where(e => !StaysBehind.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase))
            .Where(e => !Skipped.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase))
            .ToList();

        var files = 0;
        long bytes = 0;
        var linked = new Dictionary<(uint, ulong), string>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            var target = Path.Combine(to, name);
            progress?.Report(name);

            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target, linked, ref files, ref bytes, progress, cancellationToken);
            }
            else
            {
                File.Copy(entry, target, overwrite: false);
                files++;
                bytes += new FileInfo(target).Length;
            }
        }

        // Only now is the old folder expendable.
        var notRemoved = new List<string>();

        foreach (var entry in entries)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception)
            {
                // A file held open - the game's log, an antivirus scan - stays; the copy
                // in the new folder is complete regardless.
                notRemoved.Add(Path.GetFileName(entry));
            }
        }

        return new DataMoveResult(files, bytes, notRemoved);
    }

    private static void CopyDirectory(
        string source,
        string target,
        Dictionary<(uint, ulong), string> linked,
        ref int files,
        ref long bytes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.Combine(target, Path.GetFileName(file));
            files++;

            if (CopyOrLink(file, destination, linked))
            {
                bytes += new FileInfo(destination).Length;
            }

            if (files % 200 == 0)
            {
                progress?.Report($"{Path.GetFileName(source)} · {files}");
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), linked, ref files, ref bytes, progress, cancellationToken);
        }
    }

    /// <summary>
    /// Copies a file, unless it is another name of one already copied: then the copy gets
    /// a second name instead.
    /// </summary>
    /// <returns>True when bytes were copied, false when a link was enough.</returns>
    private static bool CopyOrLink(string file, string destination, Dictionary<(uint, ulong), string> linked)
    {
        // Nothing smaller is ever shared, and asking costs opening the file: thousands of
        // small assets are spared the question.
        if (new FileInfo(file).Length >= Storage.SharedFileStore.MinimumSize &&
            Storage.HardLink.TryGetIdentity(file) is { Links: > 1 } identity)
        {
            var key = (identity.Volume, identity.Index);

            if (linked.TryGetValue(key, out var first) && Storage.HardLink.TryCreate(first, destination))
            {
                return false;
            }

            File.Copy(file, destination, overwrite: false);
            linked.TryAdd(key, destination);
            return true;
        }

        File.Copy(file, destination, overwrite: false);
        return true;
    }

    /// <summary>Bytes the move will copy, for the confirmation line.</summary>
    public static long EstimateSize(string from)
    {
        try
        {
            if (!Directory.Exists(from))
            {
                return 0;
            }

            // Several names of one file are one file's worth of bytes to carry.
            var seen = new HashSet<(uint, ulong)>();

            bool IsCounted(FileInfo file)
                => file.Length < Storage.SharedFileStore.MinimumSize ||
                   Storage.HardLink.TryGetIdentity(file.FullName) is not { Links: > 1 } identity ||
                   seen.Add((identity.Volume, identity.Index));

            return Directory.EnumerateFileSystemEntries(from)
                .Where(e => !Skipped.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase))
                .Sum(e => Directory.Exists(e)
                    ? new DirectoryInfo(e).EnumerateFiles("*", SearchOption.AllDirectories).Where(IsCounted).Sum(f => f.Length)
                    : new FileInfo(e).Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
