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
