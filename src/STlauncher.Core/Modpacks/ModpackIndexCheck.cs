using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace STlauncher.Core.Modpacks;

public enum ModpackIndexProblemKind
{
    /// <summary>The path leaves the build's folder.</summary>
    UnsafePath,

    /// <summary>Two entries write the same file.</summary>
    DuplicatePath,

    /// <summary>The address is not https, or not on a host the format allows.</summary>
    HostNotAllowed,

    /// <summary>No digest to check the file against.</summary>
    HashMissing,

    /// <summary>A digest that cannot be one: wrong length, or not hexadecimal.</summary>
    HashMalformed
}

public sealed record ModpackIndexProblem(ModpackIndexProblemKind Kind, string Path, string Detail);

/// <summary>The pack's list of files is not one the launcher will act on. Nothing was downloaded.</summary>
public sealed class ModpackIndexException : IOException
{
    public ModpackIndexException(IReadOnlyList<ModpackIndexProblem> problems)
        : base(Summarize(problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<ModpackIndexProblem> Problems { get; }

    private static string Summarize(IReadOnlyList<ModpackIndexProblem> problems)
    {
        const int shown = 3;

        var text = "The modpack lists files that cannot be installed, so nothing was downloaded: " +
                   string.Join("; ", problems.Take(shown).Select(p => $"{p.Path} - {p.Detail}"));

        return problems.Count > shown ? text + $"; and {problems.Count - shown} more" : text;
    }
}

/// <summary>
/// Reads the whole list of a pack before the first byte is fetched. A pack of three
/// hundred files whose last entry points at a host that is not allowed used to download
/// two hundred and ninety-nine of them and then fail; what is wrong with the list is
/// known from the list alone.
/// </summary>
public static class ModpackIndexCheck
{
    public static IReadOnlyList<ModpackIndexProblem> Check(ModpackPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var problems = new List<ModpackIndexProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in plan.Files)
        {
            var path = file.RelativePath ?? string.Empty;

            // The reader refuses these already; a plan can also be put together elsewhere.
            if (!RelativePath.IsSafe(path))
            {
                problems.Add(new(ModpackIndexProblemKind.UnsafePath, path, "the path leaves the build's folder"));
                continue;
            }

            if (!seen.Add(path.Replace('\\', '/')))
            {
                problems.Add(new(ModpackIndexProblemKind.DuplicatePath, path, "listed twice"));
            }

            // An entry without an address is one the pack leaves to the player; skipped as before.
            if (string.IsNullOrEmpty(file.Url))
            {
                continue;
            }

            if (!ModpackWriter.IsAllowedDownload(file.Url))
            {
                problems.Add(new(ModpackIndexProblemKind.HostNotAllowed, path, "the address is not https on a host modpacks may use: " + HostOf(file.Url)));
            }

            var sha1 = string.IsNullOrWhiteSpace(file.Sha1) ? null : file.Sha1;
            var sha512 = string.IsNullOrWhiteSpace(file.Sha512) ? null : file.Sha512;

            if (sha1 is null && sha512 is null)
            {
                problems.Add(new(ModpackIndexProblemKind.HashMissing, path, "no hash to check the file against"));
            }
            else if ((sha1 is not null && !IsHex(sha1, 40)) || (sha512 is not null && !IsHex(sha512, 128)))
            {
                problems.Add(new(ModpackIndexProblemKind.HashMalformed, path, "the hash is not a hash"));
            }
        }

        return problems;
    }

    /// <summary>Throws when the list has anything wrong with it.</summary>
    public static void ThrowIfBroken(ModpackPlan plan)
    {
        var problems = Check(plan);

        if (problems.Count > 0)
        {
            throw new ModpackIndexException(problems);
        }
    }

    public static bool IsHex(string? value, int length)
        => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Scheme + "://" + uri.Host : url;
}

/// <summary>
/// <c>https://cdn.modrinth.com/data/&lt;project&gt;/versions/&lt;version&gt;/&lt;file&gt;</c>: the address
/// Modrinth serves a version's file from, which names the version it belongs to.
/// </summary>
public sealed record ModrinthCdnFile(string ProjectId, string VersionId, string FileName)
{
    public static ModrinthCdnFile? TryParse(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 5 || parts[0] != "data" || parts[2] != "versions")
        {
            return null;
        }

        // Ids are short base62 strings; anything else is not put into an API address.
        if (!IsId(parts[1]) || !IsId(parts[3]))
        {
            return null;
        }

        return new ModrinthCdnFile(parts[1], parts[3], Uri.UnescapeDataString(parts[4]));
    }

    private static bool IsId(string value)
        => value.Length is > 0 and <= 32 && value.All(c => char.IsAsciiLetterOrDigit(c));
}
