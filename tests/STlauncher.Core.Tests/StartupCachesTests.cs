using System.IO.Compression;
using System.Text;
using STlauncher.Core.Mods;
using STlauncher.Core.Packs;

namespace STlauncher.Core.Tests;

/// <summary>
/// The two caches a start leans on: what each resource pack says about itself, and the
/// titles and icons of a build's mods. Both must answer from memory while a file is the
/// one that was read, and read again the moment it is not.
/// </summary>
public class StartupCachesTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WritePack(string path, string description)
    {
        File.Delete(path);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var meta = archive.CreateEntry("pack.mcmeta");
        using var writer = new StreamWriter(meta.Open(), Encoding.UTF8);
        writer.Write("{ \"pack\": { \"pack_format\": 46, \"description\": \"" + description + "\" } }");
    }

    // ===================== Resource packs =====================

    [Fact]
    public void Packs_are_read_once_while_they_do_not_change()
    {
        var root = TempRoot();
        WritePack(Path.Combine(root, "a.zip"), "first");
        WritePack(Path.Combine(root, "b.zip"), "second");

        var cache = new ResourcePackCache();

        var once = cache.ListPacks(root);
        var twice = cache.ListPacks(root);

        Assert.Equal(2, cache.Misses);
        Assert.Equal(new[] { "first", "second" }, twice.Select(p => p.Description));

        // The very same answers: a picture decoded from one is the picture for the other.
        Assert.Same(once[0], twice[0]);
        Assert.Same(once[1], twice[1]);
    }

    [Fact]
    public void A_pack_that_changed_is_read_again_and_the_others_are_not()
    {
        var root = TempRoot();
        var changing = Path.Combine(root, "a.zip");
        WritePack(changing, "old words");
        WritePack(Path.Combine(root, "b.zip"), "untouched");

        var cache = new ResourcePackCache();
        cache.ListPacks(root);

        WritePack(changing, "new words, and longer");
        File.SetLastWriteTimeUtc(changing, DateTime.UtcNow.AddMinutes(5));

        var packs = cache.ListPacks(root);

        Assert.Equal(3, cache.Misses);
        Assert.Equal("new words, and longer", packs.Single(p => p.FileName == "a.zip").Description);
    }

    [Fact]
    public void The_list_is_the_one_the_inspector_gives()
    {
        var root = TempRoot();
        WritePack(Path.Combine(root, "b.zip"), "b");
        Directory.CreateDirectory(Path.Combine(root, "A-folder"));
        File.WriteAllText(Path.Combine(root, "A-folder", "pack.mcmeta"), "{ \"pack\": { \"pack_format\": 46, \"description\": \"a\" } }");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "not a pack");
        Directory.CreateDirectory(Path.Combine(root, ".hidden"));

        var direct = ResourcePackInspector.ListPacks(root);
        var cached = new ResourcePackCache().ListPacks(root);

        Assert.Equal(direct.Select(p => (p.Name, p.IsFolder, p.Description)), cached.Select(p => (p.Name, p.IsFolder, p.Description)));
        Assert.Empty(new ResourcePackCache().ListPacks(Path.Combine(root, "no-such-folder")));
    }

    [Fact]
    public void An_unpacked_pack_keeps_its_counted_size_until_a_recount_is_asked_for()
    {
        var root = TempRoot();
        var folder = Path.Combine(root, "Unpacked");
        var deep = Path.Combine(folder, "assets", "minecraft", "textures");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(folder, "pack.mcmeta"), "{ \"pack\": { \"pack_format\": 46, \"description\": \"x\" } }");
        File.WriteAllBytes(Path.Combine(deep, "stone.png"), new byte[100]);

        var cache = new ResourcePackCache();
        var before = cache.ListPacks(root).Single().Size;

        // A file added deep inside changes neither the folder nor its pack.mcmeta.
        File.WriteAllBytes(Path.Combine(deep, "dirt.png"), new byte[400]);

        Assert.Equal(before, cache.ListPacks(root).Single().Size);
        Assert.Equal(before + 400, cache.ListPacks(root, recountFolders: true).Single().Size);

        // And the recount is what is remembered from then on.
        Assert.Equal(before + 400, cache.ListPacks(root).Single().Size);
    }

    [Fact]
    public void A_pack_that_left_the_folder_is_forgotten()
    {
        var root = TempRoot();
        var path = Path.Combine(root, "a.zip");
        WritePack(path, "here");

        var cache = new ResourcePackCache();
        cache.ListPacks(root);
        File.Delete(path);

        Assert.Empty(cache.ListPacks(root));

        // Back under the same name and, as it happens, the same size and time: read, not remembered.
        WritePack(path, "here");
        cache.ListPacks(root);

        Assert.Equal(2, cache.Misses);
    }

    // ===================== Mod titles and icons =====================

    [Fact]
    public void Mod_titles_and_icons_survive_a_restart()
    {
        var path = Path.Combine(TempRoot(), "cache", "mod-display", "build.json");
        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };

        var first = ModDisplayCache.Load(path);
        first.Set("sodium.jar", new ModDisplayEntry(1000, 42, "Sodium", "0.8.12", icon));
        first.Set("plain-library.jar", new ModDisplayEntry(10, 7, null, null, null));
        first.Save();

        var second = ModDisplayCache.Load(path);

        Assert.True(second.TryGet("sodium.jar", 1000, 42, out var sodium));
        Assert.Equal("Sodium", sodium.Name);
        Assert.Equal("0.8.12", sodium.Version);
        Assert.Equal(icon, sodium.Icon);

        // A jar that says nothing about itself is remembered as saying nothing: not opened again.
        Assert.True(second.TryGet("plain-library.jar", 10, 7, out var plain));
        Assert.Null(plain.Name);
        Assert.Null(plain.Icon);
    }

    [Fact]
    public void A_jar_of_another_size_or_time_is_not_the_one_remembered()
    {
        var path = Path.Combine(TempRoot(), "build.json");
        var cache = ModDisplayCache.Load(path);
        cache.Set("mod.jar", new ModDisplayEntry(1000, 42, "Mod", "1.0", null));

        Assert.True(cache.TryGet("mod.jar", 1000, 42, out _));
        Assert.False(cache.TryGet("mod.jar", 1001, 42, out _));
        Assert.False(cache.TryGet("mod.jar", 1000, 43, out _));
        Assert.False(cache.TryGet("other.jar", 1000, 42, out _));
    }

    [Fact]
    public void Switching_a_mod_off_does_not_make_it_another_mod()
    {
        var path = Path.Combine(TempRoot(), "build.json");
        var cache = ModDisplayCache.Load(path);
        cache.Set("mod.jar", new ModDisplayEntry(1000, 42, "Mod", "1.0", null));

        Assert.True(cache.TryGet("mod.jar.disabled", 1000, 42, out var entry));
        Assert.Equal("Mod", entry.Name);
    }

    [Fact]
    public void Saving_forgets_the_mods_that_left_the_folder()
    {
        var path = Path.Combine(TempRoot(), "build.json");
        var cache = ModDisplayCache.Load(path);
        cache.Set("stays.jar", new ModDisplayEntry(1, 1, "Stays", "1", null));
        cache.Set("goes.jar", new ModDisplayEntry(2, 2, "Goes", "1", null));
        cache.Save(new[] { "stays.jar.disabled" });

        var reloaded = ModDisplayCache.Load(path);

        Assert.Equal(1, reloaded.Count);
        Assert.True(reloaded.TryGet("stays.jar", 1, 1, out _));
    }

    [Fact]
    public void A_damaged_or_foreign_store_is_an_empty_one()
    {
        var root = TempRoot();
        var damaged = Path.Combine(root, "damaged.json");
        var foreign = Path.Combine(root, "foreign.json");
        File.WriteAllText(damaged, "{ \"Revision\": 1, \"Entries\": { \"a.jar\": { \"Size\": ");
        File.WriteAllText(foreign, "{ \"Revision\": 999, \"Entries\": { \"a.jar\": { \"Size\": 1, \"Stamp\": 1, \"Name\": \"A\" } } }");

        Assert.Equal(0, ModDisplayCache.Load(damaged).Count);
        Assert.Equal(0, ModDisplayCache.Load(foreign).Count);
        Assert.Equal(0, ModDisplayCache.Load(Path.Combine(root, "missing.json")).Count);

        // And it can be written over.
        var cache = ModDisplayCache.Load(damaged);
        cache.Set("a.jar", new ModDisplayEntry(1, 1, "A", "1", null));
        cache.Save();

        Assert.Equal(1, ModDisplayCache.Load(damaged).Count);
    }

    [Fact]
    public void Listing_mods_gives_each_its_creation_time_with_its_size()
    {
        var game = TempRoot();
        var mods = Path.Combine(game, "mods");
        Directory.CreateDirectory(mods);
        var jar = Path.Combine(mods, "a.jar");
        File.WriteAllBytes(jar, new byte[123]);

        var listed = new ModManager(new Http.DownloadClient(new HttpClient())).ListMods(game).Single();

        Assert.Equal(123, listed.Size);
        Assert.Equal(File.GetCreationTimeUtc(jar), listed.CreatedUtc);
    }
}
