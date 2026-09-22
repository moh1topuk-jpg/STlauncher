using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Http;

/// <summary>
/// Remembers which files have already been hashed and found good. Every launch used to
/// hash the whole game - four thousand asset objects, a hundred libraries, the client
/// jar - before pressing Play meant anything, which on a laptop disk was the whole wait.
/// A file whose size and modification time have not moved since it was verified is
/// taken as still good; a file that changed at all is hashed again.
/// </summary>
public sealed class VerifiedFileCache
{
    public const string FileName = "verified-files.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string? _path;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _dirty;

    /// <summary>An in-memory cache, for tests and tools that keep nothing between runs.</summary>
    public VerifiedFileCache()
    {
    }

    public VerifiedFileCache(LauncherPaths paths)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        _path = Path.Combine(paths.Meta, FileName);
        Load();
    }

    public int Count => _entries.Count;

    /// <summary>
    /// True when the file was verified before and has not changed since. The hash is
    /// compared too: a manifest that now names a different hash for the same path must
    /// not be satisfied by yesterday's verification.
    /// </summary>
    public bool IsVerified(string path, string hash, FileInfo info)
    {
        if (!_entries.TryGetValue(path, out var entry))
        {
            return false;
        }

        return entry.Size == info.Length &&
               entry.ModifiedTicks == info.LastWriteTimeUtc.Ticks &&
               string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase);
    }

    public void Remember(string path, string hash, FileInfo info)
    {
        _entries[path] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, hash.ToLowerInvariant());
        _dirty = 1;
    }

    public void Forget(string path)
    {
        if (_entries.TryRemove(path, out _))
        {
            _dirty = 1;
        }
    }

    /// <summary>Everything is hashed again on the next run: what "re-download the game files" promises.</summary>
    public void Clear()
    {
        _entries.Clear();
        _dirty = 1;
        Save();
    }

    /// <summary>Writes the cache if anything changed. Called once per batch, not per file.</summary>
    public void Save()
    {
        if (_path is null || System.Threading.Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var snapshot = new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception)
        {
            // Losing the cache costs one slow launch; it must never cost the launch itself.
            _dirty = 1;
        }
    }

    private void Load()
    {
        try
        {
            if (_path is null || !File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), JsonOptions);

            if (stored is null)
            {
                return;
            }

            foreach (var (path, entry) in stored)
            {
                _entries[path] = entry;
            }
        }
        catch (Exception)
        {
            // A damaged cache is an empty cache.
            _entries.Clear();
        }
    }

    private sealed record Entry(
        [property: JsonPropertyName("s")] long Size,
        [property: JsonPropertyName("m")] long ModifiedTicks,
        [property: JsonPropertyName("h")] string Hash);
}
