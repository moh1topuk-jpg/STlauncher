using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using STlauncher.Core.Backups;
using Xunit;

namespace STlauncher.Core.Tests;

public class InstanceBackupServiceTests
{
    private static (string Instance, string Backups) CreateInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(root, "instance");
        var backups = Path.Combine(root, "backups");

        Directory.CreateDirectory(Path.Combine(instance, "saves", "World"));
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        File.WriteAllText(Path.Combine(instance, "saves", "World", "level.dat"), "world");
        File.WriteAllText(Path.Combine(instance, "mods", "sodium.jar"), "mod");
        File.WriteAllText(Path.Combine(instance, "options.txt"), "fov:70");
        File.WriteAllText(Path.Combine(instance, "junk.tmp"), "ignored");

        return (instance, backups);
    }

    [Fact]
    public void Create_ProducesArchiveWithPlayerData()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();

        var backup = service.Create(instance, backups, "default");

        Assert.True(File.Exists(backup.Path));
        Assert.True(backup.Size > 0);
        Assert.StartsWith(InstanceBackupService.FilePrefix, backup.FileName);

        using var archive = ZipFile.OpenRead(backup.Path);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("saves/World/level.dat", names);
        Assert.Contains("mods/sodium.jar", names);
        Assert.Contains("options.txt", names);
        Assert.DoesNotContain(names, n => n.EndsWith("junk.tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void List_ReturnsOnlyBackupsOfTheRequestedInstance()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();

        service.Create(instance, backups, "alpha");
        service.Create(instance, backups, "beta");

        Assert.Single(service.List(backups, "alpha"));
        Assert.Equal(2, service.List(backups).Count);
    }

    [Fact]
    public void Prune_KeepsOnlyTheNewestByCount()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();

        for (var i = 0; i < 5; i++)
        {
            service.Create(instance, backups, "default");
        }

        Assert.Equal(5, service.List(backups).Count);

        var removed = service.Prune(backups, maxCount: 2, maxTotalBytes: 0);

        Assert.Equal(3, removed);
        Assert.Equal(2, service.List(backups).Count);
    }

    [Fact]
    public void Prune_BySize_AlwaysKeepsTheNewest()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();

        for (var i = 0; i < 3; i++)
        {
            service.Create(instance, backups, "default");
        }

        var all = service.List(backups);
        var size = all[0].Size;

        // Limit allows roughly one backup.
        service.Prune(backups, maxCount: 0, maxTotalBytes: size + size / 2);

        var remaining = service.List(backups);

        Assert.True(remaining.Count >= 1);
        Assert.Equal(all[0].FileName, remaining[0].FileName);
    }

    [Fact]
    public void Create_ThrowsWhenInstanceDirectoryIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        var service = new InstanceBackupService();

        Assert.Throws<DirectoryNotFoundException>(
            () => service.Create(Path.Combine(root, "nope"), Path.Combine(root, "backups"), "x"));
    }
}
