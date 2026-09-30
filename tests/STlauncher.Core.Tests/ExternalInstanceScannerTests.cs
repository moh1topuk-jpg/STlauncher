using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using STlauncher.Core.Import;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

public class ExternalInstanceScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    private string DotMinecraft()
    {
        var path = Path.Combine(_root, ".minecraft");
        Directory.CreateDirectory(Path.Combine(path, "versions"));
        return path;
    }

    private static void WriteVersion(string dotMinecraft, string id, string json)
    {
        var directory = Path.Combine(dotMinecraft, "versions", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
    }

    [Fact]
    public void Scan_FindsVersionsOfADotMinecraftFolder()
    {
        var mc = DotMinecraft();

        WriteVersion(mc, "1.21.1", """
            { "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }
            """);

        WriteVersion(mc, "fabric-1.21.1", """
            {
              "id": "fabric-1.21.1",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "net.fabricmc:fabric-loader:0.16.0" } ]
            }
            """);

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft);

        Assert.Equal(2, found.Count);
        Assert.All(found, i => Assert.True(i.IsUsable));
        Assert.Equal(LoaderKind.Fabric, found.Single(i => i.Name == "fabric-1.21.1").Loader);
        Assert.Equal(LoaderKind.Vanilla, found.Single(i => i.Name == "1.21.1").Loader);
    }

    [Fact]
    public void Scan_UsesTheVersionFolderWhenTheBuildLivesInIt()
    {
        // TLauncher's "separate folder" mode: the mods, worlds and configs of a build sit in
        // versions/<id>, next to the profile. The shared .minecraft has none of them - this
        // is the real layout that imported as "0 mods".
        var mc = DotMinecraft();
        const string id = "fabric 1.21.11 shield";

        WriteVersion(mc, id, """
            {
              "id": "fabric 1.21.11 shield",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [
                { "name": "net.fabricmc:intermediary:1.21.11" },
                { "name": "net.fabricmc:fabric-loader:0.18.1" }
              ]
            }
            """);

        var versionDirectory = Path.Combine(mc, "versions", id);
        Directory.CreateDirectory(Path.Combine(versionDirectory, "mods"));
        File.WriteAllText(Path.Combine(versionDirectory, "mods", "sodium.jar"), "x");
        File.WriteAllText(Path.Combine(versionDirectory, "mods", "lithium.jar"), "x");

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single();

        Assert.Equal(versionDirectory, found.GameDirectory);
        Assert.Equal(2, found.ModCount);
        Assert.Equal("1.21.11", found.GameVersion);
        Assert.Equal("0.18.1", found.LoaderVersion);
        Assert.True(found.HasOwnProfile);
    }

    [Fact]
    public void Scan_KeepsTheSharedFolderForAnOrdinaryProfile()
    {
        var mc = DotMinecraft();

        WriteVersion(mc, "fabric-loader-0.16.0-1.21.1", """
            { "id": "x", "inheritsFrom": "1.21.1", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient" }
            """);

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single();

        Assert.Equal(mc, found.GameDirectory);
        Assert.Equal("1.21.1", found.GameVersion);
    }

    [Theory]
    [InlineData("""{ "libraries": [ { "name": "net.minecraftforge:forge:1.20.1-47.2.0" } ] }""", "whatever", "1.20.1")]
    [InlineData("""{ "libraries": [] }""", "Optifine 1.16.5 HD", "1.16.5")]
    [InlineData("""{ "libraries": [] }""", "1.21", "1.21")]
    [InlineData("""{ "libraries": [] }""", "my build", null)]
    [InlineData("""{ "libraries": [] }""", "Fabric 26.2", "26.2")]
    [InlineData("""{ "downloads": { "client": { "url": "https://x/client.jar" } }, "mainClass": "net.minecraft.client.main.Main" }""", "26.3-snapshot-1", "26.3-snapshot-1")]
    [InlineData("""{ "downloads": { "client": { "url": "https://x/client.jar" } }, "mainClass": "net.minecraft.client.main.Main" }""", "OptiFine 1.16.5", "1.16.5")]
    [InlineData("""{ "downloads": { "client": { "url": "https://x/client.jar" } }, "mainClass": "net.minecraft.client.main.Main" }""", "1.21.11", "1.21.11")]
    public void DetectGameVersion_ReadsWhatTheProfileIsBuiltOn(string json, string id, string? expected)
    {
        var profile = JsonSerializer.Deserialize<VersionJson>(json, MetadataJson.Options)!;

        Assert.Equal(expected, ExternalInstanceScanner.DetectGameVersion(profile, id));
    }

    [Fact]
    public void Scan_ReportsAVersionFolderWithNoProfile()
    {
        // Exactly what a real .minecraft folder is full of: a jar and natives left behind
        // by an interrupted install. Dropping it silently would look like a lost build.
        var mc = DotMinecraft();
        Directory.CreateDirectory(Path.Combine(mc, "versions", "Fabric 1.21.8", "natives"));

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single();

        Assert.False(found.IsUsable);
        Assert.Equal(ExternalInstanceProblem.MissingVersionJson, found.Problem);
        Assert.Equal("Fabric 1.21.8", found.Name);
    }

    [Fact]
    public void Scan_ReportsAProfileThatDescribesNothing()
    {
        var mc = DotMinecraft();
        WriteVersion(mc, "empty", """{ "id": "empty" }""");

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single();

        Assert.Equal(ExternalInstanceProblem.IncompleteProfile, found.Problem);
    }

    [Fact]
    public void Scan_ReportsUnreadableJson()
    {
        var mc = DotMinecraft();
        var directory = Path.Combine(mc, "versions", "broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{ not json");

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single();

        Assert.Equal(ExternalInstanceProblem.BrokenVersionJson, found.Problem);
    }

    [Fact]
    public void Scan_AcceptsProfilesWrittenByOtherLaunchers()
    {
        // TLauncher writes whole numbers as 1.0 / 21.0. Before the tolerant parse this
        // failed, and with it every build another launcher had installed.
        var mc = DotMinecraft();

        WriteVersion(mc, "OptiFine 1.21.11", """
            {
              "id": "OptiFine 1.21.11",
              "mainClass": "net.minecraft.launchwrapper.Launch",
              "complianceLevel": 1.0,
              "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21.0 }
            }
            """);

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single();

        Assert.True(found.IsUsable);
        Assert.Equal("OptiFine 1.21.11", found.VersionId);
    }

    [Fact]
    public void Scan_CountsModsOfTheSharedGameFolder()
    {
        var mc = DotMinecraft();
        WriteVersion(mc, "1.21.1", """{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }""");

        Directory.CreateDirectory(Path.Combine(mc, "mods"));
        File.WriteAllText(Path.Combine(mc, "mods", "sodium.jar"), "x");
        File.WriteAllText(Path.Combine(mc, "mods", "notes.txt"), "x");

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single();

        Assert.Equal(1, found.ModCount);
        Assert.True(found.SharesGameDirectory);
    }

    [Fact]
    public void Scan_ReadsAPrismInstance()
    {
        var instances = Path.Combine(_root, "prism");
        var instance = Path.Combine(instances, "My pack");
        Directory.CreateDirectory(Path.Combine(instance, ".minecraft", "saves"));

        File.WriteAllText(Path.Combine(instance, "mmc-pack.json"), """
            {
              "components": [
                { "uid": "net.minecraft", "version": "1.20.1" },
                { "uid": "net.fabricmc.fabric-loader", "version": "0.16.0" }
              ]
            }
            """);

        var found = ExternalInstanceScanner.Scan(instances, ExternalLauncherKind.Prism).Single();

        Assert.Equal("My pack", found.Name);
        Assert.Equal("1.20.1", found.VersionId);
        Assert.Equal(LoaderKind.Fabric, found.Loader);
        Assert.False(found.SharesGameDirectory);
        Assert.EndsWith(".minecraft", found.GameDirectory);
    }

    [Fact]
    public void DiscoverMultiMcFamily_FindsAForkByItsFiles()
    {
        // A fork nobody listed: the folder name is new, the files are Prism's.
        var data = Path.Combine(_root, "appdata");
        var fork = Path.Combine(data, "PineconeMC");
        var instance = Path.Combine(fork, "instances", "Survival");
        Directory.CreateDirectory(Path.Combine(instance, ".minecraft", "mods"));
        File.WriteAllText(Path.Combine(fork, "elyprismlauncher.cfg"), "[General]\nInstanceDir=instances\n");
        File.WriteAllText(Path.Combine(instance, "instance.cfg"), "[General]\nname=Survival\n");
        File.WriteAllText(Path.Combine(instance, "mmc-pack.json"), """
            { "components": [ { "uid": "net.minecraft", "version": "1.21.1" }, { "uid": "net.neoforged", "version": "21.1.0" } ] }
            """);
        Directory.CreateDirectory(Path.Combine(data, "Unrelated", "instances"));

        var roots = ExternalInstanceScanner.DiscoverMultiMcFamily(data);

        Assert.All(roots, r => Assert.Equal(ExternalLauncherKind.Prism, r.Kind));
        Assert.Contains(roots, r => r.Path.TrimEnd(Path.DirectorySeparatorChar).EndsWith(Path.Combine("PineconeMC", "instances"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(roots, r => r.Path.Contains("Unrelated", StringComparison.OrdinalIgnoreCase));

        var found = ExternalInstanceScanner.ScanAll(roots).Single();
        Assert.Equal("PineconeMC", found.LauncherName);
        Assert.Equal("Survival", found.Name);
        Assert.Equal("1.21.1", found.VersionId);
        Assert.Equal(LoaderKind.NeoForge, found.Loader);
    }

    [Fact]
    public void DiscoverMultiMcFamily_FindsAPortableZipUnderAnyName()
    {
        var downloads = Path.Combine(_root, "Downloads");
        var portable = Path.Combine(downloads, "PrismLauncher-Windows-MSVC-Portable-9.4");
        var instance = Path.Combine(portable, "instances", "Pack");
        Directory.CreateDirectory(Path.Combine(instance, "minecraft", "saves"));
        File.WriteAllText(Path.Combine(portable, "prismlauncher.cfg"), "InstanceDir=instances\n");
        File.WriteAllText(Path.Combine(instance, "mmc-pack.json"), """
            { "components": [ { "uid": "net.minecraft", "version": "1.20.4" } ] }
            """);

        var found = ExternalInstanceScanner.ScanAll(ExternalInstanceScanner.DiscoverMultiMcFamily(downloads)).Single();

        Assert.Equal("Pack", found.Name);
        Assert.Null(found.LauncherName);
        Assert.Equal(ExternalLauncherKind.Prism, found.Source);
        Assert.EndsWith("minecraft", found.GameDirectory);
    }

    [Fact]
    public void Scan_ReadsLegacyLauncherSubfolders()
    {
        var dotMinecraft = DotMinecraft();
        WriteVersion(dotMinecraft, "Fabric 1.21.11", """
            { "id": "Fabric 1.21.11", "inheritsFrom": "1.21.11", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "net.fabricmc:fabric-loader:0.16.9" }, { "name": "net.fabricmc:intermediary:1.21.11" } ] }
            """);
        var home = Path.Combine(dotMinecraft, "home", "Fabric 1.21.11");
        Directory.CreateDirectory(Path.Combine(home, "mods"));
        Directory.CreateDirectory(Path.Combine(home, "saves"));
        File.WriteAllBytes(Path.Combine(home, "mods", "sodium.jar"), new byte[] { 1 });
        Directory.CreateDirectory(Path.Combine(dotMinecraft, "home", "1.8.9", "saves"));

        var found = ExternalInstanceScanner.Scan(dotMinecraft, ExternalLauncherKind.DotMinecraft);

        var fabric = found.Single(f => f.Name == "Fabric 1.21.11" && f.HasOwnFolder);
        Assert.Equal(home, fabric.GameDirectory);
        Assert.Equal("Fabric 1.21.11", fabric.VersionId);
        Assert.Equal("1.21.11", fabric.GameVersion);
        Assert.Equal(LoaderKind.Fabric, fabric.Loader);
        Assert.Equal(1, fabric.ModCount);
        Assert.False(fabric.SharesGameDirectory);

        var vanilla = found.Single(f => f.Name == "1.8.9");
        Assert.True(vanilla.HasOwnFolder);
        Assert.Equal("1.8.9", vanilla.GameVersion);
    }

    [Fact]
    public void ConfiguredGameDirectories_ReadsEveryPropertiesFileUnderTlauncher()
    {
        var settings = Path.Combine(_root, ".tlauncher");
        var gameA = Path.Combine(_root, "GameA");
        var gameB = Path.Combine(_root, "GameB");
        Directory.CreateDirectory(gameA);
        Directory.CreateDirectory(gameB);
        Directory.CreateDirectory(Path.Combine(settings, "legacy", "Minecraft"));
        File.WriteAllText(Path.Combine(settings, "tlauncher-2.0.properties"), "minecraft.gamedir=" + gameA.Replace("\\", "\\\\").Replace(":", "\\:") + "\n");
        File.WriteAllText(Path.Combine(settings, "legacy", "Minecraft", "tl.properties"), "minecraft.gamedir=" + gameB + "\nother=1\n");
        File.WriteAllText(Path.Combine(settings, "legacy.properties"), "client.gamedir=" + Path.Combine(_root, "missing") + "\n");

        var found = ExternalInstanceScanner.ConfiguredGameDirectories(settings);

        Assert.Equal(2, found.Count);
        Assert.Contains(gameA, found, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(gameB, found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_ReadsACurseForgeInstance()
    {
        var instances = Path.Combine(_root, "curseforge");
        var instance = Path.Combine(instances, "All the Mods");
        Directory.CreateDirectory(Path.Combine(instance, "saves"));

        File.WriteAllText(Path.Combine(instance, "minecraftinstance.json"), """
            { "baseModLoader": { "name": "forge-47.2.0", "minecraftVersion": "1.20.1" } }
            """);

        var found = ExternalInstanceScanner.Scan(instances, ExternalLauncherKind.CurseForge).Single();

        Assert.Equal("1.20.1", found.VersionId);
        Assert.Equal(LoaderKind.Forge, found.Loader);
    }

    [Fact]
    public void ScanUnknownFolder_RecognisesADotMinecraftLayout()
    {
        var mc = DotMinecraft();
        WriteVersion(mc, "1.21.1", """{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }""");

        var found = ExternalInstanceScanner.ScanUnknownFolder(mc);

        Assert.Single(found);
        Assert.Equal("1.21.1", found[0].VersionId);
    }

    [Fact]
    public void Scan_IgnoresAMissingFolder()
        => Assert.Empty(ExternalInstanceScanner.Scan(
            Path.Combine(_root, "nothing here"), ExternalLauncherKind.DotMinecraft));

    [Theory]
    [InlineData("net.fabricmc.loader.impl.launch.knot.KnotClient", LoaderKind.Fabric)]
    [InlineData("org.quiltmc.loader.impl.launch.knot.KnotClient", LoaderKind.Quilt)]
    [InlineData("cpw.mods.bootstraplauncher.BootstrapLauncher", LoaderKind.Forge)]
    [InlineData("net.minecraft.launchwrapper.Launch", LoaderKind.Vanilla)]
    [InlineData("net.minecraft.client.main.Main", LoaderKind.Vanilla)]
    public void DetectLoader_ReadsTheMainClass(string mainClass, LoaderKind expected)
    {
        var json = JsonSerializer.Deserialize<VersionJson>(
            $$"""{ "id": "x", "mainClass": "{{mainClass}}" }""", MetadataJson.Options);

        Assert.Equal(expected, ExternalInstanceScanner.DetectLoader(json!));
    }

    [Fact]
    public void DetectLoader_PrefersNeoForgeOverForge()
    {
        var json = JsonSerializer.Deserialize<VersionJson>("""
            {
              "id": "x",
              "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
              "libraries": [ { "name": "net.neoforged:neoforge:21.1.0" } ]
            }
            """, MetadataJson.Options);

        Assert.Equal(LoaderKind.NeoForge, ExternalInstanceScanner.DetectLoader(json!));
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
