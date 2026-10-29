using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Http;

/// <summary>
/// Files fetched ahead of time for a build, not yet part of it: "prepare in advance" puts
/// the catalog's next files here while the launcher sits idle, and the download that would
/// fetch one later takes it from here instead - a rename, not a download.
/// </summary>
/// <remarks>
/// A file is named after its SHA-1 (or after its address, when the catalog gives no hash)
/// and is checked against the item's digest again before it is handed out, so a file that
/// changed here since is fetched anew rather than trusted. Nothing in this folder is ever
/// seen by the game; it is the launcher's own and can be emptied at any time.
/// </remarks>
public sealed class PreparedFiles
{
    public const string DirectoryName = "prepared";

    private const string PartSuffix = ".part";

    public PreparedFiles(string directory)
    {
        Directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
    }

    public PreparedFiles(LauncherPaths paths)
        : this(Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).Root, DirectoryName))
    {
    }

    public string Directory { get; }

    /// <summary>Where the file for this item is kept here.</summary>
    public string PathFor(DownloadItem item)
    {
        var key = item.Sha1 is { Length: 40 } sha1 && sha1.All(Uri.IsHexDigit)
            ? sha1.ToLowerInvariant()
            : "url-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(item.Url))).ToLowerInvariant();

        return Path.Combine(Directory, key);
    }

    /// <summary>True when the item is here and still the promised file.</summary>
    public bool Has(DownloadItem item)
    {
        var path = PathFor(item);
        return File.Exists(path) && DownloadClient.Matches(path, item);
    }

    /// <summary>
    /// Fetches the item into this folder, verified. The build is not touched:
    /// <see cref="DownloadItem.DestinationPath"/> only says where it will go one day.
    /// </summary>
    /// <returns>False when it was here already.</returns>
    public async Task<bool> PrepareAsync(DownloadClient downloader, DownloadItem item, CancellationToken cancellationToken = default)
    {
        if (downloader is null)
        {
            throw new ArgumentNullException(nameof(downloader));
        }

        if (Has(item))
        {
            return false;
        }

        System.IO.Directory.CreateDirectory(Directory);
        var target = PathFor(item);
        var part = target + "." + Guid.NewGuid().ToString("N") + PartSuffix;

        try
        {
            await downloader.StageAsync(item, part, cancellationToken).ConfigureAwait(false);
            File.Move(part, target, overwrite: true);
        }
        finally
        {
            TryDelete(part);
        }

        return true;
    }

    /// <summary>
    /// Hands the prepared file over to <paramref name="destination"/> by renaming it there.
    /// False when there is none, or it no longer matches (and then it is thrown away).
    /// </summary>
    public bool TryTake(DownloadItem item, string destination)
    {
        var path = PathFor(item);

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (!DownloadClient.Matches(path, item))
            {
                TryDelete(path);
                return false;
            }

            var directory = Path.GetDirectoryName(destination);

            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            // A rename over whatever is there, never a write into it: the old name may be a
            // link that other builds share.
            File.Move(path, destination, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How many files wait here and their size.</summary>
    public (int Files, long Bytes) Measure()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return (0, 0);
        }

        try
        {
            var files = new DirectoryInfo(Directory).EnumerateFiles()
                .Where(f => !f.Name.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return (files.Count, files.Sum(f => f.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    /// <summary>Drops what is no longer wanted: the catalog moved on, and an older file would only take room.</summary>
    /// <returns>How many files went.</returns>
    public int KeepOnly(System.Collections.Generic.IEnumerable<DownloadItem> wanted)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        var keep = wanted.Select(PathFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory).ToList())
        {
            if (!keep.Contains(path) && !path.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Empties the folder. Nothing in a build depends on it.</summary>
    public void Clear()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory))
        {
            TryDelete(path);
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
