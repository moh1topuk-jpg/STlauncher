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

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            var target = Path.Combine(to, name);
            progress?.Report(name);

            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target, ref files, ref bytes, progress, cancellationToken);
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
            File.Copy(file, destination, overwrite: false);
            files++;
            bytes += new FileInfo(destination).Length;

            if (files % 200 == 0)
            {
                progress?.Report($"{Path.GetFileName(source)} · {files}");
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), ref files, ref bytes, progress, cancellationToken);
        }
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

            return Directory.EnumerateFileSystemEntries(from)
                .Where(e => !Skipped.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase))
                .Sum(e => Directory.Exists(e)
                    ? new DirectoryInfo(e).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                    : new FileInfo(e).Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
