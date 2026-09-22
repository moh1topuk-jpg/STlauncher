using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using STlauncher.Core.Import;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// One test per launcher, each with a fixture in the shape that launcher really writes.
/// Import used to work for TLauncher only: every other launcher was either not looked
/// for where it actually installs, or its description file was not understood.
/// </summary>
public class OtherLaunchersImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    private string Instance(string launcher, string name, string? gameSubfolder = null)
    {
        var directory = Path.Combine(_root, launcher, name);
        var game = gameSubfolder is null ? directory : Path.Combine(directory, gameSubfolder);
        Directory.CreateDirectory(Path.Combine(game, "saves", "world"));
        return directory;
    }

    private static void Write(string directory, string file, string content)
    {
        var path = Path.Combine(directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A mod jar with the metadata files a real one carries.</summary>
    private static void WriteModJar(string modsDirectory, string name, params (string Entry, string Content)[] entries)
    {
        Directory.CreateDirectory(modsDirectory);
        using var archive = ZipFile.Open(Path.Combine(modsDirectory, name), ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }
    }

    [Fact]
    public void Prism_ReadsThePackAndTheDisplayName()
    {
        var instance = Instance("prism", "AllTheMods9", ".minecraft");
        Write(instance, "mmc-pack.json", """
            { "components": [
                { "uid": "net.minecraft", "version": "1.20.1" },
                { "uid": "net.neoforged", "version": "47.1.3" } ] }
            """);
        Write(instance, "instance.cfg", "[General]\nInstanceType=OneSix\nname=All the Mods 9\n");

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "prism"), ExternalLauncherKind.Prism).Single();

        Assert.Equal("All the Mods 9", found.Name);
        Assert.Equal("1.20.1", found.VersionId);
        Assert.Equal(LoaderKind.NeoForge, found.Loader);
        Assert.Equal("47.1.3", found.LoaderVersion);
        Assert.EndsWith(".minecraft", found.GameDirectory);
    }

    [Fact]
    public void Prism_TheInstanceFolderCanBeMovedInTheSettings()
    {
        var elsewhere = Path.Combine(_root, "D-drive", "MyInstances");
        var instance = Path.Combine(elsewhere, "Pack");
        Directory.CreateDirectory(Path.Combine(instance, ".minecraft", "mods"));
        Write(instance, "mmc-pack.json", """{ "components": [ { "uid": "net.minecraft", "version": "1.21.1" } ] }""");

        var appData = Path.Combine(_root, "appdata", "PrismLauncher");
        Write(appData, "prismlauncher.cfg", $"[General]\nInstanceDir={elsewhere.Replace('\\', '/')}\n");

        // The same code DefaultRoots runs, on a folder the test controls.
        var roots = new System.Collections.Generic.List<(string, ExternalLauncherKind)>();
        ExternalInstanceScanner.AddConfiguredInstanceDirectory(roots, Path.Combine(appData, "prismlauncher.cfg"), ExternalLauncherKind.Prism);

        Assert.Single(roots);
        var found = ExternalInstanceScanner.ScanAll(roots);
        Assert.Equal("1.21.1", found.Single().VersionId);
    }

    [Fact]
    public void CurseForge_AVanillaInstanceHasNoLoaderBlockAtAll()
    {
        // baseModLoader is JSON null for vanilla; that ended the parse and lost the instance.
        var instance = Instance("curseforge", "Vanilla 1.21");
        Write(instance, "minecraftinstance.json", """
            { "name": "Vanilla 1.21", "gameVersion": "1.21", "baseModLoader": null }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "curseforge"), ExternalLauncherKind.CurseForge).Single();

        Assert.True(found.IsUsable);
        Assert.Equal("1.21", found.VersionId);
        Assert.Equal(LoaderKind.Vanilla, found.Loader);
    }

    [Fact]
    public void CurseForge_ReadsTheLoaderVersionFromItsName()
    {
        var instance = Instance("curseforge", "Fabulously Optimized");
        Write(instance, "minecraftinstance.json", """
            { "gameVersion": "1.20.4", "baseModLoader": { "name": "fabric-0.15.11", "minecraftVersion": "1.20.4" } }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "curseforge"), ExternalLauncherKind.CurseForge).Single();

        Assert.Equal(LoaderKind.Fabric, found.Loader);
        Assert.Equal("0.15.11", found.LoaderVersion);
    }

    [Fact]
    public void Modrinth_OldAppWroteAProfileFile()
    {
        var instance = Instance("modrinth", "Cobblemon");
        Write(instance, "profile.json", """
            { "metadata": { "name": "Cobblemon", "game_version": "1.20.1", "loader": "fabric", "loader_version": "0.15.7" } }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "modrinth"), ExternalLauncherKind.Modrinth).Single();

        Assert.Equal("1.20.1", found.VersionId);
        Assert.Equal(LoaderKind.Fabric, found.Loader);
        Assert.Equal("0.15.7", found.LoaderVersion);
    }

    [Fact]
    public void Modrinth_NewAppKeepsNothingReadable_SoTheModsAreAsked()
    {
        // The Modrinth App now stores profiles in a database. The folder has only the
        // game files - which is still enough: the mods say what they are for.
        var instance = Instance("modrinth", "Simply Optimized");
        var mods = Path.Combine(instance, "mods");

        WriteModJar(mods, "sodium.jar", ("fabric.mod.json", """{ "id": "sodium", "depends": { "minecraft": "~1.21.4" } }"""));
        WriteModJar(mods, "lithium.jar", ("fabric.mod.json", """{ "id": "lithium", "depends": { "minecraft": ">=1.21.4 <1.22" } }"""));
        WriteModJar(mods, "iris.jar", ("fabric.mod.json", """{ "id": "iris", "depends": { "minecraft": "1.21.x" } }"""));

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "modrinth"), ExternalLauncherKind.Modrinth).Single();

        Assert.True(found.IsUsable);
        Assert.Equal("1.21.4", found.VersionId);
        Assert.Equal(LoaderKind.Fabric, found.Loader);
        Assert.True(found.VersionInferred);
        Assert.Equal(3, found.ModCount);
    }

    [Fact]
    public void ModInspection_TellsNeoForgeFromForge()
    {
        var mods = Path.Combine(_root, "mods-neo");

        WriteModJar(mods, "a.jar", ("META-INF/neoforge.mods.toml", """
            modLoader="javafml"
            [[dependencies.a]]
                modId="minecraft"
                type="required"
                versionRange="[1.21.1,1.22)"
            """));
        WriteModJar(mods, "b.jar", ("META-INF/neoforge.mods.toml", """
            [[dependencies.b]]
            modId="neoforge"
            versionRange="[21.1,)"
            [[dependencies.b]]
            modId="minecraft"
            versionRange="[1.21.1]"
            """));

        var verdict = ModFolderInspector.Inspect(mods)!;

        Assert.Equal(LoaderKind.NeoForge, verdict.Loader);
        Assert.Equal("1.21.1", verdict.GameVersion);
    }

    [Fact]
    public void ModInspection_ForgeToml_WithVersionRangeBeforeModId()
    {
        var mods = Path.Combine(_root, "mods-forge");

        WriteModJar(mods, "jei.jar", ("META-INF/mods.toml", """
            [[dependencies.jei]]
                versionRange="[1.20.1,1.21)"
                modId="minecraft"
                mandatory=true
            """));

        var verdict = ModFolderInspector.Inspect(mods)!;

        Assert.Equal(LoaderKind.Forge, verdict.Loader);
        Assert.Equal("1.20.1", verdict.GameVersion);
    }

    [Fact]
    public void ModInspection_AMultiLoaderJarDoesNotVote()
    {
        var mods = Path.Combine(_root, "mods-mixed");

        WriteModJar(mods, "both.jar",
            ("fabric.mod.json", """{ "id": "both" }"""),
            ("META-INF/mods.toml", "modLoader=\"javafml\""));
        WriteModJar(mods, "quilt-only.jar", ("quilt.mod.json", """{ "quilt_loader": { "id": "q" } }"""));

        Assert.Equal(LoaderKind.Quilt, ModFolderInspector.Inspect(mods)!.Loader);
    }

    [Fact]
    public void GdLauncher_TheElectronOneKeepsALoaderBlockInConfigJson()
    {
        var instance = Instance("gdl", "SkyFactory");
        Write(instance, "config.json", """
            { "loader": { "loaderType": "forge", "mcVersion": "1.16.5", "loaderVersion": "36.2.39" } }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "gdl"), ExternalLauncherKind.GdLauncher).Single();

        Assert.Equal("1.16.5", found.VersionId);
        Assert.Equal(LoaderKind.Forge, found.Loader);
        Assert.Equal("36.2.39", found.LoaderVersion);
    }

    [Fact]
    public void GdLauncherCarbon_KeepsTheGameInAnInstanceSubfolder()
    {
        var instance = Instance("carbon", "Create Pack", "instance");
        Write(instance, "instance.json", """
            { "name": "Create Pack", "game_configuration": { "version": { "Standard": {
                "release": "1.20.1", "modloaders": [ { "type_": "fabric", "version": "0.15.11" } ] } } } }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "carbon"), ExternalLauncherKind.GdLauncher).Single();

        Assert.Equal("1.20.1", found.VersionId);
        Assert.Equal(LoaderKind.Fabric, found.Loader);
        Assert.EndsWith("instance", found.GameDirectory);
    }

    [Fact]
    public void AtLauncher_ReadsInstanceJson()
    {
        var instance = Instance("atl", "Vault Hunters");
        Write(instance, "instance.json", """
            { "id": "1.18.2", "launcher": { "name": "Vault Hunters 3", "loaderVersion": { "type": "Forge", "version": "40.2.0" } } }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "atl"), ExternalLauncherKind.AtLauncher).Single();

        Assert.Equal("Vault Hunters 3", found.Name);
        Assert.Equal("1.18.2", found.VersionId);
        Assert.Equal(LoaderKind.Forge, found.Loader);
        Assert.Equal("40.2.0", found.LoaderVersion);
    }

    [Fact]
    public void FtbApp_NamesTheLoaderAndItsVersionInOneString()
    {
        var instance = Instance("ftb", "8f2c-uuid");
        Write(instance, "instance.json", """
            { "name": "FTB Skies", "mcVersion": "1.19.2", "modLoader": "forge-43.2.14" }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "ftb"), ExternalLauncherKind.Ftb).Single();

        Assert.Equal("FTB Skies", found.Name);
        Assert.Equal("1.19.2", found.VersionId);
        Assert.Equal(LoaderKind.Forge, found.Loader);
        Assert.Equal("43.2.14", found.LoaderVersion);
    }

    [Fact]
    public void Xmcl_ReadsTheRuntimeBlock()
    {
        var instance = Instance("xmcl", "my-pack");
        Write(instance, "instance.json", """
            { "name": "My pack", "runtime": { "minecraft": "1.21.1", "fabricLoader": "0.16.5", "forge": "" } }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "xmcl"), ExternalLauncherKind.Xmcl).Single();

        Assert.Equal("1.21.1", found.VersionId);
        Assert.Equal(LoaderKind.Fabric, found.Loader);
        Assert.Equal("0.16.5", found.LoaderVersion);
    }

    [Fact]
    public void Technic_ThePackCarriesAFullProfile()
    {
        var instance = Instance("technic", "tekkit-2");
        Write(instance, "bin/version.json", """
            { "id": "1.12.2-forge-14.23.5.2860", "inheritsFrom": "1.12.2",
              "mainClass": "net.minecraft.launchwrapper.Launch",
              "libraries": [ { "name": "net.minecraftforge:forge:1.12.2-14.23.5.2860" } ] }
            """);

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "technic"), ExternalLauncherKind.Technic).Single();

        Assert.Equal("1.12.2", found.VersionId);
        Assert.Equal(LoaderKind.Forge, found.Loader);
        // Not launched by that profile: the launcher installs Forge for 1.12.2 itself.
        Assert.False(found.HasOwnProfile);
    }

    [Fact]
    public void OfficialLauncher_AProfileWithItsOwnGameFolderIsABuild()
    {
        // The official launcher's "game directory" setting: worlds and mods live there,
        // the version in the shared versions/ folder.
        var mc = Path.Combine(_root, ".minecraft");
        Write(mc, "versions/fabric-loader-0.16.0-1.21.1/fabric-loader-0.16.0-1.21.1.json", """
            { "id": "fabric-loader-0.16.0-1.21.1", "inheritsFrom": "1.21.1",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "net.fabricmc:fabric-loader:0.16.0" } ] }
            """);

        var own = Path.Combine(_root, "MyFabricWorld");
        Directory.CreateDirectory(Path.Combine(own, "mods"));
        File.WriteAllText(Path.Combine(own, "mods", "sodium.jar"), "x");

        Write(mc, "launcher_profiles.json", $$"""
            { "profiles": {
                "abc": { "name": "Fabric survival", "gameDir": {{System.Text.Json.JsonSerializer.Serialize(own)}}, "lastVersionId": "fabric-loader-0.16.0-1.21.1" },
                "def": { "name": "Latest", "lastVersionId": "latest-release" } } }
            """);

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft);

        var build = found.Single(i => i.Name == "Fabric survival");
        Assert.Equal(own, build.GameDirectory);
        Assert.Equal("fabric-loader-0.16.0-1.21.1", build.VersionId);
        Assert.Equal("1.21.1", build.GameVersion);
        Assert.Equal(1, build.ModCount);
        Assert.True(build.HasOwnFolder);
        Assert.False(build.SharesGameDirectory);

        // The plain version and the profile with no folder of its own: just the version.
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void AFolderNobodyDescribed_IsStillOffered_WithTheVersionLeftToThePlayer()
    {
        var instance = Instance("hand-made", "Old world");

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "hand-made"), ExternalLauncherKind.Unknown).Single();

        Assert.True(found.IsUsable);
        Assert.False(found.HasKnownVersion);
        Assert.Equal(string.Empty, found.VersionId);
    }

    [Fact]
    public void UnknownFolder_ALauncherRootIsWalkedIntoItsInstances()
    {
        var launcher = Path.Combine(_root, "MultiMC");
        var instance = Path.Combine(launcher, "instances", "Pack");
        Directory.CreateDirectory(Path.Combine(instance, ".minecraft", "saves"));
        Write(instance, "mmc-pack.json", """{ "components": [ { "uid": "net.minecraft", "version": "1.20.1" } ] }""");

        var found = ExternalInstanceScanner.ScanUnknownFolder(launcher);

        Assert.Equal("1.20.1", found.Single().VersionId);
    }

    [Fact]
    public void UnknownFolder_ASingleDescribedBuild_DoesNotAlsoListItsMinecraftSubfolder()
    {
        var instance = Path.Combine(_root, "Pack");
        Directory.CreateDirectory(Path.Combine(instance, ".minecraft", "saves", "w"));
        Write(instance, "mmc-pack.json", """{ "components": [ { "uid": "net.minecraft", "version": "1.20.1" } ] }""");

        var found = ExternalInstanceScanner.ScanUnknownFolder(instance);

        Assert.Single(found);
        Assert.Equal("Pack", found[0].Name);
    }

    [Fact]
    public void ReadPropertiesValue_UndoesJavaEscapes()
    {
        var path = Path.Combine(_root, "tlauncher-2.0.properties");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "login.auto=true\nminecraft.gamedir=D\\:\\\\Games\\\\mc\n");

        Assert.Equal(@"D:\Games\mc", ExternalInstanceScanner.ReadPropertiesValue(path, "minecraft.gamedir"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
