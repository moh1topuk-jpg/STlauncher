using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using STlauncher.Core.Packs;
using Xunit;

namespace STlauncher.Core.Tests;

public class PacksTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteZip(string path, string mcmeta, bool withIcon = true, string prefix = "")
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var meta = archive.CreateEntry(prefix + "pack.mcmeta");
        using (var w = new StreamWriter(meta.Open(), Encoding.UTF8)) { w.Write(mcmeta); }

        if (withIcon)
        {
            var png = archive.CreateEntry(prefix + "pack.png");
            using var s = png.Open();
            s.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        }

        var tex = archive.CreateEntry(prefix + "assets/minecraft/textures/block/stone.png");
        using var t = tex.Open();
        t.Write(new byte[64]);
    }

    [Fact]
    public void Inspect_ReadsDescriptionFormatAndIcon()
    {
        var root = TempRoot();
        var zip = Path.Combine(root, "Faithful 32x.zip");
        WriteZip(zip, """{ "pack": { "pack_format": 46, "supported_formats": [34, 46], "description": "§6Faithful§r 32x\nfor 1.21" } }""");

        var info = ResourcePackInspector.Inspect(zip);

        Assert.Equal("Faithful 32x", info.Name);
        Assert.Equal("Faithful 32x.zip", info.FileName);
        Assert.False(info.IsFolder);
        Assert.Equal(46, info.Format);
        Assert.Equal(34, info.MinFormat);
        Assert.Equal(46, info.MaxFormat);
        Assert.Equal("Faithful 32x for 1.21", info.Description);
        Assert.NotNull(info.Icon);
        Assert.True(info.Supports(40));
        Assert.False(info.Supports(50));
    }

    [Fact]
    public void Inspect_HandlesComponentDescriptions_AndNestedFolder()
    {
        var root = TempRoot();
        var zip = Path.Combine(root, "nested.zip");
        WriteZip(zip, """{ "pack": { "pack_format": 15, "description": [{ "text": "Better ", "color": "green" }, { "text": "Leaves" }] } }""", withIcon: false, prefix: "BetterLeaves/");

        var info = ResourcePackInspector.Inspect(zip);

        Assert.Equal("Better Leaves", info.Description);
        Assert.Equal(15, info.Format);
        Assert.Null(info.Icon);
        Assert.False(info.Supports(46));
        Assert.True(info.Supports(15));
    }

    [Fact]
    public void ListPacks_FindsZipsAndFolders()
    {
        var root = TempRoot();
        WriteZip(Path.Combine(root, "b.zip"), """{ "pack": { "pack_format": 46, "description": "b" } }""");
        Directory.CreateDirectory(Path.Combine(root, "A-folder"));
        File.WriteAllText(Path.Combine(root, "A-folder", "pack.mcmeta"), """{ "pack": { "pack_format": 46, "description": "a" } }""");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "not a pack");

        var packs = ResourcePackInspector.ListPacks(root);

        Assert.Equal(2, packs.Count);
        Assert.Equal("A-folder", packs[0].Name);
        Assert.True(packs[0].IsFolder);
        Assert.Equal("b", packs[1].Name);
    }

    [Fact]
    public void ResourcePackOrder_RoundTrips_HighestPriorityFirst_KeepingBuiltIns()
    {
        var root = TempRoot();
        File.WriteAllText(Path.Combine(root, "options.txt"), "lang:ru_ru\nresourcePacks:[\"vanilla\",\"file/old.zip\",\"fabric\"]\nfov:0.5\n");

        Assert.Equal(new[] { "old.zip" }, ResourcePackOrder.ReadEnabled(root));

        ResourcePackOrder.WriteEnabled(root, new[] { "Top.zip", "Bottom.zip" });

        var lines = File.ReadAllLines(Path.Combine(root, "options.txt"));
        Assert.Equal("lang:ru_ru", lines[0]);
        Assert.Equal("resourcePacks:[\"vanilla\",\"fabric\",\"file/Bottom.zip\",\"file/Top.zip\"]", lines[1]);
        Assert.Equal("fov:0.5", lines[2]);

        Assert.Equal(new[] { "Top.zip", "Bottom.zip" }, ResourcePackOrder.ReadEnabled(root));
    }

    [Fact]
    public void ResourcePackOrder_WithoutOptionsFile_StartsFromVanilla()
    {
        var root = TempRoot();

        ResourcePackOrder.WriteEnabled(root, new[] { "One.zip" });

        Assert.Equal("resourcePacks:[\"vanilla\",\"file/One.zip\"]", File.ReadAllText(Path.Combine(root, "options.txt")).Trim());
    }

    [Fact]
    public void ShaderConfig_RoundTrips_AndKeepsOtherKeys()
    {
        var root = TempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "iris.properties"), "#Iris\nmaxShadowRenderDistance=8\nshaderPack=Old.zip\nenableShaders=false\n");

        var before = ShaderConfig.Read(root);
        Assert.Equal("Old.zip", before.ShaderPack);
        Assert.False(before.Enabled);

        ShaderConfig.Write(root, "Complementary_r5.zip");

        var after = ShaderConfig.Read(root);
        Assert.Equal("Complementary_r5.zip", after.ShaderPack);
        Assert.True(after.Enabled);
        Assert.Contains("maxShadowRenderDistance=8", File.ReadAllText(Path.Combine(root, "config", "iris.properties")));

        ShaderConfig.Write(root, null);
        Assert.False(ShaderConfig.Read(root).Enabled);
    }

    [Fact]
    public void ShaderConfig_Oculus_UsesItsOwnFile_AndDetectionReadsJarNames()
    {
        var root = TempRoot();

        ShaderConfig.Write(root, "BSL_v8.zip", ShaderLoader.Oculus);

        Assert.True(File.Exists(Path.Combine(root, "config", "oculus.properties")));
        Assert.False(File.Exists(Path.Combine(root, "config", "iris.properties")));
        Assert.Equal("BSL_v8.zip", ShaderConfig.Read(root, ShaderLoader.Oculus).ShaderPack);
        Assert.Null(ShaderConfig.Read(root, ShaderLoader.Iris).ShaderPack);

        Assert.Equal(new[] { ShaderLoader.Iris }, ShaderConfig.Detect(new[] { "sodium.jar", "iris-fabric-1.8.jar" }));
        Assert.Equal(new[] { ShaderLoader.Oculus }, ShaderConfig.Detect(new[] { "oculus-mc1.20.1-1.7.0.jar", "embeddium.jar" }));
        Assert.Empty(ShaderConfig.Detect(new[] { "sodium.jar" }));
    }

    [Fact]
    public void GamePackFormat_ReadsTheClientJar()
    {
        var root = TempRoot();
        var jar = Path.Combine(root, "1.21.11.jar");

        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("version.json");
            using var w = new StreamWriter(entry.Open());
            w.Write("""{ "id": "1.21.11", "pack_version": { "resource": 46, "data": 61 } }""");
        }

        Assert.Equal(46, GamePackFormat.ReadResourceFormat(jar));
        Assert.Null(GamePackFormat.ReadResourceFormat(Path.Combine(root, "missing.jar")));
    }
}
