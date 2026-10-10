using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace STlauncher.Core.Storage;

/// <summary>What the store holds and what it spares, for the line in Settings.</summary>
/// <param name="Objects">Files in the store.</param>
/// <param name="StoreBytes">Their size: what the shared files really take on disk.</param>
/// <param name="SavedBytes">
/// What the builds would take on top of that if every one kept its own copy. Null where
/// the system does not say how many names a file has.
/// </param>
/// <param name="UnusedBytes">Objects no build is linked to: what a clean-up gives back.</param>
public sealed record SharedStoreUsage(int Objects, long StoreBytes, long? SavedBytes, long UnusedBytes);

/// <summary>What one clean-up did.</summary>
/// <param name="LinkedFiles">Build files that were separate copies and are now names of one file.</param>
/// <param name="LinkedBytes">Disk space that gave back.</param>
/// <param name="RemovedObjects">Store objects no build uses, deleted.</param>
/// <param name="SkippedFiles">Identical files that could not be linked: held open, or the disk cannot link.</param>
public sealed record SharedStoreCleanup(int LinkedFiles, long LinkedBytes, int RemovedObjects, long RemovedBytes, int SkippedFiles);

/// <param name="Done">Files looked at so far.</param>
/// <param name="Total">Files to look at in this stage.</param>
public sealed record SharedStoreProgress(SharedStoreStage Stage, int Done, int Total, string? FileName);

public enum SharedStoreStage
{
    Scanning,
    Comparing,
    Linking,
    Removing
}

/// <summary>
/// One copy of a file on disk for all builds. Twenty builds with the same Sodium, Iris and
/// resource packs used to keep twenty copies; here each of them holds a hard link to one
/// object under <c>objects/&lt;first two hex of sha1&gt;/&lt;sha1&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// A hard link is a second name of the same file, so deleting a mod or a whole build only
/// removes names, and the object stays for the others. The other side of that: whatever
/// writes <i>into</i> a linked file changes it under every name. Hence the two rules the
/// rest of the launcher keeps. Nothing replaces a file in a shared folder by writing into
/// it - a new file is written beside and renamed over (<see cref="FileReplace"/>, the
/// downloader's .part file). And an object is hashed again every time before it is handed
/// to another build: if something outside the launcher did write through a link, the
/// object no longer matches its name, is thrown away, and the file is downloaded.
/// </para>
/// <para>
/// Only what is downloaded once and then left alone is shared: jars and zips lying
/// directly in a build's mods, resourcepacks, shaderpacks and datapacks folders, 64 KiB
/// and up. Worlds, configs, logs and screenshots are written by the game and are never
/// touched; neither are unpacked packs, which players do edit in place. A build kept in
/// an outside folder is left alone too - another launcher works in it by its own rules.
/// </para>
/// <para>
/// Where the disk cannot link, every call here answers "no" and the caller keeps a plain
/// file, exactly as before the store existed.
/// </para>
/// </remarks>
public sealed class SharedFileStore
{
    public const string DirectoryName = "objects";

    /// <summary>Below this a link saves less than the bookkeeping is worth.</summary>
    public const long MinimumSize = 64 * 1024;

    /// <summary>The build folders whose files are shared.</summary>
    public static readonly IReadOnlyList<string> SharedFolders = new[] { "mods", "resourcepacks", "shaderpacks", "datapacks" };

    private const string DisabledSuffix = ".disabled";

    private readonly string _instancesPrefix;
    private readonly Func<string, string, bool> _link;
    private readonly Func<string, FileIdentity?> _identity;

    public SharedFileStore(LauncherPaths paths)
        : this(Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).Root, DirectoryName), paths.Instances)
    {
    }

    /// <param name="link">(existing file, new name) to success. Tests pass one that refuses, as a FAT disk does.</param>
    /// <param name="identity">Which file a path names; a function answering null stands for a system that cannot say.</param>
    public SharedFileStore(
        string objectsDirectory,
        string instancesDirectory,
        Func<string, string, bool>? link = null,
        Func<string, FileIdentity?>? identity = null)
    {
        ObjectsDirectory = Path.GetFullPath(objectsDirectory);
        InstancesDirectory = Path.GetFullPath(instancesDirectory);
        _instancesPrefix = InstancesDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _link = link ?? HardLink.TryCreate;
        _identity = identity ?? HardLink.TryGetIdentity;
    }

    public string ObjectsDirectory { get; }

    public string InstancesDirectory { get; }

    /// <summary>
    /// Off: new downloads neither enter the store nor come from it. What is already linked
    /// stays linked, and the clean-up still works when the player asks for it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where the object with this SHA-1 lives, or null when the string is not a SHA-1.</summary>
    public string? ObjectPath(string? sha1)
    {
        // The hash comes from a catalog or a modpack and becomes part of a path: anything
        // but forty hex digits is refused rather than combined.
        if (sha1 is null || sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
        {
            return null;
        }

        var name = sha1.ToLowerInvariant();
        return Path.Combine(ObjectsDirectory, name[..2], name);
    }

    /// <summary>
    /// True for <c>instances/&lt;build&gt;/&lt;shared folder&gt;/&lt;name&gt;.jar|.zip</c>, switched off or
    /// not. Says nothing about the size: the file may not exist yet.
    /// </summary>
    public bool IsShareable(string path)
    {
        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        if (!full.StartsWith(_instancesPrefix, PathComparison))
        {
            return false;
        }

        var parts = full[_instancesPrefix.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return parts.Length == 3 &&
               SharedFolders.Contains(parts[1], StringComparer.OrdinalIgnoreCase) &&
               IsSharedName(parts[2]);
    }

    private static bool IsSharedName(string fileName)
    {
        if (fileName.StartsWith('.'))
        {
            return false;
        }

        if (fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^DisabledSuffix.Length];
        }

        return fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Before a download: gives the build a link to the stored object instead. The object
    /// is hashed first, every time - all its names are one file, so anything that wrote
    /// through a link in some build changed the object as well. One that no longer
    /// matches is deleted and the caller downloads as usual.
    /// </summary>
    /// <param name="verify">
    /// Checks the object against what the caller expects, when it has a stronger hash
    /// than the SHA-1 in the name. Null means the SHA-1 itself.
    /// </param>
    /// <returns>True when <paramref name="destination"/> now is the wanted file.</returns>
    public bool TryLinkInto(string? sha1, string destination, Func<string, bool>? verify = null)
    {
        if (!Enabled || ObjectPath(sha1) is not { } objectPath || !IsShareable(destination))
        {
            return false;
        }

        try
        {
            if (!File.Exists(objectPath))
            {
                return false;
            }

            var good = verify is not null
                ? verify(objectPath)
                : string.Equals(ComputeSha1(objectPath, CancellationToken.None), Path.GetFileName(objectPath), StringComparison.Ordinal);

            if (!good)
            {
                TryDelete(objectPath);
                return false;
            }

            return ReplaceWithLink(objectPath, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The same for a file that is not to appear in the build yet: the link is made under
    /// <paramref name="stagingPath"/>, a name of the caller's choosing in the same folder,
    /// and the caller renames it to <paramref name="destination"/> when everything else
    /// about the replacement is settled. Nothing existing is touched here.
    /// </summary>
    public bool TryLinkStaged(string? sha1, string stagingPath, string destination, Func<string, bool>? verify = null)
    {
        if (!Enabled || ObjectPath(sha1) is not { } objectPath || !IsShareable(destination))
        {
            return false;
        }

        try
        {
            if (!File.Exists(objectPath) || File.Exists(stagingPath))
            {
                return false;
            }

            var good = verify is not null
                ? verify(objectPath)
                : string.Equals(ComputeSha1(objectPath, CancellationToken.None), Path.GetFileName(objectPath), StringComparison.Ordinal);

            if (!good)
            {
                TryDelete(objectPath);
                return false;
            }

            return _link(objectPath, stagingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// After a verified download: the file becomes the store's object, so the next build
    /// that needs it links instead of downloading. Never throws - a download that
    /// succeeded must not fail because sharing did not work out.
    /// </summary>
    /// <param name="knownSha1">The SHA-1 the file was just verified against; null to compute it.</param>
    public void Adopt(string path, string? knownSha1 = null)
    {
        if (!Enabled || !IsShareable(path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists || info.Length < MinimumSize)
            {
                return;
            }

            var objectPath = ObjectPath(knownSha1) ?? ObjectPath(ComputeSha1(path, CancellationToken.None));

            if (objectPath is null)
            {
                return;
            }

            if (!File.Exists(objectPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
                _link(path, objectPath);
                return;
            }

            if (IsLinkedTo(path, objectPath))
            {
                return;
            }

            // The object was there and this file was downloaded anyway: its hash was not
            // known beforehand. A good object takes the new copy's place; a damaged one
            // gives way to it.
            if (new FileInfo(objectPath).Length == info.Length &&
                string.Equals(ComputeSha1(objectPath, CancellationToken.None), Path.GetFileName(objectPath), StringComparison.Ordinal))
            {
                ReplaceWithLink(objectPath, path);
            }
            else if (TryDelete(objectPath))
            {
                _link(path, objectPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// How much the store holds and spares. Reads names and sizes only, hashes nothing.
    /// </summary>
    public SharedStoreUsage Measure(CancellationToken cancellationToken = default)
    {
        var objects = EnumerateObjects().ToList();
        long storeBytes = 0, saved = 0, unused = 0;
        var known = true;

        foreach (var entry in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            storeBytes += entry.Length;

            if (entry.Identity is not { } identity)
            {
                known = false;
                continue;
            }

            // One name is the object's own; the rest are builds.
            var users = identity.Links - 1;

            if (users <= 0)
            {
                unused += entry.Length;
            }
            else
            {
                saved += (users - 1) * entry.Length;
            }
        }

        return known
            ? new SharedStoreUsage(objects.Count, storeBytes, saved, unused)
            : new SharedStoreUsage(objects.Count, storeBytes, null, 0);
    }

    /// <summary>
    /// The clean-up the player asks for. First, files that are the same in several builds
    /// but were installed as separate copies - before the store existed, by an import, by
    /// a restored backup - become links to one object. Then objects no build is linked to
    /// are removed. A build never loses a file: each one is replaced by a link to a file
    /// with the same hash, or left exactly as it was.
    /// </summary>
    public SharedStoreCleanup Optimize(IProgress<SharedStoreProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new SharedStoreProgress(SharedStoreStage.Scanning, 0, 0, null));

        var files = EnumerateBuildFiles(cancellationToken).ToList();
        var objects = EnumerateObjects().ToDictionary(o => Path.GetFileName(o.Path), StringComparer.Ordinal);
        var objectSizes = objects.Values.Select(o => o.Length).ToHashSet();

        // Build files already linked to an object need no hashing: their hash is its name.
        var linkedTo = new Dictionary<string, string>(PathComparer);

        foreach (var (sha1, entry) in objects)
        {
            foreach (var file in files.Where(f => f.Length == entry.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsSameFile(file, entry))
                {
                    linkedTo[file.Path] = sha1;
                }
            }
        }

        // Only a file whose size is met again somewhere can have a twin.
        var bySize = files.Where(f => !linkedTo.ContainsKey(f.Path)).ToLookup(f => f.Length);
        var toHash = bySize
            .Where(g => g.Count() > 1 || objectSizes.Contains(g.Key))
            .SelectMany(g => g)
            .ToList();

        var byHash = new Dictionary<string, List<StoredFile>>(StringComparer.Ordinal);
        var done = 0;

        foreach (var file in toHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SharedStoreProgress(SharedStoreStage.Comparing, done++, toHash.Count, Path.GetFileName(file.Path)));

            string sha1;

            try
            {
                sha1 = ComputeSha1(file.Path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!byHash.TryGetValue(sha1, out var list))
            {
                byHash[sha1] = list = new List<StoredFile>();
            }

            list.Add(file);
        }

        var used = new HashSet<string>(linkedTo.Values, StringComparer.Ordinal);
        var linkedFiles = 0;
        var skipped = 0;
        long linkedBytes = 0;
        done = 0;

        var groups = byHash.Where(g => g.Value.Count > 1 || objects.ContainsKey(g.Key)).ToList();
        var toLink = groups.Sum(g => g.Value.Count);

        foreach (var (sha1, twins) in groups)
        {
            var objectPath = ObjectPath(sha1)!;
            var rest = twins;

            // An object that stopped matching its name must not be handed to anyone.
            if (objects.ContainsKey(sha1) && !ObjectIsIntact(objectPath, cancellationToken))
            {
                if (!TryDelete(objectPath))
                {
                    skipped += twins.Count;
                    done += twins.Count;
                    continue;
                }

                objects.Remove(sha1);
                used.Remove(sha1);
            }

            if (!File.Exists(objectPath))
            {
                // The first twin becomes the object; it is linked by that very act.
                Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);

                if (!_link(twins[0].Path, objectPath))
                {
                    skipped += twins.Count;
                    done += twins.Count;
                    continue;
                }

                used.Add(sha1);
                rest = twins.Skip(1).ToList();
                done++;
            }

            // Several names of one file give the space back once, when the last is moved over.
            var copies = new HashSet<(uint, ulong)>();

            foreach (var twin in rest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SharedStoreProgress(SharedStoreStage.Linking, done++, toLink, Path.GetFileName(twin.Path)));

                if (ReplaceWithLink(objectPath, twin.Path))
                {
                    used.Add(sha1);
                    linkedFiles++;

                    if (twin.Identity is not { } identity || copies.Add((identity.Volume, identity.Index)))
                    {
                        linkedBytes += twin.Length;
                    }
                }
                else
                {
                    skipped++;
                }
            }
        }

        var removed = 0;
        long removedBytes = 0;
        var stale = EnumerateObjects().Where(o => !used.Contains(Path.GetFileName(o.Path))).ToList();
        done = 0;

        foreach (var entry in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SharedStoreProgress(SharedStoreStage.Removing, done++, stale.Count, null));

            if (TryDelete(entry.Path))
            {
                removed++;
                removedBytes += entry.Length;
            }
        }

        return new SharedStoreCleanup(linkedFiles, linkedBytes, removed, removedBytes, skipped);
    }

    /// <summary>SHA-1 of a file in lower-case hex, the way object names are spelled.</summary>
    public static string ComputeSha1(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 16];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private bool ObjectIsIntact(string objectPath, CancellationToken cancellationToken)
    {
        try
        {
            return string.Equals(ComputeSha1(objectPath, cancellationToken), Path.GetFileName(objectPath), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes <paramref name="path"/> a name of the object. The link is made under a
    /// temporary name beside it and renamed over: whatever was at the path is replaced as
    /// a directory entry, never opened, and a failure leaves it as it was.
    /// </summary>
    private bool ReplaceWithLink(string objectPath, string path)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.part";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (!_link(objectPath, temp))
            {
                return false;
            }

            File.Move(temp, path, overwrite: true);

            // Renaming one name of a file over another name of the same file does
            // nothing on Unix and leaves both: the temporary one is ours to remove.
            if (File.Exists(temp))
            {
                TryDelete(temp);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open by the running game, read-only, gone: the file stays as it is.
            TryDelete(temp);
            return false;
        }
    }

    private bool IsLinkedTo(string path, string objectPath)
        => _identity(path) is { } a && _identity(objectPath) is { } b && a.IsSameFile(b);

    /// <summary>
    /// Where the system does not say which names are one file, the answer is "no": the
    /// file is then hashed and linked again, which changes nothing when it already was.
    /// </summary>
    private static bool IsSameFile(StoredFile file, StoredFile entry)
        => file.Identity is { } a && entry.Identity is { } b && a.IsSameFile(b);

    private IEnumerable<StoredFile> EnumerateObjects()
    {
        if (!Directory.Exists(ObjectsDirectory))
        {
            yield break;
        }

        foreach (var shard in SafeEnumerate(() => Directory.EnumerateDirectories(ObjectsDirectory)))
        {
            foreach (var path in SafeEnumerate(() => Directory.EnumerateFiles(shard)))
            {
                // Only what the store itself would have put there, in the place it would
                // have put it. Anything else is not ours to count or to delete.
                if (ObjectPath(Path.GetFileName(path)) is { } expected &&
                    string.Equals(expected, path, PathComparison) &&
                    Describe(path) is { } entry)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Every shareable file of every build under <c>instances/</c>, 64 KiB and up.</summary>
    private IEnumerable<StoredFile> EnumerateBuildFiles(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(InstancesDirectory))
        {
            yield break;
        }

        foreach (var build in SafeEnumerate(() => Directory.EnumerateDirectories(InstancesDirectory)))
        {
            foreach (var folder in SharedFolders)
            {
                var directory = Path.Combine(build, folder);

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var path in SafeEnumerate(() => Directory.EnumerateFiles(directory)))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (IsSharedName(Path.GetFileName(path)) && Describe(path) is { Length: >= MinimumSize } file)
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    private StoredFile? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);

            // A link somebody else made into the folder (a symbolic one) is not followed.
            if (!info.Exists || info.LinkTarget is not null)
            {
                return null;
            }

            return new StoredFile(path, info.Length, _identity(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> SafeEnumerate(Func<IEnumerable<string>> list)
    {
        try
        {
            return list().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record StoredFile(string Path, long Length, FileIdentity? Identity);
}
