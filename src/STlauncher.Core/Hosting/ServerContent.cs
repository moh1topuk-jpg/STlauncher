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
    Disabled,

    /// <summary>
    /// A Forge or NeoForge mod that does not say "client only" in so many words, but
    /// declares that it needs the game and the loader on the client side only. Left out
    /// as a client mod; the screen says that this is read from its dependencies.
    /// </summary>
    LikelyClientOnly
}

/// <summary>How firmly a jar says it is for the client only; see <see cref="ServerContent.ClientSide"/>.</summary>
public enum ModClientSide
{
    /// <summary>The jar says nothing either way, or says it is for both sides. It is copied.</summary>
    NotStated,

    /// <summary>Not said outright, but its dependencies on the game are for the client side only.</summary>
    Implied,

    /// <summary>The mod's own metadata says it is for the client only.</summary>
    Stated
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

    /// <summary>Added to the world folder's name while a copy is being made beside it.</summary>
    public const string CopyingSuffix = ".copying";

    private const string DisabledSuffix = ".disabled";

    [GeneratedRegex(@"^\s*clientSideOnly\s*=\s*true\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ForgeClientOnlyRegex();

    [GeneratedRegex(@"^\s*\[")]
    private static partial Regex TomlTableRegex();

    [GeneratedRegex(@"^\s*\[\[\s*dependencies\s*\.")]
    private static partial Regex TomlDependencyRegex();

    [GeneratedRegex(@"^\s*(?<key>modId|side)\s*=\s*[""'](?<value>[^""']*)[""']")]
    private static partial Regex TomlStringRegex();

    /// <summary>The game and the loaders themselves, as mods.toml names them in a dependency.</summary>
    private static readonly HashSet<string> PlatformIds = new(StringComparer.OrdinalIgnoreCase) { "minecraft", "forge", "neoforge" };

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

        var result = Walk(instanceGameDirectory, serverDirectory, copy: true, cancellationToken);

        // Noted at the copy, so the server's mods page can still say "from the build"
        // after the build itself has changed or been deleted. The record is only a label:
        // a copy that worked is not reported as failed because the label could not be written.
        try
        {
            ServerMods.RecordBuildCopies(serverDirectory, result.Copied.Select(m => m.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>True when <see cref="ClientSide"/> finds any statement that the jar is for the client only.</summary>
    public static bool IsClientOnly(string jarPath) => ClientSide(jarPath) != ModClientSide.NotStated;

    /// <summary>
    /// What the jar itself says about being for the client only. The rule, in full:
    /// <list type="bullet">
    /// <item><see cref="ModClientSide.Stated"/>: <c>"environment": "client"</c> in
    /// fabric.mod.json, the same under <c>minecraft</c> in quilt.mod.json, or a top-level
    /// <c>clientSideOnly = true</c> (above the first table) in mods.toml or
    /// neoforge.mods.toml - the key with which Forge since 1.20.4 itself skips the mod
    /// on a dedicated server. NeoForge does not read it, but a mod that writes it says
    /// the same thing in the same words.</item>
    /// <item><see cref="ModClientSide.Implied"/>: a Forge or NeoForge mod without that
    /// key whose dependencies on the game and the loader (<c>minecraft</c>,
    /// <c>forge</c>, <c>neoforge</c>) are all declared with <c>side = "CLIENT"</c>, at
    /// least one of them. The author says the mod needs the game only on the client;
    /// that is not the same as saying a server must not have it.</item>
    /// <item><see cref="ModClientSide.NotStated"/>: everything else, which is most
    /// Forge mods. <c>displayTest</c> is not used: it says the mod may be missing on
    /// one side without saying which. The annotations of 1.12 and older are not read.</item>
    /// </list>
    /// A jar carrying metadata for several loaders counts as client-only only when all
    /// of them agree.
    /// </summary>
    public static ModClientSide ClientSide(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var verdicts = new List<ModClientSide>();

            if (archive.GetEntry("fabric.mod.json") is { } fabric)
            {
                using var doc = ParseJson(fabric);
                verdicts.Add(Stated(doc.RootElement.ValueKind == JsonValueKind.Object && IsClient(doc.RootElement, "environment")));
            }
            else if (archive.GetEntry("quilt.mod.json") is { } quilt)
            {
                using var doc = ParseJson(quilt);
                verdicts.Add(Stated(
                    doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("minecraft", out var minecraft) &&
                    minecraft.ValueKind == JsonValueKind.Object &&
                    IsClient(minecraft, "environment")));
            }

            foreach (var name in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
            {
                if (archive.GetEntry(name) is { } toml)
                {
                    using var reader = new StreamReader(toml.Open());
                    verdicts.Add(ForgeClientSide(reader.ReadToEnd()));
                }
            }

            return verdicts.Count == 0 ? ModClientSide.NotStated : verdicts.Min();
        }
        catch (Exception)
        {
            // Not a readable jar: nothing says it is client-only, and the server will
            // report it itself if it cannot load it.
            return ModClientSide.NotStated;
        }
    }

    private static ModClientSide Stated(bool clientOnly) => clientOnly ? ModClientSide.Stated : ModClientSide.NotStated;

    /// <summary>Reads the side out of a mods.toml; see <see cref="ClientSide"/> for the rule.</summary>
    private static ModClientSide ForgeClientSide(string toml)
    {
        var platform = 0;
        var clientOnly = 0;
        string? modId = null;
        string? side = null;
        var inDependency = false;
        var inAnyTable = false;

        void Close()
        {
            if (inDependency && modId is not null && PlatformIds.Contains(modId))
            {
                platform++;

                if (string.Equals(side, "CLIENT", StringComparison.OrdinalIgnoreCase))
                {
                    clientOnly++;
                }
            }

            inDependency = false;
            modId = null;
            side = null;
        }

        foreach (var line in toml.Split('\n'))
        {
            if (TomlTableRegex().IsMatch(line))
            {
                Close();
                inAnyTable = true;
                inDependency = TomlDependencyRegex().IsMatch(line);
                continue;
            }

            // Only above the first table is it the file's own key: the same word inside
            // a [[mods]] or [[dependencies]] entry is not what the loader reads.
            if (!inAnyTable && ForgeClientOnlyRegex().IsMatch(line))
            {
                return ModClientSide.Stated;
            }

            if (!inDependency)
            {
                continue;
            }

            var pair = TomlStringRegex().Match(line);

            if (pair.Success && pair.Groups["key"].Value == "modId")
            {
                modId = pair.Groups["value"].Value;
            }
            else if (pair.Success && pair.Groups["key"].Value == "side")
            {
                side = pair.Groups["value"].Value;
            }
        }

        Close();

        return platform > 0 && clientOnly == platform ? ModClientSide.Implied : ModClientSide.NotStated;
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
        var occupied = Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any();

        if (occupied && !replaceExisting)
        {
            return new WorldCopyResult(WorldCopyStatus.DestinationExists, target);
        }

        // Copied beside the world's place and moved in whole. A copy cut short - the disk
        // full, a file locked, the launcher closed - must never be left looking like a
        // world: the server would start on it and build the missing regions anew, over
        // what the player had made. What is found here was left by exactly such a copy.
        var staging = target + CopyingSuffix;

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
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

            var destination = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);

            files++;
            bytes += new FileInfo(destination).Length;
            bytesCopied?.Report(bytes);
        }

        // Only now, with the whole copy on disk, does the server's earlier world step aside.
        string? previous = null;

        if (occupied)
        {
            var replaced = Path.Combine(serverDirectory, ReplacedFolderName);
            Directory.CreateDirectory(replaced);

            previous = Path.Combine(
                replaced,
                Path.GetFileName(target) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

            Directory.Move(target, previous);
        }
        else if (Directory.Exists(target))
        {
            Directory.Delete(target);
        }

        Directory.Move(staging, target);

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

                var side = ClientSide(file);

                if (side != ModClientSide.NotStated)
                {
                    skipped.Add(new SkippedMod(
                        fileName,
                        title,
                        side == ModClientSide.Stated ? ModSkipReason.ClientOnly : ModSkipReason.LikelyClientOnly));
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
