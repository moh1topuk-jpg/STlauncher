using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using STlauncher.Core;
using STlauncher.Core.Import;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

public class InstanceImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    private readonly LauncherPaths _paths;
    private readonly InstanceManager _instances;
    private readonly InstanceImporter _importer;

    public InstanceImporterTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "launcher"));
        _paths.EnsureCreated();
        _instances = new InstanceManager(_paths);
        _importer = new InstanceImporter(_paths, _instances);
    }

    /// <summary>A .minecraft folder with one profile, a world and a mod.</summary>
    private ExternalInstance ExternalBuild(string id = "fabric-1.21.1")
    {
        var mc = Path.Combine(_root, "other-launcher", ".minecraft");
        var versionDirectory = Path.Combine(mc, "versions", id);
        Directory.CreateDirectory(versionDirectory);
        Directory.CreateDirectory(Path.Combine(mc, "mods"));
        Directory.CreateDirectory(Path.Combine(mc, "saves", "My world"));
        Directory.CreateDirectory(Path.Combine(mc, "assets", "objects"));

        File.WriteAllText(Path.Combine(versionDirectory, id + ".json"),
            $$"""{ "id": "{{id}}", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient" }""");
        File.WriteAllText(Path.Combine(versionDirectory, id + ".jar"), "jar bytes");
        File.WriteAllText(Path.Combine(mc, "mods", "sodium.jar"), "mod bytes");
        File.WriteAllText(Path.Combine(mc, "saves", "My world", "level.dat"), "world bytes");
        File.WriteAllText(Path.Combine(mc, "options.txt"), "lang:ru_ru");
        File.WriteAllText(Path.Combine(mc, "assets", "objects", "huge"), new string('x', 5000));

        return new ExternalInstance(
            id, mc, id, LoaderKind.Fabric, ExternalLauncherKind.DotMinecraft,
            Path.Combine(versionDirectory, id + ".json"), ModCount: 1);
    }

    [Fact]
    public async Task Link_LeavesTheFilesWhereTheyAre()
    {
        var external = ExternalBuild();

        var result = await _importer.ImportAsync(external, ImportMode.Link);

        Assert.Equal(ImportMode.Link, result.Mode);
        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(external.GameDirectory, result.Instance.ExternalGameDirectory);
        Assert.Equal(external.GameDirectory, _instances.GameDirectory(result.Instance));

        // Nothing was duplicated into the launcher's own folder.
        Assert.False(Directory.Exists(Path.Combine(_paths.InstanceDirectory(result.Instance.Id), "saves")));
    }

    [Fact]
    public async Task Copy_BringsWorldsAndModsAcross()
    {
        var external = ExternalBuild();

        var result = await _importer.ImportAsync(external, ImportMode.Copy);

        var directory = _instances.GameDirectory(result.Instance);

        Assert.Null(result.Instance.ExternalGameDirectory);
        Assert.Equal(_paths.InstanceDirectory(result.Instance.Id), directory);
        Assert.True(File.Exists(Path.Combine(directory, "mods", "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(directory, "saves", "My world", "level.dat")));
        Assert.True(File.Exists(Path.Combine(directory, "options.txt")));
        Assert.True(result.CopiedFiles >= 3);
    }

    [Fact]
    public async Task Copy_SkipsWhatTheLauncherCanFetchAgain()
    {
        // assets, libraries and versions are shared caches worth gigabytes: copying them
        // would turn a small import into a very large one for no benefit.
        var result = await _importer.ImportAsync(ExternalBuild(), ImportMode.Copy);

        var directory = _instances.GameDirectory(result.Instance);

        Assert.False(Directory.Exists(Path.Combine(directory, "assets")));
    }

    [Fact]
    public async Task Import_CopiesTheProfileSoTheVersionCanBeResolved()
    {
        // The profile is what describes how the game starts, and the launcher only looks
        // for it in its own versions folder - so both modes need it.
        var external = ExternalBuild();

        await _importer.ImportAsync(external, ImportMode.Link);

        Assert.True(File.Exists(_paths.VersionJsonPath(external.VersionId)));

        // A hand-made profile usually carries no download link for the client jar, so the
        // local jar has to come along or there is nothing to launch.
        Assert.True(File.Exists(_paths.VersionJarPath(external.VersionId)));
    }

    [Fact]
    public async Task Import_KeepsTheVersionAndLoader()
    {
        var result = await _importer.ImportAsync(ExternalBuild(), ImportMode.Link);

        Assert.Equal("fabric-1.21.1", result.Instance.VersionId);
        Assert.Equal(LoaderKind.Fabric, result.Instance.Loader);
    }

    [Fact]
    public async Task Import_AcceptsAName()
    {
        var result = await _importer.ImportAsync(ExternalBuild(), ImportMode.Link, "Мой старый мир");

        Assert.Equal("Мой старый мир", result.Instance.Name);
    }

    [Fact]
    public async Task Import_RefusesABuildItCannotUse()
    {
        var broken = ExternalBuild() with { Problem = ExternalInstanceProblem.MissingVersionJson };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _importer.ImportAsync(broken, ImportMode.Link));
    }

    [Fact]
    public async Task Delete_NeverTouchesALinkedFolder()
    {
        // The whole point of linking: removing the build here must not remove somebody
        // else's worlds.
        var external = ExternalBuild();
        var result = await _importer.ImportAsync(external, ImportMode.Link);

        _instances.Delete(result.Instance.Id);

        Assert.True(File.Exists(Path.Combine(external.GameDirectory, "saves", "My world", "level.dat")));
    }

    [Fact]
    public async Task Duplicate_DoesNotShareTheExternalFolder()
    {
        var result = await _importer.ImportAsync(ExternalBuild(), ImportMode.Link);

        var copy = _instances.Duplicate(result.Instance.Id);

        // Two builds writing into one external folder would fight over the same mods.
        Assert.Null(copy.ExternalGameDirectory);
    }

    [Fact]
    public void EstimateCopySize_IgnoresTheCaches()
    {
        var external = ExternalBuild();

        var size = InstanceImporter.EstimateCopySize(external);

        // The 5000-byte file lives in assets/, which is not copied.
        Assert.InRange(size, 1, 4999);
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
