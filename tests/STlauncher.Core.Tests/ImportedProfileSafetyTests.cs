using System;
using System.IO;
using STlauncher.Core.Launch;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// A player's build imported from another launcher stopped starting for good: a damaged
/// resource pack was read as a broken installation, "re-download the game files" deleted
/// the build's own profile, and Mojang has no copy of it to fetch.
/// </summary>
public sealed class ImportedProfileSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("profile-safety-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_damaged_resource_pack_is_not_a_broken_installation()
    {
        var log = new[]
        {
            @"[22:49:00] [Render thread/ERROR]: Failed to open pack C:\Users\p\AppData\Roaming\STlauncher\instances\b\resourcepacks\RunicGrimoireDisabler.zip",
            "java.util.zip.ZipException: invalid CEN header (bad entry name or comment)",
            "\tat java.base/java.util.zip.ZipFile$Source.zerror(Unknown Source)",
        };

        Assert.NotEqual(CrashCause.BrokenInstallation, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void A_damaged_game_jar_still_is()
    {
        var log = new[]
        {
            "Exception in thread \"main\" java.lang.RuntimeException: boom",
            "java.util.zip.ZipException: zip END header not found",
        };

        Assert.Equal(CrashCause.BrokenInstallation, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void A_profile_set_aside_can_be_put_back()
    {
        var json = Path.Combine(_root, "Fabric 1.21.11.json");
        File.WriteAllText(json, "{\"id\":\"Fabric 1.21.11\",\"inheritsFrom\":\"1.21.11\"}");

        var aside = ForcedVersionFile.SetAside(json);

        Assert.NotNull(aside);
        Assert.False(File.Exists(json));

        ForcedVersionFile.Restore(aside!, json);

        Assert.True(File.Exists(json));
        Assert.False(File.Exists(aside));
    }

    [Fact]
    public void A_version_fetched_again_keeps_the_new_file_and_drops_the_old()
    {
        var json = Path.Combine(_root, "1.21.11.json");
        File.WriteAllText(json, "old");

        var aside = ForcedVersionFile.SetAside(json);
        File.WriteAllText(json, "new");

        ForcedVersionFile.Restore(aside!, json);
        Assert.Equal("new", File.ReadAllText(json));

        ForcedVersionFile.Discard(aside);
        Assert.False(File.Exists(aside));
    }

    [Fact]
    public void Nothing_to_set_aside_is_null()
        => Assert.Null(ForcedVersionFile.SetAside(Path.Combine(_root, "missing.json")));
}
