using System;
using System.IO;
using System.IO.Compression;
using STlauncher.Core.Backups;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

public class InstanceBackupRestoreTests
{
    private static (string Instance, string Backups) CreateInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(root, "instance");
        var backups = Path.Combine(root, "backups");

        Directory.CreateDirectory(Path.Combine(instance, "saves", "World", "region"));
        Directory.CreateDirectory(Path.Combine(instance, "saves", "Creative"));
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        Directory.CreateDirectory(Path.Combine(instance, "config"));
        File.WriteAllText(Path.Combine(instance, "saves", "World", "level.dat"), "world");
        File.WriteAllText(Path.Combine(instance, "saves", "World", "region", "r.0.0.mca"), "region");
        File.WriteAllText(Path.Combine(instance, "saves", "Creative", "level.dat"), "creative");
        File.WriteAllText(Path.Combine(instance, "mods", "sodium.jar"), "mod");
        File.WriteAllText(Path.Combine(instance, "mods", "lithium.jar"), "mod2");
        File.WriteAllText(Path.Combine(instance, "config", "sodium.json"), "{}");
        File.WriteAllText(Path.Combine(instance, "options.txt"), "fov:70");

        return (instance, backups);
    }

    private static Instance Definition() => new()
    {
        Id = "showtime",
        Name = "Showtime",
        VersionId = "1.21.11",
        Loader = LoaderKind.Fabric,
        LoaderVersion = "0.16.9",
        MaxMemoryMb = 4096
    };

    [Fact]
    public void Create_StoresTheDefinition_AndInspectReadsItBack()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();

        var backup = service.Create(instance, backups, "showtime", Definition());
        var contents = service.Inspect(backup.Path);

        Assert.Equal("showtime", backup.InstanceId);
        Assert.NotNull(contents.Definition);
        Assert.Equal("Showtime", contents.Definition!.Name);
        Assert.Equal("1.21.11", contents.Definition.VersionId);
        Assert.Equal(LoaderKind.Fabric, contents.Definition.Loader);
        Assert.Equal(2, contents.Worlds);
        Assert.Equal(2, contents.Mods);
        Assert.True(contents.HasConfig);
        Assert.True(contents.HasOptions);
    }

    [Fact]
    public void List_ReadsTheInstanceIdFromTheFileName()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();

        service.Create(instance, backups, "my-build-2");

        var listed = service.List(backups);

        Assert.Single(listed);
        Assert.Equal("my-build-2", listed[0].InstanceId);
    }

    [Fact]
    public void Restore_WorldsOnly_ReplacesSavesAndLeavesModsAlone()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();
        var backup = service.Create(instance, backups, "showtime");

        // Life goes on: a world is lost, a mod is added.
        Directory.Delete(Path.Combine(instance, "saves", "World"), recursive: true);
        File.WriteAllText(Path.Combine(instance, "saves", "Creative", "level.dat"), "changed");
        File.WriteAllText(Path.Combine(instance, "mods", "iris.jar"), "new mod");

        service.Restore(backup.Path, instance, RestoreScope.Worlds);

        Assert.Equal("world", File.ReadAllText(Path.Combine(instance, "saves", "World", "level.dat")));
        Assert.Equal("region", File.ReadAllText(Path.Combine(instance, "saves", "World", "region", "r.0.0.mca")));
        Assert.Equal("creative", File.ReadAllText(Path.Combine(instance, "saves", "Creative", "level.dat")));
        Assert.True(File.Exists(Path.Combine(instance, "mods", "iris.jar")));
    }

    [Fact]
    public void Restore_Everything_ReplacesModsToo()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();
        var backup = service.Create(instance, backups, "showtime");

        File.WriteAllText(Path.Combine(instance, "mods", "iris.jar"), "new mod");
        File.Delete(Path.Combine(instance, "mods", "sodium.jar"));
        File.WriteAllText(Path.Combine(instance, "options.txt"), "fov:110");

        service.Restore(backup.Path, instance, RestoreScope.Everything);

        Assert.False(File.Exists(Path.Combine(instance, "mods", "iris.jar")));
        Assert.True(File.Exists(Path.Combine(instance, "mods", "sodium.jar")));
        Assert.Equal("fov:70", File.ReadAllText(Path.Combine(instance, "options.txt")));
    }

    [Fact]
    public void Restore_IntoEmptyFolder_RecreatesTheBuild()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();
        var backup = service.Create(instance, backups, "showtime", Definition());

        var fresh = Path.Combine(Path.GetDirectoryName(instance)!, "fresh");

        service.Restore(backup.Path, fresh, RestoreScope.Everything);

        Assert.True(File.Exists(Path.Combine(fresh, "saves", "World", "level.dat")));
        Assert.True(File.Exists(Path.Combine(fresh, "mods", "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(fresh, "config", "sodium.json")));

        // The definition is data about the build, not part of the game folder.
        Assert.False(File.Exists(Path.Combine(fresh, InstanceManager.DefinitionFileName)));
        Assert.Equal("Showtime", service.ReadDefinition(backup.Path)!.Name);
    }

    [Fact]
    public void Restore_RefusesEntriesOutsideTheGameFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evil = Path.Combine(root, "backup-x-20260922-101112.zip");

        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("saves/../../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("nope");
        }

        var service = new InstanceBackupService();
        var game = Path.Combine(root, "game");

        Assert.Throws<InvalidDataException>(() => service.Restore(evil, game, RestoreScope.Worlds));
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var (instance, backups) = CreateInstance();
        var service = new InstanceBackupService();
        var backup = service.Create(instance, backups, "showtime");

        Assert.True(service.Delete(backup.Path));
        Assert.Empty(service.List(backups));
    }
}
