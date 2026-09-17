using System;
using System.IO;
using STlauncher.Core;
using Xunit;

namespace STlauncher.Core.Tests;

public class LauncherPathsTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "stlauncher-root");

    [Fact]
    public void AllPaths_AreAbsoluteAndUnderRoot()
    {
        var paths = new LauncherPaths(Root);

        foreach (var path in new[]
                 {
                     paths.Meta, paths.Versions, paths.Libraries, paths.Assets,
                     paths.AssetIndexes, paths.AssetObjects, paths.Runtime,
                     paths.Instances, paths.Logs
                 })
        {
            Assert.True(Path.IsPathRooted(path), $"'{path}' must be absolute.");
            Assert.StartsWith(Root, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MultiSegmentPaths_KeepTheRootPrefix()
    {
        var paths = new LauncherPaths(Root);

        Assert.Equal(Path.Combine(Root, "assets", "objects"), paths.AssetObjects);
        Assert.Equal(Path.Combine(Root, "assets", "indexes"), paths.AssetIndexes);
    }

    [Fact]
    public void VersionAndInstancePaths_AreUnderRoot()
    {
        var paths = new LauncherPaths(Root);

        Assert.Equal(Path.Combine(Root, "versions", "1.20.1", "1.20.1.json"), paths.VersionJsonPath("1.20.1"));
        Assert.Equal(Path.Combine(Root, "instances", "default"), paths.InstanceDirectory("default"));
    }

    [Fact]
    public void LibraryPath_UsesPlatformSeparators()
    {
        var paths = new LauncherPaths(Root);

        Assert.Equal(
            Path.Combine(Root, "libraries", "org", "lwjgl", "lwjgl", "3.3.1", "lwjgl-3.3.1.jar"),
            paths.LibraryPath("org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar"));
    }

    [Fact]
    public void RelativeRoot_IsRejected()
        => Assert.Throws<ArgumentException>(() => new LauncherPaths("relative/path"));
}