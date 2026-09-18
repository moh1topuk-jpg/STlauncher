using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using STlauncher.Core.Auth;
using STlauncher.Core.Launch;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// TLauncher writes profiles in its own dialect of the version format. A build imported
/// from it launched with no classpath and failed with "Could not find or load main class
/// net.fabricmc.loader.impl.launch.knot.KnotClient". The profile below keeps the shape of
/// the real one that did that.
/// </summary>
public class TLauncherProfileTests
{
    private const string Profile = """
        {
          "id": "fabric 1.21.11 shield",
          "type": "modified",
          "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
          "complianceLevel": 1.0,
          "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21.0 },
          "arguments": {
            "jvm": [
              { "values": [ "-Xss1M" ], "rules": [ { "action": "allow", "os": { } } ] },
              { "values": [ "-Djava.library.path=${natives_directory}" ], "rules": [ ] },
              { "values": [ "-cp" ], "rules": [ ] },
              { "values": [ "${classpath}" ], "rules": [ ] }
            ],
            "game": [
              { "values": [ "--username", "${auth_player_name}" ], "rules": [ ] }
            ]
          },
          "libraries": [
            { "name": "net.fabricmc:fabric-loader:0.19.3", "url": "https://maven.fabricmc.net/" },
            {
              "name": "at.yawk.lz4:lz4-java:1.8.1",
              "artifact": {
                "sha1": "1f30a180ee04ccd53339a716aec13cae60a013b5",
                "size": 683071,
                "path": "at/yawk/lz4/lz4-java/1.8.1/lz4-java-1.8.1.jar",
                "url": "https://libraries.minecraft.net/at/yawk/lz4/lz4-java/1.8.1/lz4-java-1.8.1.jar"
              }
            },
            {
              "name": "ca.weblite:java-objc-bridge:1.1",
              "rules": [ { "action": "allow", "os": { "name": "osx" } } ],
              "artifact": {
                "path": "ca/weblite/java-objc-bridge/1.1/java-objc-bridge-1.1.jar",
                "url": "https://libraries.minecraft.net/ca/weblite/java-objc-bridge/1.1/java-objc-bridge-1.1.jar"
              }
            }
          ]
        }
        """;

    private static readonly RuleContext Windows = new()
    {
        OsName = "windows",
        Arch = "x86_64",
        Features = new Dictionary<string, bool>()
    };

    private static ResolvedVersion Resolve()
        => ResolvedVersion.FromLeaf(JsonSerializer.Deserialize<VersionJson>(Profile, MetadataJson.Options)!);

    [Fact]
    public void ReadsArgumentsWrittenAsValues()
    {
        var version = Resolve();

        Assert.Contains(version.JvmArguments, a => a.Values.Contains("-cp"));
        Assert.Contains(version.JvmArguments, a => a.Values.Contains("${classpath}"));
        Assert.Contains(version.GameArguments, a => a.Values.Contains("--username"));
    }

    [Fact]
    public void ReadsLibrariesWhoseArtifactSitsBesideTheName()
    {
        var paths = new LauncherPaths(Path.Combine(Path.GetTempPath(), "stl-" + Guid.NewGuid().ToString("N")));

        var libraries = LibraryResolver.Resolve(Resolve(), paths, Windows);

        Assert.Contains(libraries.Classpath, p => p.EndsWith("fabric-loader-0.19.3.jar", StringComparison.Ordinal));
        Assert.Contains(libraries.Classpath, p => p.EndsWith("lz4-java-1.8.1.jar", StringComparison.Ordinal));

        // The rule still applies to the TLauncher shape: no macOS bridge on Windows.
        Assert.DoesNotContain(libraries.Classpath, p => p.Contains("java-objc-bridge", StringComparison.Ordinal));

        Assert.Contains(libraries.Downloads, d =>
            d.Url == "https://libraries.minecraft.net/at/yawk/lz4/lz4-java/1.8.1/lz4-java-1.8.1.jar");
    }

    [Fact]
    public void TheCommandCarriesTheClasspath()
    {
        var command = Build(Resolve());

        var cp = command.Arguments.ToList().IndexOf("-cp");

        Assert.True(cp >= 0);
        Assert.Contains("fabric-loader", command.Arguments[cp + 1], StringComparison.Ordinal);
        Assert.True(cp < command.Arguments.ToList().IndexOf("net.fabricmc.loader.impl.launch.knot.KnotClient"));
    }

    [Fact]
    public void AProfileWithNoClasspathArgumentStillGetsOne()
    {
        var version = Resolve();
        version.JvmArguments.RemoveAll(a => a.Values.Contains("-cp") || a.Values.Contains("${classpath}"));

        var command = Build(version);

        Assert.Contains("-cp", command.Arguments);
    }

    private static LaunchCommand Build(ResolvedVersion version)
        => LaunchCommandBuilder.Build(new LaunchOptions
        {
            Version = version,
            Account = OfflineAuth.Login("Tester"),
            JavaPath = "java",
            GameDirectory = "game",
            AssetsDirectory = "assets",
            NativesDirectory = "natives",
            LibrariesDirectory = "libraries",
            Classpath = new[] { "libraries/net/fabricmc/fabric-loader/0.19.3/fabric-loader-0.19.3.jar" }
        }, Windows);
}
