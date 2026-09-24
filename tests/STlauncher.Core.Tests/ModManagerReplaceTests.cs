using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using STlauncher.Core.Http;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

public class ModManagerReplaceTests
{
    private static string TempGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        return root;
    }

    private static void FabricJar(string game, string file, string id, string version)
    {
        using var archive = ZipFile.Open(Path.Combine(game, "mods", file), ZipArchiveMode.Create);
        var entry = archive.CreateEntry("fabric.mod.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write("{\"schemaVersion\":1,\"id\":\"" + id + "\",\"version\":\"" + version + "\",\"name\":\"" + id + "\"}");
    }

    private static ModManager Manager() => new(new DownloadClient(new System.Net.Http.HttpClient()));

    [Fact]
    public void RemoveOtherVersions_DeletesTheOlderJarWithTheSameId()
    {
        var game = TempGame();
        FabricJar(game, "sodium-fabric-0.8.2+mc1.21.11.jar", "sodium", "0.8.2");
        FabricJar(game, "sodium-fabric-0.8.12+mc1.21.11.jar", "sodium", "0.8.12");
        FabricJar(game, "lithium-fabric-0.21.4+mc1.21.11.jar", "lithium", "0.21.4");

        var removed = Manager().RemoveOtherVersions(game, "sodium-fabric-0.8.12+mc1.21.11.jar");

        Assert.Equal(new[] { "sodium-fabric-0.8.2+mc1.21.11.jar" }, removed);
        Assert.False(File.Exists(Path.Combine(game, "mods", "sodium-fabric-0.8.2+mc1.21.11.jar")));
        Assert.True(File.Exists(Path.Combine(game, "mods", "sodium-fabric-0.8.12+mc1.21.11.jar")));
        Assert.True(File.Exists(Path.Combine(game, "mods", "lithium-fabric-0.21.4+mc1.21.11.jar")));
    }

    [Fact]
    public void RemoveOtherVersions_LeavesDisabledCopiesAndUnknownJars()
    {
        var game = TempGame();
        FabricJar(game, "sodium-old.jar.disabled", "sodium", "0.8.2");
        FabricJar(game, "sodium-new.jar", "sodium", "0.8.12");
        File.WriteAllBytes(Path.Combine(game, "mods", "mystery.jar"), new byte[] { 1, 2, 3 });

        var removed = Manager().RemoveOtherVersions(game, "sodium-new.jar");

        Assert.Empty(removed);
        Assert.True(File.Exists(Path.Combine(game, "mods", "sodium-old.jar.disabled")));
        Assert.True(File.Exists(Path.Combine(game, "mods", "mystery.jar")));
    }
}
