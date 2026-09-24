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
}
