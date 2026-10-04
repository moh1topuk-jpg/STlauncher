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
///
/// The index is also the one file whose loss would cost the player every name at once,
/// so it is never written unless it was read first: an index that cannot be read makes
/// every change throw, and one that is damaged is set aside and rebuilt from the files.
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

    /// <summary>An id is this many lowercase hex digits, and the PNG is named after it.</summary>
    private const int IdLength = 12;

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

    /// <summary>
    /// False while the index is on disk but could not be read: what <see cref="List"/>
    /// gave then is "not known yet", not "no skins", and is worth asking for again.
    /// </summary>
    public bool IsRead
    {
        get
        {
            lock (_gate)
            {
                return LoadForReading() is not null;
            }
        }
    }

    /// <summary>The skins whose files are there, the newest first.</summary>
    public IReadOnlyList<SkinEntry> List()
    {
        lock (_gate)
        {
            if (LoadForReading() is not { } index)
            {
                return Array.Empty<SkinEntry>();
            }

            return index.Skins
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
            var entry = LoadForReading()?.Skins.FirstOrDefault(e => e.Id == id);
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
                return Find(LoadForReading()?.Worn);
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
                Id = Guid.NewGuid().ToString("N")[..IdLength],
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

    /// <summary>
    /// The index, read once and kept. Throws when the file is there and cannot be read:
    /// held by an antivirus or a sync client, a cloud placeholder that is offline. That
    /// is not an empty library, so nothing is kept and the next call reads again - and,
    /// since every change starts here, nothing is saved over a list that was never read.
    /// </summary>
    private IndexFile Load()
    {
        if (_index is not null)
        {
            return _index;
        }

        IndexFile? index = null;

        if (File.Exists(_indexPath))
        {
            var text = File.ReadAllText(_indexPath);

            try
            {
                index = JsonSerializer.Deserialize<IndexFile>(text, JsonOptions);
            }
            catch (JsonException)
            {
                index = Rebuild();
            }
        }

        index ??= new IndexFile();
        index.Skins ??= new List<SkinEntry>();

        // An id is a file name. One that is anything but letters and digits did not come
        // from here, and must never be followed out of the folder.
        index.Skins.RemoveAll(entry => entry is null || string.IsNullOrEmpty(entry.Id) || !entry.Id.All(char.IsAsciiLetterOrDigit));
        return _index = index;
    }

    /// <summary>
    /// For looking, not for changing: an index that cannot be read right now shows as an
    /// empty library instead of an error, and is read again at the next look.
    /// </summary>
    private IndexFile? LoadForReading()
    {
        try
        {
            return Load();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The index is there and is not JSON any more. Saving the next change over it would
    /// leave every skin a file the launcher never lists again, so the damaged file is set
    /// aside as <c>index.json.bad</c> and the list is put together from the skins
    /// themselves. Their names and models were in the index and are gone; the pictures,
    /// which are what the player made, are all back on the page.
    /// </summary>
    private IndexFile Rebuild()
    {
        var index = new IndexFile();

        try
        {
            File.Copy(_indexPath, _indexPath + ".bad", overwrite: true);
        }
        catch (IOException)
        {
            // The copy is a courtesy to whoever wants to mend the file by hand.
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.png").OrderBy(f => f.LastWriteTimeUtc))
            {
                var id = Path.GetFileNameWithoutExtension(file.Name);

                // Only what Add names its files: a picture somebody put here by hand is not adopted.
                if (id.Length == IdLength && id.All(char.IsAsciiHexDigitLower))
                {
                    index.Skins.Add(new SkinEntry
                    {
                        Id = id,
                        Name = UniqueName(index, "Skin", exceptId: null),
                        AddedAt = file.LastWriteTimeUtc
                    });
                }
            }
        }
        catch (IOException)
        {
            // Whatever was found so far is still more than nothing.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return index;
    }

    private void Save()
        => AtomicFile.WriteAllText(_indexPath, JsonSerializer.Serialize(Load(), JsonOptions));
}
