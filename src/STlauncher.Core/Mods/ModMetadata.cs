using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Mods;

/// <summary>One thing a mod asks for: another mod's id and the versions it accepts.</summary>
/// <param name="Required">False for "recommends" / "suggests": nice to have, never a launch failure.</param>
public sealed record ModDependency2(string Id, string? VersionRange, bool Required);

/// <summary>
/// What a jar says about itself in fabric.mod.json, quilt.mod.json or mods.toml: its id,
/// what it depends on, and which game versions it was made for. Read once per jar, no
/// network, so a build can be checked before the game is even started.
/// </summary>
public sealed record ModMetadata(
    string FileName,
    string Id,
    string Name,
    string Version,
    LoaderKind Loader,
    IReadOnlyList<ModDependency2> Dependencies,
    string? MinecraftRange,
    IReadOnlyList<string> Provides);

public static class ModMetadataReader
{
    /// <summary>Ids the loader or the game itself provide; asking for them is never a missing mod.</summary>
    public static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "quilted_fabric_loader",
        "forge", "neoforge", "mixinextras", "mixin", "fml", "lowcodefml", "javafml", "kotlinforforge"
    };

    /// <summary>Metadata for every loader section found in the jar; empty for a plain library jar.</summary>
    public static IReadOnlyList<ModMetadata> Read(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);
        var result = new List<ModMetadata>();

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            if (archive.GetEntry("fabric.mod.json") is { } fabric)
            {
                var meta = ReadFabric(archive, fabric, fileName);
                if (meta is not null) result.Add(meta);
            }

            if (archive.GetEntry("quilt.mod.json") is { } quilt && result.Count == 0)
            {
                var meta = ReadQuilt(quilt, fileName);
                if (meta is not null) result.Add(meta);
            }

            if (archive.GetEntry("META-INF/neoforge.mods.toml") is { } neo)
            {
                var meta = ReadToml(neo, fileName, LoaderKind.NeoForge);
                if (meta is not null) result.Add(meta);
            }

            if (archive.GetEntry("META-INF/mods.toml") is { } forge)
            {
                var meta = ReadToml(forge, fileName, LoaderKind.Forge);
                if (meta is not null) result.Add(meta);
            }
        }
        catch (Exception)
        {
            // A corrupt jar is reported by the game; here it is simply not a mod.
        }

        return result;
    }

    private static ModMetadata? ReadFabric(ZipArchive archive, ZipArchiveEntry entry, string fileName)
    {
        using var doc = ParseJson(entry);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idEl))
        {
            return null;
        }

        var id = idEl.GetString() ?? string.Empty;
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : id;
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "?";

        var deps = new List<ModDependency2>();
        string? minecraft = null;

        foreach (var (key, required) in new[] { ("depends", true), ("recommends", false) })
        {
            if (!root.TryGetProperty(key, out var block) || block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in block.EnumerateObject())
            {
                var range = RangeText(property.Value);

                if (property.Name.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    minecraft ??= range;
                }

                deps.Add(new ModDependency2(property.Name, range, required));
            }
        }

        // Fabric API is one jar that carries forty modules as nested jars; a mod that
        // asks for "fabric-item-api-v1" is satisfied by it. "provides" is the same idea.
        var provides = new List<string>();

        if (root.TryGetProperty("provides", out var providesEl) && providesEl.ValueKind == JsonValueKind.Array)
        {
            provides.AddRange(providesEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        }

        if (root.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
        {
            foreach (var jar in jars.EnumerateArray())
            {
                if (jar.ValueKind != JsonValueKind.Object || !jar.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                provides.AddRange(NestedIds(archive, file.GetString()!));
            }
        }

        return new ModMetadata(fileName, id, name, version, LoaderKind.Fabric, deps, minecraft, provides);
    }

    /// <summary>Ids declared by a nested jar (one level: that is how Fabric API ships).</summary>
    private static IEnumerable<string> NestedIds(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);

        if (entry is null)
        {
            yield break;
        }

        string? id = null;
        var provides = new List<string>();

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;

            using var nested = new ZipArchive(buffer, ZipArchiveMode.Read);
            var meta = nested.GetEntry("fabric.mod.json");

            if (meta is null)
            {
                yield break;
            }

            using var doc = ParseJson(meta);

            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                id = idEl.GetString();
            }

            if (doc.RootElement.TryGetProperty("provides", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                provides.AddRange(p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            }
        }
        catch (Exception)
        {
            yield break;
        }

        if (id is not null)
        {
            yield return id;
        }

        foreach (var extra in provides)
        {
            yield return extra;
        }
    }

    private static ModMetadata? ReadQuilt(ZipArchiveEntry entry, string fileName)
    {
        using var doc = ParseJson(entry);

        if (!doc.RootElement.TryGetProperty("quilt_loader", out var loader) || loader.ValueKind != JsonValueKind.Object ||
            !loader.TryGetProperty("id", out var idEl))
        {
            return null;
        }

        var id = idEl.GetString() ?? string.Empty;
        var version = loader.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "?";
        var name = id;

        if (loader.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object &&
            md.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            name = n.GetString()!;
        }

        var deps = new List<ModDependency2>();
        string? minecraft = null;

        if (loader.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in depends.EnumerateArray())
            {
                string? depId = null;
                string? range = null;
                var optional = false;

                if (dep.ValueKind == JsonValueKind.String)
                {
                    depId = dep.GetString();
                }
                else if (dep.ValueKind == JsonValueKind.Object)
                {
                    depId = dep.TryGetProperty("id", out var i) ? i.GetString() : null;
                    range = dep.TryGetProperty("versions", out var r) ? RangeText(r) : null;
                    optional = dep.TryGetProperty("optional", out var o) && o.ValueKind == JsonValueKind.True;
                }

                if (depId is null)
                {
                    continue;
                }

                if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    minecraft ??= range;
                }

                deps.Add(new ModDependency2(depId, range, !optional));
            }
        }

        return new ModMetadata(fileName, id, name, version, LoaderKind.Quilt, deps, minecraft, Array.Empty<string>());
    }

    private static readonly Regex TomlMod = new(@"^\s*\[\[mods\]\]", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TomlDeps = new(@"^\s*\[\[dependencies\.(?<mod>[^\]]+)\]\]", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TomlKey = new(@"^\s*(?<key>\w+)\s*=\s*(?:""(?<str>[^""]*)""|'(?<str2>[^']*)'|(?<bare>[^#\r\n]+))", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// A small TOML reader for the two table shapes mods.toml uses: [[mods]] and
    /// [[dependencies.modid]]. A real parser would be a dependency for four keys.
    /// </summary>
    private static ModMetadata? ReadToml(ZipArchiveEntry entry, string fileName, LoaderKind loader)
    {
        string text;

        using (var reader = new StreamReader(entry.Open()))
        {
            text = reader.ReadToEnd();
        }

        var sections = SplitSections(text);
        var mod = sections.FirstOrDefault(s => TomlMod.IsMatch(s.Header));

        if (mod.Header is null)
        {
            return null;
        }

        var modValues = KeyValues(mod.Body);

        if (!modValues.TryGetValue("modId", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = modValues.TryGetValue("displayName", out var dn) ? dn : id;
        var version = modValues.TryGetValue("version", out var ver) ? ver : "?";

        var deps = new List<ModDependency2>();
        string? minecraft = null;

        foreach (var section in sections)
        {
            var match = TomlDeps.Match(section.Header);

            if (!match.Success || !match.Groups["mod"].Value.Trim('"', '\'').Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = KeyValues(section.Body);

            if (!values.TryGetValue("modId", out var depId))
            {
                continue;
            }

            var required = !(values.TryGetValue("mandatory", out var mandatory) && mandatory.Equals("false", StringComparison.OrdinalIgnoreCase)) &&
                           !(values.TryGetValue("type", out var type) && !type.Equals("required", StringComparison.OrdinalIgnoreCase));
            values.TryGetValue("versionRange", out var range);

            if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            {
                minecraft ??= range;
            }

            deps.Add(new ModDependency2(depId, range, required));
        }

        return new ModMetadata(fileName, id, name, version, loader, deps, minecraft, Array.Empty<string>());
    }

    private static List<(string Header, string Body)> SplitSections(string text)
    {
        var result = new List<(string, string)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? header = null;
        var body = new List<string>();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("[[", StringComparison.Ordinal) || line.TrimStart().StartsWith("[", StringComparison.Ordinal) && !line.TrimStart().StartsWith("[[", StringComparison.Ordinal) && line.TrimEnd().EndsWith("]", StringComparison.Ordinal))
            {
                if (header is not null)
                {
                    result.Add((header, string.Join('\n', body)));
                }

                header = line;
                body.Clear();
            }
            else
            {
                body.Add(line);
            }
        }

        if (header is not null)
        {
            result.Add((header, string.Join('\n', body)));
        }

        return result;
    }

    private static Dictionary<string, string> KeyValues(string body)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in TomlKey.Matches(body))
        {
            var value = m.Groups["str"].Success ? m.Groups["str"].Value
                : m.Groups["str2"].Success ? m.Groups["str2"].Value
                : m.Groups["bare"].Value.Trim();
            values[m.Groups["key"].Value] = value;
        }

        return values;
    }

    private static string? RangeText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => string.Join(" || ", element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())),
        _ => null
    };

    private static JsonDocument ParseJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }
}
