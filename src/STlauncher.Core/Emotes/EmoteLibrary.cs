using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace STlauncher.Core.Emotes;

/// <summary>
/// The emotes a build has: the files the player put into its <c>emotes</c> folder, and
/// the ones the Emotecraft mod carries inside its own jar.
/// </summary>
/// <remarks>
/// Everything is read, nothing is written, and nothing is downloaded: the launcher only
/// looks at what is already in the build. A build's list is kept until its files change,
/// so going back to the home screen costs a directory listing and not a re-read.
/// </remarks>
public sealed class EmoteLibrary
{
    /// <summary>More than a list on the home screen can use; a folder of thousands is cut here.</summary>
    public const int MaxEmotes = 300;

    private const int MaxFilesLookedAt = 3000;
    private const string BundledFolder = "assets/emotecraft/emotes/";
    private const string LanguageFolder = "assets/emotecraft/lang/";

    private readonly object _gate = new();
    private readonly object _reading = new();
    private readonly Dictionary<string, (string Stamp, IReadOnlyList<Emote> Emotes)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The build's emotes, the player's own first, each group by name. Slow the first
    /// time and after a change (it opens files): call it off the UI thread.
    /// </summary>
    /// <param name="gameDirectory">The build's game folder, the one with <c>mods</c> in it.</param>
    /// <param name="locale">The game's name for the language to show bundled emotes in: <c>ru_ru</c>, <c>en_us</c>.</param>
    public IReadOnlyList<Emote> Load(string gameDirectory, string locale)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Array.Empty<Emote>();
        }

        try
        {
            var files = Sources(gameDirectory);
            var stamp = Stamp(files, locale);

            lock (_gate)
            {
                if (_cache.TryGetValue(gameDirectory, out var cached) && cached.Stamp == stamp)
                {
                    return cached.Emotes;
                }
            }

            // One read at a time. The home screen asks again on every visit, and a folder
            // that is slow to read would otherwise be read by as many threads as there
            // were visits; the ones that waited find the list already made.
            lock (_reading)
            {
                lock (_gate)
                {
                    if (_cache.TryGetValue(gameDirectory, out var cached) && cached.Stamp == stamp)
                    {
                        return cached.Emotes;
                    }
                }

                var emotes = Read(files, locale);

                lock (_gate)
                {
                    _cache[gameDirectory] = (stamp, emotes);
                }

                return emotes;
            }
        }
        catch (Exception)
        {
            // A folder that cannot be listed is a build without emotes, not an error to show.
            return Array.Empty<Emote>();
        }
    }

    private sealed record SourceFiles(IReadOnlyList<FileInfo> Loose, IReadOnlyList<FileInfo> Jars);

    private static SourceFiles Sources(string gameDirectory)
    {
        var loose = new List<FileInfo>();
        var folder = new DirectoryInfo(Path.Combine(gameDirectory, "emotes"));

        if (folder.Exists)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };

            foreach (var file in folder.EnumerateFiles("*", options).Take(MaxFilesLookedAt))
            {
                if (IsEmoteFile(file.Name))
                {
                    loose.Add(file);
                }
            }
        }

        var jars = new List<FileInfo>();
        var mods = new DirectoryInfo(Path.Combine(gameDirectory, "mods"));

        if (mods.Exists)
        {
            // By name, not by opening every jar of a two-hundred-mod build. A mod that is
            // switched off (".jar.disabled") does not count: the game would not have its emotes.
            jars.AddRange(mods.EnumerateFiles("*.jar")
                .Where(file => file.Name.Contains("emotecraft", StringComparison.OrdinalIgnoreCase) &&
                               file.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)));
        }

        loose.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));
        jars.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));

        return new SourceFiles(loose, jars);
    }

    private static bool IsEmoteFile(string name)
        => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
           name.EndsWith(".emotecraft", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names, sizes and dates: equal stamps mean nothing needs reading again.</summary>
    private static string Stamp(SourceFiles files, string locale)
    {
        var stamp = new StringBuilder(locale);

        foreach (var file in files.Loose.Concat(files.Jars))
        {
            stamp.Append('|').Append(file.FullName)
                .Append(':').Append(file.Length)
                .Append(':').Append(file.LastWriteTimeUtc.Ticks);
        }

        return stamp.ToString();
    }

    private static IReadOnlyList<Emote> Read(SourceFiles files, string locale)
    {
        var own = new List<Emote>();

        foreach (var file in files.Loose)
        {
            if (own.Count >= MaxEmotes)
            {
                break;
            }

            if (file.Length is 0 or > EmoteReader.MaxFileBytes)
            {
                continue;
            }

            try
            {
                if (EmoteReader.TryRead(ReadShared(file.FullName), NameFromFile(file.Name)) is { } emote)
                {
                    own.Add(emote);
                }
            }
            catch (Exception)
            {
                // Locked, gone since the listing, unreadable: the next file.
            }
        }

        var bundled = new List<Emote>();

        foreach (var jar in files.Jars)
        {
            try
            {
                ReadJar(jar.FullName, locale, bundled);
            }
            catch (Exception)
            {
                // Not a zip, or being replaced by an update right now.
            }
        }

        var result = new List<Emote>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var emote in own.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Concat(bundled.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)))
        {
            if (result.Count < MaxEmotes && seen.Add(emote.Name))
            {
                result.Add(emote);
            }
        }

        return result;
    }

    private static void ReadJar(string path, string locale, List<Emote> into)
    {
        // Shared with everything: the game may have the jar open, and the launcher may
        // be about to update it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var english = Language(zip, "en_us");
        var local = string.Equals(locale, "en_us", StringComparison.OrdinalIgnoreCase) ? english : Language(zip, locale);

        string? Translate(string key)
            => local.TryGetValue(key, out var text) && text.Length > 0 ? text
                : english.TryGetValue(key, out text) && text.Length > 0 ? text
                : null;

        foreach (var entry in zip.Entries)
        {
            if (into.Count >= MaxEmotes)
            {
                return;
            }

            if (!entry.FullName.StartsWith(BundledFolder, StringComparison.OrdinalIgnoreCase) ||
                !IsEmoteFile(entry.Name) || entry.Length is 0 or > EmoteReader.MaxFileBytes)
            {
                continue;
            }

            try
            {
                if (EmoteReader.TryRead(ReadEntry(entry), NameFromFile(entry.Name), Translate) is { } emote)
                {
                    into.Add(emote);
                }
            }
            catch (Exception)
            {
                // One bad entry does not take the rest of the jar with it.
            }
        }
    }

    private static Dictionary<string, string> Language(ZipArchive zip, string locale)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var entry = zip.GetEntry(LanguageFolder + locale.ToLowerInvariant() + ".json");

            if (entry is null || entry.Length > EmoteReader.MaxFileBytes)
            {
                return table;
            }

            using var document = JsonDocument.Parse(ReadEntry(entry));

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    property.Name.StartsWith("emotecraft.emote.", StringComparison.Ordinal))
                {
                    table[property.Name] = property.Value.GetString()!;
                }
            }
        }
        catch (Exception)
        {
            // Without the table the emotes are named after their files.
        }

        return table;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(entry.Length, EmoteReader.MaxFileBytes));

        // The length in a zip's directory is a claim; the copy stops at the limit whatever it says.
        var chunk = new byte[81920];
        int read;

        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > EmoteReader.MaxFileBytes)
            {
                return Array.Empty<byte>();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>"club_penguin_dance.json" to "Club penguin dance": what to call an emote that does not say.</summary>
    internal static string NameFromFile(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('-', ' ').Trim();

        return name.Length == 0
            ? fileName
            : char.ToUpper(name[0]) + name[1..];
    }
}
