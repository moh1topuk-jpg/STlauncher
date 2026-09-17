using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;
using Xunit;

namespace STlauncher.Core.Tests;

public class ModpackReaderTests
{
    private static ZipArchive BuildPack(string indexJson, params (string Path, string Content)[] extra)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "modrinth.index.json", indexJson);

            foreach (var (path, content) in extra)
            {
                Write(zip, path, content);
            }
        }

        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);

        static void Write(ZipArchive zip, string path, string content)
        {
            var entry = zip.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    private const string FabricPack = """
    {
      "formatVersion": 1,
      "name": "Test Pack",
      "versionId": "1.2.3",
      "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.0" },
      "files": [
        {
          "path": "mods/sodium.jar",
          "hashes": { "sha1": "aa", "sha512": "bb" },
          "env": { "client": "required", "server": "unsupported" },
          "downloads": ["https://cdn.modrinth.com/sodium.jar"],
          "fileSize": 1024
        },
        {
          "path": "mods/serveronly.jar",
          "hashes": { "sha1": "cc" },
          "env": { "client": "unsupported", "server": "required" },
          "downloads": ["https://cdn.modrinth.com/serveronly.jar"],
          "fileSize": 2048
        }
      ]
    }
    """;

    [Fact]
    public void Read_DetectsLoaderAndGameVersion()
    {
        using var archive = BuildPack(FabricPack);

        var plan = ModpackReader.Read(archive);

        Assert.Equal("Test Pack", plan.Name);
        Assert.Equal("1.20.1", plan.GameVersion);
        Assert.Equal(LoaderKind.Fabric, plan.Loader);
        Assert.Equal("0.15.0", plan.LoaderVersion);
    }

    [Fact]
    public void Read_SkipsClientUnsupportedFiles()
    {
        using var archive = BuildPack(FabricPack);

        var plan = ModpackReader.Read(archive);

        var file = Assert.Single(plan.Files);
        Assert.Equal("mods/sodium.jar", file.RelativePath);
        Assert.Equal("aa", file.Sha1);
        Assert.Equal("bb", file.Sha512);
    }

    [Fact]
    public void Read_DetectsOverrides()
    {
        using var archive = BuildPack(FabricPack, ("overrides/config/test.txt", "hello"));

        var plan = ModpackReader.Read(archive);

        Assert.True(plan.HasOverrides);
    }

    [Fact]
    public void Read_RejectsPathTraversal()
    {
        const string malicious = """
        {
          "formatVersion": 1,
          "name": "Evil",
          "dependencies": { "minecraft": "1.20.1" },
          "files": [
            {
              "path": "../evil.jar",
              "hashes": {},
              "downloads": ["https://cdn.modrinth.com/evil.jar"],
              "fileSize": 1
            }
          ]
        }
        """;

        using var archive = BuildPack(malicious);

        Assert.Throws<InvalidDataException>(() => ModpackReader.Read(archive));
    }

    [Theory]
    [InlineData("mods/a.jar", true)]
    [InlineData("config/x/y.json", true)]
    [InlineData("../evil.jar", false)]
    [InlineData("mods/../../evil.jar", false)]
    [InlineData("C:/windows/system32/x.dll", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("", false)]
    public void IsSafe_RejectsEscapes(string path, bool expected)
        => Assert.Equal(expected, RelativePath.IsSafe(path));

    [Theory]
    [InlineData("forge", LoaderKind.Forge)]
    [InlineData("neoforge", LoaderKind.NeoForge)]
    [InlineData("quilt-loader", LoaderKind.Quilt)]
    [InlineData("fabric-loader", LoaderKind.Fabric)]
    public void DetectLoader_MapsDependencyKeys(string key, LoaderKind expected)
    {
        var (loader, version) = ModpackReader.DetectLoader(
            new System.Collections.Generic.Dictionary<string, string> { [key] = "1.0" });

        Assert.Equal(expected, loader);
        Assert.Equal("1.0", version);
    }
}
