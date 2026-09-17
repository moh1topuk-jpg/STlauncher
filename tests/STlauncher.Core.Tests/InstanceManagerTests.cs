using System;
using System.IO;
using System.Linq;
using STlauncher.Core;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

public class InstanceManagerTests
{
    private static (InstanceManager Manager, LauncherPaths Paths) Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        var paths = new LauncherPaths(root);
        paths.EnsureCreated();
        return (new InstanceManager(paths), paths);
    }

    [Fact]
    public void Create_MakesDirectoryAndDefinition()
    {
        var (manager, paths) = Create();

        var instance = manager.Create("My Pack");

        Assert.Equal("my-pack", instance.Id);
        Assert.Equal("My Pack", instance.Name);
        Assert.True(Directory.Exists(Path.Combine(paths.Instances, "my-pack")));
        Assert.True(File.Exists(manager.DefinitionPath("my-pack")));
        Assert.True(manager.Exists("my-pack"));
    }

    [Fact]
    public void Create_GivesUniqueIdsForDuplicateNames()
    {
        var (manager, _) = Create();

        var first = manager.Create("Server");
        var second = manager.Create("Server");
        var third = manager.Create("Server");

        Assert.Equal("server", first.Id);
        Assert.Equal("server-2", second.Id);
        Assert.Equal("server-3", third.Id);
        Assert.Equal(3, manager.List().Count);
    }

    [Fact]
    public void SaveAndLoad_PreservesAllFields()
    {
        var (manager, _) = Create();

        var instance = manager.Create("Fabric Pack");
        instance.VersionId = "1.20.1";
        instance.Loader = LoaderKind.Fabric;
        instance.LoaderVersion = "0.15.0";
        instance.MaxMemoryMb = 4096;
        instance.MinMemoryMb = 1024;
        instance.Width = 1920;
        instance.Height = 1080;
        instance.JavaPath = @"C:\Java\bin\java.exe";
        instance.ServerName = "Showtime";
        instance.ServerAddress = "mc.showtime.su";
        manager.Save(instance);

        var loaded = manager.Get("fabric-pack");

        Assert.NotNull(loaded);
        Assert.Equal("1.20.1", loaded!.VersionId);
        Assert.Equal(LoaderKind.Fabric, loaded.Loader);
        Assert.Equal("0.15.0", loaded.LoaderVersion);
        Assert.Equal(4096, loaded.MaxMemoryMb);
        Assert.Equal(1024, loaded.MinMemoryMb);
        Assert.Equal(1920, loaded.Width);
        Assert.Equal(1080, loaded.Height);
        Assert.Equal(@"C:\Java\bin\java.exe", loaded.JavaPath);
        Assert.Equal("mc.showtime.su", loaded.ServerAddress);
    }

    [Fact]
    public void Delete_RemovesTheInstanceDirectory()
    {
        var (manager, paths) = Create();
        manager.Create("Temp");

        manager.Delete("temp");

        Assert.False(manager.Exists("temp"));
        Assert.False(Directory.Exists(Path.Combine(paths.Instances, "temp")));
        Assert.Empty(manager.List());
    }

    [Fact]
    public void Delete_RefusesToEscapeTheInstancesDirectory()
    {
        var (manager, _) = Create();

        Assert.Throws<InvalidOperationException>(() => manager.Delete("../escape"));
    }

    [Fact]
    public void List_SkipsBrokenDefinitions()
    {
        var (manager, paths) = Create();
        manager.Create("Good");

        var brokenDir = Path.Combine(paths.Instances, "broken");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, InstanceManager.DefinitionFileName), "{ not json");

        var list = manager.List();

        Assert.Single(list);
        Assert.Equal("good", list[0].Id);
    }

    [Theory]
    [InlineData("My Pack", "my-pack")]
    [InlineData("  Server  ", "server")]
    [InlineData("Ванилла 1.20.1", "ванилла-1-20-1")]
    [InlineData("a---b", "a-b")]
    [InlineData("!!!", "instance")]
    [InlineData("", "instance")]
    public void Slugify_ProducesFilesystemSafeIds(string input, string expected)
        => Assert.Equal(expected, InstanceManager.Slugify(input));

    [Fact]
    public void List_IsOrderedByCreation()
    {
        var (manager, _) = Create();
        manager.Create("First");
        manager.Create("Second");

        var names = manager.List().Select(i => i.Name).ToArray();

        Assert.Equal(new[] { "First", "Second" }, names);
    }

    [Fact]
    public void Duplicate_CopiesTheDefinitionOnly()
    {
        var (manager, paths) = Create();

        var source = manager.Create("Server");
        source.VersionId = "1.20.1";
        source.Loader = LoaderKind.Fabric;
        source.LoaderVersion = "0.15.0";
        source.MaxMemoryMb = 4096;
        source.Width = 1920;
        source.Height = 1080;
        source.ServerAddress = "mc.showtime.su";
        source.ExtraGameArgs = "--fullscreen";
        source.EnabledCatalogItems = new List<string> { "sodium", "lithium" };
        manager.Save(source);

        var copy = manager.Duplicate("server", "Server 2");

        Assert.Equal("server-2", copy.Id);
        Assert.Equal("Server 2", copy.Name);
        Assert.Equal("1.20.1", copy.VersionId);
        Assert.Equal(LoaderKind.Fabric, copy.Loader);
        Assert.Equal("0.15.0", copy.LoaderVersion);
        Assert.Equal(4096, copy.MaxMemoryMb);
        Assert.Equal(1920, copy.Width);
        Assert.Equal("--fullscreen", copy.ExtraGameArgs);
        Assert.Equal(new[] { "sodium", "lithium" }, copy.EnabledCatalogItems);

        // The original is untouched and files are not copied.
        Assert.Equal("server", manager.Get("server")!.Id);
        Assert.True(Directory.Exists(Path.Combine(paths.Instances, "server-2")));
    }

    [Fact]
    public void Duplicate_GeneratesANameWhenNoneIsGiven()
    {
        var (manager, _) = Create();
        manager.Create("Lite");

        var copy = manager.Duplicate("lite");

        Assert.Equal("Lite copy", copy.Name);
        Assert.Equal("lite-copy", copy.Id);
    }

    [Fact]
    public void Duplicate_ThrowsForUnknownSource()
    {
        var (manager, _) = Create();

        Assert.Throws<InvalidOperationException>(() => manager.Duplicate("missing"));
    }
}