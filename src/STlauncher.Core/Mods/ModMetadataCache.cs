using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Mods;

/// <summary>
/// What <see cref="ModMetadataReader.Read"/> answered for the jars of one build, kept so
/// the next check does not unzip two hundred files again - and the fifty jars inside
/// Fabric API with them. An answer holds for as long as the file has the same name, size
/// and modification time and the reader is the same revision
/// (<see cref="ModMetadataReader.ParserRevision"/>). Kept in memory and, when a path is
/// given, in a small file of the launcher's own; the jars are only ever read.
/// </summary>
public sealed class ModMetadataCache
{
    private const string DisabledSuffix = ".disabled";

    private static readonly ConcurrentDictionary<string, ModMetadataCache> Stores = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string? _storePath;
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private bool _dirty;

    /// <param name="storePath">Where the answers live between launches; null keeps them for this run only.</param>
    public ModMetadataCache(string? storePath = null)
    {
        _storePath = string.IsNullOrWhiteSpace(storePath) ? null : storePath;
    }

    /// <summary>The one cache for a store file, so every check of a build shares what was read.</summary>
    public static ModMetadataCache For(string storePath)
        => Stores.GetOrAdd(Path.GetFullPath(storePath), path => new ModMetadataCache(path));

    /// <summary>How many jars were actually opened; the rest were answered from memory.</summary>
    public int Misses { get; private set; }

    /// <summary>The jar's metadata, from the cache when the file is the one it was read from.</summary>
    public IReadOnlyList<ModMetadata> Read(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);
        long size;
        long stamp;

        try
        {
            var info = new FileInfo(jarPath);
            size = info.Length;
            stamp = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception)
        {
            return ModMetadataReader.Read(jarPath);
        }

        // Switching a mod off renames the file and changes nothing in it.
        var key = Key(fileName);

        lock (_gate)
        {
            Load();

            if (_entries.TryGetValue(key, out var known) && known.Size == size && known.Stamp == stamp)
            {
                return known.Sections.All(s => string.Equals(s.FileName, fileName, StringComparison.Ordinal))
                    ? known.Sections
                    : known.Sections.Select(s => s with { FileName = fileName }).ToList();
            }
        }

        var sections = ModMetadataReader.Read(jarPath).ToList();

        lock (_gate)
        {
            _entries[key] = new Entry { Size = size, Stamp = stamp, Sections = sections };
            _dirty = true;
            Misses++;
        }

        return sections;
    }

    /// <summary>
    /// Forgets the files that are no longer in the folder and writes the rest down, if
    /// anything changed. A cache that cannot be written is only a slower next check.
    /// </summary>
    public void Save(IEnumerable<string>? presentFileNames = null)
    {
        lock (_gate)
        {
            Load();

            if (presentFileNames is not null)
            {
                var present = presentFileNames.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var gone in _entries.Keys.Where(k => !present.Contains(k)).ToList())
                {
                    _entries.Remove(gone);
                    _dirty = true;
                }
            }

            if (!_dirty || _storePath is null)
            {
                _dirty = false;
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);

                var temp = _storePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(
                    new StoreFile { Revision = ModMetadataReader.ParserRevision, Entries = _entries },
                    JsonOptions));
                File.Move(temp, _storePath, overwrite: true);
                _dirty = false;
            }
            catch (Exception)
            {
            }
        }
    }

    private static string Key(string fileName)
        => fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^DisabledSuffix.Length] : fileName;

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        if (_storePath is null || !File.Exists(_storePath))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_storePath), JsonOptions);

            // Another revision read these jars: what it understood is not what this one would.
            if (stored is { Entries: not null } && stored.Revision == ModMetadataReader.ParserRevision)
            {
                _entries = new Dictionary<string, Entry>(
                    stored.Entries.Where(e => e.Value?.Sections is not null && e.Value.Sections.All(IsWhole)),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>A file edited by hand or cut short must not hand out records with holes in them.</summary>
    private static bool IsWhole(ModMetadata? section)
        => section is { Id: not null, Name: not null, Version: not null, FileName: not null, Dependencies: not null, Provides: not null, Conflicts: not null, Bundled: not null } &&
           section.Dependencies.All(d => d?.Id is not null) &&
           section.Provides.All(p => p is not null) &&
           section.Conflicts.All(c => c?.Id is not null) &&
           section.Bundled.All(b => b?.Id is not null);

    private sealed class StoreFile
    {
        public int Revision { get; set; }

        public Dictionary<string, Entry>? Entries { get; set; }
    }

    private sealed class Entry
    {
        public long Size { get; set; }

        public long Stamp { get; set; }

        public List<ModMetadata> Sections { get; set; } = new();
    }
}
