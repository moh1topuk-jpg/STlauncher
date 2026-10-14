using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using STlauncher.Core.Java;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Modpacks;

/// <summary>What a file of a shared build is, told from the folder it goes into.</summary>
public enum BuildFileKind
{
    Mod,
    ResourcePack,
    Shader,

    /// <summary>Settings and everything else: config/, a file in the build's root.</summary>
    Other
}

/// <summary>One file a code would download: its name, where it goes, how big, and who serves it.</summary>
public sealed record BuildPreviewFile(string Name, string Path, long Size, string Source, BuildFileKind Kind);

/// <summary>The files of one kind, by name, and what they weigh together.</summary>
public sealed record BuildPreviewGroup(BuildFileKind Kind, IReadOnlyList<BuildPreviewFile> Files, long Size);

/// <summary>
/// What adding a build from a code would put on the computer, worked out from the code
/// alone: nothing here touches the disk or the network.
/// </summary>
public sealed record BuildCodePreview(
    BuildCodePayload Payload,
    IReadOnlyList<BuildPreviewGroup> Groups,
    int FileCount,
    long TotalSize,
    IReadOnlyList<string> Missing)
{
    /// <summary>The longest name a build takes from a code; the player may type another.</summary>
    public const int MaxNameLength = 64;

    /// <summary>How many jars go into mods/: what a new build's memory is set from.</summary>
    public int ModCount => Payload.Files.Count(f =>
        f.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
        f.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

    public static BuildCodePreview From(BuildCodePayload payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var groups = payload.Files
            .Select(f => new BuildPreviewFile(NameOf(f.Path), f.Path, Math.Max(0, f.Size), SourceOf(f.Url), KindOf(f.Path)))
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var files = g.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return new BuildPreviewGroup(g.Key, files, files.Sum(f => f.Size));
            })
            .ToList();

        // A name is all a missing file has; one that came with folders in it is shown by its last part.
        var missing = payload.Missing
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => CleanText(NameOf(name), 120))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BuildCodePreview(payload, groups, groups.Sum(g => g.Files.Count), groups.Sum(g => g.Size), missing);
    }

    /// <summary>mods/, resourcepacks/, shaderpacks/; anything else is settings and the rest.</summary>
    public static BuildFileKind KindOf(string? path)
    {
        var normal = (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var slash = normal.IndexOf('/');
        var folder = slash < 0 ? string.Empty : normal[..slash];

        return folder.ToLowerInvariant() switch
        {
            "mods" => BuildFileKind.Mod,
            "resourcepacks" => BuildFileKind.ResourcePack,
            "shaderpacks" => BuildFileKind.Shader,
            _ => BuildFileKind.Other
        };
    }

    /// <summary>
    /// Who serves the file, for the tag beside it: the site's name for the hosts a code
    /// may name, the bare host for anything else. Told from the address alone.
    /// </summary>
    public static string SourceOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var host = uri.Host.ToLowerInvariant();

        if (host == "cdn.modrinth.com")
        {
            return "Modrinth";
        }

        if (Mods.CurseForgeClient.IsCdnUrl(url) || host.EndsWith(".forgecdn.net", StringComparison.Ordinal))
        {
            return "CurseForge";
        }

        return host switch
        {
            "github.com" or "raw.githubusercontent.com" => "GitHub",
            "gitlab.com" => "GitLab",
            _ => host
        };
    }

    /// <summary>
    /// The name a build would get from a code: one line, no control characters, not
    /// longer than a list row shows. A code is text from somebody else.
    /// </summary>
    public static string CleanName(string? name)
    {
        var clean = CleanText(name, MaxNameLength);
        return clean.Length == 0 ? "Shared build" : clean;
    }

    /// <summary>
    /// Whether a build already holds exactly the mods the code names: the same file
    /// names in mods/, nothing more and nothing less. Names only - no file is read.
    /// </summary>
    public bool HasSameMods(IEnumerable<string> modFileNames)
    {
        var wanted = Payload.Files
            .Where(f => KindOf(f.Path) == BuildFileKind.Mod)
            .Select(f => NameOf(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return wanted.Count > 0 && wanted.SetEquals(modFileNames ?? Array.Empty<string>());
    }

    private static string NameOf(string path)
    {
        var normal = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        var slash = normal.LastIndexOf('/');

        return slash < 0 ? normal : normal[(slash + 1)..];
    }

    private static string CleanText(string? text, int max)
    {
        var builder = new StringBuilder();

        foreach (var c in text ?? string.Empty)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        var clean = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return clean.Length <= max ? clean : clean[..max].TrimEnd();
    }
}

/// <summary>What is wrong with one file of a code, for the page to put into words.</summary>
public enum BuildCodeProblemKind
{
    /// <summary>The entry is not an entry: a part is missing, or the size is not a number.</summary>
    Broken,

    /// <summary>The path leaves the build's folder.</summary>
    UnsafePath,

    /// <summary>Two entries write the same file.</summary>
    DuplicatePath,

    /// <summary>The address is not https, or not on a host the installer downloads from.</summary>
    HostNotAllowed,

    /// <summary>No hash to check the file against, or one that cannot be a hash.</summary>
    BadHash
}

/// <param name="Path">The file as the code names it; empty when the entry had no name.</param>
/// <param name="Host">For <see cref="BuildCodeProblemKind.HostNotAllowed"/>: where the code points.</param>
public sealed record BuildCodeProblem(BuildCodeProblemKind Kind, string Path, string Host);

public enum BuildCodeVerdict
{
    /// <summary>The text holds no build code this launcher reads.</summary>
    NotACode,

    /// <summary>A build code the launcher will not act on; <see cref="BuildCodeReading.Problems"/> says why.</summary>
    Refused,

    /// <summary>A build code that can be shown and, if the player agrees, added.</summary>
    Ready
}

public sealed record BuildCodeReading(
    BuildCodeVerdict Verdict,
    BuildCodePreview? Preview,
    IReadOnlyList<BuildCodeProblem> Problems);

/// <summary>
/// Reads a build code for showing. Everything that would stop the installer - an address
/// on a host it does not download from, a path out of the build, a hash that is not one -
/// is found here, from the code alone, so the player is told before a build exists rather
/// than after an empty one was made. A code with anything wrong in it is refused whole:
/// "this build without the files I did not like" is not what the friend sent.
/// </summary>
public static class BuildCodeInspector
{
    public static BuildCodeReading Read(string? text)
    {
        if (!BuildCode.TryDecode(text, out var payload, out var rejected) || payload is null)
        {
            return new BuildCodeReading(BuildCodeVerdict.NotACode, null, Array.Empty<BuildCodeProblem>());
        }

        var problems = new List<BuildCodeProblem>();

        foreach (var entry in rejected)
        {
            problems.Add(new BuildCodeProblem(
                entry.Reason switch
                {
                    BuildCodeRejection.UnsafePath => BuildCodeProblemKind.UnsafePath,
                    BuildCodeRejection.HostNotAllowed => BuildCodeProblemKind.HostNotAllowed,
                    _ => BuildCodeProblemKind.Broken
                },
                entry.Path,
                HostOf(entry.Url)));
        }

        problems.AddRange(Check(payload));

        return problems.Count > 0
            ? new BuildCodeReading(BuildCodeVerdict.Refused, null, problems)
            : new BuildCodeReading(BuildCodeVerdict.Ready, BuildCodePreview.From(payload), problems);
    }

    /// <summary>
    /// The installer's own check of the list (<see cref="ModpackIndexCheck"/>), run on
    /// the code: what it would refuse after the build was made is refused before.
    /// </summary>
    public static IReadOnlyList<BuildCodeProblem> Check(BuildCodePayload payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var plan = new ModpackPlan(
            payload.Name,
            "1.0.0",
            payload.GameVersion,
            payload.Loader,
            payload.LoaderVersion,
            payload.Files.Select(f => new ModpackFilePlan(f.Path, f.Url, f.Sha1, null, f.Size)).ToList(),
            HasOverrides: false);

        var urls = payload.Files
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Url, StringComparer.Ordinal);

        return ModpackIndexCheck.Check(plan)
            .Select(p => new BuildCodeProblem(
                p.Kind switch
                {
                    ModpackIndexProblemKind.UnsafePath => BuildCodeProblemKind.UnsafePath,
                    ModpackIndexProblemKind.DuplicatePath => BuildCodeProblemKind.DuplicatePath,
                    ModpackIndexProblemKind.HostNotAllowed => BuildCodeProblemKind.HostNotAllowed,
                    _ => BuildCodeProblemKind.BadHash
                },
                p.Path,
                p.Kind == ModpackIndexProblemKind.HostNotAllowed && urls.TryGetValue(p.Path, out var url) ? HostOf(url) : string.Empty))
            .ToList();
    }

    private static string HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Scheme + "://" + uri.Host : string.Empty;
}

/// <summary>Whether something a build needs is on the computer, as far as a look at the folders tells.</summary>
public enum Presence
{
    /// <summary>Not to be told without the network or without starting things: nothing is claimed.</summary>
    Unknown,

    Present,

    Absent
}

/// <param name="JavaMajor">The Java the game version asks for; null when the version is not on the computer to ask.</param>
public sealed record BuildPrerequisites(Presence Game, Presence Loader, int? JavaMajor, Presence Java);

/// <summary>
/// What of the game, the loader and Java a build from a code would still have to fetch.
/// Only what a few looks at the launcher's own folders can answer: a file is there or it
/// is not. Where that is not enough the answer is <see cref="Presence.Unknown"/>, never a guess.
/// </summary>
public static class BuildPrerequisiteProbe
{
    public static BuildPrerequisites Probe(LauncherPaths paths, string gameVersion, LoaderKind loader, string? loaderVersion)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        try
        {
            // A version id becomes a folder name here; one that is not a plain name is not looked for.
            if (!IsPlainName(gameVersion))
            {
                return new BuildPrerequisites(Presence.Unknown, Presence.Unknown, null, Presence.Unknown);
            }

            var json = paths.VersionJsonPath(gameVersion);
            var game = File.Exists(json) && File.Exists(paths.VersionJarPath(gameVersion)) ? Presence.Present : Presence.Absent;

            var javaMajor = File.Exists(json) ? ReadJavaMajor(json) : null;
            var java = Presence.Unknown;

            // Only the launcher's own runtime is looked at. Without it a Java installed on
            // the computer may still do, and finding that out means running it.
            if (javaMajor is { } major &&
                JavaManager.FindJavaExecutable(JavaManager.RuntimeDirectory(paths, major)) is { } executable &&
                JavaManager.IsRuntimeComplete(executable))
            {
                java = Presence.Present;
            }

            return new BuildPrerequisites(game, ProbeLoader(paths, gameVersion, loader, loaderVersion), javaMajor, java);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new BuildPrerequisites(Presence.Unknown, Presence.Unknown, null, Presence.Unknown);
        }
    }

    private static Presence ProbeLoader(LauncherPaths paths, string gameVersion, LoaderKind loader, string? loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion) || !IsPlainName(loaderVersion))
        {
            return Presence.Unknown;
        }

        // Fabric and Quilt name their profile after the two versions. Forge and NeoForge
        // are put in by their own installers under names that changed over the years.
        var id = loader switch
        {
            LoaderKind.Fabric => $"fabric-loader-{loaderVersion}-{gameVersion}",
            LoaderKind.Quilt => $"quilt-loader-{loaderVersion}-{gameVersion}",
            _ => null
        };

        if (id is null)
        {
            return Presence.Unknown;
        }

        return File.Exists(paths.VersionJsonPath(id)) ? Presence.Present : Presence.Absent;
    }

    private static int? ReadJavaMajor(string versionJsonPath)
    {
        try
        {
            using var stream = File.OpenRead(versionJsonPath);
            using var document = JsonDocument.Parse(stream);

            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("javaVersion", out var java) &&
                   java.ValueKind == JsonValueKind.Object &&
                   java.TryGetProperty("majorVersion", out var major) &&
                   major.TryGetInt32(out var value) && value > 0
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsPlainName(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length <= 80 &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or ' ');
}
