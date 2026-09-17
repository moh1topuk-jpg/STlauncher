using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

public class CurseForgeModpackTests
{
    private const string Manifest = """
    {
      "manifestType": "minecraftModpack",
      "manifestVersion": 1,
      "name": "Curse Pack",
      "version": "2.0.0",
      "minecraft": {
        "version": "1.20.1",
        "modLoaders": [ { "id": "forge-47.2.0", "primary": true } ]
      },
      "files": [
        { "projectID": 238222, "fileID": 4593052, "required": true },
        { "projectID": 306612, "fileID": 1234567, "required": false }
      ],
      "overrides": "overrides"
    }
    """;

    private static ZipArchive Build(IEnumerable<(string, string)> entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    [Fact]
    public void Read_DetectsCurseForgeFormat()
    {
        using var archive = Build(new[]
        {
            ("manifest.json", Manifest),
            ("overrides/config/x.txt", "hi")
        });

        Assert.Equal(ModpackFormat.CurseForge, ModpackReader.DetectFormat(archive));

        var plan = ModpackReader.ReadCurseForge(archive);

        Assert.Equal(ModpackFormat.CurseForge, plan.Format);
        Assert.Equal("Curse Pack", plan.Name);
        Assert.Equal("2.0.0", plan.VersionId);
        Assert.Equal("1.20.1", plan.GameVersion);
        Assert.Equal(LoaderKind.Forge, plan.Loader);
        Assert.Equal("47.2.0", plan.LoaderVersion);
        Assert.True(plan.HasOverrides);
    }

    [Fact]
    public void Read_KeepsProjectAndFileIds_WithoutUrls()
    {
        using var archive = Build(new[] { ("manifest.json", Manifest) });

        var plan = ModpackReader.ReadCurseForge(archive);

        Assert.Equal(2, plan.Files.Count);
        Assert.All(plan.Files, f => Assert.Null(f.Url));
        Assert.Equal(238222, plan.Files[0].ProjectId);
        Assert.Equal(4593052, plan.Files[0].FileId);
        Assert.True(plan.Files[0].Required);
        Assert.False(plan.Files[1].Required);
    }

    [Fact]
    public void Read_ThrowsWhenNeitherFormatIsPresent()
    {
        using var archive = Build(new[] { ("readme.txt", "nothing") });

        Assert.Throws<InvalidDataException>(() => ModpackReader.Read(archive));
    }

    [Theory]
    [InlineData("forge-47.2.0", LoaderKind.Forge, "47.2.0")]
    [InlineData("fabric-0.15.0", LoaderKind.Fabric, "0.15.0")]
    [InlineData("neoforge-20.4.237", LoaderKind.NeoForge, "20.4.237")]
    [InlineData("quilt-0.23.0", LoaderKind.Quilt, "0.23.0")]
    public void DetectCurseForgeLoader_ParsesLoaderId(string id, LoaderKind expectedKind, string expectedVersion)
    {
        var modLoaders = new List<ModLoaderRef> { new() { Id = id, Primary = true } };

        var (loader, version) = ModpackReader.DetectCurseForgeLoader(modLoaders);

        Assert.Equal(expectedKind, loader);
        Assert.Equal(expectedVersion, version);
    }

    [Fact]
    public void DetectCurseForgeLoader_UsesPrimaryEntry()
    {
        var modLoaders = new List<ModLoaderRef>
        {
            new() { Id = "fabric-0.15.0", Primary = false },
            new() { Id = "forge-47.2.0", Primary = true }
        };

        var (loader, version) = ModpackReader.DetectCurseForgeLoader(modLoaders);

        Assert.Equal(LoaderKind.Forge, loader);
        Assert.Equal("47.2.0", version);
    }

    [Theory]
    [InlineData(6, "mods")]
    [InlineData(12, "resourcepacks")]
    [InlineData(6552, "shaderpacks")]
    [InlineData(4471, "")]
    [InlineData(9999, "mods")]
    public void FolderForClassId_MapsCategories(int classId, string expected)
        => Assert.Equal(expected, CurseForgeClient.FolderForClassId(classId));

    [Fact]
    public void CurseForgeClient_IsConfiguredWhenKeyProvided()
    {
        using var http = new System.Net.Http.HttpClient();
        var client = new CurseForgeClient(http, "test-key");

        Assert.True(client.IsConfigured);

        client.ApiKey = null;
        Assert.False(client.IsConfigured);

        client.ApiKey = "another-key";
        Assert.True(client.IsConfigured);
    }
}