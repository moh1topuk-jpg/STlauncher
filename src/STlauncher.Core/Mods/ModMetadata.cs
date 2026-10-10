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
/// A mod this one says it does not run with: Fabric's "breaks" and "conflicts", Forge's
/// and NeoForge's dependency of type "incompatible" or "discouraged". The range is part
/// of the statement: "breaks fabric-api &lt;0.144.3" asks for a newer Fabric API, it does
/// not say the two cannot be in one build.
/// </summary>
/// <param name="VersionRange">The versions of the other mod it does not run with; null for every version.</param>
/// <param name="Breaks">True when the loader refuses to start; false when it only warns.</param>
public sealed record ModConflict(string Id, string? VersionRange, bool Breaks);

/// <summary>A mod id a jar brings besides its own - an alias, or a jar nested inside it - with that mod's version.</summary>
public sealed record ProvidedMod(string Id, string? Version);

/// <summary>
/// What a jar says about itself in fabric.mod.json, quilt.mod.json or mods.toml: its id,
/// what it depends on, and which game versions it was made for. Read once per jar, no
/// network, so a build can be checked before the game is even started.
/// </summary>
/// <param name="Provides">Every other id the jar answers to: its aliases and the mods nested in it.</param>
public sealed record ModMetadata(
    string FileName,
    string Id,
    string Name,
    string Version,
    LoaderKind Loader,
    IReadOnlyList<ModDependency2> Dependencies,
    string? MinecraftRange,
    IReadOnlyList<string> Provides)
{
    /// <summary>What this mod says it does not run with.</summary>
    public IReadOnlyList<ModConflict> Conflicts { get; init; } = Array.Empty<ModConflict>();

    /// <summary>The ids in <see cref="Provides"/> with the version each comes in, where the jar says.</summary>
    public IReadOnlyList<ProvidedMod> Bundled { get; init; } = Array.Empty<ProvidedMod>();
}

/// <summary>The title, version and icon a jar carries for itself. Any of them may be missing.</summary>
public sealed record ModDisplayInfo(string? Name, string? Version, byte[]? Icon)
{
    /// <summary>The mod's own sentence or two about itself, when the jar carries them.</summary>
    public string? Description { get; init; }
}

public static partial class ModMetadataReader
{
    private static readonly Regex TomlMod = new(@"^\s*\[\[mods\]\]", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TomlDeps = new(@"^\s*\[\[dependencies\.(?<mod>[^\]]+)\]\]", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TomlKey = new(@"^\s*(?<key>\w+)\s*=\s*(?:""(?<str>[^""]*)""|'(?<str2>[^']*)'|(?<bare>[^#\r\n]+))", RegexOptions.Multiline | RegexOptions.Compiled);


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
            string? description = null;

            if (archive.GetEntry("fabric.mod.json") is { } fabric)
            {
                try
                {
                    using var doc = ParseJson(fabric);
                    name = TextOf(doc.RootElement, "name");
                    version = TextOf(doc.RootElement, "version");
                    iconPath = IconPathOf(doc.RootElement);
                    description = TextOf(doc.RootElement, "description");
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
                            description = TextOf(metadata, "description");
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
                        description = TomlDescription(mod.Body);
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

            description = string.IsNullOrWhiteSpace(description) ? null : description!.Trim();

            return name is null && version is null && icon is null && description is null
                ? null
                : new ModDisplayInfo(name, version, icon) { Description = description };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A Forge description is usually a block between triple quotes, which the one-line
    /// key reader does not see; a plainly quoted one is taken as well.
    /// </summary>
    private static string? TomlDescription(string body)
    {
        var block = Regex.Match(
            body,
            @"^\s*description\s*=\s*(?:""""""|''')(?<text>.*?)(?:""""""|''')",
            RegexOptions.Multiline | RegexOptions.Singleline);

        if (block.Success)
        {
            return block.Groups["text"].Value;
        }

        var line = Regex.Match(
            body,
            @"^\s*description\s*=\s*(?:""(?<text>[^""\r\n]*)""|'(?<text>[^'\r\n]*)')",
            RegexOptions.Multiline);

        return line.Success ? line.Groups["text"].Value : null;
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
        if (entry.Length > MaxMetadataBytes)
        {
            throw new InvalidDataException("metadata entry too large");
        }

        using var stream = entry.Open();
        return JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }
}
