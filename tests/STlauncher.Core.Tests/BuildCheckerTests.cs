using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

public class BuildCheckerTests
{
    private static string TempGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        return root;
    }

    private static void FabricJar(string game, string file, string json)
    {
        using var archive = ZipFile.Open(Path.Combine(game, "mods", file), ZipArchiveMode.Create);
        var entry = archive.CreateEntry("fabric.mod.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(json);
    }

    private static void ForgeJar(string game, string file, string toml)
    {
        using var archive = ZipFile.Open(Path.Combine(game, "mods", file), ZipArchiveMode.Create);
        var entry = archive.CreateEntry("META-INF/mods.toml");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(toml);
    }

    [Theory]
    [InlineData(">=1.21 <1.22", "1.21.4", true)]
    [InlineData(">=1.21 <1.22", "1.22", false)]
    [InlineData("1.21.x", "1.21.4", true)]
    [InlineData("1.21.x", "1.20.1", false)]
    [InlineData("1.21", "1.21.4", true)]
    [InlineData("1.21.1", "1.21.4", false)]
    [InlineData("~1.21.1", "1.21.4", true)]
    [InlineData("~1.21.1", "1.22", false)]
    [InlineData("*", "1.8.9", true)]
    [InlineData("1.20.1 || 1.21.x", "1.21.11", true)]
    [InlineData("[1.21,1.22)", "1.21.4", true)]
    [InlineData("[1.21,1.22)", "1.22", false)]
    [InlineData("[1.20.1]", "1.20.1", true)]
    [InlineData("[1.20.1]", "1.20.2", false)]
    [InlineData("[1.21.1,)", "1.21.11", true)]
    [InlineData("garbage!!", "1.21.4", true)]
    public void VersionRange_Understands_FabricAndMavenRanges(string range, string version, bool expected)
    {
        Assert.Equal(expected, VersionRange.Satisfies(range, version));
    }

    [Fact]
    public void ReportsMissingDependency_Duplicate_WrongLoader_AndVersion()
    {
        var game = TempGame();
        FabricJar(game, "sodium-0.6.2.jar", """{ "id": "sodium", "name": "Sodium", "version": "0.6.2", "depends": { "fabricloader": ">=0.16", "minecraft": "1.21.x" } }""");
        FabricJar(game, "sodium-extra.jar", """{ "id": "sodium-extra", "name": "Sodium Extra", "version": "0.5", "depends": { "sodium": "*", "fabric-api": "*", "minecraft": ">=1.21 <1.22" } }""");
        FabricJar(game, "fabric-api.jar", """{ "id": "fabric-api", "name": "Fabric API", "version": "0.100", "depends": { "minecraft": "1.21.x" } }""");
        FabricJar(game, "iris-old.jar", """{ "id": "iris", "name": "Iris", "version": "1.7.0", "depends": { "minecraft": "1.20.x" } }""");
        FabricJar(game, "iris-new.jar", """{ "id": "iris", "name": "Iris", "version": "1.8.1", "depends": { "minecraft": "1.21.x" } }""");
        FabricJar(game, "emf.jar", """{ "id": "entity_model_features", "name": "EMF", "version": "2.0", "depends": { "entity_texture_features": "*" } }""");
        File.Move(Path.Combine(game, "mods", "emf.jar"), Path.Combine(game, "mods", "emf.jar.disabled"));
        FabricJar(game, "fresh.jar", """{ "id": "fresh_animations", "name": "Fresh Animations", "version": "1.9", "depends": { "entity_model_features": "*" } }""");
        ForgeJar(game, "jei-forge.jar", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"jei\"\ndisplayName=\"JEI\"\nversion=\"19.0\"\n[[dependencies.jei]]\nmodId=\"forge\"\nmandatory=true\nversionRange=\"[47,)\"\n");

        var issues = BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4");

        // Sodium Extra's fabric-api is present, sodium is present: no issue from it.
        Assert.DoesNotContain(issues, i => i.Subject == "Sodium Extra");

        var duplicate = Assert.Single(issues, i => i.Kind == BuildIssueKind.DuplicateMod);
        Assert.Equal("iris-old.jar", duplicate.FileName);
        Assert.Equal("iris-new.jar", duplicate.Detail);

        var wrongLoader = Assert.Single(issues, i => i.Kind == BuildIssueKind.WrongLoader);
        Assert.Equal("jei-forge.jar", wrongLoader.FileName);

        var missing = Assert.Single(issues, i => i.Kind == BuildIssueKind.MissingDependency);
        Assert.Equal("Fresh Animations", missing.Subject);
        Assert.Equal("entity_model_features", missing.Detail);
        Assert.Equal("emf.jar.disabled", missing.DisabledFileName);

        var version = Assert.Single(issues, i => i.Kind == BuildIssueKind.WrongGameVersion);
        Assert.Equal("iris-old.jar", version.FileName);
        Assert.False(version.IsBlocking);
    }

    [Fact]
    public void FabricApiModules_CountAsProvided_ByTheUmbrellaJar()
    {
        var game = TempGame();
        FabricJar(game, "fabric-api.jar", """{ "id": "fabric-api", "name": "Fabric API", "version": "0.100" }""");
        FabricJar(game, "cloth.jar", """{ "id": "cloth-config", "name": "Cloth Config", "version": "15", "depends": { "fabric-resource-loader-v0": "*" } }""");

        Assert.Empty(BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4"));
    }

    [Fact]
    public void VanillaBuild_AndOptionalDependencies_RaiseNothing()
    {
        var game = TempGame();
        FabricJar(game, "a.jar", """{ "id": "a", "name": "A", "version": "1", "recommends": { "b": "*" } }""");

        Assert.Empty(BuildChecker.Check(game, LoaderKind.Fabric, "1.21.4"));
        Assert.Empty(BuildChecker.Check(game, LoaderKind.Vanilla, "1.21.4"));
    }

    [Fact]
    public void ReadsForgeToml_WithDependencies()
    {
        var game = TempGame();
        ForgeJar(game, "jei.jar", "modLoader=\"javafml\"\nloaderVersion=\"[47,)\"\n[[mods]]\nmodId=\"jei\"\nversion=\"19.0\"\ndisplayName=\"Just Enough Items\"\n[[dependencies.jei]]\nmodId=\"forge\"\nmandatory=true\nversionRange=\"[47,)\"\n[[dependencies.jei]]\nmodId=\"minecraft\"\nmandatory=true\nversionRange=\"[1.20.1,1.20.2)\"\n[[dependencies.jei]]\nmodId=\"curios\"\nmandatory=false\nversionRange=\"*\"\n");

        var meta = Assert.Single(ModMetadataReader.Read(Path.Combine(game, "mods", "jei.jar")));

        Assert.Equal("jei", meta.Id);
        Assert.Equal("Just Enough Items", meta.Name);
        Assert.Equal(LoaderKind.Forge, meta.Loader);
        Assert.Equal("[1.20.1,1.20.2)", meta.MinecraftRange);
        Assert.Equal(3, meta.Dependencies.Count);
        Assert.False(meta.Dependencies.Single(d => d.Id == "curios").Required);

        var issues = BuildChecker.Check(game, LoaderKind.Forge, "1.21.1");
        Assert.Single(issues, i => i.Kind == BuildIssueKind.WrongGameVersion);
    }
}
