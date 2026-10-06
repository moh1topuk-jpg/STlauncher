using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace STlauncher.Core.Hosting;

public enum ConfigFormat
{
    Properties,
    Toml,
    Json,
    Json5,
    Yaml,
    Cfg,
    Conf
}

/// <summary>A settings file of one of the server's mods.</summary>
/// <param name="RelativePath">From the server's folder, with forward slashes: "config/voicechat/voicechat-server.properties".</param>
public sealed record ServerConfigFile(string RelativePath, long Size, ConfigFormat Format)
{
    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];

    /// <summary>Larger than the launcher opens; such a file is listed and left to an editor.</summary>
    public bool TooLarge => Size > ServerConfigFiles.MaxBytes;
}

/// <summary>Settings files sorted by the mod they belong to.</summary>
/// <param name="ByMod">Keyed by <see cref="ServerModEntry.BaseName"/>.</param>
/// <param name="Other">Files no mod could be named for.</param>
public sealed record ServerConfigIndex(
    IReadOnlyDictionary<string, IReadOnlyList<ServerConfigFile>> ByMod,
    IReadOnlyList<ServerConfigFile> Other);

public enum ConfigFileStatus
{
    Ok,
    Missing,

    /// <summary>Over <see cref="ServerConfigFiles.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>The path does not stay inside the server's folder, or is not a settings file.</summary>
    NotAllowed,

    /// <summary>The file is not text.</summary>
    NotText
}

/// <summary>How the file's bytes were read, so that it is written back the same way.</summary>
public enum ConfigEncoding
{
    Utf8,
    Utf8WithBom,

    /// <summary>Not valid UTF-8: an old .properties file in ISO-8859-1.</summary>
    Latin1
}

public sealed record ConfigReadResult(ConfigFileStatus Status, string Text = "", ConfigEncoding Encoding = ConfigEncoding.Utf8)
{
    public bool IsOk => Status == ConfigFileStatus.Ok;
}

/// <param name="BackupPath">Where the previous content was kept; null when the file did not exist before.</param>
public sealed record ConfigWriteResult(ConfigFileStatus Status, string? BackupPath = null)
{
    public bool IsOk => Status == ConfigFileStatus.Ok;
}

/// <summary>
/// The settings files of a server's mods: finding them, telling which mod each belongs
/// to, and reading and writing one as text. server.properties is not one of them - the
/// page has its own fields for it. Every path given here is checked to stay inside the
/// server's folder, and a write keeps the previous content beside the file.
/// </summary>
public static class ServerConfigFiles
{
    /// <summary>The largest file opened or written. Settings are small; a megabyte of "settings" is data.</summary>
    public const long MaxBytes = 512 * 1024;

    public const string ConfigFolderName = "config";

    /// <summary>Forge and NeoForge keep a world's server settings inside the world.</summary>
    public const string WorldConfigFolderName = "serverconfig";

    /// <summary>The middle of a backup's name: <c>name.toml.bak-20261005-101500</c>.</summary>
    public const string BackupMarker = ".bak-";

    private const int MaxDepth = 4;
    private const int MaxFiles = 600;

    private static readonly Dictionary<string, ConfigFormat> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".properties"] = ConfigFormat.Properties,
        [".toml"] = ConfigFormat.Toml,
        [".json"] = ConfigFormat.Json,
        [".json5"] = ConfigFormat.Json5,
        [".yaml"] = ConfigFormat.Yaml,
        [".yml"] = ConfigFormat.Yaml,
        [".cfg"] = ConfigFormat.Cfg,
        [".conf"] = ConfigFormat.Conf
    };

    /// <summary>
    /// The settings files in the server's <c>config</c> folder and, for a Forge world, in
    /// its <c>serverconfig</c>. Only the kinds of file a person edits are listed.
    /// </summary>
    public static IReadOnlyList<ServerConfigFile> Discover(string serverDirectory)
    {
        var root = Path.GetFullPath(serverDirectory);
        var result = new List<ServerConfigFile>();

        Walk(Path.Combine(root, ConfigFolderName), 0);

        try
        {
            Walk(Path.Combine(ServerContent.WorldDirectory(root), WorldConfigFolderName), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // server.properties could not be read: the config folder is still listed.
        }

        return result
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        void Walk(string directory, int depth)
        {
            if (depth > MaxDepth || result.Count >= MaxFiles || !Directory.Exists(directory) || IsLink(directory))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (result.Count >= MaxFiles)
                    {
                        return;
                    }

                    var name = Path.GetFileName(file);

                    if (name.StartsWith('.') || !Formats.TryGetValue(Path.GetExtension(name), out var format) || IsLink(file))
                    {
                        continue;
                    }

                    result.Add(new ServerConfigFile(
                        Path.GetRelativePath(root, file).Replace('\\', '/'),
                        new FileInfo(file).Length,
                        format));
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!Path.GetFileName(child).StartsWith('.'))
                    {
                        Walk(child, depth + 1);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed has no files to offer.
            }
        }
    }

    /// <summary>
    /// Sorts files by mod. A file is a mod's when its name, or the folder it is in right
    /// under <c>config</c>, begins with the mod's id or title: "voicechat/…",
    /// "lithium.properties", "jei-server.toml". Anything less clear stays in
    /// <see cref="ServerConfigIndex.Other"/> rather than being guessed at.
    /// </summary>
    public static ServerConfigIndex Match(IReadOnlyList<ServerConfigFile> files, IReadOnlyList<ServerModEntry> mods)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var mod in mods)
        {
            foreach (var key in mod.ModIds.Append(mod.Title).Select(Normalize))
            {
                // One letter is not a name, and the first mod to claim a key keeps it.
                if (key.Length > 1)
                {
                    owners.TryAdd(key, mod.BaseName);
                }
            }
        }

        var byMod = new Dictionary<string, List<ServerConfigFile>>(StringComparer.OrdinalIgnoreCase);
        var other = new List<ServerConfigFile>();

        foreach (var file in files)
        {
            var owner = Candidates(file).Select(OwnerOf).FirstOrDefault(o => o is not null);

            if (owner is null)
            {
                other.Add(file);
                continue;
            }

            if (!byMod.TryGetValue(owner, out var list))
            {
                byMod[owner] = list = new List<ServerConfigFile>();
            }

            list.Add(file);
        }

        return new ServerConfigIndex(
            byMod.ToDictionary(p => p.Key, p => (IReadOnlyList<ServerConfigFile>)p.Value, StringComparer.OrdinalIgnoreCase),
            other);

        string? OwnerOf(string name)
        {
            var tokens = name.Split(new[] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // The longest beginning wins: "simple-voice-chat-server" is Simple Voice Chat's
            // before it is anything called "simple".
            for (var count = tokens.Length; count > 0; count--)
            {
                if (owners.TryGetValue(Normalize(string.Concat(tokens.Take(count))), out var owner))
                {
                    return owner;
                }
            }

            return null;
        }
    }

    /// <summary>Reads a settings file as text. The path is relative to the server's folder.</summary>
    public static ConfigReadResult Read(string serverDirectory, string relativePath)
    {
        if (Resolve(serverDirectory, relativePath) is not { } path)
        {
            return new ConfigReadResult(ConfigFileStatus.NotAllowed);
        }

        var info = new FileInfo(path);

        if (!info.Exists)
        {
            return new ConfigReadResult(ConfigFileStatus.Missing);
        }

        if (info.Length > MaxBytes)
        {
            return new ConfigReadResult(ConfigFileStatus.TooLarge);
        }

        var bytes = File.ReadAllBytes(path);

        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return new ConfigReadResult(ConfigFileStatus.NotText);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new ConfigReadResult(ConfigFileStatus.Ok, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), ConfigEncoding.Utf8WithBom);
        }

        try
        {
            return new ConfigReadResult(ConfigFileStatus.Ok, new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return new ConfigReadResult(ConfigFileStatus.Ok, Encoding.Latin1.GetString(bytes), ConfigEncoding.Latin1);
        }
    }

    /// <summary>
    /// Writes a settings file. What the file held before is first copied beside it as
    /// <c>name.bak-yyyyMMdd-HHmmss</c>; the new content is written to a temporary file
    /// and moved over, so a write cut short leaves the old file whole.
    /// </summary>
    public static ConfigWriteResult Write(
        string serverDirectory,
        string relativePath,
        string text,
        ConfigEncoding encoding = ConfigEncoding.Utf8,
        DateTime? now = null)
    {
        if (Resolve(serverDirectory, relativePath) is not { } path)
        {
            return new ConfigWriteResult(ConfigFileStatus.NotAllowed);
        }

        var bytes = Encode(text ?? string.Empty, encoding);

        if (bytes.Length > MaxBytes)
        {
            return new ConfigWriteResult(ConfigFileStatus.TooLarge);
        }

        string? backup = null;

        if (File.Exists(path))
        {
            var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            backup = path + BackupMarker + stamp;

            // Two saves within one second: both earlier contents are kept.
            for (var n = 2; File.Exists(backup); n++)
            {
                backup = path + BackupMarker + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            File.Copy(path, backup, overwrite: false);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Our own temporary file; the write's own error is the one to report.
            }

            throw;
        }

        return new ConfigWriteResult(ConfigFileStatus.Ok, backup);
    }

    /// <summary>
    /// The full path of a settings file, or null when the relative path leaves the
    /// server's folder, passes through a link, or is not one of the kinds of file that
    /// are edited here. The path may come from a list on the screen; it is still a path.
    /// </summary>
    public static string? Resolve(string serverDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory) || string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) || relativePath!.Contains(':'))
        {
            return null;
        }

        // A backslash is a separator on Windows and a plain character elsewhere; read it as a
        // separator everywhere, so "..\" means the same on every system and is refused.
        relativePath = relativePath.Replace('\\', '/');

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverDirectory));
        string full;

        try
        {
            full = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Path.GetFileName(full);

        // The server's own files in its root are not mod settings: server.properties,
        // server.json, the whitelist and the record of mods all have their own editors.
        if (!Formats.ContainsKey(Path.GetExtension(name)) ||
            string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A link inside the folder could point anywhere; neither it nor anything under it is followed.
        for (var current = full; current is not null && current.Length > root.Length; current = Path.GetDirectoryName(current))
        {
            if (IsLink(current))
            {
                return null;
            }
        }

        return full;
    }

    /// <summary>A name reduced to its letters and digits in lower case, which is how ids, titles and file names are compared.</summary>
    public static string Normalize(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : new string(text!.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The names a file can be claimed by: its folder right under the config root first, then its own name.</summary>
    private static IEnumerable<string> Candidates(ServerConfigFile file)
    {
        var segments = file.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var configAt = Array.FindIndex(
            segments,
            s => string.Equals(s, ConfigFolderName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s, WorldConfigFolderName, StringComparison.OrdinalIgnoreCase));

        // A folder under config is the mod's own; the file's name comes second.
        if (configAt >= 0 && configAt + 2 < segments.Length)
        {
            yield return segments[configAt + 1];
        }

        yield return Path.GetFileNameWithoutExtension(segments[^1]);
    }

    private static byte[] Encode(string text, ConfigEncoding encoding) => encoding switch
    {
        ConfigEncoding.Utf8WithBom => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(),

        // A character Latin-1 does not have would be written as "?": the file becomes UTF-8 instead.
        ConfigEncoding.Latin1 when text.All(c => c <= 0xFF) => Encoding.Latin1.GetBytes(text),
        _ => new UTF8Encoding(false).GetBytes(text)
    };

    private static bool IsLink(string path)
    {
        try
        {
            // Not the reparse attribute alone: a folder synced by OneDrive carries it too
            // and is no link. A target is what a symbolic link or a junction has.
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Exists && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not there yet (a new file) or not readable: not a link we would follow.
            return false;
        }
    }
}
