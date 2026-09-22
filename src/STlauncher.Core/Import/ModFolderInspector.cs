using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Import;

/// <summary>
/// Works out a build's loader and Minecraft version from the mod files themselves, for
/// launchers that keep no readable description next to them (the Modrinth App keeps its
/// profiles in a database; a folder assembled by hand keeps nothing at all).
/// </summary>
/// <remarks>
/// Every mod jar declares which loader it is for and which game version it depends on.
/// Read a few dozen and the answer is unambiguous in practice: a folder of Fabric 1.20.1
/// mods is a Fabric 1.20.1 build. The result is still marked as inferred, so the player
/// can glance at it.
/// </remarks>
public static class ModFolderInspector
{
    /// <summary>Enough for a verdict, few enough to stay quick on a 300-mod pack.</summary>
    private const int MaxJars = 40;

    public sealed record Verdict(LoaderKind Loader, string? GameVersion);

    public static Verdict? Inspect(string modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
        {
            return null;
        }

        IEnumerable<string> jars;

        try
        {
            jars = Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).Take(MaxJars).ToList();
        }
        catch (Exception)
        {
            return null;
        }

        var loaderVotes = new Dictionary<LoaderKind, int>();
        var versionVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        var inspected = 0;

        foreach (var jar in jars)
        {
            try
            {
                using var archive = ZipFile.OpenRead(jar);

                var fabric = archive.GetEntry("fabric.mod.json");
                var quilt = archive.GetEntry("quilt.mod.json");
                var neoForge = archive.GetEntry("META-INF/neoforge.mods.toml");
                var forge = archive.GetEntry("META-INF/mods.toml");

                inspected++;

                // A jar built for several loaders at once says nothing about which one
                // this folder is for, so only the single-loader jars vote.
                var loaders = new List<LoaderKind>();
                if (fabric is not null) loaders.Add(LoaderKind.Fabric);
                if (quilt is not null && fabric is null) loaders.Add(LoaderKind.Quilt);
                if (neoForge is not null) loaders.Add(LoaderKind.NeoForge);
                if (forge is not null && neoForge is null) loaders.Add(LoaderKind.Forge);

                if (loaders.Count == 1)
                {
                    loaderVotes[loaders[0]] = loaderVotes.GetValueOrDefault(loaders[0]) + 1;
                }

                foreach (var version in VersionsFromFabric(fabric).Concat(VersionsFromToml(neoForge ?? forge)))
                {
                    versionVotes[version] = versionVotes.GetValueOrDefault(version) + 1;
                }
            }
            catch (Exception)
            {
                // A corrupt or locked jar is not evidence either way.
            }
        }

        if (inspected == 0)
        {
            return null;
        }

        var loader = loaderVotes.Count == 0
            ? LoaderKind.Vanilla
            : loaderVotes.OrderByDescending(v => v.Value).ThenBy(v => v.Key).First().Key;

        var gameVersion = versionVotes.Count == 0
            ? null
            : versionVotes
                .OrderByDescending(v => v.Value)
                .ThenByDescending(v => v.Key, VersionOrder.Instance)
                .First().Key;

        return new Verdict(loader, gameVersion);
    }

    private static IEnumerable<string> VersionsFromFabric(ZipArchiveEntry? entry)
    {
        if (entry is null)
        {
            yield break;
        }

        string? value = null;

        try
        {
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (document.RootElement.TryGetProperty("depends", out var depends) &&
                depends.ValueKind == JsonValueKind.Object &&
                depends.TryGetProperty("minecraft", out var minecraft))
            {
                value = minecraft.ValueKind switch
                {
                    JsonValueKind.String => minecraft.GetString(),
                    JsonValueKind.Array => minecraft.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString())
                        .FirstOrDefault(),
                    _ => null
                };
            }
        }
        catch (Exception)
        {
            yield break;
        }

        var version = ExactVersion(value);

        if (version is not null)
        {
            yield return version;
        }
    }

    private static IEnumerable<string> VersionsFromToml(ZipArchiveEntry? entry)
    {
        if (entry is null)
        {
            yield break;
        }

        string text;

        try
        {
            using var reader = new StreamReader(entry.Open());
            text = reader.ReadToEnd();
        }
        catch (Exception)
        {
            yield break;
        }

        // [[dependencies.<mod>]] blocks: the minecraft one carries versionRange="[1.20.1,1.21)".
        // The two keys come in either order, so both are tried, each kept inside one block.
        var match = Regex.Match(text, @"modId\s*=\s*""minecraft""[^\[]*?versionRange\s*=\s*""\[?([0-9][0-9.]*)");

        if (!match.Success)
        {
            match = Regex.Match(text, @"versionRange\s*=\s*""\[?([0-9][0-9.]*)[^""]*""[^\[]*?modId\s*=\s*""minecraft""");
        }

        if (match.Success && ExactVersion(match.Groups[1].Value) is { } version)
        {
            yield return version;
        }
    }

    /// <summary>
    /// The lower bound of a dependency, when it is a real version. "1.21.x" and "*" say
    /// nothing; "~1.21.1", ">=1.21.1 <1.22" and "1.21.1" all say 1.21.1.
    /// </summary>
    private static string? ExactVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var first = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (first is null)
        {
            return null;
        }

        var trimmed = first.TrimStart('~', '^', '>', '=', '[', '(');
        return Regex.IsMatch(trimmed, @"^(1\.\d{1,2}(\.\d{1,2})?|2\d\.\d{1,2}(\.\d{1,2})?)$") ? trimmed : null;
    }

    /// <summary>Orders "1.20.1" before "1.21" numerically, not as text.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Parts(x);
            var b = Parts(y);

            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var left = i < a.Length ? a[i] : 0;
                var right = i < b.Length ? b[i] : 0;

                if (left != right)
                {
                    return left.CompareTo(right);
                }
            }

            return 0;
        }

        private static int[] Parts(string? value)
            => (value ?? string.Empty).Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }
}
