using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;
using Xunit;

namespace STlauncher.Core.Tests;

public class ModpackWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    public ModpackWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    [Fact]
    public void A_written_pack_reads_back_the_same()
    {
        var own = Path.Combine(_root, "own-mod.jar");
        File.WriteAllBytes(own, new byte[] { 1, 2, 3 });
        var config = Path.Combine(_root, "sodium-options.json");
        File.WriteAllText(config, "{}");
        var target = Path.Combine(_root, "out", "My build.mrpack");

        ModpackWriter.Write(
            target,
            "My build",
            "1.0.0",
            "1.21.11",
            LoaderKind.Fabric,
            "0.19.5",
            new[] { new ModpackExportFile("mods/sodium.jar", "https://cdn.modrinth.com/data/AANobbMI/versions/x/sodium.jar", "aa", "bb", 1234) },
            new[]
            {
                new ModpackOverride("mods/own-mod.jar", own),
                new ModpackOverride("config/sodium-options.json", config),
                new ModpackOverride("config/gone.json", Path.Combine(_root, "gone.json"))
            });

        var plan = ModpackReader.Read(target);

        Assert.Equal("My build", plan.Name);
        Assert.Equal("1.21.11", plan.GameVersion);
        Assert.Equal(LoaderKind.Fabric, plan.Loader);
        Assert.Equal("0.19.5", plan.LoaderVersion);
        var file = Assert.Single(plan.Files);
        Assert.Equal("mods/sodium.jar", file.RelativePath);
        Assert.Equal("aa", file.Sha1);
        Assert.True(plan.HasOverrides);

        using var zip = ZipFile.OpenRead(target);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("overrides/mods/own-mod.jar", names);
        Assert.Contains("overrides/config/sodium-options.json", names);
        Assert.DoesNotContain("overrides/config/gone.json", names);
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/a/b.jar", true)]
    [InlineData("https://github.com/o/r/releases/download/v1/a.jar", true)]
    [InlineData("http://cdn.modrinth.com/data/a/b.jar", false)]
    [InlineData("https://example.com/a.jar", false)]
    [InlineData("", false)]
    public void Only_the_hosts_the_format_allows_go_into_the_index(string url, bool allowed)
        => Assert.Equal(allowed, ModpackWriter.IsAllowedDownload(url));
}
