using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Skins;

/// <summary>One skin of the library: a PNG named after the id, and what the index says about it.</summary>
public sealed class SkinEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>"classic" or "slim".</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "classic";

    [JsonPropertyName("added")]
    public DateTimeOffset AddedAt { get; set; }

    [JsonIgnore]
    public SkinModel SkinModel
    {
        get => string.Equals(Model, "slim", StringComparison.OrdinalIgnoreCase) ? SkinModel.Slim : SkinModel.Classic;
        set => Model = value == SkinModel.Slim ? "slim" : "classic";
    }
}

/// <summary>
/// The player's own skins: PNG files in one folder and a small index beside them with
/// the name, the model and the date of each, and which one is worn.
/// </summary>
/// <remarks>
/// The index is the list; a PNG it does not mention is left alone, and an entry whose
/// PNG has gone is simply not shown. Nothing here decodes an image: the bytes come in
/// already checked and go to disk as they are.
/// </remarks>
public sealed class SkinLibrary
{
    private sealed class IndexFile
    {
        [JsonPropertyName("worn")]
        public string? Worn { get; set; }

        [JsonPropertyName("skins")]
        public List<SkinEntry> Skins { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _indexPath;
    private IndexFile? _index;

    public SkinLibrary(string directory)
    {
        Folder = directory ?? throw new ArgumentNullException(nameof(directory));
        _indexPath = Path.Combine(directory, "index.json");
    }

    public string Folder { get; }

    /// <summary>Where the copies handed to a skin site are put.</summary>
    public string PublishDirectory => Path.Combine(Folder, "publish");

    public string PathOf(string id) => Path.Combine(Folder, id + ".png");

    /// <summary>The skins whose files are there, the newest first.</summary>
    public IReadOnlyList<SkinEntry> List()
    {
        lock (_gate)
        {
            return Load().Skins
                .Where(entry => File.Exists(PathOf(entry.Id)))
                .OrderByDescending(entry => entry.AddedAt)
                .ToList();
        }
    }

    public SkinEntry? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        lock (_gate)
        {
            var entry = Load().Skins.FirstOrDefault(e => e.Id == id);
            return entry is not null && File.Exists(PathOf(entry.Id)) ? entry : null;
        }
    }

    /// <summary>The skin the launcher's figure wears, or null when it wears none from here.</summary>
    public SkinEntry? Worn
    {
        get
        {
            lock (_gate)
            {
                return Find(Load().Worn);
            }
        }
    }

    public void SetWorn(string? id)
    {
        lock (_gate)
        {
            Load().Worn = id;
            Save();
        }
    }

    public SkinEntry Add(string name, SkinModel model, byte[] png, DateTimeOffset? addedAt = null)
    {
        ArgumentNullException.ThrowIfNull(png);

        lock (_gate)
        {
            var index = Load();
            var entry = new SkinEntry
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = UniqueName(index, name, exceptId: null),
                SkinModel = model,
                AddedAt = addedAt ?? DateTimeOffset.Now
            };

            AtomicFile.Write(PathOf(entry.Id), stream => stream.Write(png));
            index.Skins.Add(entry);
            Save();
            return entry;
        }
    }

    /// <summary>The editor's save: new pixels, and the model they were drawn for.</summary>
    public bool Update(string id, byte[] png, SkinModel model)
    {
        ArgumentNullException.ThrowIfNull(png);

        lock (_gate)
        {
            var entry = Load().Skins.FirstOrDefault(e => e.Id == id);

            if (entry is null)
            {
                return false;
            }

            AtomicFile.Write(PathOf(id), stream => stream.Write(png));
            entry.SkinModel = model;
            Save();
            return true;
        }
    }

    public SkinEntry? Rename(string id, string name)
    {
        lock (_gate)
        {
            var index = Load();
            var entry = index.Skins.FirstOrDefault(e => e.Id == id);

            if (entry is null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            entry.Name = UniqueName(index, name, exceptId: id);
            Save();
            return entry;
        }
    }

    public SkinEntry? Duplicate(string id, string name)
    {
        lock (_gate)
        {
            var source = Load().Skins.FirstOrDefault(e => e.Id == id);

            if (source is null || !File.Exists(PathOf(id)))
            {
                return null;
            }

            return Add(name, source.SkinModel, File.ReadAllBytes(PathOf(id)));
        }
    }

    /// <summary>Removes the skin and its file. The caller has already asked the player.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            var index = Load();
            var entry = index.Skins.FirstOrDefault(e => e.Id == id);

            if (entry is null)
            {
                return false;
            }

            index.Skins.Remove(entry);

            if (index.Worn == id)
            {
                index.Worn = null;
            }

            Save();

            try
            {
                File.Delete(PathOf(id));
            }
            catch (IOException)
            {
                // The entry is gone from the index, so the file is only a leftover.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return true;
        }
    }

    /// <summary>"Knight", then "Knight 2", "Knight 3": two cards with one name cannot be told apart.</summary>
    private static string UniqueName(IndexFile index, string wanted, string? exceptId)
    {
        var name = string.IsNullOrWhiteSpace(wanted) ? "Skin" : wanted.Trim();

        if (name.Length > 40)
        {
            name = name[..40].TrimEnd();
        }

        bool Taken(string candidate) => index.Skins.Any(e =>
            e.Id != exceptId && string.Equals(e.Name, candidate, StringComparison.CurrentCultureIgnoreCase));

        if (!Taken(name))
        {
            return name;
        }

        for (var number = 2; ; number++)
        {
            var candidate = $"{name} {number}";

            if (!Taken(candidate))
            {
                return candidate;
            }
        }
    }

    private IndexFile Load()
    {
        if (_index is not null)
        {
            return _index;
        }

        try
        {
            if (File.Exists(_indexPath))
            {
                _index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(_indexPath), JsonOptions);
            }
        }
        catch (JsonException)
        {
            // An unreadable index is an empty library, not a crash; the PNGs stay where they are.
        }
        catch (IOException)
        {
        }

        _index ??= new IndexFile();
        _index.Skins ??= new List<SkinEntry>();

        // An id is a file name. One that is anything but letters and digits did not come
        // from here, and must never be followed out of the folder.
        _index.Skins.RemoveAll(entry => entry is null || string.IsNullOrEmpty(entry.Id) || !entry.Id.All(char.IsAsciiLetterOrDigit));
        return _index;
    }

    private void Save()
        => AtomicFile.WriteAllText(_indexPath, JsonSerializer.Serialize(Load(), JsonOptions));
}
