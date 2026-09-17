using System;
using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

public class CatalogBuildSyncTests
{
    private static CatalogBuild Build() => new()
    {
        Id = "showtime-fabric",
        Name = "Showtime — Fabric",
        GameVersion = "1.21.11",
        Loader = LoaderKind.Fabric,
        LoaderVersion = "0.19.5",
        MemoryMb = 4096,
        ServerName = "Showtime",
        ServerAddress = "mc.showtime.su",
        Items = { "sodium", "lithium" }
    };

    [Fact]
    public void Recommended_PrefersTheFlaggedBuild()
    {
        var catalog = new ContentCatalog
        {
            Builds =
            {
                new CatalogBuild { Id = "old" },
                new CatalogBuild { Id = "current", Recommended = true }
            }
        };

        Assert.Equal("current", CatalogBuildSync.Recommended(catalog)?.Id);
    }

    [Fact]
    public void Recommended_FallsBackToTheFirstBuild()
    {
        var catalog = new ContentCatalog
        {
            Builds = { new CatalogBuild { Id = "only" } }
        };

        Assert.Equal("only", CatalogBuildSync.Recommended(catalog)?.Id);
        Assert.Null(CatalogBuildSync.Recommended(new ContentCatalog()));
        Assert.Null(CatalogBuildSync.Recommended(null));
    }

    [Fact]
    public void FindLinked_MatchesTheStoredBuildId()
    {
        var build = Build();
        var linked = new Instance { Id = "a", Name = "Renamed by the player", CatalogBuildId = build.Id };

        var instances = new List<Instance> { new() { Id = "b", Name = "Mine" }, linked };

        Assert.Same(linked, CatalogBuildSync.FindLinked(instances, build));
    }

    [Fact]
    public void FindLinked_AdoptsAnOlderInstanceByName()
    {
        // Upgrading from a version that did not store the link must not leave a second
        // copy of the recommended build next to the original.
        var build = Build();
        var legacy = new Instance { Id = "a", Name = build.Name };

        Assert.Same(legacy, CatalogBuildSync.FindLinked(new[] { legacy }, build));
    }

    [Fact]
    public void FindLinked_AdoptsARenamedCopyOfTheSameBuild()
    {
        // Exactly the case an upgrading player is in: the build is theirs, they renamed
        // it, and it must not be duplicated just because the name no longer matches.
        var build = Build();

        var renamed = new Instance
        {
            Id = "a",
            Name = "SHOWTIME 1.21.11 fabric",
            VersionId = build.GameVersion,
            Loader = build.Loader,
            EnabledCatalogItems = { "lithium", "sodium" }
        };

        Assert.Same(renamed, CatalogBuildSync.FindLinked(new[] { renamed }, build));
    }

    [Fact]
    public void FindLinked_LeavesADifferentBuildAlone()
    {
        var build = Build();

        var mine = new Instance
        {
            Id = "a",
            Name = "My own",
            VersionId = build.GameVersion,
            Loader = build.Loader,
            EnabledCatalogItems = { "sodium" }
        };

        Assert.Null(CatalogBuildSync.FindLinked(new[] { mine }, build));
    }

    [Fact]
    public void FindLinked_IgnoresInstancesClaimedByAnotherBuild()
    {
        var build = Build();
        var other = new Instance { Id = "a", Name = build.Name, CatalogBuildId = "something-else" };

        Assert.Null(CatalogBuildSync.FindLinked(new[] { other }, build));
    }

    [Fact]
    public void Apply_WritesEverythingTheCatalogOwns()
    {
        var instance = new Instance { Id = "a", Name = "Build" };

        var changes = CatalogBuildSync.Apply(instance, Build(), isNew: true);

        Assert.True(changes.Any);
        Assert.Equal("showtime-fabric", instance.CatalogBuildId);
        Assert.Equal("1.21.11", instance.VersionId);
        Assert.Equal(LoaderKind.Fabric, instance.Loader);
        Assert.Equal("0.19.5", instance.LoaderVersion);
        Assert.Equal("mc.showtime.su", instance.ServerAddress);
        Assert.Equal(new[] { "sodium", "lithium" }, instance.EnabledCatalogItems);
        Assert.Equal(4096, instance.MaxMemoryMb);
    }

    [Fact]
    public void Apply_KeepsTheMemoryThePlayerChose()
    {
        var instance = new Instance { Id = "a", Name = "Build" };
        CatalogBuildSync.Apply(instance, Build(), isNew: true);

        instance.MaxMemoryMb = 8192;

        // A later sync is about what the build *is*, not how the player runs it.
        CatalogBuildSync.Apply(instance, Build());

        Assert.Equal(8192, instance.MaxMemoryMb);
    }

    [Fact]
    public void Apply_ReportsNoChangesWhenAlreadyInStep()
    {
        var instance = new Instance { Id = "a", Name = "Build" };
        CatalogBuildSync.Apply(instance, Build(), isNew: true);

        var second = CatalogBuildSync.Apply(instance, Build());

        Assert.False(second.Any);
    }

    [Fact]
    public void Apply_PicksUpItemsAddedToTheCatalog()
    {
        var instance = new Instance { Id = "a", Name = "Build" };
        CatalogBuildSync.Apply(instance, Build(), isNew: true);

        var updated = Build();
        updated.Items.Add("iris");

        var changes = CatalogBuildSync.Apply(instance, updated);

        Assert.True(changes.ItemsChanged);
        Assert.Contains("iris", instance.EnabledCatalogItems);
    }

    [Fact]
    public void Apply_PicksUpAVersionBumpInTheCatalog()
    {
        var instance = new Instance { Id = "a", Name = "Build" };
        CatalogBuildSync.Apply(instance, Build(), isNew: true);

        var updated = Build();
        updated.GameVersion = "1.21.12";
        updated.LoaderVersion = "0.19.6";

        var changes = CatalogBuildSync.Apply(instance, updated);

        Assert.True(changes.VersionChanged);
        Assert.True(changes.LoaderChanged);
        Assert.Equal("1.21.12", instance.VersionId);
        Assert.Equal("0.19.6", instance.LoaderVersion);
    }

    [Fact]
    public void Apply_LeavesFieldsTheBuildDoesNotSpecify()
    {
        var instance = new Instance { Id = "a", Name = "Build", VersionId = "1.20.1", ServerAddress = "mine.example" };

        CatalogBuildSync.Apply(instance, new CatalogBuild { Id = "empty", Name = "Empty" });

        Assert.Equal("1.20.1", instance.VersionId);
        Assert.Equal("mine.example", instance.ServerAddress);
    }
}
