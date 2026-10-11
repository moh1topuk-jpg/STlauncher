using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Mods;

/// <summary>What the mod list shows for one jar: its title, its version and a small icon.</summary>
/// <param name="Size">The jar's size when this was read.</param>
/// <param name="Stamp">The jar's creation time then, in ticks.</param>
/// <param name="Icon">The icon as an image file's bytes, already brought down to list size; null when the mod has none.</param>
public sealed record ModDisplayEntry(long Size, long Stamp, string? Name, string? Version, byte[]? Icon);

/// <summary>
/// The titles and icons of a build's mods, kept between launches next to the metadata
/// cache. Without it every start opened every jar of the selected build again to learn
/// what it had learnt the day before: a hundred and fifty archives for a list of names.
/// </summary>
/// <remarks>
/// One small file per build, read in one go. An entry holds while the jar has the name,
/// size and creation time it was read with; switching a mod off renames it and changes
/// nothing in it, so the ".disabled" suffix is not part of the name. The jars are only
/// ever read. Not thread-safe: one pass loads it, uses it and saves it.
/// </remarks>
public sealed class ModDisplayCache
{
    /// <summary>Goes up when what is stored changes shape or meaning; files of another revision are ignored.</summary>
    public const int Revision = 1;

    private const string DisabledSuffix = ".disabled";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly Dictionary<string, ModDisplayEntry> _entries;
    private bool _dirty;

    private ModDisplayCache(string path, Dictionary<string, ModDisplayEntry> entries)
    {
        _path = path;
        _entries = entries;
    }

    public int Count => _entries.Count;

    /// <summary>Reads the store; a missing, damaged or foreign file is an empty one.</summary>
    public static ModDisplayCache Load(string path)
    {
        var entries = new Dictionary<string, ModDisplayEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<StoreFile>(File.ReadAllBytes(path), JsonOptions);

                if (stored is { Entries: not null } && stored.Revision == Revision)
                {
                    foreach (var (name, entry) in stored.Entries)
                    {
                        if (entry is not null)
                        {
                            entries[name] = entry;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            entries.Clear();
        }

        return new ModDisplayCache(path, entries);
    }

    /// <summary>What was read from this jar before, if the file is still the one it was read from.</summary>
    public bool TryGet(string fileName, long size, long stamp, out ModDisplayEntry entry)
    {
        if (_entries.TryGetValue(Key(fileName), out var known) && known.Size == size && known.Stamp == stamp)
        {
            entry = known;
            return true;
        }

        entry = null!;
        return false;
    }

    public void Set(string fileName, ModDisplayEntry entry)
    {
        _entries[Key(fileName)] = entry ?? throw new ArgumentNullException(nameof(entry));
        _dirty = true;
    }

    /// <summary>
    /// Forgets the files that are no longer in the folder and writes the rest down, if
    /// anything changed. A cache that cannot be written is only a slower next start.
    /// </summary>
    public void Save(IEnumerable<string>? presentFileNames = null)
    {
        if (presentFileNames is not null)
        {
            var present = presentFileNames.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var gone in _entries.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _entries.Remove(gone);
                _dirty = true;
            }
        }

        if (!_dirty)
        {
            return;
        }

        try
        {
            AtomicFile.Write(_path, stream => JsonSerializer.Serialize(
                stream,
                new StoreFile { Revision = Revision, Entries = _entries },
                JsonOptions));
            _dirty = false;
        }
        catch (Exception)
        {
        }
    }

    private static string Key(string fileName)
        => fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^DisabledSuffix.Length] : fileName;

    private sealed class StoreFile
    {
        public int Revision { get; set; }

        public Dictionary<string, ModDisplayEntry>? Entries { get; set; }
    }
}
