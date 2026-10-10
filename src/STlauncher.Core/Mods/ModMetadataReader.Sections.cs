using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Mods;

/// <summary>
/// The part that reads what a jar requires, provides and refuses to run with, one record
/// per loader the jar was built for. A jar is often several mods at once: Fabric API is
/// fifty modules nested under META-INF/jars, Create carries Flywheel under
/// META-INF/jarjar. Those ids count as present, or every mod that asks for
/// "fabric-rendering-v1" would look broken.
/// </summary>
public static partial class ModMetadataReader
{
    /// <summary>
    /// Bumped whenever this reader starts answering differently for the same jar. The
    /// answers are kept between launches (<see cref="ModMetadataCache"/>); a new number
    /// makes every kept answer stale at once.
    /// </summary>
    public const int ParserRevision = 2;

    /// <summary>Jars inside jars inside jars: three levels is one more than anything published needs.</summary>
    private const int MaxNestedDepth = 3;

    /// <summary>Nested jars opened for one file in mods/. Fabric API has about fifty.</summary>
    private const int MaxNestedJars = 256;

    /// <summary>A nested jar is unpacked into memory to be read; a larger one is skipped.</summary>
    private const long MaxNestedBytes = 48L * 1024 * 1024;

    /// <summary>fabric.mod.json and mods.toml are a few kilobytes; a megabyte of "metadata" is not metadata.</summary>
    private const long MaxMetadataBytes = 1024 * 1024;

    /// <summary>Ids the loader or the game itself provide; asking for them is never a missing mod.</summary>
    public static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "quilted_fabric_loader",
        "forge", "neoforge", "mixinextras", "mixin", "fml", "lowcodefml", "javafml", "kotlinforforge"
    };

    /// <summary>
    /// True for an id the running loader brings by itself, so no jar in mods/ has to.
    /// MixinExtras is the one that depends on the loader's age: Fabric Loader carries it
    /// from 0.15 on, and a mod written for an older loader ships its own copy.
    /// </summary>
    /// <param name="loaderVersion">The build's loader version when known; null assumes a current one.</param>
    public static bool IsLoaderProvided(string id, LoaderKind loader, string? loaderVersion = null)
    {
        if (!BuiltIn.Contains(id))
        {
            return false;
        }

        if (id.Equals("mixinextras", StringComparison.OrdinalIgnoreCase) &&
            loader == LoaderKind.Fabric &&
            !string.IsNullOrWhiteSpace(loaderVersion) &&
            char.IsDigit(loaderVersion.TrimStart()[0]) &&
            VersionRange.CompareVersions(loaderVersion, "0.15.0") < 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Metadata for every loader section found in the jar; empty for a plain library jar.
    /// A jar built for several loaders gives several records, each with that loader's own
    /// requirements: see <see cref="SectionFor"/> for which one a build goes by.
    /// </summary>
    public static IReadOnlyList<ModMetadata> Read(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);
        var result = new List<ModMetadata>();

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var budget = new NestedBudget();

            // One broken section must not hide the others: a jar for two loaders with a
            // fabric.mod.json nobody validated is still a good NeoForge mod.
            if (archive.GetEntry("fabric.mod.json") is { } fabric)
            {
                Add(() => ReadFabric(archive, fabric, fileName, budget));
            }

            if (archive.GetEntry("quilt.mod.json") is { } quilt)
            {
                Add(() => ReadQuilt(archive, quilt, fileName, budget));
            }

            if (archive.GetEntry("META-INF/neoforge.mods.toml") is { } neo)
            {
                Add(() => ReadToml(archive, neo, fileName, LoaderKind.NeoForge, budget));
            }

            if (archive.GetEntry("META-INF/mods.toml") is { } forge)
            {
                Add(() => ReadToml(archive, forge, fileName, LoaderKind.Forge, budget));
            }
        }
        catch (Exception)
        {
            // A corrupt jar is reported by the game; here it is simply not a mod.
        }

        return result;

        void Add(Func<ModMetadata?> read)
        {
            try
            {
                if (read() is { } meta)
                {
                    result.Add(meta);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// The section the given loader goes by, or null when it would not load the jar at
    /// all. Fabric reads fabric.mod.json and nothing else. Quilt prefers quilt.mod.json
    /// and falls back to Fabric's. Forge reads mods.toml. NeoForge reads
    /// neoforge.mods.toml from Minecraft 1.20.5 on and mods.toml before that - which is
    /// why one jar can carry both with different requirements in each.
    /// </summary>
    public static ModMetadata? SectionFor(IReadOnlyList<ModMetadata> sections, LoaderKind loader, string? gameVersion = null)
    {
        ModMetadata? Of(LoaderKind kind) => sections.FirstOrDefault(s => s.Loader == kind);

        switch (loader)
        {
            case LoaderKind.Fabric:
                return Of(LoaderKind.Fabric);

            case LoaderKind.Quilt:
                return Of(LoaderKind.Quilt) ?? Of(LoaderKind.Fabric);

            case LoaderKind.Forge:
                return Of(LoaderKind.Forge);

            case LoaderKind.NeoForge:
                if (string.IsNullOrWhiteSpace(gameVersion) || !char.IsDigit(gameVersion.TrimStart()[0]))
                {
                    return Of(LoaderKind.NeoForge) ?? Of(LoaderKind.Forge);
                }

                return VersionRange.CompareVersions(gameVersion, "1.20.5") >= 0
                    ? Of(LoaderKind.NeoForge)
                    : Of(LoaderKind.Forge) ?? Of(LoaderKind.NeoForge);

            default:
                return null;
        }
    }

    // ===================== Fabric and Quilt =====================

    private static ModMetadata? ReadFabric(ZipArchive archive, ZipArchiveEntry entry, string fileName, NestedBudget budget)
    {
        using var doc = ParseJson(entry);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object || TextOf(root, "id") is not { Length: > 0 } id)
        {
            return null;
        }

        var name = TextOf(root, "name") ?? id;
        var version = RealVersion(TextOf(root, "version"), archive) ?? "?";

        var deps = new List<ModDependency2>();
        string? minecraft = null;

        foreach (var (key, required) in new[] { ("depends", true), ("recommends", false) })
        {
            foreach (var (depId, range) in FabricRelations(root, key))
            {
                if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    minecraft ??= range;
                }

                deps.Add(new ModDependency2(depId, range, required));
            }
        }

        // "breaks" stops the game, "conflicts" is a warning in the log; both name versions.
        var conflicts = new List<ModConflict>();

        foreach (var (key, breaks) in new[] { ("breaks", true), ("conflicts", false) })
        {
            conflicts.AddRange(FabricRelations(root, key).Select(r => new ModConflict(r.Id, r.Range, breaks)));
        }

        var bundled = new List<ProvidedMod>();
        bundled.AddRange(StringsOf(root, "provides").Select(alias => new ProvidedMod(alias, version)));
        CollectNested(archive, FabricJarPaths(root), fabricFamily: true, depth: 1, budget, bundled);

        return Build(fileName, id, name, version, LoaderKind.Fabric, deps, minecraft, conflicts, bundled);
    }

    private static IEnumerable<(string Id, string? Range)> FabricRelations(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var block) || block.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in block.EnumerateObject())
        {
            yield return (property.Name, RangeText(property.Value));
        }
    }

    /// <summary>"jars": [{ "file": "META-INF/jars/x.jar" }] - the only nested jars Fabric Loader opens.</summary>
    private static List<string> FabricJarPaths(JsonElement root)
    {
        var paths = new List<string>();

        if (root.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
        {
            foreach (var jar in jars.EnumerateArray())
            {
                if (jar.ValueKind == JsonValueKind.Object && TextOf(jar, "file") is { Length: > 0 } file)
                {
                    paths.Add(file);
                }
            }
        }

        return paths;
    }

    private static ModMetadata? ReadQuilt(ZipArchive archive, ZipArchiveEntry entry, string fileName, NestedBudget budget)
    {
        using var doc = ParseJson(entry);

        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("quilt_loader", out var loader) || loader.ValueKind != JsonValueKind.Object ||
            TextOf(loader, "id") is not { Length: > 0 } id)
        {
            return null;
        }

        var version = RealVersion(TextOf(loader, "version"), archive) ?? "?";
        var name = id;

        if (loader.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object && TextOf(md, "name") is { Length: > 0 } title)
        {
            name = title;
        }

        var deps = new List<ModDependency2>();
        var conflicts = new List<ModConflict>();
        string? minecraft = null;

        foreach (var relation in QuiltRelations(loader, "depends"))
        {
            if (relation.Id.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            {
                minecraft ??= relation.Range;
            }

            deps.Add(new ModDependency2(relation.Id, relation.Range, !relation.Optional));
        }

        foreach (var relation in QuiltRelations(loader, "breaks"))
        {
            // A range written in a shape this reader does not take, or an "unless" that
            // lifts the ban, is not something to raise an alarm on.
            if (!relation.Unclear)
            {
                conflicts.Add(new ModConflict(relation.Id, relation.Range, Breaks: true));
            }
        }

        var bundled = new List<ProvidedMod>();

        if (loader.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in provides.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } alias)
                {
                    bundled.Add(new ProvidedMod(QuiltId(alias), version));
                }
                else if (item.ValueKind == JsonValueKind.Object && TextOf(item, "id") is { Length: > 0 } aliasId)
                {
                    bundled.Add(new ProvidedMod(QuiltId(aliasId), TextOf(item, "version") ?? version));
                }
            }
        }

        CollectNested(archive, StringsOf(loader, "jars"), fabricFamily: true, depth: 1, budget, bundled);

        return Build(fileName, id, name, version, LoaderKind.Quilt, deps, minecraft, conflicts, bundled);
    }

    private readonly record struct QuiltRelation(string Id, string? Range, bool Optional, bool Unclear);

    private static IEnumerable<QuiltRelation> QuiltRelations(JsonElement loader, string key)
    {
        if (!loader.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } plain)
            {
                yield return new QuiltRelation(QuiltId(plain), null, false, false);
            }
            else if (item.ValueKind == JsonValueKind.Object && TextOf(item, "id") is { Length: > 0 } id)
            {
                var hasVersions = item.TryGetProperty("versions", out var versions);
                var range = hasVersions ? RangeText(versions) : null;

                yield return new QuiltRelation(
                    QuiltId(id),
                    range,
                    item.TryGetProperty("optional", out var o) && o.ValueKind == JsonValueKind.True,
                    (hasVersions && range is null) || item.TryGetProperty("unless", out _));
            }
        }
    }

    /// <summary>Quilt may write an id with its Maven group in front: "org.quiltmc:qsl".</summary>
    private static string QuiltId(string id) => id.Contains(':') ? id[(id.LastIndexOf(':') + 1)..] : id;

    // ===================== Forge and NeoForge =====================

    /// <summary>
    /// A small TOML reader for the two table shapes mods.toml uses: [[mods]] and
    /// [[dependencies.modid]]. A real parser would be a dependency for four keys.
    /// </summary>
    private static ModMetadata? ReadToml(ZipArchive archive, ZipArchiveEntry entry, string fileName, LoaderKind loader, NestedBudget budget)
    {
        var sections = SplitSections(ReadText(entry));
        var mods = sections.Where(s => TomlMod.IsMatch(s.Header)).Select(s => KeyValues(s.Body))
            .Where(v => v.TryGetValue("modId", out var modId) && !string.IsNullOrWhiteSpace(modId))
            .ToList();

        if (mods.Count == 0)
        {
            return null;
        }

        var first = mods[0];
        var id = first["modId"];
        var name = first.TryGetValue("displayName", out var dn) ? dn : id;
        var version = RealVersion(first.TryGetValue("version", out var ver) ? ver : null, archive) ?? "?";

        // One file may declare several mods; the rest are ids this jar answers to as well.
        var ownIds = mods.Select(m => m["modId"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bundled = new List<ProvidedMod>();

        foreach (var mod in mods)
        {
            var modVersion = RealVersion(mod.TryGetValue("version", out var v) ? v : null, archive) ?? version;

            if (!mod["modId"].Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                bundled.Add(new ProvidedMod(mod["modId"], modVersion));
            }

            if (mod.TryGetValue("provides", out var provides))
            {
                bundled.AddRange(QuotedStrings(provides).Select(alias => new ProvidedMod(alias, modVersion)));
            }
        }

        var deps = new List<ModDependency2>();
        var conflicts = new List<ModConflict>();
        string? minecraft = null;

        foreach (var section in sections)
        {
            var match = TomlDeps.Match(section.Header);

            if (!match.Success || !ownIds.Contains(match.Groups["mod"].Value.Trim().Trim('"', '\'')))
            {
                continue;
            }

            var values = KeyValues(section.Body);

            if (!values.TryGetValue("modId", out var depId) || string.IsNullOrWhiteSpace(depId))
            {
                continue;
            }

            values.TryGetValue("versionRange", out var range);
            values.TryGetValue("type", out var type);

            // The newer "type" key: besides required and optional it can say the opposite
            // of a dependency, and then the range is the versions that do not work.
            if (string.Equals(type, "incompatible", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "discouraged", StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new ModConflict(depId, range, string.Equals(type, "incompatible", StringComparison.OrdinalIgnoreCase)));
                continue;
            }

            var required = !(values.TryGetValue("mandatory", out var mandatory) && mandatory.Equals("false", StringComparison.OrdinalIgnoreCase)) &&
                           !(type is not null && !type.Equals("required", StringComparison.OrdinalIgnoreCase));

            if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            {
                minecraft ??= range;
            }

            deps.Add(new ModDependency2(depId, range, required));
        }

        CollectNested(archive, JarJarPaths(archive), fabricFamily: false, depth: 1, budget, bundled);

        return Build(fileName, id, name, version, loader, deps, minecraft, conflicts, bundled);
    }

    /// <summary>
    /// The jars JarJar would unpack: the "path" of every entry in
    /// META-INF/jarjar/metadata.json, or, when that list cannot be read, every jar that
    /// lies in the folder.
    /// </summary>
    private static List<string> JarJarPaths(ZipArchive archive)
    {
        var paths = new List<string>();

        if (archive.GetEntry("META-INF/jarjar/metadata.json") is { } metadata)
        {
            try
            {
                using var doc = ParseJson(metadata);

                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
                {
                    foreach (var jar in jars.EnumerateArray())
                    {
                        if (jar.ValueKind == JsonValueKind.Object && TextOf(jar, "path") is { Length: > 0 } path)
                        {
                            paths.Add(path);
                        }
                    }
                }
            }
            catch (Exception)
            {
                paths.Clear();
            }
        }

        if (paths.Count == 0)
        {
            paths.AddRange(archive.Entries
                .Where(e => e.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase) &&
                            e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName));
        }

        return paths;
    }

    // ===================== Jars inside the jar =====================

    /// <summary>How many nested jars have been opened for the file being read, all levels together.</summary>
    private sealed class NestedBudget
    {
        public int Opened { get; set; }
    }

    /// <summary>
    /// Adds the ids of the nested jars at <paramref name="paths"/>, and of the jars nested
    /// in those. Each one is unpacked into memory, which is why there is a ceiling on
    /// its size, on how many are opened and on how deep it goes: a jar is a file somebody
    /// else made.
    /// </summary>
    private static void CollectNested(ZipArchive archive, IEnumerable<string> paths, bool fabricFamily, int depth, NestedBudget budget, List<ProvidedMod> into)
    {
        foreach (var path in paths)
        {
            if (budget.Opened >= MaxNestedJars)
            {
                return;
            }

            var entry = archive.GetEntry(path.Replace('\\', '/').TrimStart('/'));

            if (entry is null || entry.Length <= 0 || entry.Length > MaxNestedBytes)
            {
                continue;
            }

            budget.Opened++;

            try
            {
                using var buffer = new MemoryStream((int)entry.Length);

                using (var stream = entry.Open())
                {
                    CopyBounded(stream, buffer, MaxNestedBytes);
                }

                buffer.Position = 0;

                using var nested = new ZipArchive(buffer, ZipArchiveMode.Read);

                if (fabricFamily)
                {
                    ReadNestedFabric(nested, depth, budget, into);
                }
                else
                {
                    ReadNestedToml(nested, depth, budget, into);
                }
            }
            catch (Exception)
            {
                // A nested jar that cannot be read provides nothing the launcher can name.
            }
        }
    }

    private static void ReadNestedFabric(ZipArchive nested, int depth, NestedBudget budget, List<ProvidedMod> into)
    {
        var paths = new List<string>();

        if (nested.GetEntry("fabric.mod.json") is { } fabric)
        {
            using var doc = ParseJson(fabric);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && TextOf(root, "id") is { Length: > 0 } id)
            {
                var version = RealVersion(TextOf(root, "version"), nested);
                into.Add(new ProvidedMod(id, version));
                into.AddRange(StringsOf(root, "provides").Select(alias => new ProvidedMod(alias, version)));
                paths.AddRange(FabricJarPaths(root));
            }
        }
        else if (nested.GetEntry("quilt.mod.json") is { } quilt)
        {
            using var doc = ParseJson(quilt);

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("quilt_loader", out var loader) && loader.ValueKind == JsonValueKind.Object &&
                TextOf(loader, "id") is { Length: > 0 } id)
            {
                into.Add(new ProvidedMod(id, RealVersion(TextOf(loader, "version"), nested)));
                paths.AddRange(StringsOf(loader, "jars"));
            }
        }

        if (depth < MaxNestedDepth && paths.Count > 0)
        {
            CollectNested(nested, paths, fabricFamily: true, depth + 1, budget, into);
        }
    }

    private static void ReadNestedToml(ZipArchive nested, int depth, NestedBudget budget, List<ProvidedMod> into)
    {
        foreach (var entryName in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
        {
            if (nested.GetEntry(entryName) is not { } toml)
            {
                continue;
            }

            foreach (var section in SplitSections(ReadText(toml)).Where(s => TomlMod.IsMatch(s.Header)))
            {
                var values = KeyValues(section.Body);

                if (values.TryGetValue("modId", out var id) && !string.IsNullOrWhiteSpace(id))
                {
                    into.Add(new ProvidedMod(id, RealVersion(values.TryGetValue("version", out var v) ? v : null, nested)));
                }
            }
        }

        if (depth < MaxNestedDepth)
        {
            CollectNested(nested, JarJarPaths(nested), fabricFamily: false, depth + 1, budget, into);
        }
    }

    // ===================== Small things =====================

    private static ModMetadata Build(
        string fileName,
        string id,
        string name,
        string version,
        LoaderKind loader,
        IReadOnlyList<ModDependency2> deps,
        string? minecraft,
        IReadOnlyList<ModConflict> conflicts,
        List<ProvidedMod> bundled)
    {
        // The same module comes nested in several places; one line each is enough, and
        // where the copies differ the loader runs the newest.
        var distinct = bundled
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.Version ?? string.Empty, Comparer<string>.Create(VersionRange.CompareVersions)).First())
            .ToList();

        return new ModMetadata(fileName, id, name, version, loader, deps, minecraft, distinct.Select(p => p.Id).ToList())
        {
            Conflicts = conflicts.Where(c => !c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList(),
            Bundled = distinct
        };
    }

    /// <summary>
    /// "${version}" and "${file.jarVersion}" are build placeholders nobody filled in; the
    /// jar's manifest usually has the real number.
    /// </summary>
    private static string? RealVersion(string? declared, ZipArchive archive)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        return declared.Contains("${", StringComparison.Ordinal) ? ManifestVersion(archive) : declared.Trim();
    }

    private static IEnumerable<string> StringsOf(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return list.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static readonly Regex Quoted = new(@"[""']([^""']+)[""']", RegexOptions.Compiled);

    /// <summary>The strings of a one-line TOML array: provides = ["a", "b"].</summary>
    private static IEnumerable<string> QuotedStrings(string value)
        => Quoted.Matches(value).Select(m => m.Groups[1].Value);

    private static string? RangeText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => string.Join(" || ", element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())),
        _ => null
    };

    private static string ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxMetadataBytes)
        {
            throw new InvalidDataException("metadata entry too large");
        }

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes: the size in a zip header is a claim, not a fact.</summary>
    private static void CopyBounded(Stream source, Stream target, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;

            if (total > limit)
            {
                throw new InvalidDataException("nested jar larger than it says");
            }

            target.Write(buffer, 0, read);
        }
    }
}
