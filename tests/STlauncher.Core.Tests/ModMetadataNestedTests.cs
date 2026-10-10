using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// What a jar says about itself beyond its own id: the mods nested in it, the versions of
/// other mods it refuses to run with, and a different list of requirements per loader.
/// Every jar here is built by the test, entry by entry.
/// </summary>
public class ModMetadataNestedTests
{
    private static string TempGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        return root;
    }

    /// <summary>A jar as bytes: entry name to text, or to the bytes of a jar nested in it.</summary>
    private static byte[] Jar(params (string Name, object Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                var bytes = content as byte[] ?? Encoding.UTF8.GetBytes((string)content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        return buffer.ToArray();
    }

    private static string Put(string game, string file, byte[] jar)
    {
        var path = Path.Combine(game, "mods", file);
        File.WriteAllBytes(path, jar);
        return path;
    }

    private static byte[] Fabric(string json) => Jar(("fabric.mod.json", json));

    private static string Toml(string id, string version, string dependencies = "")
        => $"modLoader=\"javafml\"\n[[mods]]\nmodId=\"{id}\"\ndisplayName=\"{id}\"\nversion=\"{version}\"\n{dependencies}";

    // ===================== Nested jars =====================

    [Fact]
    public void FabricNestedJars_ProvideTheirIds_AliasesAndWhatIsNestedInThem()
    {
        var game = TempGame();

        var deepest = Fabric("""{ "id": "fabric-api-base", "version": "0.4.50" }""");
        var module = Jar(
            ("fabric.mod.json", """{ "id": "fabric-rendering-v1", "version": "8.0.1", "provides": ["fabric-rendering"], "jars": [{ "file": "META-INF/jars/base.jar" }] }"""),
            ("META-INF/jars/base.jar", deepest));

        Put(game, "fabric-api.jar", Jar(
            ("fabric.mod.json", """{ "id": "fabric-api", "name": "Fabric API", "version": "0.144.3", "jars": [{ "file": "META-INF/jars/rendering.jar" }, { "file": "META-INF/jars/absent.jar" }] }"""),
            ("META-INF/jars/rendering.jar", module),
            ("META-INF/jars/undeclared.jar", Fabric("""{ "id": "not-loaded", "version": "1" }"""))));

        // Not "fabric-" ids on purpose: those are waved through by name when Fabric API is present.
        Put(game, "needs.jar", Fabric("""{ "id": "needs", "version": "1", "depends": { "fabric-rendering": "*", "fabric-api-base": "*" } }"""));

        var meta = Assert.Single(ModMetadataReader.Read(Path.Combine(game, "mods", "fabric-api.jar")));

        Assert.Contains("fabric-rendering-v1", meta.Provides);
        Assert.Contains("fabric-rendering", meta.Provides);
        Assert.Contains("fabric-api-base", meta.Provides);

        // Fabric Loader opens only the jars the "jars" list names.
        Assert.DoesNotContain("not-loaded", meta.Provides);
        Assert.Equal("8.0.1", meta.Bundled.Single(b => b.Id == "fabric-rendering-v1").Version);

        Assert.Empty(BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4"));
    }

    [Fact]
    public void NestedJars_AreFollowedOnlySoDeep()
    {
        var game = TempGame();

        // level5 inside level4 inside ... inside the jar in mods/.
        var jar = Fabric("""{ "id": "level5", "version": "1" }""");

        for (var level = 4; level >= 0; level--)
        {
            var id = level == 0 ? "outer" : $"level{level}";
            jar = Jar(
                ("fabric.mod.json", $$"""{ "id": "{{id}}", "version": "1", "jars": [{ "file": "META-INF/jars/in.jar" }] }"""),
                ("META-INF/jars/in.jar", jar));
        }

        var meta = Assert.Single(ModMetadataReader.Read(Put(game, "outer.jar", jar)));

        Assert.Equal(new[] { "level1", "level2", "level3" }, meta.Provides.OrderBy(p => p).ToArray());
    }

    [Fact]
    public void NestedJars_AreCountedSoOneFileCannotOpenThousands()
    {
        var game = TempGame();
        var entries = new List<(string, object)>();
        var listed = new List<string>();

        for (var i = 0; i < 300; i++)
        {
            entries.Add(($"META-INF/jars/m{i}.jar", Fabric($$"""{ "id": "m{{i}}", "version": "1" }""")));
            listed.Add($$"""{ "file": "META-INF/jars/m{{i}}.jar" }""");
        }

        entries.Add(("fabric.mod.json", $$"""{ "id": "many", "version": "1", "jars": [{{string.Join(",", listed)}}] }"""));

        var meta = Assert.Single(ModMetadataReader.Read(Put(game, "many.jar", Jar(entries.ToArray()))));

        Assert.Equal(256, meta.Provides.Count);
    }

    [Fact]
    public void JarJar_ProvidesTheModsItCarries_ByMetadataAndByFolder()
    {
        var game = TempGame();
        var flywheel = Jar(("META-INF/neoforge.mods.toml", Toml("flywheel", "1.0.4")));
        var ponder = Jar(("META-INF/mods.toml", Toml("ponder", "${file.jarVersion}")), ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nImplementation-Version: 1.0.52\r\n"));
        var plainLibrary = Jar(("com/example/Lib.class", "not a mod"));

        Put(game, "create.jar", Jar(
            ("META-INF/neoforge.mods.toml", Toml("create", "6.0.6")),
            ("META-INF/jarjar/metadata.json", """{ "jars": [ { "identifier": { "group": "dev.engine_room", "artifact": "flywheel" }, "version": { "range": "[1.0.4,)", "artifactVersion": "1.0.4" }, "path": "META-INF/jarjar/flywheel-1.0.4.jar", "isObfuscated": false }, { "identifier": { "group": "x", "artifact": "lib" }, "version": { "range": "[1,)", "artifactVersion": "1" }, "path": "META-INF/jarjar/lib.jar" } ] }"""),
            ("META-INF/jarjar/flywheel-1.0.4.jar", flywheel),
            ("META-INF/jarjar/lib.jar", plainLibrary)));

        // No metadata.json: whatever lies in the folder is taken.
        Put(game, "addon.jar", Jar(
            ("META-INF/neoforge.mods.toml", Toml("addon", "1.0", "[[dependencies.addon]]\nmodId=\"flywheel\"\ntype=\"required\"\nversionRange=\"[1.0,)\"\n[[dependencies.addon]]\nmodId=\"ponder\"\ntype=\"required\"\nversionRange=\"*\"\n")),
            ("META-INF/jarjar/ponder.jar", ponder)));

        var create = Assert.Single(ModMetadataReader.Read(Path.Combine(game, "mods", "create.jar")));
        Assert.Equal(new[] { "flywheel" }, create.Provides.ToArray());

        var addon = Assert.Single(ModMetadataReader.Read(Path.Combine(game, "mods", "addon.jar")));
        Assert.Equal("1.0.52", addon.Bundled.Single(b => b.Id == "ponder").Version);

        Assert.Empty(BuildChecker.Check(game, LoaderKind.NeoForge, "1.21.1"));
    }

    // ===================== Breaks, conflicts, incompatible =====================

    [Theory]
    [InlineData("0.144.3+1.21.11", false)]
    [InlineData("0.150.0", false)]
    [InlineData("0.140.0+1.21.11", true)]
    public void Breaks_IsAConflictOnlyForTheVersionsItNames(string fabricApiVersion, bool expected)
    {
        var game = TempGame();
        Put(game, "fabric-api.jar", Fabric($$"""{ "id": "fabric-api", "name": "Fabric API", "version": "{{fabricApiVersion}}" }"""));
        Put(game, "sodium.jar", Fabric("""{ "id": "sodium", "name": "Sodium", "version": "0.6.13", "breaks": { "fabric-api": "<0.144.3", "optifabric": "*" } }"""));

        var issues = BuildChecker.Check(game, LoaderKind.Fabric, "1.21.11");

        if (!expected)
        {
            Assert.Empty(issues);
            return;
        }

        var issue = Assert.Single(issues);
        Assert.Equal(BuildIssueKind.Incompatible, issue.Kind);
        Assert.Equal("Sodium", issue.Subject);
        Assert.Equal("sodium.jar", issue.FileName);
        Assert.Equal("Fabric API", issue.Detail);
        Assert.Equal("<0.144.3", issue.Range);
        Assert.Equal(fabricApiVersion, issue.OtherVersion);
        Assert.Equal("fabric-api.jar", issue.OtherFileName);
        Assert.True(issue.IsBlocking);

        // Which of the two goes, or is updated, is not for "fix everything" to decide.
        Assert.False(issue.FixableBySwitch);
    }

    [Fact]
    public void Breaks_WithoutARange_AndConflicts_AreToldApart()
    {
        var game = TempGame();
        Put(game, "optifabric.jar", Fabric("""{ "id": "optifabric", "name": "OptiFabric", "version": "1.14" }"""));
        Put(game, "sodium.jar", Fabric("""{ "id": "sodium", "name": "Sodium", "version": "0.6", "breaks": { "optifabric": "*" } }"""));
        Put(game, "iris.jar", Fabric("""{ "id": "iris", "name": "Iris", "version": "1.8", "conflicts": { "optifabric": ">=1.0" }, "breaks": { "absent-mod": "*", "sodium": "what is this" } }"""));

        var issues = BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4");

        var hard = Assert.Single(issues, i => i.Kind == BuildIssueKind.Incompatible);
        Assert.Equal("Sodium", hard.Subject);
        Assert.Null(hard.Range);

        var soft = Assert.Single(issues, i => i.Kind == BuildIssueKind.Discouraged);
        Assert.Equal("Iris", soft.Subject);
        Assert.False(soft.IsBlocking);

        // A range nobody can read is not a reason to stop the player.
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void Breaks_OnANestedMod_GoesByTheNestedVersion()
    {
        var game = TempGame();
        Put(game, "fabric-api.jar", Jar(
            ("fabric.mod.json", """{ "id": "fabric-api", "name": "Fabric API", "version": "0.100.0", "jars": [{ "file": "META-INF/jars/r.jar" }] }"""),
            ("META-INF/jars/r.jar", Fabric("""{ "id": "fabric-rendering-v1", "version": "3.2.0" }"""))));
        Put(game, "a.jar", Fabric("""{ "id": "a", "name": "A", "version": "1", "breaks": { "fabric-rendering-v1": "<4.0.0" } }"""));
        Put(game, "b.jar", Fabric("""{ "id": "b", "name": "B", "version": "1", "breaks": { "fabric-rendering-v1": "<3.0.0" } }"""));

        var issue = Assert.Single(BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4"));

        Assert.Equal("A", issue.Subject);
        Assert.Equal("3.2.0", issue.OtherVersion);
    }

    [Theory]
    [InlineData("1.5.0", true)]
    [InlineData("2.0.0", false)]
    public void ForgeIncompatibleType_IsReadWithItsRange(string otherVersion, bool expected)
    {
        var game = TempGame();
        Put(game, "other.jar", Jar(("META-INF/neoforge.mods.toml", Toml("other", otherVersion))));
        Put(game, "mod.jar", Jar(("META-INF/neoforge.mods.toml", Toml(
            "mod",
            "3.0",
            "[[dependencies.mod]]\nmodId=\"other\"\ntype=\"incompatible\"\nversionRange=\"[1.0,2.0)\"\nreason=\"crashes\"\n[[dependencies.mod]]\nmodId=\"other\"\ntype=\"discouraged\"\nversionRange=\"[5,)\"\n"))));

        var meta = Assert.Single(ModMetadataReader.Read(Path.Combine(game, "mods", "mod.jar")));

        // Neither is a dependency: a mod that names what it refuses does not ask for it.
        Assert.Empty(meta.Dependencies);
        Assert.Equal(2, meta.Conflicts.Count);

        var issues = BuildChecker.Check(game, LoaderKind.NeoForge, "1.21.1");

        if (expected)
        {
            Assert.Equal(BuildIssueKind.Incompatible, Assert.Single(issues).Kind);
        }
        else
        {
            Assert.Empty(issues);
        }
    }

    [Theory]
    [InlineData("<0.144.3", "0.140.0", true)]
    [InlineData("<0.144.3", "0.144.3", false)]
    [InlineData("*", null, true)]
    [InlineData(null, "?", true)]
    [InlineData("<2", "?", false)]
    [InlineData("<2", null, false)]
    [InlineData("nonsense", "1.0", false)]
    [InlineData("[1.0,2.0)", "1.5", true)]
    [InlineData("[1.0,2.0", "1.5", false)]
    [InlineData(">=1 <2 || 3.x", "3.4", true)]
    public void DefinitelyMatches_NeedsARangeItCanRead(string? range, string? version, bool expected)
    {
        Assert.Equal(expected, VersionRange.DefinitelyMatches(range, version));
    }

    // ===================== One jar, several loaders =====================

    [Fact]
    public void AJarForSeveralLoaders_IsJudgedByTheSectionOfTheLoaderThatRunsIt()
    {
        var game = TempGame();
        Put(game, "multi.jar", Jar(
            ("fabric.mod.json", """{ "id": "multi", "name": "Multi", "version": "2.0", "depends": { "fabric-api": "*" }, "breaks": { "helper": "*" } }"""),
            ("META-INF/neoforge.mods.toml", Toml("multi", "2.0", "[[dependencies.multi]]\nmodId=\"helper\"\ntype=\"required\"\nversionRange=\"[1,)\"\n"))));
        Put(game, "helper.jar", Jar(
            ("fabric.mod.json", """{ "id": "helper", "name": "Helper", "version": "1.0" }"""),
            ("META-INF/neoforge.mods.toml", Toml("helper", "1.0"))));

        // NeoForge reads its own section: "helper" is required there, and present.
        Assert.Empty(BuildChecker.Check(game, LoaderKind.NeoForge, "1.21.1"));

        // Fabric reads the other one: Fabric API is missing and "helper" is refused.
        var onFabric = BuildChecker.Check(game, LoaderKind.Fabric, "1.21.1");
        Assert.Single(onFabric, i => i.Kind == BuildIssueKind.MissingDependency && i.Detail == "fabric-api");
        Assert.Single(onFabric, i => i.Kind == BuildIssueKind.Incompatible && i.Detail == "Helper");
        Assert.Equal(2, onFabric.Count);

        // Forge reads neither.
        Assert.All(BuildChecker.Check(game, LoaderKind.Forge, "1.21.1"), i => Assert.Equal(BuildIssueKind.WrongLoader, i.Kind));
    }

    [Fact]
    public void WhichSectionALoaderReads_FollowsTheLoader()
    {
        var game = TempGame();
        var quiltOnly = ModMetadataReader.Read(Put(game, "q.jar", Jar(("quilt.mod.json", """{ "quilt_loader": { "id": "q", "version": "1", "depends": ["quilt_base", { "id": "org.quiltmc:qsl", "versions": ">=5" }], "breaks": [{ "id": "old", "versions": "<2" }], "provides": ["q-alias"] } }"""))));
        var both = ModMetadataReader.Read(Put(game, "fq.jar", Jar(
            ("fabric.mod.json", """{ "id": "fq", "version": "1", "depends": { "fabric-api": "*" } }"""),
            ("quilt.mod.json", """{ "quilt_loader": { "id": "fq", "version": "1", "depends": ["quilted_fabric_api"] } }"""))));
        var tomlOnly = ModMetadataReader.Read(Put(game, "t.jar", Jar(("META-INF/mods.toml", Toml("t", "1")))));

        Assert.Null(ModMetadataReader.SectionFor(quiltOnly, LoaderKind.Fabric));

        var quilt = ModMetadataReader.SectionFor(quiltOnly, LoaderKind.Quilt)!;
        Assert.Equal(new[] { "quilt_base", "qsl" }, quilt.Dependencies.Select(d => d.Id).ToArray());
        Assert.Equal("<2", Assert.Single(quilt.Conflicts).VersionRange);
        Assert.Contains("q-alias", quilt.Provides);

        Assert.Equal("fabric-api", ModMetadataReader.SectionFor(both, LoaderKind.Fabric)!.Dependencies.Single().Id);
        Assert.Equal("quilted_fabric_api", ModMetadataReader.SectionFor(both, LoaderKind.Quilt)!.Dependencies.Single().Id);

        // NeoForge took mods.toml until Minecraft 1.20.5 and only its own file after.
        Assert.NotNull(ModMetadataReader.SectionFor(tomlOnly, LoaderKind.Forge, "1.21.1"));
        Assert.NotNull(ModMetadataReader.SectionFor(tomlOnly, LoaderKind.NeoForge, "1.20.1"));
        Assert.Null(ModMetadataReader.SectionFor(tomlOnly, LoaderKind.NeoForge, "1.21.1"));
    }

    // ===================== What the loader brings =====================

    [Theory]
    [InlineData(null, false)]
    [InlineData("0.16.14", false)]
    [InlineData("0.15.0", false)]
    [InlineData("0.14.25", true)]
    public void MixinExtras_IsTheLoadersOwn_FromFabricLoader015(string? loaderVersion, bool missing)
    {
        var game = TempGame();
        Put(game, "a.jar", Fabric("""{ "id": "a", "name": "A", "version": "1", "depends": { "mixinextras": ">=0.3.5", "fabricloader": ">=0.14", "java": ">=17", "minecraft": "*" } }"""));

        var issues = BuildChecker.Check(game, LoaderKind.Fabric, "1.20.1", loaderVersion);

        if (missing)
        {
            Assert.Equal("mixinextras", Assert.Single(issues).Detail);
        }
        else
        {
            Assert.Empty(issues);
        }
    }

    // ===================== The cache =====================

    [Fact]
    public void Cache_OpensAJarOnce_UntilTheFileOrTheReaderChanges()
    {
        var game = TempGame();
        var store = Path.Combine(game, "cache", "meta.json");
        var a = Put(game, "a.jar", Fabric("""{ "id": "a", "name": "A", "version": "1", "depends": { "b": "*" } }"""));
        Put(game, "b.jar", Fabric("""{ "id": "b", "name": "B", "version": "1" }"""));
        Put(game, "library.jar", Jar(("x.class", "no metadata at all")));

        var cache = new ModMetadataCache(store);

        Assert.Empty(BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4", cache: cache));
        Assert.Equal(3, cache.Misses);

        Assert.Empty(BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4", cache: cache));
        Assert.Equal(3, cache.Misses);

        // Switched off: a new name for the same file. Not read again, and reported under the new name.
        File.Move(Path.Combine(game, "mods", "b.jar"), Path.Combine(game, "mods", "b.jar.disabled"));
        var missing = Assert.Single(BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4", cache: cache));
        Assert.Equal("b.jar.disabled", missing.DisabledFileName);
        Assert.Equal(3, cache.Misses);

        // A different file under the old name is read.
        File.WriteAllBytes(a, Fabric("""{ "id": "a", "name": "A", "version": "2", "depends": { "b": "*", "c-is-new": "*" } }"""));
        Assert.Equal(2, BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4", cache: cache).Count);
        Assert.Equal(4, cache.Misses);

        // The next start of the launcher finds the answers on disk.
        var restarted = new ModMetadataCache(store);
        Assert.Equal(2, BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4", cache: restarted).Count);
        Assert.Equal(0, restarted.Misses);

        // A reader of another revision does not trust them.
        var text = File.ReadAllText(store);
        Assert.Contains($"\"Revision\":{ModMetadataReader.ParserRevision}", text);
        File.WriteAllText(store, text.Replace($"\"Revision\":{ModMetadataReader.ParserRevision}", "\"Revision\":1"));

        var newer = new ModMetadataCache(store);
        Assert.Equal(2, BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4", cache: newer).Count);
        Assert.Equal(3, newer.Misses);

        // A file that left the folder leaves the cache.
        File.Delete(Path.Combine(game, "mods", "library.jar"));
        BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4", cache: newer);
        Assert.DoesNotContain("library.jar", File.ReadAllText(store));
    }

    [Fact]
    public void Cache_SurvivesAStoreThatIsNotItsOwn()
    {
        var game = TempGame();
        var store = Path.Combine(game, "cache", "meta.json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, "{ \"Revision\": " + ModMetadataReader.ParserRevision + ", \"Entries\": { \"a.jar\": { \"Size\": 1, \"Stamp\": 1, \"Sections\": [ { \"Id\": null } ] } } ");
        Put(game, "a.jar", Fabric("""{ "id": "a", "name": "A", "version": "1" }"""));

        var cache = new ModMetadataCache(store);

        Assert.Empty(BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4", cache: cache));
        Assert.Equal(1, cache.Misses);
    }
}
