using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace STlauncher.Core.Packs;

/// <summary>
/// What <see cref="ResourcePackInspector"/> read from each pack, kept for as long as the
/// pack is the file it was read from. The pack list is rebuilt whenever anything in the
/// build changes - a mod switched off, a file installed - and each rebuild opened every
/// archive again and added up every file of every unpacked pack.
/// </summary>
/// <remarks>
/// An archive is the same pack while its size and modification time are the same. An
/// unpacked pack has no single file to go by: it is taken as unchanged while its folder,
/// its pack.mcmeta and its pack.png are, which leaves its size as it was last counted;
/// a caller that can afford the walk asks for unpacked packs to be read afresh.
/// Safe to use from several threads.
/// </remarks>
public sealed class ResourcePackCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many packs were actually opened; the rest were answered from memory.</summary>
    public int Misses => _misses;

    private int _misses;

    /// <summary>The same list <see cref="ResourcePackInspector.ListPacks"/> gives, reading only what changed.</summary>
    /// <param name="recountFolders">Read unpacked packs again even when they look unchanged, so their size is current.</param>
    public IReadOnlyList<ResourcePackInfo> ListPacks(string packsDirectory, bool recountFolders = false)
    {
        if (!Directory.Exists(packsDirectory))
        {
            return Array.Empty<ResourcePackInfo>();
        }

        var result = new List<ResourcePackInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ResourcePackInspector.EnumeratePacks(packsDirectory))
        {
            seen.Add(path);
            result.Add(Inspect(path, recountFolders));
        }

        // Packs that left this folder are forgotten; other folders' entries are not this call's to judge.
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packsDirectory)) + Path.DirectorySeparatorChar;

        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !seen.Contains(key))
            {
                _entries.TryRemove(key, out _);
            }
        }

        return result
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ResourcePackInfo Inspect(string path, bool recountFolders)
    {
        var stamp = Stamp(path, out var isFolder);

        if (stamp is not null &&
            !(isFolder && recountFolders) &&
            _entries.TryGetValue(path, out var known) &&
            known.Stamp == stamp)
        {
            return known.Info;
        }

        var info = ResourcePackInspector.Inspect(path);
        System.Threading.Interlocked.Increment(ref _misses);

        if (stamp is not null)
        {
            _entries[path] = new Entry(stamp, info);
        }

        return info;
    }

    /// <summary>What tells one state of a pack from another, or null when it cannot be told.</summary>
    private static string? Stamp(string path, out bool isFolder)
    {
        isFolder = false;

        try
        {
            if (Directory.Exists(path))
            {
                isFolder = true;

                return string.Join('|',
                    Directory.GetLastWriteTimeUtc(path).Ticks,
                    FileStamp(Path.Combine(path, "pack.mcmeta")),
                    FileStamp(Path.Combine(path, "pack.png")));
            }

            return FileStamp(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
    }

    private sealed record Entry(string Stamp, ResourcePackInfo Info);
}
