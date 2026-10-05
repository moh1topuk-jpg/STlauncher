using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Hosting;

/// <summary>How a mod got into the server's folder.</summary>
public enum ServerModOrigin
{
    /// <summary>Nothing says where it is from: put there by hand, most likely.</summary>
    Unknown,

    /// <summary>Copied from the build the server was made from.</summary>
    Build,

    /// <summary>Added from Modrinth on the server's own page.</summary>
    Modrinth
}

/// <summary>One line of the server's record of its mods.</summary>
public sealed class ServerModRecord
{
    /// <summary>The jar's name without the ".disabled" a switched-off mod carries.</summary>
    [JsonPropertyName("file")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("origin")]
    public ServerModOrigin Origin { get; set; }

    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("versionId")]
    public string? VersionId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("addedAt")]
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>A mod in the server's mods folder, as the page shows it.</summary>
/// <param name="FileName">The name on disk, with ".disabled" when the mod is switched off.</param>
/// <param name="BaseName">The jar's name without ".disabled": what the record and the screen address it by.</param>
/// <param name="Title">The mod's own title, or the file name without ".jar" when the jar states none.</param>
/// <param name="ModIds">The ids the jar declares; settings files are matched to the mod by them.</param>
public sealed record ServerModEntry(
    string FileName,
    string BaseName,
    string Path,
    bool Enabled,
    long Size,
    string Title,
    string? Version,
    string? Description,
    byte[]? Icon,
    ServerModOrigin Origin,
    string? ProjectId,
    IReadOnlyList<string> ModIds);

/// <summary>
/// The mods of a server: what is in its folder, where each came from, switching one off
/// and taking one out. Nothing here deletes: a switched-off mod is the same file under
/// another name, and a removed one is moved into <c>.removed</c> inside the server's
/// folder, where it can be taken back from.
/// </summary>
public static class ServerMods
{
    /// <summary>The record of where mods came from, beside server.json.</summary>
    public const string RecordsFileName = "server-mods.json";

    /// <summary>Where removed mods go, inside the server's folder.</summary>
    public const string RemovedFolderName = ".removed";

    private const string DisabledSuffix = ".disabled";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string RecordsPath(string serverDirectory) => Path.Combine(serverDirectory, RecordsFileName);

    /// <summary>
    /// The server's mods, switched-on ones first. A mod without a record is taken as the
    /// build's when the build has a file of the same name: servers made before the record
    /// existed were filled exactly that way.
    /// </summary>
    /// <param name="buildGameDirectory">The game folder of the build the server was made from, when it still exists.</param>
    public static IReadOnlyList<ServerModEntry> List(string serverDirectory, string? buildGameDirectory = null)
    {
        var directory = ModManager.ModsDirectory(serverDirectory);

        if (!Directory.Exists(directory))
        {
            return Array.Empty<ServerModEntry>();
        }

        var records = ReadRecords(serverDirectory).ToDictionary(r => r.FileName, StringComparer.OrdinalIgnoreCase);
        var buildNames = BuildFileNames(buildGameDirectory);
        var result = new List<ServerModEntry>();

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!ModManager.IsModFile(path))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            var enabled = !fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            var baseName = BaseNameOf(fileName);

            var display = ModMetadataReader.ReadDisplay(path);
            var ids = ModMetadataReader.Read(path).Select(m => m.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            records.TryGetValue(baseName, out var record);

            var origin = record?.Origin
                         ?? (buildNames.Contains(baseName) ? ServerModOrigin.Build : ServerModOrigin.Unknown);

            long size = 0;

            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The size is for display only.
            }

            result.Add(new ServerModEntry(
                fileName,
                baseName,
                path,
                enabled,
                size,
                display?.Name ?? record?.Title ?? Path.GetFileNameWithoutExtension(baseName),
                display?.Version,
                FirstLine(display?.Description),
                display?.Icon,
                origin,
                record?.ProjectId,
                ids));
        }

        return result
            .OrderByDescending(m => m.Enabled)
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The record as it is on disk; empty when there is none or it cannot be read.</summary>
    public static IReadOnlyList<ServerModRecord> ReadRecords(string serverDirectory)
    {
        try
        {
            var path = RecordsPath(serverDirectory);

            if (!File.Exists(path))
            {
                return Array.Empty<ServerModRecord>();
            }

            var file = JsonSerializer.Deserialize<RecordsFile>(File.ReadAllText(path), JsonOptions);

            return (file?.Mods ?? new List<ServerModRecord>())
                .Where(r => !string.IsNullOrWhiteSpace(r.FileName))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The record only says where a mod came from; the mods folder is the truth.
            return Array.Empty<ServerModRecord>();
        }
    }

    /// <summary>Notes where a mod came from. A record for the same file is replaced.</summary>
    public static void Record(string serverDirectory, ServerModRecord record)
    {
        if (!IsPlainModName(record.FileName))
        {
            throw new ArgumentException("A mod is recorded by its jar's name.", nameof(record));
        }

        record.FileName = BaseNameOf(record.FileName);

        var records = ReadRecords(serverDirectory)
            .Where(r => !string.Equals(r.FileName, record.FileName, StringComparison.OrdinalIgnoreCase))
            .Append(record)
            .ToList();

        WriteRecords(serverDirectory, records);
    }

    /// <summary>
    /// Notes the mods that were just copied from the build, so they stay "from the build"
    /// even after the build itself changes or is deleted. Files already on record keep theirs.
    /// </summary>
    public static void RecordBuildCopies(string serverDirectory, IEnumerable<string> fileNames)
    {
        var records = ReadRecords(serverDirectory).ToList();
        var known = records.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = false;

        foreach (var name in fileNames.Where(IsPlainModName).Select(BaseNameOf))
        {
            if (known.Add(name))
            {
                records.Add(new ServerModRecord { FileName = name, Origin = ServerModOrigin.Build, AddedAt = DateTimeOffset.UtcNow });
                added = true;
            }
        }

        if (added)
        {
            WriteRecords(serverDirectory, records);
        }
    }

    /// <summary>
    /// Switches a mod on or off by renaming it: off is the same jar with ".disabled" at
    /// the end, which no loader reads. Returns the name the file has afterwards, or null
    /// when there is no such mod or a file of the other name is in the way.
    /// </summary>
    public static string? SetEnabled(string serverDirectory, string fileName, bool enabled)
    {
        if (!IsPlainModName(fileName))
        {
            return null;
        }

        var directory = ModManager.ModsDirectory(serverDirectory);
        var baseName = BaseNameOf(fileName);
        var on = Path.Combine(directory, baseName);
        var off = on + DisabledSuffix;

        var (from, to) = enabled ? (off, on) : (on, off);

        if (!File.Exists(from))
        {
            // Already the way it was asked to be.
            return File.Exists(to) ? Path.GetFileName(to) : null;
        }

        if (File.Exists(to))
        {
            return null;
        }

        File.Move(from, to, overwrite: false);
        return Path.GetFileName(to);
    }

    /// <summary>
    /// Takes a mod out of the server: the jar is moved into <c>.removed</c> inside the
    /// server's folder under a name that says when, and its record is dropped. Returns
    /// where the file now is, or null when there was no such mod.
    /// </summary>
    public static string? Remove(string serverDirectory, string fileName, DateTime? now = null)
    {
        if (!IsPlainModName(fileName))
        {
            return null;
        }

        var source = Path.Combine(ModManager.ModsDirectory(serverDirectory), fileName);

        if (!File.Exists(source))
        {
            return null;
        }

        var removed = Path.Combine(serverDirectory, RemovedFolderName);
        Directory.CreateDirectory(removed);

        var baseName = BaseNameOf(fileName);
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(removed, $"{stamp}-{baseName}");

        // The same mod removed twice within a second: still two files, never an overwrite.
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(removed, $"{stamp}-{n}-{baseName}");
        }

        File.Move(source, target, overwrite: false);

        var records = ReadRecords(serverDirectory);
        var kept = records.Where(r => !string.Equals(r.FileName, baseName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (kept.Count != records.Count)
        {
            WriteRecords(serverDirectory, kept);
        }

        return target;
    }

    /// <summary>The jar's name without the ".disabled" of a switched-off mod.</summary>
    public static string BaseNameOf(string fileName)
        => fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^DisabledSuffix.Length]
            : fileName;

    /// <summary>A mod is addressed by a file name from a list; it is still checked to be a name and not a path.</summary>
    internal static bool IsPlainModName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName) &&
           fileName!.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
           fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           ModManager.IsModFile(fileName);

    private static void WriteRecords(string serverDirectory, IReadOnlyList<ServerModRecord> records)
    {
        var path = RecordsPath(serverDirectory);
        var json = JsonSerializer.Serialize(
            new RecordsFile { Mods = records.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList() },
            JsonOptions);

        // Written beside and moved over: a record cut short would read as "no records".
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static HashSet<string> BuildFileNames(string? buildGameDirectory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(buildGameDirectory))
        {
            return names;
        }

        try
        {
            var directory = ModManager.ModsDirectory(buildGameDirectory!);

            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory).Where(ModManager.IsModFile))
                {
                    names.Add(BaseNameOf(Path.GetFileName(path)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the build's folder the mod is simply of unknown origin.
        }

        return names;
    }

    /// <summary>A row has one line for the description; a mod may have written a paragraph.</summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text!
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

        return string.IsNullOrEmpty(line) ? null : line;
    }

    private sealed class RecordsFile
    {
        [JsonPropertyName("mods")]
        public List<ServerModRecord> Mods { get; set; } = new();
    }
}
