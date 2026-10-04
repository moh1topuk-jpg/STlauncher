using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Hosting;

public enum ModSkipReason
{
    /// <summary>The mod says it is for the client only (shaders, menus, minimaps); a server would not load it or would crash on it.</summary>
    ClientOnly,

    /// <summary>Switched off in the build, so it is not part of what the player plays with.</summary>
    Disabled
}

/// <param name="Name">The mod's own title when the jar states one; otherwise null.</param>
/// <param name="AlreadyThere">True when the server already had this exact file and nothing was written.</param>
public sealed record CopiedMod(string FileName, string? Name, bool AlreadyThere = false);

public sealed record SkippedMod(string FileName, string? Name, ModSkipReason Reason);

/// <param name="Copied">Mods that are (or, for a plan, would be) in the server.</param>
/// <param name="Skipped">Mods left out, each with the reason.</param>
/// <param name="ServerOnly">Files in the server's mods folder that the build does not have. They are left alone.</param>
public sealed record ModCopyResult(
    IReadOnlyList<CopiedMod> Copied,
    IReadOnlyList<SkippedMod> Skipped,
    IReadOnlyList<string> ServerOnly);

/// <param name="FolderName">The folder under <c>saves/</c>, which is how a world is addressed.</param>
public sealed record SaveInfo(string FolderName, string Path, DateTimeOffset LastPlayed, long SizeBytes);

public enum WorldCopyStatus
{
    Copied,

    /// <summary>There is no such world in the build, or it has no level.dat.</summary>
    SourceMissing,

    /// <summary>The world is open in the game right now; a copy taken mid-save would be a broken one.</summary>
    SourceInUse,

    /// <summary>The server already has a world and the caller did not ask to replace it.</summary>
    DestinationExists
}

/// <param name="WorldPath">Where the server's world now is.</param>
/// <param name="PreviousWorldPath">Where the server's earlier world was set aside, when one was replaced.</param>
public sealed record WorldCopyResult(
    WorldCopyStatus Status,
    string? WorldPath = null,
    int Files = 0,
    long Bytes = 0,
    string? PreviousWorldPath = null);

/// <summary>
/// Fills a server from a build: its mods and, if the player wants, one of its worlds.
/// Everything here copies. The build is only ever read; a file of the server's that
/// would be in the way is set aside, not deleted.
/// </summary>
public static partial class ServerContent
{
    /// <summary>Where a replaced world goes inside the server's folder.</summary>
    public const string ReplacedFolderName = ".replaced";

    private const string DisabledSuffix = ".disabled";

    [GeneratedRegex(@"^\s*clientSideOnly\s*=\s*true\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ForgeClientOnlyRegex();

    /// <summary>
    /// What <see cref="CopyMods"/> would do, without touching the server: lets the screen
    /// show the two lists before the player agrees.
    /// </summary>
    public static ModCopyResult PlanMods(string instanceGameDirectory, string? serverDirectory = null)
        => Walk(instanceGameDirectory, serverDirectory, copy: false, CancellationToken.None);

    /// <summary>
    /// Copies the build's mods into the server's <c>mods</c> folder, leaving out the ones
    /// that are client-only or switched off. The build's files are not changed.
    /// </summary>
    public static ModCopyResult CopyMods(
        string instanceGameDirectory,
        string serverDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory))
        {
            throw new ArgumentException("A server folder is required.", nameof(serverDirectory));
        }

        return Walk(instanceGameDirectory, serverDirectory, copy: true, cancellationToken);
    }

    /// <summary>
    /// True when the jar declares itself client-only: <c>"environment": "client"</c> in
    /// fabric.mod.json, the same under <c>minecraft</c> in quilt.mod.json, or
    /// <c>clientSideOnly=true</c> in a Forge mods.toml. A jar that says nothing is taken
    /// as needed on both sides.
    /// </summary>
    public static bool IsClientOnly(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            if (archive.GetEntry("fabric.mod.json") is { } fabric)
            {
                using var doc = ParseJson(fabric);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       IsClient(doc.RootElement, "environment");
            }

            if (archive.GetEntry("quilt.mod.json") is { } quilt)
            {
                using var doc = ParseJson(quilt);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty("minecraft", out var minecraft) &&
                       minecraft.ValueKind == JsonValueKind.Object &&
                       IsClient(minecraft, "environment");
            }

            foreach (var name in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
            {
                if (archive.GetEntry(name) is { } toml)
                {
                    using var reader = new StreamReader(toml.Open());
                    return ForgeClientOnlyRegex().IsMatch(reader.ReadToEnd());
                }
            }
        }
        catch (Exception)
        {
            // Not a readable jar: nothing says it is client-only, and the server will
            // report it itself if it cannot load it.
        }

        return false;
    }

    /// <summary>The single-player worlds of a build, most recently played first.</summary>
    public static IReadOnlyList<SaveInfo> ListWorlds(string instanceGameDirectory)
    {
        var saves = Path.Combine(instanceGameDirectory, "saves");

        if (!Directory.Exists(saves))
        {
            return Array.Empty<SaveInfo>();
        }

        var result = new List<SaveInfo>();

        foreach (var directory in Directory.EnumerateDirectories(saves))
        {
            var level = new FileInfo(Path.Combine(directory, "level.dat"));

            if (!level.Exists)
            {
                continue;
            }

            long size = 0;

            try
            {
                size = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The size is for display; a file that cannot be seen is not a reason to hide the world.
            }

            result.Add(new SaveInfo(Path.GetFileName(directory), directory, level.LastWriteTimeUtc, size));
        }

        return result.OrderByDescending(s => s.LastPlayed).ToList();
    }

    /// <summary>
    /// Copies one single-player world of a build into the server's world folder. The
    /// original stays where it is, untouched. When the server already has a world it is
    /// replaced only if <paramref name="replaceExisting"/> is true, and then the old one
    /// is moved into <c>.replaced/</c> inside the server's folder rather than deleted.
    /// </summary>
    public static WorldCopyResult CopyWorld(
        string instanceGameDirectory,
        string saveFolderName,
        string serverDirectory,
        bool replaceExisting = false,
        IProgress<long>? bytesCopied = null,
        CancellationToken cancellationToken = default)
    {
        var saves = Path.GetFullPath(Path.Combine(instanceGameDirectory, "saves"));
        var source = Path.GetFullPath(Path.Combine(saves, saveFolderName ?? string.Empty));

        // The name comes from a list on the screen, but it is still a path segment.
        if (!string.Equals(Path.GetDirectoryName(source), saves, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(source, "level.dat")))
        {
            return new WorldCopyResult(WorldCopyStatus.SourceMissing);
        }

        if (IsWorldOpen(source))
        {
            return new WorldCopyResult(WorldCopyStatus.SourceInUse);
        }

        var target = WorldDirectory(serverDirectory);
        string? previous = null;

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            if (!replaceExisting)
            {
                return new WorldCopyResult(WorldCopyStatus.DestinationExists, target);
            }

            var replaced = Path.Combine(serverDirectory, ReplacedFolderName);
            Directory.CreateDirectory(replaced);

            previous = Path.Combine(
                replaced,
                Path.GetFileName(target) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

            Directory.Move(target, previous);
        }

        var files = 0;
        long bytes = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);

            // The lock belongs to whoever has the world open; the server makes its own.
            if (string.Equals(relative, "session.lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);

            files++;
            bytes += new FileInfo(destination).Length;
            bytesCopied?.Report(bytes);
        }

        return new WorldCopyResult(WorldCopyStatus.Copied, target, files, bytes, previous);
    }

    /// <summary>The folder the server keeps its world in: <c>level-name</c> from server.properties, "world" by default.</summary>
    public static string WorldDirectory(string serverDirectory)
    {
        var name = ServerProperties.Load(ServerProperties.PathIn(serverDirectory)).Get(ServerProperties.LevelNameKey)?.Trim();

        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.'))
        {
            name = ServerProperties.DefaultLevelName;
        }

        return Path.Combine(serverDirectory, name);
    }

    /// <summary>True when the server already has a world on disk.</summary>
    public static bool HasWorld(string serverDirectory)
        => File.Exists(Path.Combine(WorldDirectory(serverDirectory), "level.dat"));

    private static ModCopyResult Walk(string instanceGameDirectory, string? serverDirectory, bool copy, CancellationToken cancellationToken)
    {
        var copied = new List<CopiedMod>();
        var skipped = new List<SkippedMod>();

        var source = ModManager.ModsDirectory(instanceGameDirectory);
        var target = serverDirectory is null ? null : ModManager.ModsDirectory(serverDirectory);
        var fromBuild = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(source))
        {
            foreach (var file in Directory.EnumerateFiles(source).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(file);

                if (fileName.StartsWith('.'))
                {
                    continue;
                }

                if (fileName.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(new SkippedMod(fileName, TitleOf(file), ModSkipReason.Disabled));
                    continue;
                }

                if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = TitleOf(file);

                if (IsClientOnly(file))
                {
                    skipped.Add(new SkippedMod(fileName, title, ModSkipReason.ClientOnly));
                    continue;
                }

                fromBuild.Add(fileName);

                var destination = target is null ? null : Path.Combine(target, fileName);
                var already = destination is not null && IsSameFile(file, destination);

                if (copy && !already)
                {
                    Directory.CreateDirectory(target!);

                    // The same name with other contents is the build's newer copy of the
                    // mod; the player asked for the server to have the build's mods.
                    File.Copy(file, destination!, overwrite: true);
                }

                copied.Add(new CopiedMod(fileName, title, already));
            }
        }

        var serverOnly = target is not null && Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*.jar")
                .Select(Path.GetFileName)
                .Where(n => n is not null && !fromBuild.Contains(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        return new ModCopyResult(copied, skipped, serverOnly);
    }

    private static bool IsSameFile(string source, string destination)
    {
        var a = new FileInfo(source);
        var b = new FileInfo(destination);

        return b.Exists && a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
    }

    private static string? TitleOf(string jarPath)
    {
        var name = ModMetadataReader.Read(jarPath).FirstOrDefault()?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static bool IsClient(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String &&
           string.Equals(value.GetString(), "client", StringComparison.OrdinalIgnoreCase);

    private static JsonDocument ParseJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }

    /// <summary>
    /// The game holds <c>session.lock</c> open for as long as the world is loaded, so a
    /// lock that cannot be opened exclusively means somebody is playing in it. Opened for
    /// reading only: the check must not change the player's world either.
    /// </summary>
    private static bool IsWorldOpen(string worldDirectory)
    {
        var path = Path.Combine(worldDirectory, "session.lock");

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
