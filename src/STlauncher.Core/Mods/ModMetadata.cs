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

/// <summary>The title, version and icon a jar carries for itself. Any of them may be missing.</summary>
public sealed record ModDisplayInfo(string? Name, string? Version, byte[]? Icon);

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

    /// <summary>
    /// What to show for a jar in the mod list instead of its file name: the mod's own
    /// title, version and icon, from fabric.mod.json, quilt.mod.json or mods.toml. Null
    /// when the jar says nothing about itself - a plain library, or not a mod at all.
    /// </summary>
    public static ModDisplayInfo? ReadDisplay(string jarPath, int maxIconBytes = 512 * 1024)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            string? name = null;
            string? version = null;
            string? iconPath = null;

            if (archive.GetEntry("fabric.mod.json") is { } fabric)
            {
                try
                {
                    using var doc = ParseJson(fabric);
                    name = TextOf(doc.RootElement, "name");
                    version = TextOf(doc.RootElement, "version");
                    iconPath = IconPathOf(doc.RootElement);
                }
                catch (Exception)
                {
                }
            }

            if (name is null && archive.GetEntry("quilt.mod.json") is { } quilt)
            {
                try
                {
                    using var doc = ParseJson(quilt);

                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("quilt_loader", out var loader) && loader.ValueKind == JsonValueKind.Object)
                    {
                        version = TextOf(loader, "version");

                        if (loader.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
                        {
                            name = TextOf(metadata, "name");
                            iconPath = IconPathOf(metadata);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }

            if (name is null &&
                (archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml")) is { } toml)
            {
                try
                {
                    string text;

                    using (var reader = new StreamReader(toml.Open()))
                    {
                        text = reader.ReadToEnd();
                    }

                    var mod = SplitSections(text).FirstOrDefault(s => TomlMod.IsMatch(s.Header));

                    if (mod.Header is not null)
                    {
                        var values = KeyValues(mod.Body);
                        name = values.TryGetValue("displayName", out var dn) ? dn : null;
                        version = values.TryGetValue("version", out var ver) ? ver : null;
                        iconPath = values.TryGetValue("logoFile", out var logo) ? logo : null;
                    }

                    if (iconPath is null)
                    {
                        var match = Regex.Match(text, "^\\s*logoFile\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.Multiline);
                        iconPath = match.Success ? match.Groups[1].Value : null;
                    }
                }
                catch (Exception)
                {
                }
            }

            // "${version}" and "${file.jarVersion}" are build placeholders nobody filled in;
            // the jar's manifest usually has the real number.
            if (version is not null && version.Contains("${", StringComparison.Ordinal))
            {
                version = ManifestVersion(archive);
            }

            byte[]? icon = null;

            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                var entry = archive.GetEntry(iconPath!.Replace('\\', '/').TrimStart('.', '/'));

                if (entry is not null && entry.Length > 0 && entry.Length <= maxIconBytes)
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    icon = buffer.ToArray();
                }
            }

            name = string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
            version = string.IsNullOrWhiteSpace(version) ? null : version!.Trim();

            return name is null && version is null && icon is null ? null : new ModDisplayInfo(name, version, icon);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TextOf(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>"icon" is a path, or a map of sizes to paths; the largest size is the one worth having.</summary>
    private static string? IconPathOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("icon", out var icon))
        {
            return null;
        }

        if (icon.ValueKind == JsonValueKind.String)
        {
            return icon.GetString();
        }

        if (icon.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? best = null;
        var bestSize = -1;

        foreach (var entry in icon.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String &&
                int.TryParse(entry.Name, out var size) && size > bestSize)
            {
                (best, bestSize) = (entry.Value.GetString(), size);
            }
        }

        return best;
    }

    private static string? ManifestVersion(ZipArchive archive)
    {
        try
        {
            if (archive.GetEntry("META-INF/MANIFEST.MF") is not { } manifest)
            {
                return null;
            }

            using var reader = new StreamReader(manifest.Open());

            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase))
                {
                    return line["Implementation-Version:".Length..].Trim();
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static JsonDocument ParseJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }
}
