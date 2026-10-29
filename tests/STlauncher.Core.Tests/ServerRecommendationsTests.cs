using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;
using Old = STlauncher.Core.Content.Released062;

namespace STlauncher.Core.Tests;

/// <summary>
/// Resource packs and shaders the server recommends: the catalog fields, what a released
/// launcher makes of them, and where each pack stands on the panel.
/// </summary>
public class ServerRecommendationsTests
{
    /// <summary>The shape proposed for catalog.json: a section of packs, and the build naming them under "recommends".</summary>
    private const string CatalogWithRecommendations = """
    {
      "schemaVersion": 1,
      "name": "Showtime Server",
      "sections": [
        {
          "id": "server-mods",
          "title": "Моды сервера",
          "items": [
            { "id": "fabric-api", "type": "mod", "name": "Fabric API", "required": true, "source": { "kind": "modrinth", "project": "fabric-api", "version": "0.141.6+1.21.11" } },
            { "id": "iris", "type": "mod", "name": "Iris Shaders", "required": true, "source": { "kind": "modrinth", "project": "iris", "version": "1.10.7+1.21.11-fabric" } }
          ]
        },
        {
          "id": "server-looks",
          "title": "Рекомендует сервер",
          "description": "Ресурспаки и шейдеры по желанию",
          "items": [
            { "id": "low-on-fire", "type": "resourcepack", "name": "Low On Fire", "description": "Огонь не закрывает пол-экрана.", "required": false, "source": { "kind": "modrinth", "project": "low-on-fire", "version": "vWFpZjNP" } },
            { "id": "complementary-reimagined", "type": "shader", "name": "Complementary Reimagined", "description": "Красиво и не тяжело.", "required": false, "source": { "kind": "modrinth", "project": "complementary-reimagined", "version": "Bqen1mJX" } },
            { "id": "future-thing", "type": "datapack", "name": "Something new", "optional": true, "source": { "kind": "torrent", "magnet": "x" } }
          ]
        }
      ],
      "builds": [
        {
          "id": "showtime-fabric-1.21.11",
          "name": "Showtime — Fabric 1.21.11",
          "recommended": true,
          "gameVersion": "1.21.11",
          "loader": "fabric",
          "items": [ "fabric-api", "iris" ],
          "recommends": [ "low-on-fire", "complementary-reimagined", "future-thing", "fabric-api" ],
          "someFieldFromTheFuture": { "a": [1, 2] }
        }
      ]
    }
    """;

    // ===================== The released launcher =====================

    [Fact]
    public void ReleasedParser_ReadsTheNewCatalog_AndItsBuildIsOnlyTheMods()
    {
        var catalog = Old.ContentCatalogService.Parse(CatalogWithRecommendations);
        var build = Assert.Single(catalog.Builds);

        // The build a 0.6.2 launcher syncs is exactly the mods: "recommends" is unknown to
        // it and ignored, so nothing in it is ever installed or removed by that launcher.
        Assert.Equal(new[] { "fabric-api", "iris" }, build.Items);

        var instance = new Instance { Name = "Showtime" };
        Old.CatalogBuildSync.Apply(instance, build, isNew: true);
        Assert.Equal(new[] { "fabric-api", "iris" }, instance.EnabledCatalogItems);

        // The new kinds parse to harmless values: a pack type the old launcher knows, a
        // shader it reads as Other - which it cannot even place - and a type it never heard of.
        Assert.Equal(Old.CatalogItemType.ResourcePack, catalog.FindItem("low-on-fire")!.Type);
        Assert.Equal(Old.CatalogItemType.Other, catalog.FindItem("complementary-reimagined")!.Type);
        Assert.Equal(Old.CatalogItemType.Other, catalog.FindItem("future-thing")!.Type);
        Assert.Equal(Old.CatalogSourceKind.Unknown, catalog.FindItem("future-thing")!.Source.Kind);
        Assert.Null(Old.CatalogPlacement.ResolveRelativePath(catalog.FindItem("complementary-reimagined")!, "x.zip"));
    }

    [Fact]
    public void ReleasedParser_FindsTheSameBuildForAnInstanceAsBefore()
    {
        var catalog = Old.ContentCatalogService.Parse(CatalogWithRecommendations);
        var instance = new Instance { Name = "Showtime — Fabric 1.21.11", CatalogBuildId = "showtime-fabric-1.21.11", EnabledCatalogItems = { "fabric-api", "iris" } };

        Assert.Same(instance, Old.CatalogBuildSync.FindLinked(new[] { instance }, catalog.Builds[0]));
        Assert.False(Old.CatalogBuildSync.Apply(instance, catalog.Builds[0]).ItemsChanged);
    }

    /// <summary>
    /// The copy above is the released code: compared with what tag v0.6.2 holds, when git
    /// and the tag are at hand (a shallow CI checkout has no tags, and then this says so
    /// and passes).
    /// </summary>
    [Fact]
    public void ReleasedParserCopy_IsTheTaggedCode()
    {
        var root = FindRepoRoot();
        var folder = Path.Combine(root, "tests", "STlauncher.Core.Tests", "Released062");
        var compared = 0;

        foreach (var file in Directory.GetFiles(folder, "*.v062.cs"))
        {
            var name = Path.GetFileName(file).Replace(".v062.cs", ".cs");
            var tagged = GitShow(root, $"v0.6.2:src/STlauncher.Core/Content/{name}");

            if (tagged is null)
            {
                return;
            }

            var copy = File.ReadAllText(file);
            copy = copy[(copy.IndexOf('\n') + 1)..]
                .Replace("namespace STlauncher.Core.Content.Released062;", "namespace STlauncher.Core.Content;");

            Assert.Equal(Normalize(tagged), Normalize(copy));
            compared++;
        }

        Assert.Equal(5, compared);
    }

    // ===================== This launcher =====================

    [Fact]
    public void Parser_ReadsShaderAndRecommends()
    {
        var catalog = ContentCatalogService.Parse(CatalogWithRecommendations);
        var build = catalog.Builds[0];

        Assert.Equal(CatalogItemType.ShaderPack, catalog.FindItem("complementary-reimagined")!.Type);
        Assert.Equal(CatalogItemType.ResourcePack, catalog.FindItem("low-on-fire")!.Type);
        Assert.Equal(CatalogItemType.Other, catalog.FindItem("future-thing")!.Type);
        Assert.Equal(4, build.Recommends.Count);
        Assert.Equal("shaderpacks/x.zip", CatalogPlacement.ResolveRelativePath(catalog.FindItem("complementary-reimagined")!, "x.zip"));

        // The sync still sees the mods only.
        var instance = new Instance();
        CatalogBuildSync.Apply(instance, build, isNew: true);
        Assert.Equal(new[] { "fabric-api", "iris" }, instance.EnabledCatalogItems);
    }

    [Fact]
    public void For_TakesOnlyPacksOfTheKind_NotModsNotUnknownNotTheBuildsOwn()
    {
        var catalog = ContentCatalogService.Parse(CatalogWithRecommendations);
        var build = catalog.Builds[0];

        Assert.Equal(new[] { "low-on-fire" }, CatalogRecommendations.For(catalog, build, CatalogItemType.ResourcePack).Select(i => i.Id));
        Assert.Equal(new[] { "complementary-reimagined" }, CatalogRecommendations.For(catalog, build, CatalogItemType.ShaderPack).Select(i => i.Id));
        Assert.Empty(CatalogRecommendations.For(catalog, build, CatalogItemType.Mod));
        Assert.Empty(CatalogRecommendations.For(catalog, null, CatalogItemType.ResourcePack));
    }

    [Fact]
    public void Evaluate_MissingWhenNothingOnDisk()
    {
        var item = Pack("low-on-fire", "1.5");

        var status = CatalogRecommendations.Evaluate(item, Array.Empty<InstalledModRecord>(), Array.Empty<string>(), null, Array.Empty<string>());

        Assert.Equal(RecommendationState.Missing, status.State);
        Assert.Null(status.FileName);
    }

    [Fact]
    public void Evaluate_PinnedFileOnDisk_IsInstalled_EnabledFollowsTheGameList()
    {
        var item = Pack("low-on-fire", "vWFpZjNP");
        var pinned = new PinnedPack("1.5", "LowOnFire.zip", 42728);

        var off = CatalogRecommendations.Evaluate(item, Array.Empty<InstalledModRecord>(), new[] { "LowOnFire.zip", "Other.zip" }, pinned, new[] { "Other.zip" });
        var on = CatalogRecommendations.Evaluate(item, Array.Empty<InstalledModRecord>(), new[] { "LowOnFire.zip" }, pinned, new[] { "lowonfire.zip" });

        Assert.Equal(RecommendationState.Installed, off.State);
        Assert.False(off.Enabled);
        Assert.True(on.Enabled);
    }

    [Fact]
    public void Evaluate_RecordOfAnotherVersion_IsOtherVersion_OnlyWhenItCanTell()
    {
        var item = Pack("low-on-fire", "vWFpZjNP");
        var record = new InstalledModRecord { FileName = "LowOnFire-old.zip", Source = ModSource.Modrinth, Id = "low-on-fire", Version = "1.4", Folder = "resourcepacks" };

        // The pin is an id: until Modrinth says which number it is, the pack is not called old.
        var unknown = CatalogRecommendations.Evaluate(item, new[] { record }, new[] { "LowOnFire-old.zip" }, null, Array.Empty<string>());
        Assert.Equal(RecommendationState.Installed, unknown.State);

        var known = CatalogRecommendations.Evaluate(item, new[] { record }, new[] { "LowOnFire-old.zip" }, new PinnedPack("1.5", "LowOnFire.zip", 1), Array.Empty<string>());
        Assert.Equal(RecommendationState.OtherVersion, known.State);
        Assert.Equal("1.4", known.InstalledVersion);
        Assert.Equal("LowOnFire-old.zip", known.FileName);
    }

    [Fact]
    public void Evaluate_IgnoresRecordsWhoseFileIsGone_AndRecordsInAnotherFolder()
    {
        var item = Shader("complementary-reimagined", "r5.9.3");
        var gone = new InstalledModRecord { FileName = "Compl.zip", Source = ModSource.Modrinth, Id = "complementary-reimagined", Version = "r5.9.3", Folder = "shaderpacks" };
        var elsewhere = new InstalledModRecord { FileName = "Compl2.zip", Source = ModSource.Modrinth, Id = "complementary-reimagined", Version = "r5.9.3", Folder = "resourcepacks" };

        var status = CatalogRecommendations.Evaluate(item, new[] { gone, elsewhere }, new[] { "Compl2.zip" }, null, Array.Empty<string>());

        Assert.Equal(RecommendationState.Missing, status.State);
    }

    [Fact]
    public void Evaluate_ActiveShaderIsEnabled()
    {
        var item = Shader("complementary-reimagined", "r5.9.3");
        var record = new InstalledModRecord { FileName = "Compl.zip", Source = ModSource.Modrinth, Id = "complementary-reimagined", Version = "r5.9.3", Folder = "shaderpacks" };

        var status = CatalogRecommendations.Evaluate(item, new[] { record }, new[] { "Compl.zip" }, null, new[] { "Compl.zip" });

        Assert.Equal(RecommendationState.Installed, status.State);
        Assert.True(status.Enabled);
    }

    [Theory]
    [InlineData("1.5", "1.5", null, true)]
    [InlineData("1.4", "1.5", null, false)]
    [InlineData("1.4", "vWFpZjNP", null, true)]
    [InlineData("1.4", "vWFpZjNP", "1.5", false)]
    [InlineData("1.5", "vWFpZjNP", "1.5", true)]
    [InlineData(null, "1.5", null, true)]
    public void IsPinnedVersion(string? recorded, string pin, string? number, bool expected)
        => Assert.Equal(expected, CatalogRecommendations.IsPinnedVersion(recorded, pin, number));

    [Fact]
    public void PickPinned_ById_ThenByNumberForTheGame()
    {
        var versions = new[]
        {
            new ModVersion("aaaaaaaa", "x", "2.0", new[] { "1.21.11" }, new[] { "minecraft" }, Array.Empty<ModFile>()),
            new ModVersion("bbbbbbbb", "x", "1.0", new[] { "1.20.1" }, new[] { "minecraft" }, Array.Empty<ModFile>()),
            new ModVersion("cccccccc", "x", "1.0", new[] { "1.21.11" }, new[] { "minecraft" }, Array.Empty<ModFile>())
        };

        Assert.Equal("bbbbbbbb", CatalogRecommendations.PickPinned(versions, "bbbbbbbb", "1.21.11")!.Id);
        Assert.Equal("cccccccc", CatalogRecommendations.PickPinned(versions, "1.0", "1.21.11")!.Id);
        Assert.Null(CatalogRecommendations.PickPinned(versions, "9.9", "1.21.11"));
        Assert.Equal("aaaaaaaa", CatalogRecommendations.PickPinned(versions, null, "1.21.11")!.Id);
    }

    private static CatalogItem Pack(string id, string version) => new()
    {
        Id = id,
        Type = CatalogItemType.ResourcePack,
        Name = id,
        Source = new CatalogSource { Kind = CatalogSourceKind.Modrinth, Project = id, Version = version }
    };

    private static CatalogItem Shader(string id, string version) => new()
    {
        Id = id,
        Type = CatalogItemType.ShaderPack,
        Name = id,
        Source = new CatalogSource { Kind = CatalogSourceKind.Modrinth, Project = id, Version = version }
    };

    private static string Normalize(string text) => text.TrimStart('﻿').Replace("\r\n", "\n").TrimEnd();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }

    /// <summary>A file at a revision, or null when git or the revision is not there.</summary>
    private static string? GitShow(string root, string spec)
    {
        try
        {
            var start = new ProcessStartInfo("git", $"show {spec}")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(20000);

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
