using System;
using STlauncher.Core.Friends;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

public class JoinPlanTests
{
    private static JoinBuild Build(string id, string? version, LoaderKind loader, bool dedicated = false)
        => new(id, version, loader, dedicated);

    [Fact]
    public void Build_made_for_the_server_wins_over_everything()
    {
        var builds = new[]
        {
            Build("fits", "1.21.1", LoaderKind.Fabric),
            Build("own", "1.20.1", LoaderKind.Forge, dedicated: true)
        };

        var plan = JoinPlan.Decide("1.21.1", LoaderKind.Fabric, inviteBringsBuild: true, builds, "fits");

        Assert.Equal(JoinAction.UseDedicated, plan.Action);
        Assert.Equal("own", plan.BuildId);
        Assert.True(plan.Launches);
        Assert.False(plan.ComesWithInvite);
    }

    [Fact]
    public void Build_made_for_the_server_is_used_even_when_the_version_is_unknown()
    {
        var plan = JoinPlan.Decide(null, null, false, new[] { Build("own", "1.21.1", LoaderKind.Fabric, dedicated: true) }, null);

        Assert.Equal(JoinAction.UseDedicated, plan.Action);
        Assert.Equal("own", plan.BuildId);
    }

    [Fact]
    public void Invite_build_wins_over_a_build_that_fits()
    {
        var plan = JoinPlan.Decide("1.21.1", LoaderKind.Fabric, inviteBringsBuild: true, new[] { Build("fits", "1.21.1", LoaderKind.Fabric) }, "fits");

        Assert.Equal(JoinAction.UseDedicated, plan.Action);
        Assert.Null(plan.BuildId);
        Assert.True(plan.ComesWithInvite);

        // It is not on the computer yet: there is nothing to start before it is added.
        Assert.False(plan.Launches);
    }

    [Fact]
    public void Existing_build_of_the_same_version_and_loader_is_used()
    {
        var builds = new[]
        {
            Build("other-version", "1.20.1", LoaderKind.Fabric),
            Build("fits", "1.21.1", LoaderKind.Fabric)
        };

        var plan = JoinPlan.Decide("1.21.1", LoaderKind.Fabric, false, builds, "other-version");

        Assert.Equal(JoinAction.UseExisting, plan.Action);
        Assert.Equal("fits", plan.BuildId);
        Assert.True(plan.Launches);
    }

    [Fact]
    public void Selected_build_goes_first_among_those_that_fit()
    {
        var builds = new[]
        {
            Build("first", "1.21.1", LoaderKind.Fabric),
            Build("selected", "1.21.1", LoaderKind.Fabric)
        };

        Assert.Equal("selected", JoinPlan.Decide("1.21.1", LoaderKind.Fabric, false, builds, "SELECTED").BuildId);
        Assert.Equal("first", JoinPlan.Decide("1.21.1", LoaderKind.Fabric, false, builds, null).BuildId);
        Assert.Equal("first", JoinPlan.Decide("1.21.1", LoaderKind.Fabric, false, builds, "gone").BuildId);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, LoaderKind.Fabric)]
    [InlineData(LoaderKind.Forge, LoaderKind.Vanilla)]
    [InlineData(LoaderKind.NeoForge, LoaderKind.Forge)]
    [InlineData(LoaderKind.Quilt, LoaderKind.Fabric)]
    [InlineData(LoaderKind.Fabric, LoaderKind.Quilt)]
    [InlineData(LoaderKind.Quilt, LoaderKind.Vanilla)]
    [InlineData(LoaderKind.Vanilla, LoaderKind.Fabric)]
    public void Build_of_another_loader_is_never_used(LoaderKind buildLoader, LoaderKind serverLoader)
    {
        // Selected, and of the right game version: still not this server's build.
        var plan = JoinPlan.Decide("1.21.1", serverLoader, false, new[] { Build("pack", "1.21.1", buildLoader) }, "pack");

        Assert.Equal(JoinAction.CreateClean, plan.Action);
        Assert.Null(plan.BuildId);
        Assert.Equal("1.21.1", plan.GameVersion);
        Assert.Equal(serverLoader, plan.Loader);
        Assert.False(plan.Launches);
    }

    [Theory]
    [InlineData(LoaderKind.Vanilla)]
    [InlineData(LoaderKind.Fabric)]
    public void Vanilla_server_takes_a_vanilla_or_a_fabric_build(LoaderKind buildLoader)
    {
        var plan = JoinPlan.Decide("1.21.1", LoaderKind.Vanilla, false, new[] { Build("b", "1.21.1", buildLoader) }, null);

        Assert.Equal(JoinAction.UseExisting, plan.Action);
        Assert.Equal("b", plan.BuildId);
    }

    [Fact]
    public void Vanilla_server_prefers_a_vanilla_build_unless_the_fabric_one_is_selected()
    {
        var builds = new[]
        {
            Build("fabric", "1.21.1", LoaderKind.Fabric),
            Build("vanilla", "1.21.1", LoaderKind.Vanilla)
        };

        Assert.Equal("vanilla", JoinPlan.Decide("1.21.1", LoaderKind.Vanilla, false, builds, null).BuildId);
        Assert.Equal("fabric", JoinPlan.Decide("1.21.1", LoaderKind.Vanilla, false, builds, "fabric").BuildId);
    }

    [Fact]
    public void Unknown_loader_is_treated_as_a_plain_server()
    {
        var builds = new[]
        {
            Build("forge", "1.21.1", LoaderKind.Forge),
            Build("fabric", "1.21.1", LoaderKind.Fabric)
        };

        Assert.Equal("fabric", JoinPlan.Decide("1.21.1", null, false, builds, "forge").BuildId);

        var none = JoinPlan.Decide("1.21.1", null, false, new[] { builds[0] }, "forge");

        Assert.Equal(JoinAction.CreateClean, none.Action);
        Assert.Equal(LoaderKind.Vanilla, none.Loader);
    }

    [Fact]
    public void Build_of_another_game_version_is_never_used()
    {
        var builds = new[]
        {
            Build("older", "1.21", LoaderKind.Fabric),
            Build("newer", "1.21.11", LoaderKind.Fabric),
            Build("no-version", null, LoaderKind.Fabric)
        };

        var plan = JoinPlan.Decide(" 1.21.1 ", LoaderKind.Fabric, false, builds, "older");

        Assert.Equal(JoinAction.CreateClean, plan.Action);
        Assert.Equal("1.21.1", plan.GameVersion);
        Assert.Equal(LoaderKind.Fabric, plan.Loader);
    }

    [Fact]
    public void No_builds_at_all_means_a_clean_one()
    {
        var plan = JoinPlan.Decide("1.21.1", LoaderKind.Vanilla, false, null, null);

        Assert.Equal(JoinAction.CreateClean, plan.Action);
        Assert.Equal(JoinQuestion.None, plan.Question);
    }

    [Fact]
    public void Nothing_fits_and_nothing_can_be_made_asks_the_player()
    {
        var plan = JoinPlan.Decide("1.21.1", LoaderKind.Fabric, false, new[] { Build("pack", "1.21.1", LoaderKind.Forge) }, "pack", canCreate: false);

        Assert.Equal(JoinAction.Ask, plan.Action);
        Assert.Equal(JoinQuestion.NoBuildFits, plan.Question);

        // A build that cannot fit is not even offered.
        Assert.Null(plan.BuildId);
        Assert.False(plan.Launches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Unknown_version_asks_and_never_launches(string? version)
    {
        var builds = new[] { Build("selected", "1.21.1", LoaderKind.Fabric), Build("other", "1.20.1", LoaderKind.Vanilla) };

        var plan = JoinPlan.Decide(version, LoaderKind.Fabric, false, builds, "selected");

        Assert.Equal(JoinAction.Ask, plan.Action);
        Assert.Equal(JoinQuestion.VersionUnknown, plan.Question);

        // The selected build is what the question is about, not an answer to it.
        Assert.Equal("selected", plan.BuildId);
        Assert.False(plan.Launches);
    }

    [Fact]
    public void Unknown_version_without_a_selected_build_offers_nothing()
    {
        var plan = JoinPlan.Decide(null, null, false, new[] { Build("b", "1.21.1", LoaderKind.Vanilla) }, null);

        Assert.Equal(JoinAction.Ask, plan.Action);
        Assert.Null(plan.BuildId);
        Assert.False(plan.Launches);

        Assert.Equal(JoinAction.Ask, JoinPlan.Decide(null, null, false, Array.Empty<JoinBuild>(), null).Action);
    }

    [Theory]
    [InlineData("1.21.1", "1.21.1")]
    [InlineData("Paper 1.21.1", "1.21.1")]
    [InlineData("Purpur 1.20", "1.20")]
    [InlineData("fabric-1.21.11", "1.21.11")]
    [InlineData("24w14a", "24w14a")]
    [InlineData("Spigot 1.21.1 (MC: 1.21.1)", "1.21.1")]
    public void Version_is_read_out_of_a_server_status(string name, string expected)
    {
        Assert.Equal(expected, JoinPlan.VersionFromStatus(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Velocity 1.7.2-1.21.1")]
    [InlineData("1.8.x-1.21.x")]
    [InlineData("1.8.x-1.21.1")]
    [InlineData("Requires MC 1.8 / 1.21")]
    [InlineData("BungeeCord")]
    public void Status_that_names_no_single_version_is_unknown(string? name)
    {
        Assert.Null(JoinPlan.VersionFromStatus(name));
    }
}
