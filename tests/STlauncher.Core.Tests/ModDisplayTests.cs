using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

public class ModDisplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    public ModDisplayTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private string Jar(string name, params (string Path, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entryPath, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entryPath).Open();
            stream.Write(bytes);
        }

        return path;
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Fabric_jar_gives_its_title_version_and_icon()
    {
        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var jar = Jar("entityculling-fabric-1.10.5-mc1.21.11.jar",
            ("fabric.mod.json", Text("""{ "id": "entityculling", "name": "Entity Culling", "version": "1.10.5", "icon": "assets/entityculling/icon.png" }""")),
            ("assets/entityculling/icon.png", icon));

        var info = ModMetadataReader.ReadDisplay(jar)!;

        Assert.Equal("Entity Culling", info.Name);
        Assert.Equal("1.10.5", info.Version);
        Assert.Equal(icon, info.Icon);
    }

    [Fact]
    public void Icon_map_picks_the_largest_size()
    {
        var jar = Jar("a.jar",
            ("fabric.mod.json", Text("""{ "id": "a", "name": "A", "version": "1", "icon": { "16": "small.png", "128": "big.png" } }""")),
            ("small.png", new byte[] { 1 }),
            ("big.png", new byte[] { 9, 9 }));

        Assert.Equal(new byte[] { 9, 9 }, ModMetadataReader.ReadDisplay(jar)!.Icon);
    }

    [Fact]
    public void Forge_toml_gives_the_display_name_and_the_manifest_version()
    {
        var jar = Jar("jei.jar",
            ("META-INF/mods.toml", Text("modLoader=\"javafml\"\nlogoFile=\"logo.png\"\n[[mods]]\nmodId=\"jei\"\nversion=\"${file.jarVersion}\"\ndisplayName=\"Just Enough Items\"\n")),
            ("META-INF/MANIFEST.MF", Text("Manifest-Version: 1.0\nImplementation-Version: 15.2.0.27\n")),
            ("logo.png", new byte[] { 4, 5 }));

        var info = ModMetadataReader.ReadDisplay(jar)!;

        Assert.Equal("Just Enough Items", info.Name);
        Assert.Equal("15.2.0.27", info.Version);
        Assert.Equal(new byte[] { 4, 5 }, info.Icon);
    }

    [Fact]
    public void Quilt_jar_is_read_from_its_loader_block()
    {
        var jar = Jar("q.jar",
            ("quilt.mod.json", Text("""{ "quilt_loader": { "id": "q", "version": "2.0", "metadata": { "name": "Quilted" } } }""")));

        var info = ModMetadataReader.ReadDisplay(jar)!;

        Assert.Equal("Quilted", info.Name);
        Assert.Equal("2.0", info.Version);
        Assert.Null(info.Icon);
    }

    [Fact]
    public void A_jar_that_says_nothing_gives_nothing()
    {
        var jar = Jar("lib.jar", ("com/example/A.class", new byte[] { 0 }));

        Assert.Null(ModMetadataReader.ReadDisplay(jar));
        Assert.Null(ModMetadataReader.ReadDisplay(Path.Combine(_root, "missing.jar")));
    }
}
