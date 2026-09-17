using System;
using System.IO;
using STlauncher.Core;
using STlauncher.Core.Content;
using Xunit;

namespace STlauncher.Core.Tests;

public class ContentCatalogTests
{
    private const string CatalogJson = """
    {
      "schemaVersion": 1,
      "name": "Showtime Server",
      "description": "Mods for the server",
      "sections": [
        {
          "id": "required",
          "title": "Обязательные",
          "description": "Без них не пустит",
          "items": [
            {
              "id": "sodium",
              "type": "mod",
              "name": "Sodium",
              "description": "Повышает FPS",
              "required": true,
              "side": "client",
              "tags": ["performance"],
              "source": { "kind": "modrinth", "project": "sodium", "version": "mc1.20.1-0.5.13-fabric" }
            },
            {
              "type": "resourcepack",
              "name": "Faithful",
              "source": { "kind": "direct", "url": "https://mc.showtime.su/files/faithful.zip", "sha1": "aa" }
            },
            {
              "type": "datapack",
              "name": "Future kind",
              "targetPath": "world/datapacks/pack.zip",
              "source": { "kind": "direct", "url": "https://mc.showtime.su/files/pack.zip" }
            }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ReadsSectionsAndItems()
    {
        var catalog = ContentCatalogService.Parse(CatalogJson);

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal("Showtime Server", catalog.Name);
        Assert.Single(catalog.Sections);
        Assert.Equal(3, catalog.ItemCount);
        Assert.Equal("Обязательные", catalog.Sections[0].Title);
    }

    [Fact]
    public void Parse_ReadsItemFields()
    {
        var item = ContentCatalogService.Parse(CatalogJson).Sections[0].Items[0];

        Assert.Equal("sodium", item.Id);
        Assert.Equal(CatalogItemType.Mod, item.Type);
        Assert.Equal("Sodium", item.Name);
        Assert.Equal("Повышает FPS", item.Description);
        Assert.True(item.Required);
        Assert.Equal("client", item.Side);
        Assert.Equal(new[] { "performance" }, item.Tags);
        Assert.Equal(CatalogSourceKind.Modrinth, item.Source.Kind);
        Assert.Equal("sodium", item.Source.Project);
        Assert.Equal("mc1.20.1-0.5.13-fabric", item.Source.Version);
    }

    [Theory]
    [InlineData("mod", CatalogItemType.Mod)]
    [InlineData("resourcepack", CatalogItemType.ResourcePack)]
    [InlineData("resourcePack", CatalogItemType.ResourcePack)]
    [InlineData("ResourcePack", CatalogItemType.ResourcePack)]
    [InlineData("resource_pack", CatalogItemType.ResourcePack)]
    [InlineData("shaderpack", CatalogItemType.ShaderPack)]
    [InlineData("datapack", CatalogItemType.Other)]
    public void Parse_AcceptsEnumSpellingVariants(string raw, CatalogItemType expected)
    {
        var json = $$"""
        { "schemaVersion": 1, "sections": [ { "id": "s", "items": [
          { "id": "x", "type": "{{raw}}", "name": "X", "source": { "kind": "direct", "url": "https://e/x.zip" } } ] } ] }
        """;

        var item = ContentCatalogService.Parse(json).Sections[0].Items[0];

        Assert.Equal(expected, item.Type);
    }

    [Fact]
    public void Parse_FallsBackToUnknownForUnknownSourceKind()
    {
        const string json = """
        { "schemaVersion": 1, "sections": [ { "id": "s", "items": [
          { "id": "x", "name": "X", "source": { "kind": "telepathy" } } ] } ] }
        """;

        var item = ContentCatalogService.Parse(json).Sections[0].Items[0];

        Assert.Equal(CatalogSourceKind.Unknown, item.Source.Kind);
    }

    [Fact]
    public void Parse_GeneratesIdsWhenMissing()
    {
        var item = ContentCatalogService.Parse(CatalogJson).Sections[0].Items[1];

        Assert.Equal("required/Faithful", item.Id);
    }

    [Fact]
    public void Parse_RejectsNewerSchemaVersion()
    {
        const string json = """{ "schemaVersion": 99, "sections": [] }""";

        Assert.Throws<InvalidDataException>(() => ContentCatalogService.Parse(json));
    }

    [Fact]
    public void Parse_RejectsMalformedJson()
    {
        Assert.ThrowsAny<Exception>(() => ContentCatalogService.Parse("{ nope"));
    }

    [Theory]
    [InlineData(CatalogItemType.Mod, "mods")]
    [InlineData(CatalogItemType.ResourcePack, "resourcepacks")]
    [InlineData(CatalogItemType.ShaderPack, "shaderpacks")]
    [InlineData(CatalogItemType.Config, "config")]
    [InlineData(CatalogItemType.Modpack, "")]
    [InlineData(CatalogItemType.Other, "")]
    public void FolderFor_MapsItemKinds(CatalogItemType type, string expected)
        => Assert.Equal(expected, CatalogPlacement.FolderFor(type));

    [Fact]
    public void ResolveRelativePath_UsesFolderForKnownKinds()
    {
        var item = new CatalogItem { Type = CatalogItemType.ResourcePack };

        Assert.Equal("resourcepacks/pack.zip", CatalogPlacement.ResolveRelativePath(item, "pack.zip"));
    }

    [Fact]
    public void ResolveRelativePath_TargetPathWins()
    {
        var item = new CatalogItem
        {
            Type = CatalogItemType.Other,
            TargetPath = "world/datapacks/pack.zip"
        };

        Assert.Equal("world/datapacks/pack.zip", CatalogPlacement.ResolveRelativePath(item, "ignored.zip"));
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("mods/../../escape.zip")]
    [InlineData("C:/windows/system32/x.dll")]
    [InlineData("/etc/passwd")]
    public void ResolveRelativePath_RejectsUnsafeTargetPath(string target)
    {
        var item = new CatalogItem { Type = CatalogItemType.Mod, TargetPath = target };

        Assert.Null(CatalogPlacement.ResolveRelativePath(item, "x.jar"));
    }

    [Fact]
    public void ResolveRelativePath_ReturnsNullForUnplaceableKind()
    {
        var item = new CatalogItem { Type = CatalogItemType.Modpack };

        Assert.Null(CatalogPlacement.ResolveRelativePath(item, "pack.mrpack"));
    }

    [Theory]
    [InlineData("https://mc.showtime.su/launcher/catalog.json", true)]
    [InlineData("http://example.com/catalog.json", true)]
    [InlineData(@"C:\data\catalog.json", false)]
    [InlineData("file:///C:/data/catalog.json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHttpUrl_DetectsHttpSources(string? value, bool expected)
        => Assert.Equal(expected, ContentCatalogService.IsHttpUrl(value));

    [Theory]
    [InlineData(@"C:\data\catalog.json", true)]
    [InlineData("catalog.json", true)]
    [InlineData("file:///C:/data/catalog.json", true)]
    [InlineData("https://example.com/catalog.json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalPath_DetectsFilesystemSources(string? value, bool expected)
        => Assert.Equal(expected, ContentCatalogService.IsLocalPath(value));

    [Fact]
    public void ResolveLocalFile_ConvertsFileUriToPath()
    {
        var resolved = ContentCatalogService.ResolveLocalFile("file:///C:/data/catalog.json");

        Assert.Equal(@"C:\data\catalog.json", resolved);
    }

    [Fact]
    public void ResolveLocalFile_KeepsPlainPathsUntouched()
        => Assert.Equal(@"C:\data\catalog.json", ContentCatalogService.ResolveLocalFile(@"C:\data\catalog.json"));

    [Theory]
    [InlineData("https://cdn.example.com/a/b/sodium-fabric-0.5.13%2Bmc1.20.1.jar", "sodium-fabric-0.5.13+mc1.20.1.jar")]
    [InlineData("https://mc.showtime.su/files/stuff.jar", "stuff.jar")]
    [InlineData("not a url", null)]
    public void FileNameFromUrl_DecodesNames(string url, string? expected)
        => Assert.Equal(expected, CatalogInstaller.FileNameFromUrl(url));
}