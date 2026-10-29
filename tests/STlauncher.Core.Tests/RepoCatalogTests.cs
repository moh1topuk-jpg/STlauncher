using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Content;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The catalog shipped in this repository is what every player's server build follows,
/// so a typo in it is a broken build for everyone. This parses the real file.
/// </summary>
public class RepoCatalogTests
{
    private static string FindRepoCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "catalog.json");

            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("catalog.json was not found above the test directory");
    }

    [Fact]
    public void RepoCatalog_ParsesAndEveryBuildItemExists()
    {
        var catalog = ContentCatalogService.Parse(File.ReadAllText(FindRepoCatalog()));
        var known = catalog.Sections.SelectMany(s => s.Items).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(catalog.Builds);

        foreach (var build in catalog.Builds)
        {
            foreach (var id in build.Items)
            {
                Assert.True(known.Contains(id), $"build '{build.Id}' lists '{id}', which no section defines");
            }
        }
    }

    [Fact]
    public void RepoCatalog_EveryItemCanBePlaced()
    {
        var catalog = ContentCatalogService.Parse(File.ReadAllText(FindRepoCatalog()));

        foreach (var item in catalog.Sections.SelectMany(s => s.Items))
        {
            Assert.NotEqual(CatalogSourceKind.Unknown, item.Source.Kind);
            Assert.NotNull(CatalogPlacement.ResolveRelativePath(item, "file.jar"));
        }
    }

    /// <summary>
    /// What a build recommends is a pack or a shader from Modrinth, pinned, and never also
    /// one of its items: an item is installed for everyone, a recommendation only on request.
    /// </summary>
    [Fact]
    public void RepoCatalog_RecommendationsArePinnedPacksOutsideTheBuild()
    {
        var catalog = ContentCatalogService.Parse(File.ReadAllText(FindRepoCatalog()));

        foreach (var build in catalog.Builds)
        {
            foreach (var id in build.Recommends)
            {
                var item = catalog.FindItem(id);

                Assert.True(item is not null, $"build '{build.Id}' recommends '{id}', which no section defines");
                Assert.True(item!.Type is CatalogItemType.ResourcePack or CatalogItemType.ShaderPack, $"'{id}' is recommended but is a {item.Type}");
                Assert.False(item.Required, $"'{id}' is recommended, so it cannot be required");
                Assert.Equal(CatalogSourceKind.Modrinth, item.Source.Kind);
                Assert.False(string.IsNullOrWhiteSpace(item.Source.Version), $"'{id}' has no pinned version");
                Assert.DoesNotContain(id, build.Items, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
