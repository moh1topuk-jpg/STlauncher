using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;

namespace STlauncher.Core.Mods;

/// <summary>
/// Whether the game of the build in this folder is running. A jar the game holds open
/// cannot be renamed on Windows, and one it has not opened yet would be swapped under a
/// session that already read the old list. The launcher's own knowledge is plugged in
/// here; with nothing plugged in the answer is "no".
/// </summary>
public delegate bool GameRunningCheck(string gameDirectory);

public enum ModReplaceStep
{
    /// <summary>The build's game is running.</summary>
    GameRunning,

    /// <summary>The new file could not be put under its name.</summary>
    Place,

    /// <summary>The file being replaced could not be moved aside.</summary>
    MoveOld,

    /// <summary>The kept file is gone, or its place in the build is taken.</summary>
    Restore
}

/// <summary>A replacement that did not happen. The build is as it was before the attempt.</summary>
public sealed class ModReplaceException : IOException
{
    public ModReplaceException(ModReplaceStep step, string message, Exception? inner = null)
        : base(message, inner)
    {
        Step = step;
    }

    public ModReplaceStep Step { get; }
}

/// <param name="Folder">"mods", "resourcepacks" or "shaderpacks".</param>
/// <param name="OldPath">The file the new one takes over from; null when it only joins the build.</param>
public sealed record ModReplaceRequest(
    string GameDirectory,
    string Folder,
    string? OldPath,
    string NewFileName,
    string Url,
    string? Sha1,
    long Size)
{
    public string? Sha512 { get; init; }

    /// <summary>What ties the versions of one mod together (its project id); decides how many old files are kept.</summary>
    public string? Key { get; init; }

    /// <summary>The version being replaced, as the player knows it: shown when offering to go back.</summary>
    public string? OldLabel { get; init; }
}

/// <summary>A file the launcher moved out of a build when a newer one took its place.</summary>
/// <param name="FileName">Its name in the build, without ".disabled".</param>
/// <param name="ReplacedBy">The name of the file that took its place, without ".disabled".</param>
public sealed record ReplacedModFile(
    string Folder,
    string FileName,
    string ReplacedBy,
    string StoredPath,
    DateTime WhenUtc,
    string? Label,
    string? Key);

/// <param name="Previous">Where the file that stepped aside is kept; null when nothing did.</param>
public sealed record ModReplaceResult(string NewPath, string NewFileName, bool Enabled, ReplacedModFile? Previous);

/// <summary>
/// Puts a new version of a mod or a pack in place of the old one so that the build is
/// never left without it.
/// </summary>
/// <remarks>
/// <para>
/// The order is the point. The new file is downloaded under a hidden name in the same
/// folder (or linked there from the shared store) and its digest is checked while the old
/// file still sits untouched. Only then, and only if the game is not running, is it
/// renamed to its real name - keeping ".disabled" if the old one was switched off - and
/// only after that does the old file leave, when its name is a different one. Every step
/// is a rename of a directory entry: nothing is opened for writing, so a file that is one
/// of several names of a shared object (see <see cref="Storage.SharedFileStore"/>) is
/// never written through. A step that fails undoes the ones before it.
/// </para>
/// <para>
/// The old file is not deleted. It goes to <c>.stlauncher/replaced/&lt;date&gt;/</c> inside
/// the build, with a line in an index saying what took its place, so "bring the previous
/// version back" is a rename as well. The last <see cref="KeepPerMod"/> of each mod stay.
/// </para>
/// </remarks>
public sealed class ModFileReplacer
{
    public const string ReplacedDirectory = ".stlauncher/replaced";
    public const int KeepPerMod = 3;
    public const int KeepInAll = 40;

    private const string DisabledSuffix = ".disabled";
    private const string StagingSuffix = ".stlnew";
    private const string IndexName = "index.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly DownloadClient _downloader;
    private readonly Func<DateTime> _now;
    private readonly object _indexGate = new();
    private (string Path, DateTime Stamp, long Length, List<Entry> Entries)? _indexCache;

    public ModFileReplacer(DownloadClient downloader, Func<DateTime>? utcNow = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _now = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>See <see cref="GameRunningCheck"/>.</summary>
    public GameRunningCheck? IsGameRunning { get; set; }

    /// <summary>Download, check, rename, move the old one aside: the whole replacement.</summary>
    public async Task<ModReplaceResult> ReplaceAsync(ModReplaceRequest request, CancellationToken cancellationToken = default)
    {
        var staged = await StageAsync(request, cancellationToken).ConfigureAwait(false);
        return staged.Commit();
    }

    /// <summary>
    /// The first half: the new file, verified, under a hidden name beside its place. The
    /// build does not see it. The caller commits it or discards it; several can be staged
    /// and committed together, so that one of them failing to download stops them all
    /// before anything in the build has moved.
    /// </summary>
    /// <exception cref="DownloadFailedException">The file did not come, or is not the promised one.</exception>
    public async Task<StagedModFile> StageAsync(ModReplaceRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.NewFileName) ||
            request.NewFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            request.NewFileName.StartsWith('.'))
        {
            throw new ArgumentException("Not a file name: " + request.NewFileName, nameof(request));
        }

        // Refused before the download as well as before the rename: no point in fetching
        // forty megabytes to say "close the game first".
        ThrowIfGameRunning(request.GameDirectory);

        var directory = Path.Combine(request.GameDirectory, request.Folder);
        Directory.CreateDirectory(directory);
        SweepStaleStaging(directory);

        // A leading dot keeps it out of every list the launcher draws and out of the
        // shared store; the extension keeps it out of the game's own scan of the folder.
        var staging = Path.Combine(directory, $".{request.NewFileName}.{Guid.NewGuid():N}{StagingSuffix}");
        var item = new DownloadItem(request.Url, Path.Combine(directory, request.NewFileName), request.Sha1, request.Size, Sha512: request.Sha512);

        try
        {
            await _downloader.StageAsync(item, staging, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        return new StagedModFile(this, request, staging);
    }

    /// <summary>The file this one took over from, if the launcher still keeps it.</summary>
    public ReplacedModFile? FindPrevious(string gameDirectory, string folder, string currentFileName)
    {
        var current = BaseName(currentFileName);

        return List(gameDirectory)
            .Where(e => string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.ReplacedBy, current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.WhenUtc)
            .FirstOrDefault(e => File.Exists(e.StoredPath));
    }

    /// <summary>Everything kept for this build, newest first.</summary>
    public IReadOnlyList<ReplacedModFile> List(string gameDirectory)
    {
        var root = ReplacedRoot(gameDirectory);

        return ReadIndex(root)
            .Select(e => ToPublic(root, e))
            .OrderByDescending(e => e.WhenUtc)
            .ToList();
    }

    /// <summary>
    /// Brings a kept file back in place of the one that replaced it; that one is kept in
    /// turn, so the step can be taken back the same way. No download is involved.
    /// </summary>
    /// <param name="currentLabel">The version stepping aside, as the player knows it.</param>
    public ModReplaceResult Restore(string gameDirectory, ReplacedModFile previous, string? currentLabel = null)
    {
        if (previous is null)
        {
            throw new ArgumentNullException(nameof(previous));
        }

        ThrowIfGameRunning(gameDirectory);

        if (!File.Exists(previous.StoredPath))
        {
            throw new ModReplaceException(ModReplaceStep.Restore, "The kept file is no longer there: " + previous.StoredPath);
        }

        var directory = Path.Combine(gameDirectory, previous.Folder);
        Directory.CreateDirectory(directory);

        var currentEnabled = Path.Combine(directory, previous.ReplacedBy);
        var currentPath = File.Exists(currentEnabled) ? currentEnabled
            : File.Exists(currentEnabled + DisabledSuffix) ? currentEnabled + DisabledSuffix
            : null;

        var disabled = currentPath is not null && IsDisabled(currentPath);
        var finalPath = Path.Combine(directory, previous.FileName + (disabled ? DisabledSuffix : string.Empty));
        var root = ReplacedRoot(gameDirectory);
        var journal = new Journal();
        Entry? added = null;

        try
        {
            if (currentPath is not null && SamePath(currentPath, finalPath))
            {
                var stored = NewStoredPath(root, previous.ReplacedBy);
                SwapIn(previous.StoredPath, finalPath, stored, journal);
                added = NewEntry(root, previous.Folder, previous.ReplacedBy, previous.FileName, stored, currentLabel, previous.Key);
            }
            else
            {
                if (File.Exists(finalPath))
                {
                    throw new ModReplaceException(ModReplaceStep.Restore, "A file with that name is already in the build: " + finalPath);
                }

                // In first, out second: for a moment the build has both, never neither.
                journal.Move(previous.StoredPath, finalPath);

                if (currentPath is not null)
                {
                    var stored = NewStoredPath(root, previous.ReplacedBy);
                    journal.Move(currentPath, stored);
                    added = NewEntry(root, previous.Folder, previous.ReplacedBy, previous.FileName, stored, currentLabel, previous.Key);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal.Undo();

            throw ex as ModReplaceException
                  ?? new ModReplaceException(ModReplaceStep.Restore, "The previous version could not be put back: " + ex.Message, ex);
        }

        UpdateIndex(root, entries =>
        {
            entries.RemoveAll(e => SamePath(Path.Combine(root, e.Stored), previous.StoredPath));

            if (added is not null)
            {
                entries.Add(added);
            }
        });

        return new ModReplaceResult(
            finalPath,
            Path.GetFileName(finalPath),
            !disabled,
            added is null ? null : ToPublic(root, added));
    }

    // ------------------------------------------------------------------ commit

    internal ModReplaceResult Commit(StagedModFile staged, Journal journal, List<Entry> added)
    {
        var request = staged.Request;
        ThrowIfGameRunning(request.GameDirectory);

        if (!File.Exists(staged.StagingPath))
        {
            throw new ModReplaceException(ModReplaceStep.Place, "The downloaded file is gone: " + staged.StagingPath);
        }

        var directory = Path.Combine(request.GameDirectory, request.Folder);
        var oldPath = request.OldPath is { Length: > 0 } given && File.Exists(given) ? given : null;
        var disabled = oldPath is not null && IsDisabled(oldPath);
        var finalPath = Path.Combine(directory, request.NewFileName + (disabled ? DisabledSuffix : string.Empty));
        var root = ReplacedRoot(request.GameDirectory);
        var step = ModReplaceStep.Place;
        Entry? previous = null;

        try
        {
            if (File.Exists(finalPath))
            {
                // The name is taken - by the old file itself when the version kept its
                // name, by a stray copy otherwise. One swap puts the new file there and
                // the other in the keep: the name is never empty.
                var stored = NewStoredPath(root, request.NewFileName);
                SwapIn(staged.StagingPath, finalPath, stored, journal);
                previous = NewEntry(root, request.Folder, request.NewFileName, request.NewFileName, stored, request.OldLabel, request.Key);
                added.Add(previous);
            }
            else
            {
                File.Move(staged.StagingPath, finalPath);
                journal.Did(() => File.Delete(finalPath));
            }

            if (oldPath is not null && !SamePath(oldPath, finalPath) && File.Exists(oldPath))
            {
                step = ModReplaceStep.MoveOld;

                var oldName = BaseName(Path.GetFileName(oldPath));
                var stored = NewStoredPath(root, oldName);
                journal.Move(oldPath, stored);
                previous = NewEntry(root, request.Folder, oldName, request.NewFileName, stored, request.OldLabel, request.Key);
                added.Add(previous);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal.Undo();
            added.Clear();
            TryDelete(staged.StagingPath);

            throw new ModReplaceException(
                step,
                (step == ModReplaceStep.MoveOld ? "The old file could not be moved aside: " : "The new file could not be put in place: ") + ex.Message,
                ex);
        }

        var kept = added.ToList();
        UpdateIndex(root, entries => entries.AddRange(kept));

        // The file is under its real name now: the store may take it, or give a link.
        // The SHA-1 is taken on trust only when it is the hash the download was checked by.
        _downloader.SharedFiles?.Adopt(finalPath, string.IsNullOrEmpty(request.Sha512) ? request.Sha1 : null);

        return new ModReplaceResult(
            finalPath,
            Path.GetFileName(finalPath),
            !disabled,
            previous is null ? null : ToPublic(root, previous));
    }

    internal void Forget(string gameDirectory, IReadOnlyCollection<Entry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        UpdateIndex(ReplacedRoot(gameDirectory), all => all.RemoveAll(e => entries.Any(x => x.Stored == e.Stored)));
    }

    private void ThrowIfGameRunning(string gameDirectory)
    {
        if (IsGameRunning?.Invoke(gameDirectory) == true)
        {
            throw new ModReplaceException(ModReplaceStep.GameRunning, "The game is running: files of the build are not replaced under it.");
        }
    }

    /// <summary>
    /// <paramref name="source"/> takes the name <paramref name="target"/>; what was there
    /// goes to <paramref name="backup"/>. One system call where the disk can do it.
    /// </summary>
    private static void SwapIn(string source, string target, string backup, Journal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);

        try
        {
            File.Replace(source, target, backup, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Not every file system has the swap. Two renames then; the journal puts the
            // first back if the second fails.
            if (!File.Exists(source) || !File.Exists(target) || File.Exists(backup))
            {
                throw;
            }

            File.Move(target, backup);

            try
            {
                File.Move(source, target);
            }
            catch
            {
                File.Move(backup, target);
                throw;
            }
        }

        journal.Did(() => File.Move(backup, target, overwrite: true));
    }

    // ------------------------------------------------------------------ the keep

    private static string ReplacedRoot(string gameDirectory)
        => Path.Combine(gameDirectory, ReplacedDirectory.Replace('/', Path.DirectorySeparatorChar));

    private string NewStoredPath(string root, string fileName)
    {
        var now = _now();
        var day = Path.Combine(root, now.ToString("yyyy-MM-dd"));
        var path = Path.Combine(day, fileName);

        // The same file replaced twice in a day: each keeps its own place.
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(day, n.ToString(), fileName);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private Entry NewEntry(string root, string folder, string fileName, string replacedBy, string storedPath, string? label, string? key)
        => new()
        {
            Folder = folder,
            FileName = fileName,
            ReplacedBy = replacedBy,
            Stored = Path.GetRelativePath(root, storedPath).Replace('\\', '/'),
            WhenUtc = _now(),
            Label = label,
            Key = key
        };

    private static ReplacedModFile ToPublic(string root, Entry entry)
        => new(
            entry.Folder,
            entry.FileName,
            entry.ReplacedBy,
            Path.GetFullPath(Path.Combine(root, entry.Stored.Replace('/', Path.DirectorySeparatorChar))),
            entry.WhenUtc,
            entry.Label,
            entry.Key);

    private List<Entry> ReadIndex(string root)
    {
        var path = Path.Combine(root, IndexName);

        lock (_indexGate)
        {
            try
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                {
                    return new List<Entry>();
                }

                // The mod list asks once per row; the file is read once per change.
                if (_indexCache is { } cache && cache.Path == path && cache.Stamp == info.LastWriteTimeUtc && cache.Length == info.Length)
                {
                    return cache.Entries.ToList();
                }

                var entries = (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), JsonOptions) ?? new List<Entry>())
                    .Where(e => IsSane(root, e))
                    .ToList();

                _indexCache = (path, info.LastWriteTimeUtc, info.Length, entries);
                return entries.ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new List<Entry>();
            }
        }
    }

    /// <summary>The index is a file in the build's folder, and anyone can edit it: an entry may only point inside the keep.</summary>
    private static bool IsSane(string root, Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Folder) || string.IsNullOrWhiteSpace(entry.FileName) ||
            string.IsNullOrWhiteSpace(entry.ReplacedBy) || string.IsNullOrWhiteSpace(entry.Stored))
        {
            return false;
        }

        if (entry.Folder.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || entry.Folder.StartsWith('.') ||
            entry.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            entry.ReplacedBy.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(root, entry.Stored.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Never throws: the files have moved already, and the index only describes them.</summary>
    private void UpdateIndex(string root, Action<List<Entry>> change)
    {
        lock (_indexGate)
        {
            try
            {
                var entries = ReadIndex(root);
                change(entries);
                Prune(root, entries);

                Directory.CreateDirectory(root);
                var path = Path.Combine(root, IndexName);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
                File.Move(temp, path, overwrite: true);
                _indexCache = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The last few of each mod, and not more than a build's worth in all. These are the launcher's own copies.</summary>
    private static void Prune(string root, List<Entry> entries)
    {
        entries.RemoveAll(e => !File.Exists(Path.Combine(root, e.Stored)));

        var newestFirst = entries.OrderByDescending(e => e.WhenUtc).ToList();

        var extra = newestFirst
            .GroupBy(e => e.Folder + "/" + (e.Key is { Length: > 0 } key ? "key:" + key : "name:" + e.ReplacedBy), StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Skip(KeepPerMod))
            .Concat(newestFirst.Skip(KeepInAll))
            .Distinct()
            .ToList();

        foreach (var entry in extra)
        {
            var stored = Path.Combine(root, entry.Stored);
            TryDelete(stored);
            entries.Remove(entry);

            // The day's folder goes with its last file.
            for (var folder = Path.GetDirectoryName(stored);
                 folder is not null && !SamePath(folder, root) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any();
                 folder = Path.GetDirectoryName(folder))
            {
                try
                {
                    Directory.Delete(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Hidden files a crash or a power cut left from an earlier replacement.</summary>
    private void SweepStaleStaging(string directory)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, ".*" + StagingSuffix))
            {
                if (_now() - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsDisabled(string path) => path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    private static string BaseName(string fileName)
        => IsDisabled(fileName) ? fileName[..^DisabledSuffix.Length] : fileName;

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void TryDelete(string path)
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

    /// <summary>What has been done so far, so that it can be taken back in reverse.</summary>
    internal sealed class Journal
    {
        private readonly List<Action> _undo = new();

        public void Move(string from, string to)
        {
            File.Move(from, to);
            _undo.Add(() => File.Move(to, from));
        }

        public void Did(Action undo) => _undo.Add(undo);

        public void Undo()
        {
            for (var i = _undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    _undo[i]();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Nothing better is left to do with this one; the others still go back.
                }
            }

            _undo.Clear();
        }
    }

    internal sealed class Entry
    {
        public string Folder { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public string ReplacedBy { get; set; } = string.Empty;

        /// <summary>Relative to the keep, with forward slashes.</summary>
        public string Stored { get; set; } = string.Empty;

        public DateTime WhenUtc { get; set; }

        public string? Label { get; set; }

        public string? Key { get; set; }
    }
}

/// <summary>
/// A downloaded and verified file waiting beside its place. Until <see cref="Commit"/>
/// the build has not changed; <see cref="Discard"/> leaves it that way.
/// </summary>
public sealed class StagedModFile
{
    private readonly ModFileReplacer _owner;
    private readonly ModFileReplacer.Journal _journal = new();
    private readonly List<ModFileReplacer.Entry> _added = new();
    private bool _finished;

    internal StagedModFile(ModFileReplacer owner, ModReplaceRequest request, string stagingPath)
    {
        _owner = owner;
        Request = request;
        StagingPath = stagingPath;
    }

    public ModReplaceRequest Request { get; }

    public string StagingPath { get; }

    /// <summary>The result of a commit; null before it and after an undo.</summary>
    public ModReplaceResult? Result { get; private set; }

    /// <summary>Renames the file into the build and moves the old one aside.</summary>
    /// <exception cref="ModReplaceException">Nothing was changed; the staged file is gone.</exception>
    public ModReplaceResult Commit()
    {
        if (_finished)
        {
            throw new InvalidOperationException("This file was committed or discarded already.");
        }

        _finished = true;

        try
        {
            Result = _owner.Commit(this, _journal, _added);
        }
        catch
        {
            // Whatever stopped it, the hidden file has no further use.
            ModFileReplacer.TryDelete(StagingPath);
            throw;
        }

        return Result;
    }

    /// <summary>Removes the staged file. Safe to call at any time; does nothing after a commit.</summary>
    public void Discard()
    {
        if (Result is null)
        {
            _finished = true;
            ModFileReplacer.TryDelete(StagingPath);
        }
    }

    /// <summary>
    /// Takes a commit back: the old file returns to its name and the new one is removed.
    /// For a group that is committed together, when a later member fails.
    /// </summary>
    public void Undo()
    {
        if (Result is null)
        {
            Discard();
            return;
        }

        _journal.Undo();
        _owner.Forget(Request.GameDirectory, _added);
        _added.Clear();
        Result = null;
    }
}
