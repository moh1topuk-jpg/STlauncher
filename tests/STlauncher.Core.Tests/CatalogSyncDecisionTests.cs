using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using Xunit;

namespace STlauncher.Core.Tests;

public class CatalogSyncDecisionTests
{
    private static CatalogItem Pinned(string version = "0.8.12") => new()
    {
        Id = "sodium",
        Name = "Sodium",
        Source = new CatalogSource
        {
            Kind = CatalogSourceKind.Modrinth,
            Project = "sodium",
            Version = version
        }
    };

    private static CatalogItem Unpinned() => new()
    {
        Id = "sodium",
        Name = "Sodium",
        Source = new CatalogSource { Kind = CatalogSourceKind.Modrinth, Project = "sodium" }
    };

    private static InstalledModRecord Record(string? version, bool disabled = false) => new()
    {
        FileName = "sodium.jar",
        Source = ModSource.Catalog,
        Id = "sodium",
        Version = version,
        DisabledByUser = disabled
    };

    [Fact]
    public void PinnedAndAlreadyAtThatVersion_IsLeftAlone()
    {
        // The bug this whole type exists for: every catalog item is pinned, the old check
        // only skipped *unpinned* items, so every start re-resolved the entire build.
        var action = CatalogSyncDecision.Decide(Pinned(), Record("0.8.12"), fileExists: true);

        Assert.Equal(CatalogSyncAction.Keep, action);
        Assert.False(action.NeedsInstall());
    }

    [Fact]
    public void PinMovedOn_IsUpdated()
    {
        var action = CatalogSyncDecision.Decide(Pinned("0.8.14"), Record("0.8.12"), fileExists: true);

        Assert.Equal(CatalogSyncAction.Update, action);
        Assert.True(action.NeedsInstall());
    }

    [Fact]
    public void PinComparisonIgnoresCase()
    {
        var action = CatalogSyncDecision.Decide(Pinned("MC1.21.11-Fabric"), Record("mc1.21.11-fabric"), fileExists: true);

        Assert.Equal(CatalogSyncAction.Keep, action);
    }

    [Fact]
    public void InstalledBeforeVersionsWereRecorded_IsResolvedOnce()
    {
        // Upgrading from a build that stored no version: one pass to learn it, then quiet.
        var action = CatalogSyncDecision.Decide(Pinned(), Record(version: null), fileExists: true);

        Assert.Equal(CatalogSyncAction.Update, action);
    }

    [Fact]
    public void Unpinned_StaysAtWhateverWasInstalled()
    {
        // The build is reproducible because it is not bumped on its own.
        var action = CatalogSyncDecision.Decide(Unpinned(), Record("0.8.12"), fileExists: true);

        Assert.Equal(CatalogSyncAction.Keep, action);
    }

    [Fact]
    public void NeverInstalled_IsInstalled()
    {
        Assert.Equal(
            CatalogSyncAction.Install,
            CatalogSyncDecision.Decide(Pinned(), record: null, fileExists: false));
    }

    [Fact]
    public void RecordedButFileIsGone_IsInstalledAgain()
    {
        Assert.Equal(
            CatalogSyncAction.Install,
            CatalogSyncDecision.Decide(Pinned(), Record("0.8.12"), fileExists: false));
    }

    [Fact]
    public void SwitchedOffByThePlayer_StaysOff()
    {
        // The file on disk is "sodium.jar.disabled" and the record follows it. Reinstalling
        // here is what made a disabled mod come back on the next start.
        var action = CatalogSyncDecision.Decide(Pinned(), Record("0.8.12", disabled: true), fileExists: true);

        Assert.Equal(CatalogSyncAction.KeepDisabled, action);
        Assert.False(action.NeedsInstall());
    }

    [Fact]
    public void SwitchedOffAndThenDeleted_IsNotResurrected()
    {
        var action = CatalogSyncDecision.Decide(Pinned(), Record("0.8.12", disabled: true), fileExists: false);

        Assert.Equal(CatalogSyncAction.KeepDisabled, action);
    }

    [Fact]
    public void SwitchedOffButThePinMoved_StillStaysOff()
    {
        // A newer pin is not a reason to override the player's decision.
        var action = CatalogSyncDecision.Decide(Pinned("0.9.0"), Record("0.8.12", disabled: true), fileExists: true);

        Assert.Equal(CatalogSyncAction.KeepDisabled, action);
    }
}
