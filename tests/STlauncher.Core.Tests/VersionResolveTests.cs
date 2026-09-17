using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

public class VersionMergeTests
{
    private static VersionJson Parent() => new()
    {
        Id = "1.20.1",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Assets = "5",
        AssetIndex = new AssetIndexRef { Id = "5", Url = "https://example/5.json" },
        Downloads = new VersionDownloads
        {
            Client = new DownloadArtifact { Url = "https://example/client.jar", Sha1 = "aa", Size = 10 }
        },
        JavaVersion = new JavaVersionRef { MajorVersion = 17 },
        Libraries = new List<Library>
        {
            new() { Name = "lib:a:1" },
            new() { Name = "lib:b:1" }
        },
        Arguments = new Arguments
        {
            Game = { new GameArgument { Values = { "--username", "${auth_player_name}" } } },
            Jvm = { new GameArgument { Values = { "-cp", "${classpath}" } } }
        }
    };

    private static VersionJson Child() => new()
    {
        Id = "fabric-loader-0.15-1.20.1",
        InheritsFrom = "1.20.1",
        MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
        Libraries = new List<Library>
        {
            new() { Name = "lib:b:2" },
            new() { Name = "lib:c:1" }
        },
        Arguments = new Arguments
        {
            Game = { new GameArgument { Values = { "--fabric" } } }
        }
    };

    [Fact]
    public void Merge_ChildOverridesMainClass()
    {
        var merged = ResolvedVersion.Merge(ResolvedVersion.FromLeaf(Parent()), Child());

        Assert.Equal("fabric-loader-0.15-1.20.1", merged.Id);
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", merged.MainClass);
    }

    [Fact]
    public void Merge_ChildLibraryOverridesParentByName()
    {
        var merged = ResolvedVersion.Merge(ResolvedVersion.FromLeaf(Parent()), Child());
        var names = merged.Libraries.Select(l => l.Name).ToList();

        Assert.Equal(new[] { "lib:a:1", "lib:b:2", "lib:c:1" }, names);
    }

    [Fact]
    public void Merge_ConcatenatesArgumentsAndKeepsClientVersion()
    {
        var merged = ResolvedVersion.Merge(ResolvedVersion.FromLeaf(Parent()), Child());

        Assert.Equal(2, merged.GameArguments.Count);
        Assert.Equal("1.20.1", merged.ClientVersionId);
        Assert.Equal(17, merged.JavaVersion?.MajorVersion);
    }

    [Fact]
    public void MavenPath_BuildsStandardLayout()
    {
        Assert.Equal(
            "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar",
            STlauncher.Core.Launch.LibraryResolver.MavenPath("org.lwjgl:lwjgl:3.3.1"));

        Assert.Equal(
            "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
            STlauncher.Core.Launch.LibraryResolver.MavenPath("org.lwjgl:lwjgl:3.3.1:natives-windows"));
    }
}

public class ArgumentParsingTests
{
    private const string Json = """
    {
      "id": "test",
      "mainClass": "Main",
      "arguments": {
        "game": [
          "--username",
          "${auth_player_name}",
          { "rules": [{ "action": "allow", "features": { "has_custom_resolution": true } }],
            "value": ["--width", "${resolution_width}"] }
        ],
        "jvm": [ "-Djava.library.path=${natives_directory}" ]
      }
    }
    """;

    [Fact]
    public void ParsesLiteralAndConditionalArguments()
    {
        var version = new MetadataClientStub().Parse(Json);

        Assert.Equal(3, version.Arguments!.Game.Count);
        Assert.False(version.Arguments.Game[0].IsConditional);
        Assert.True(version.Arguments.Game[2].IsConditional);
        Assert.Equal(new[] { "--width", "${resolution_width}" }, version.Arguments.Game[2].Values);
        Assert.Single(version.Arguments.Jvm);
    }

    private sealed class MetadataClientStub
    {
        public VersionJson Parse(string json)
            => System.Text.Json.JsonSerializer.Deserialize<VersionJson>(json, MetadataJson.Options)!;
    }
}