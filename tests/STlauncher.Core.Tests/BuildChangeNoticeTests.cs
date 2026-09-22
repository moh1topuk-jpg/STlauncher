using STlauncher.Core.Content;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

public class BuildChangeNoticeTests
{
    private static string Name(string id) => id.ToUpperInvariant();

    [Fact]
    public void Between_NamesAddedAndRemovedItems()
    {
        var before = new BuildSnapshot("1.21.4", LoaderKind.Fabric, "0.16.9", new[] { "sodium", "lithium", "old" });
        var after = new BuildSnapshot("1.21.4", LoaderKind.Fabric, "0.16.9", new[] { "sodium", "lithium", "iris", "modmenu" });

        var notice = BuildChangeNotice.Between(before, after, Name);

        Assert.True(notice.Any);
        Assert.False(notice.VersionChanged);
        Assert.False(notice.LoaderChanged);
        Assert.Equal(new[] { "IRIS", "MODMENU" }, notice.Added);
        Assert.Equal(new[] { "OLD" }, notice.Removed);
    }

    [Fact]
    public void Between_ReportsVersionAndLoaderChanges()
    {
        var before = new BuildSnapshot("1.21.4", LoaderKind.Fabric, "0.16.9", new[] { "sodium" });
        var after = new BuildSnapshot("1.21.5", LoaderKind.Fabric, "0.16.10", new[] { "sodium" });

        var notice = BuildChangeNotice.Between(before, after, Name);

        Assert.True(notice.VersionChanged);
        Assert.Equal("1.21.4", notice.OldVersion);
        Assert.Equal("1.21.5", notice.NewVersion);
        Assert.True(notice.LoaderChanged);
        Assert.Equal("Fabric 0.16.9", notice.OldLoader);
        Assert.Equal("Fabric 0.16.10", notice.NewLoader);
        Assert.Empty(notice.Added);
    }

    [Fact]
    public void Between_SameBuild_IsNothing()
    {
        var snapshot = new BuildSnapshot("1.21.4", LoaderKind.Fabric, null, new[] { "a", "b" });

        Assert.False(BuildChangeNotice.Between(snapshot, snapshot with { Items = new[] { "B", "a" } }, Name).Any);
    }

    [Fact]
    public void Between_FirstVersion_IsNotAVersionChange()
    {
        var before = new BuildSnapshot(null, LoaderKind.Vanilla, null, new string[0]);
        var after = new BuildSnapshot("1.21.4", LoaderKind.Fabric, "0.16.9", new[] { "sodium" });

        var notice = BuildChangeNotice.Between(before, after, Name);

        Assert.False(notice.VersionChanged);
        Assert.Single(notice.Added);
    }
}
