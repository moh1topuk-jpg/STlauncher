using System;
using System.IO;
using STlauncher.Core;
using Xunit;

namespace STlauncher.Core.Tests;

public class DataLocationTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Resolve_WithoutPointer_IsTheDefault()
    {
        var root = TempRoot();

        Assert.Equal(Path.Combine(root, "default"), DataLocation.Resolve(Path.Combine(root, "data-root.txt"), Path.Combine(root, "default")));
    }

    [Fact]
    public void Resolve_FollowsThePointer_WhenTheFolderExists()
    {
        var root = TempRoot();
        var custom = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(custom);
        var pointer = Path.Combine(root, "data-root.txt");

        DataLocation.Write(pointer, custom);

        Assert.Equal(custom, DataLocation.Resolve(pointer, Path.Combine(root, "default")));
    }

    [Fact]
    public void Resolve_FallsBack_WhenThePointedFolderIsGone()
    {
        var root = TempRoot();
        var pointer = Path.Combine(root, "data-root.txt");
        File.WriteAllText(pointer, Path.Combine(root, "unplugged-drive"));

        Assert.Equal(Path.Combine(root, "default"), DataLocation.Resolve(pointer, Path.Combine(root, "default")));
    }

    [Fact]
    public void Write_Null_RemovesThePointer()
    {
        var root = TempRoot();
        var pointer = Path.Combine(root, "data-root.txt");
        DataLocation.Write(pointer, root);
        Assert.True(File.Exists(pointer));

        DataLocation.Write(pointer, null);

        Assert.False(File.Exists(pointer));
    }

    [Fact]
    public void Move_CopiesEverything_ThenClearsTheSource()
    {
        var root = TempRoot();
        var from = Path.Combine(root, "from");
        var to = Path.Combine(root, "to");

        Directory.CreateDirectory(Path.Combine(from, "instances", "showtime", "saves", "World"));
        Directory.CreateDirectory(Path.Combine(from, "logs"));
        File.WriteAllText(Path.Combine(from, "instances", "showtime", "instance.json"), "{}");
        File.WriteAllText(Path.Combine(from, "instances", "showtime", "saves", "World", "level.dat"), "world");
        File.WriteAllText(Path.Combine(from, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(from, "logs", "game.log"), "noise");
        File.WriteAllText(Path.Combine(from, DataLocation.PointerFileName), "pointer");

        var result = DataDirectoryMover.Move(from, to);

        Assert.Equal(3, result.Files);
        Assert.True(File.Exists(Path.Combine(to, "instances", "showtime", "saves", "World", "level.dat")));
        Assert.True(File.Exists(Path.Combine(to, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(to, "logs")));
        Assert.False(File.Exists(Path.Combine(to, DataLocation.PointerFileName)));

        Assert.False(Directory.Exists(Path.Combine(from, "instances")));
        Assert.False(File.Exists(Path.Combine(from, "settings.json")));
        Assert.True(File.Exists(Path.Combine(from, DataLocation.PointerFileName)));
        Assert.Empty(result.NotRemoved);
    }

    [Fact]
    public void Move_RefusesANonEmptyTarget()
    {
        var root = TempRoot();
        var from = Path.Combine(root, "from");
        var to = Path.Combine(root, "to");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "somebody-elses.txt"), "x");

        Assert.Throws<InvalidOperationException>(() => DataDirectoryMover.Move(from, to));
    }

    [Fact]
    public void Move_RefusesATargetInsideTheSource()
    {
        var root = TempRoot();
        var from = Path.Combine(root, "from");
        Directory.CreateDirectory(from);

        Assert.Throws<InvalidOperationException>(() => DataDirectoryMover.Move(from, Path.Combine(from, "inner")));
        Assert.Throws<InvalidOperationException>(() => DataDirectoryMover.Move(from, from + Path.DirectorySeparatorChar));
    }
}
