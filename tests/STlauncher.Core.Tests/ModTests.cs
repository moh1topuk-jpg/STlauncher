using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Http;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

public class ModManagerTests
{
    private static string TempGameDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ModManager.ModsDirectory(dir));
        return dir;
    }

    private static ModManager CreateManager() => new(new DownloadClient(new System.Net.Http.HttpClient()));

    [Fact]
    public void ListMods_DetectsEnabledAndDisabledJarsOnly()
    {
        var gameDir = TempGameDir();
        var mods = ModManager.ModsDirectory(gameDir);

        File.WriteAllText(Path.Combine(mods, "sodium.jar"), "x");
        File.WriteAllText(Path.Combine(mods, "lithium.jar.disabled"), "x");
        File.WriteAllText(Path.Combine(mods, "readme.txt"), "x");
        File.WriteAllText(Path.Combine(mods, ".hidden.jar"), "x");

        var result = CreateManager().ListMods(gameDir);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, m => m.FileName == "sodium.jar" && m.Enabled);
        Assert.Contains(result, m => m.FileName == "lithium.jar.disabled" && !m.Enabled);
    }

    [Fact]
    public void SetEnabled_TogglesExtension()
    {
        var gameDir = TempGameDir();
        var mods = ModManager.ModsDirectory(gameDir);
        var path = Path.Combine(mods, "mod.jar");
        File.WriteAllText(path, "x");

        var manager = CreateManager();

        Assert.True(manager.SetEnabled(path, false));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".disabled"));

        Assert.True(manager.SetEnabled(path + ".disabled", true));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".disabled"));
    }

    [Fact]
    public void Uninstall_RemovesFile()
    {
        var gameDir = TempGameDir();
        var mods = ModManager.ModsDirectory(gameDir);
        var path = Path.Combine(mods, "mod.jar");
        File.WriteAllText(path, "x");

        CreateManager().Uninstall(path);

        Assert.False(File.Exists(path));
        Assert.Empty(CreateManager().ListMods(gameDir));
    }

    [Theory]
    [InlineData("mod.jar", true)]
    [InlineData("mod.JAR", true)]
    [InlineData("mod.jar.disabled", true)]
    [InlineData("mod.txt", false)]
    [InlineData(".hidden.jar", false)]
    public void IsModFile_ClassifiesFiles(string fileName, bool expected)
        => Assert.Equal(expected, ModManager.IsModFile(fileName));
}

public class ModrinthClientTests
{
    [Fact]
    public void BuildFacets_IncludesProjectTypeLoaderAndVersion()
    {
        var facets = ModrinthClient.BuildFacets("1.20.1", LoaderKind.Fabric);

        Assert.Equal("[[\"project_type:mod\"],[\"categories:fabric\"],[\"versions:1.20.1\"]]", facets);
    }

    [Fact]
    public void BuildFacets_OmitsLoaderForVanilla()
    {
        var facets = ModrinthClient.BuildFacets("1.20.1", LoaderKind.Vanilla);

        Assert.Equal("[[\"project_type:mod\"],[\"versions:1.20.1\"]]", facets);
    }

    [Theory]
    [InlineData(LoaderKind.Fabric, "fabric")]
    [InlineData(LoaderKind.Forge, "forge")]
    [InlineData(LoaderKind.NeoForge, "neoforge")]
    [InlineData(LoaderKind.Quilt, "quilt")]
    [InlineData(LoaderKind.Vanilla, null)]
    public void ToModrinthLoader_MapsKinds(LoaderKind kind, string? expected)
        => Assert.Equal(expected, ModrinthClient.ToModrinthLoader(kind));
}