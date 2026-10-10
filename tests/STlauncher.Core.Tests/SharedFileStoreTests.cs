using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Storage;
using Xunit;

namespace STlauncher.Core.Tests;

public sealed class SharedFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-shared-" + Guid.NewGuid().ToString("N"));
    private readonly string _objects;
    private readonly string _instances;

    public SharedFileStoreTests()
    {
        _objects = Path.Combine(_root, "objects");
        _instances = Path.Combine(_root, "instances");
        Directory.CreateDirectory(_instances);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private SharedFileStore Store(Func<string, string, bool>? link = null, Func<string, FileIdentity?>? identity = null)
        => new(_objects, _instances, link, identity);

    private string InBuild(string build, string folder, string name)
        => Path.Combine(_instances, build, folder, name);

    /// <summary>Bytes that differ per seed, comfortably above the size floor unless asked otherwise.</summary>
    private static byte[] Content(int seed, int size = 100_000)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha1Of(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static string Put(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Two names of one file: writing through one shows in the other.</summary>
    private static void AssertLinked(string a, string b)
    {
        if (HardLink.TryGetIdentity(a) is { } first && HardLink.TryGetIdentity(b) is { } second)
        {
            Assert.True(first.IsSameFile(second), $"{a} and {b} are separate files");
        }
    }

    private static void AssertNotLinked(string a, string b)
    {
        if (HardLink.TryGetIdentity(a) is { } first && HardLink.TryGetIdentity(b) is { } second)
        {
            Assert.False(first.IsSameFile(second), $"{a} and {b} are one file");
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public int Requests { get; private set; }

        public string Serve(string name, byte[] bytes)
        {
            var url = "https://files.test/" + name;
            _files[url] = bytes;
            return url;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            return Task.FromResult(_files.TryGetValue(request.RequestUri!.ToString(), out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // ---------- which files are shared ----------

    [Theory]
    [InlineData("mods", "sodium.jar", true)]
    [InlineData("mods", "sodium.jar.disabled", true)]
    [InlineData("resourcepacks", "faithful.zip", true)]
    [InlineData("shaderpacks", "bsl.zip", true)]
    [InlineData("datapacks", "terralith.zip", true)]
    [InlineData("config", "sodium.jar", false)]
    [InlineData("saves", "world.zip", false)]
    [InlineData("logs", "latest.zip", false)]
    [InlineData("screenshots", "shot.zip", false)]
    [InlineData("mods", "notes.txt", false)]
    [InlineData("mods", "sodium.jar.3f2a.part", false)]
    [InlineData("shaderpacks", "bsl.zip.txt", false)]
    public void Only_jars_and_zips_in_the_four_folders_are_shareable(string folder, string name, bool expected)
    {
        Assert.Equal(expected, Store().IsShareable(InBuild("a", folder, name)));
    }

    [Fact]
    public void Worlds_nested_folders_and_outside_builds_are_never_shareable()
    {
        var store = Store();

        Assert.False(store.IsShareable(Path.Combine(_instances, "a", "saves", "world", "datapacks", "pack.zip")));
        Assert.False(store.IsShareable(Path.Combine(_instances, "a", "mods", "nested", "lib.jar")));
        Assert.False(store.IsShareable(Path.Combine(_instances, "a", "resourcepacks", "unpacked", "pack.png")));
        Assert.False(store.IsShareable(Path.Combine(_root, "external", "mods", "sodium.jar")));
        Assert.False(store.IsShareable(Path.Combine(_instances + "-other", "a", "mods", "sodium.jar")));
        Assert.False(store.IsShareable(Path.Combine(_root, "libraries", "mods", "x", "sodium.jar")));
    }

    [Fact]
    public void A_hash_that_is_not_a_sha1_never_becomes_a_path()
    {
        var store = Store();

        Assert.Null(store.ObjectPath(null));
        Assert.Null(store.ObjectPath("abc"));
        Assert.Null(store.ObjectPath("../../../../../../../../../windows/system32"));
        Assert.Null(store.ObjectPath(new string('g', 40)));

        var sha1 = new string('A', 38) + "b1";
        Assert.Equal(Path.Combine(_objects, "aa", sha1.ToLowerInvariant()), store.ObjectPath(sha1));
    }

    // ---------- adopt ----------

    [Fact]
    public void Adopt_puts_the_file_into_the_store_as_a_second_name()
    {
        var bytes = Content(1);
        var path = Put(InBuild("a", "mods", "sodium.jar"), bytes);
        var store = Store();

        store.Adopt(path);

        var stored = store.ObjectPath(Sha1Of(bytes))!;
        Assert.True(File.Exists(stored));
        Assert.Equal(bytes, File.ReadAllBytes(stored));
        AssertLinked(path, stored);
    }

    [Fact]
    public void Files_under_the_size_floor_stay_out_of_the_store()
    {
        var small = Content(2, (int)SharedFileStore.MinimumSize - 1);
        var exact = Content(3, (int)SharedFileStore.MinimumSize);
        var store = Store();

        store.Adopt(Put(InBuild("a", "mods", "tiny.jar"), small));
        store.Adopt(Put(InBuild("a", "mods", "floor.jar"), exact));

        Assert.False(File.Exists(store.ObjectPath(Sha1Of(small))));
        Assert.True(File.Exists(store.ObjectPath(Sha1Of(exact))));
    }

    [Fact]
    public void Files_outside_the_shared_folders_stay_out_of_the_store()
    {
        var bytes = Content(4);
        var store = Store();

        store.Adopt(Put(InBuild("a", "config", "big.jar"), bytes));
        store.Adopt(Put(Path.Combine(_instances, "a", "saves", "world", "datapacks", "pack.zip"), bytes));

        Assert.False(Directory.Exists(_objects));
    }

    [Fact]
    public void Switched_off_the_store_neither_takes_nor_gives()
    {
        var bytes = Content(5);
        var store = Store();
        store.Adopt(Put(InBuild("a", "mods", "sodium.jar"), bytes));

        store.Enabled = false;
        var other = Content(6);
        store.Adopt(Put(InBuild("a", "mods", "iris.jar"), other));

        Assert.False(File.Exists(store.ObjectPath(Sha1Of(other))));
        Assert.False(store.TryLinkInto(Sha1Of(bytes), InBuild("b", "mods", "sodium.jar")));
        Assert.False(File.Exists(InBuild("b", "mods", "sodium.jar")));
    }

    // ---------- downloads ----------

    [Fact]
    public async Task The_second_build_links_instead_of_downloading()
    {
        var bytes = Content(10);
        var handler = new CountingHandler();
        var url = handler.Serve("sodium.jar", bytes);
        var store = Store();
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1, sharedFiles: store);

        var first = InBuild("a", "mods", "sodium.jar");
        var second = InBuild("b", "mods", "sodium-renamed.jar");

        Assert.True(await client.EnsureFileAsync(new DownloadItem(url, first, Sha1Of(bytes), bytes.Length)));
        Assert.Equal(1, handler.Requests);

        Assert.True(await client.EnsureFileAsync(new DownloadItem(url, second, Sha1Of(bytes), bytes.Length)));
        Assert.Equal(1, handler.Requests);

        Assert.Equal(bytes, File.ReadAllBytes(second));
        AssertLinked(first, second);

        // Present and verified: nothing to do, as before.
        Assert.False(await client.EnsureFileAsync(new DownloadItem(url, second, Sha1Of(bytes), bytes.Length)));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_stronger_hash_is_what_the_stored_object_is_checked_against()
    {
        var bytes = Content(11);
        var sha512 = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
        var handler = new CountingHandler();
        var url = handler.Serve("iris.jar", bytes);
        var store = Store();
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1, sharedFiles: store);

        await client.EnsureFileAsync(new DownloadItem(url, InBuild("a", "mods", "iris.jar"), Sha1Of(bytes), bytes.Length, Sha512: sha512));

        // The SHA-512 was the hash checked, so the SHA-1 was computed, not believed.
        Assert.True(File.Exists(store.ObjectPath(Sha1Of(bytes))));

        // The right SHA-1 with a SHA-512 the object does not have: no link, a download.
        var wrong = new string('0', 128);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.EnsureFileAsync(new DownloadItem(url, InBuild("b", "mods", "iris.jar"), Sha1Of(bytes), bytes.Length, Sha512: wrong)));

        Assert.Equal(2, handler.Requests);
        Assert.False(File.Exists(InBuild("b", "mods", "iris.jar")));
    }

    [Fact]
    public async Task An_object_that_no_longer_matches_its_name_is_dropped_and_the_file_downloaded()
    {
        var bytes = Content(12);
        var handler = new CountingHandler();
        var url = handler.Serve("sodium.jar", bytes);
        var store = Store();
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1, sharedFiles: store);

        var first = InBuild("a", "mods", "sodium.jar");
        var second = InBuild("b", "mods", "sodium.jar");
        await client.EnsureFileAsync(new DownloadItem(url, first, Sha1Of(bytes), bytes.Length));

        // Something outside the launcher writes into build A's jar. It is the same file
        // as the object, so the object changes with it.
        var stored = store.ObjectPath(Sha1Of(bytes))!;
        using (var stream = new FileStream(first, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(new byte[] { 1, 2, 3, 4 });
        }

        Assert.NotEqual(bytes, File.ReadAllBytes(stored));

        Assert.True(await client.EnsureFileAsync(new DownloadItem(url, second, Sha1Of(bytes), bytes.Length)));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(bytes, File.ReadAllBytes(second));

        // The fresh download is the object now; the damaged file is nobody's but build A's.
        Assert.Equal(bytes, File.ReadAllBytes(stored));
        AssertLinked(second, stored);
        AssertNotLinked(first, stored);
    }

    [Fact]
    public async Task Where_the_disk_cannot_link_everything_works_with_plain_files()
    {
        var bytes = Content(13);
        var handler = new CountingHandler();
        var url = handler.Serve("sodium.jar", bytes);
        var store = Store(link: (_, _) => false);
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1, sharedFiles: store);

        var first = InBuild("a", "mods", "sodium.jar");
        var second = InBuild("b", "mods", "sodium.jar");

        Assert.True(await client.EnsureFileAsync(new DownloadItem(url, first, Sha1Of(bytes), bytes.Length)));
        Assert.True(await client.EnsureFileAsync(new DownloadItem(url, second, Sha1Of(bytes), bytes.Length)));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(bytes, File.ReadAllBytes(first));
        Assert.Equal(bytes, File.ReadAllBytes(second));
        Assert.False(File.Exists(store.ObjectPath(Sha1Of(bytes))));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(second)!, "*.part"));
    }

    [Fact]
    public async Task A_link_that_fails_only_for_the_build_falls_back_to_a_download()
    {
        var bytes = Content(14);
        var handler = new CountingHandler();
        var url = handler.Serve("sodium.jar", bytes);

        // Into the store works, out of it does not: a build on another volume.
        var store = Store(link: (existing, created) => created.StartsWith(_objects, StringComparison.OrdinalIgnoreCase) && HardLink.TryCreate(existing, created));
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1, sharedFiles: store);

        await client.EnsureFileAsync(new DownloadItem(url, InBuild("a", "mods", "sodium.jar"), Sha1Of(bytes), bytes.Length));
        await client.EnsureFileAsync(new DownloadItem(url, InBuild("b", "mods", "sodium.jar"), Sha1Of(bytes), bytes.Length));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(bytes, File.ReadAllBytes(InBuild("b", "mods", "sodium.jar")));
    }

    // ---------- replacing never writes through ----------

    private async Task<(string First, string Second, byte[] Bytes, DownloadClient Client, CountingHandler Handler)> TwoLinkedBuildsAsync()
    {
        var bytes = Content(20);
        var handler = new CountingHandler();
        var url = handler.Serve("sodium.jar", bytes);
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1, sharedFiles: Store());

        var first = InBuild("a", "mods", "sodium.jar");
        var second = InBuild("b", "mods", "sodium.jar");
        await client.EnsureFileAsync(new DownloadItem(url, first, Sha1Of(bytes), bytes.Length));
        await client.EnsureFileAsync(new DownloadItem(url, second, Sha1Of(bytes), bytes.Length));
        Assert.Equal(1, handler.Requests);

        return (first, second, bytes, client, handler);
    }

    [Fact]
    public async Task Dropping_a_local_file_over_a_shared_one_leaves_the_other_build_alone()
    {
        var (first, second, bytes, _, _) = await TwoLinkedBuildsAsync();
        var replacement = Content(21, 120_000);
        var dropped = Put(Path.Combine(_root, "downloads", "sodium.jar"), replacement);

        FileReplace.Copy(dropped, first);

        Assert.Equal(replacement, File.ReadAllBytes(first));
        Assert.Equal(bytes, File.ReadAllBytes(second));
        Assert.Equal(bytes, File.ReadAllBytes(Store().ObjectPath(Sha1Of(bytes))!));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(first)!, "*.part"));
    }

    [Fact]
    public async Task Downloading_another_version_under_the_same_name_leaves_the_other_build_alone()
    {
        var (first, second, bytes, client, handler) = await TwoLinkedBuildsAsync();
        var update = Content(22, 130_000);
        var url = handler.Serve("sodium-new.jar", update);

        Assert.True(await client.EnsureFileAsync(new DownloadItem(url, first, Sha1Of(update), update.Length)));

        Assert.Equal(update, File.ReadAllBytes(first));
        Assert.Equal(bytes, File.ReadAllBytes(second));
        Assert.Equal(bytes, File.ReadAllBytes(Store().ObjectPath(Sha1Of(bytes))!));
    }

    [Fact]
    public async Task Modpack_overrides_over_a_shared_file_leave_the_other_build_alone()
    {
        var (first, second, bytes, _, _) = await TwoLinkedBuildsAsync();
        var bundled = Content(23, 90_000);
        var pack = Path.Combine(_root, "pack.mrpack");

        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("overrides/mods/sodium.jar").Open();
            entry.Write(bundled);
        }

        ModpackInstaller.ExtractOverrides(pack, Path.Combine(_instances, "a"), new[] { "overrides/" });

        Assert.Equal(bundled, File.ReadAllBytes(first));
        Assert.Equal(bytes, File.ReadAllBytes(second));
    }

    [Fact]
    public async Task Deleting_one_builds_file_leaves_the_other_and_the_object()
    {
        var (first, second, bytes, _, _) = await TwoLinkedBuildsAsync();

        File.Delete(first);
        Directory.Delete(Path.Combine(_instances, "a"), recursive: true);

        Assert.Equal(bytes, File.ReadAllBytes(second));
        Assert.Equal(bytes, File.ReadAllBytes(Store().ObjectPath(Sha1Of(bytes))!));
    }

    // ---------- the clean-up ----------

    public static IEnumerable<object[]> IdentitySources()
    {
        yield return new object[] { true };
        yield return new object[] { false };
    }

    /// <param name="systemTellsFiles">False stands for a system that cannot say which names are one file.</param>
    [Theory]
    [MemberData(nameof(IdentitySources))]
    public void Cleanup_links_identical_files_and_removes_only_what_no_build_uses(bool systemTellsFiles)
    {
        var store = systemTellsFiles ? Store() : Store(identity: _ => null);

        // Installed before the store existed: the same pack in three builds, as three files.
        var shared = Content(30);
        var a = Put(InBuild("a", "resourcepacks", "faithful.zip"), shared);
        var b = Put(InBuild("b", "resourcepacks", "faithful.zip"), shared);
        var c = Put(InBuild("c", "resourcepacks", "faithful.zip.disabled"), shared);

        // Same size, other contents: must not be mistaken for a twin.
        var lookalike = Content(31);
        var d = Put(InBuild("d", "resourcepacks", "faithful.zip"), lookalike);

        // In one build only.
        var unique = Content(32, 150_000);
        var e = Put(InBuild("a", "mods", "unique.jar"), unique);

        // Already shared the proper way.
        var linked = Content(33, 110_000);
        var f = Put(InBuild("a", "mods", "iris.jar"), linked);
        store.Adopt(f);

        // Never shared, whatever they hold.
        var world = Put(Path.Combine(_instances, "a", "saves", "world", "region.zip"), shared);
        var config = Put(InBuild("b", "config", "faithful.zip"), shared);

        // An object whose builds are gone.
        var orphan = Content(34, 140_000);
        var orphanPath = Put(store.ObjectPath(Sha1Of(orphan))!, orphan);

        // Not the store's file: left alone.
        var stranger = Put(Path.Combine(_objects, "readme.txt"), new byte[] { 1 });

        var result = store.Optimize();

        var sharedObject = store.ObjectPath(Sha1Of(shared))!;
        Assert.True(File.Exists(sharedObject));
        AssertLinked(a, sharedObject);
        AssertLinked(b, sharedObject);
        AssertLinked(c, sharedObject);

        foreach (var path in new[] { a, b, c, world, config })
        {
            Assert.Equal(shared, File.ReadAllBytes(path));
        }

        AssertNotLinked(world, sharedObject);
        AssertNotLinked(config, sharedObject);
        Assert.Equal(lookalike, File.ReadAllBytes(d));
        Assert.Equal(unique, File.ReadAllBytes(e));
        Assert.Equal(linked, File.ReadAllBytes(f));

        // Without identities a file already linked is linked once more, which changes
        // nothing on disk but is counted; the exact numbers hold where the system tells.
        var exact = systemTellsFiles && HardLink.TryGetIdentity(a) is not null;

        if (exact)
        {
            Assert.Equal(2, result.LinkedFiles);
            Assert.Equal(2L * shared.Length, result.LinkedBytes);
        }
        else
        {
            Assert.InRange(result.LinkedFiles, 2, 3);
        }

        Assert.Equal(0, result.SkippedFiles);

        Assert.False(File.Exists(orphanPath));
        Assert.Equal(1, result.RemovedObjects);
        Assert.Equal(orphan.Length, result.RemovedBytes);

        Assert.True(File.Exists(store.ObjectPath(Sha1Of(linked))));
        Assert.False(File.Exists(store.ObjectPath(Sha1Of(unique))));
        Assert.False(File.Exists(store.ObjectPath(Sha1Of(lookalike))));
        Assert.True(File.Exists(stranger));
        Assert.Empty(Directory.EnumerateFiles(_instances, "*.part", SearchOption.AllDirectories));

        // A second run finds nothing left to do and removes nothing a build uses.
        var again = store.Optimize();

        if (exact)
        {
            Assert.Equal(0, again.LinkedFiles);
        }

        Assert.Equal(0, again.RemovedObjects);
        Assert.Equal(0, again.SkippedFiles);
        Assert.True(File.Exists(sharedObject));
        Assert.True(File.Exists(store.ObjectPath(Sha1Of(linked))));
        Assert.Equal(shared, File.ReadAllBytes(c));
        Assert.Empty(Directory.EnumerateFiles(_instances, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public void Cleanup_links_a_separate_copy_to_the_object_another_build_already_uses()
    {
        var store = Store();
        var bytes = Content(40);
        var a = Put(InBuild("a", "mods", "sodium.jar"), bytes);
        store.Adopt(a);
        var b = Put(InBuild("b", "mods", "sodium.jar"), bytes);

        var result = store.Optimize();

        Assert.Equal(HardLink.TryGetIdentity(a) is null ? 2 : 1, result.LinkedFiles);
        Assert.Equal(0, result.RemovedObjects);
        AssertLinked(a, b);
        Assert.Equal(bytes, File.ReadAllBytes(b));
    }

    [Fact]
    public void Cleanup_never_links_a_build_to_a_damaged_object()
    {
        var store = Store();
        var bytes = Content(41);
        var stored = Put(store.ObjectPath(Sha1Of(bytes))!, Content(42));
        var a = Put(InBuild("a", "mods", "sodium.jar"), bytes);
        var b = Put(InBuild("b", "mods", "sodium.jar"), bytes);

        store.Optimize();

        Assert.Equal(bytes, File.ReadAllBytes(a));
        Assert.Equal(bytes, File.ReadAllBytes(b));
        Assert.Equal(bytes, File.ReadAllBytes(stored));
    }

    [Fact]
    public void Cleanup_where_the_disk_cannot_link_changes_no_build_and_empties_the_store()
    {
        var bytes = Content(43);
        var a = Put(InBuild("a", "mods", "sodium.jar"), bytes);
        var b = Put(InBuild("b", "mods", "sodium.jar"), bytes);

        // What a move to a FAT disk leaves: an object that is a full copy nobody links to.
        var store = Store(link: (_, _) => false);
        var stored = Put(store.ObjectPath(Sha1Of(bytes))!, bytes);

        var result = store.Optimize();

        Assert.Equal(0, result.LinkedFiles);
        Assert.Equal(2, result.SkippedFiles);
        Assert.Equal(1, result.RemovedObjects);
        Assert.False(File.Exists(stored));
        Assert.Equal(bytes, File.ReadAllBytes(a));
        Assert.Equal(bytes, File.ReadAllBytes(b));
    }

    [Fact]
    public void A_cancelled_cleanup_leaves_every_file_as_it_was()
    {
        var store = Store();
        var bytes = Content(44);
        var a = Put(InBuild("a", "mods", "sodium.jar"), bytes);
        var b = Put(InBuild("b", "mods", "sodium.jar"), bytes);
        var orphan = Put(store.ObjectPath(Sha1Of(Content(45)))!, Content(45));

        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress(p =>
        {
            if (p.Stage == SharedStoreStage.Comparing)
            {
                cancel.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => store.Optimize(progress, cancel.Token));

        Assert.Equal(bytes, File.ReadAllBytes(a));
        Assert.Equal(bytes, File.ReadAllBytes(b));
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public void Usage_counts_what_the_builds_would_take_on_their_own()
    {
        var store = Store();
        var bytes = Content(46);

        foreach (var build in new[] { "a", "b", "c" })
        {
            Put(InBuild(build, "mods", "sodium.jar"), bytes);
        }

        var orphan = Content(47, 70_000);
        Put(store.ObjectPath(Sha1Of(orphan))!, orphan);
        store.Optimize(); // links the three, removes the orphan
        Put(store.ObjectPath(Sha1Of(orphan))!, orphan);

        var usage = store.Measure();

        Assert.Equal(2, usage.Objects);
        Assert.Equal(bytes.Length + orphan.Length, usage.StoreBytes);

        if (HardLink.TryGetIdentity(InBuild("a", "mods", "sodium.jar")) is not null)
        {
            Assert.Equal(2L * bytes.Length, usage.SavedBytes);
            Assert.Equal(orphan.Length, usage.UnusedBytes);
        }

        // A system that does not tell how many names a file has gets no guess.
        var blind = Store(identity: _ => null).Measure();
        Assert.Equal(2, blind.Objects);
        Assert.Null(blind.SavedBytes);
    }

    // ---------- moving the data folder ----------

    [Fact]
    public void Moving_the_data_keeps_shared_files_shared()
    {
        var store = Store();
        var bytes = Content(50);
        var a = Put(InBuild("a", "mods", "sodium.jar"), bytes);
        store.Adopt(a);
        Assert.True(store.TryLinkInto(Sha1Of(bytes), InBuild("b", "mods", "sodium.jar")));

        var target = _root + "-moved";

        try
        {
            var result = DataDirectoryMover.Move(_root, target);

            var movedA = Path.Combine(target, "instances", "a", "mods", "sodium.jar");
            var movedB = Path.Combine(target, "instances", "b", "mods", "sodium.jar");
            var movedObject = new SharedFileStore(Path.Combine(target, "objects"), Path.Combine(target, "instances")).ObjectPath(Sha1Of(bytes))!;

            Assert.Equal(3, result.Files);
            Assert.Equal(bytes, File.ReadAllBytes(movedA));
            Assert.Equal(bytes, File.ReadAllBytes(movedB));
            Assert.Equal(bytes, File.ReadAllBytes(movedObject));
            AssertLinked(movedA, movedB);
            AssertLinked(movedA, movedObject);

            if (HardLink.TryGetIdentity(movedA) is not null)
            {
                // Three names, one file's worth of bytes carried.
                Assert.Equal(bytes.Length, result.Bytes);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class ImmediateProgress : IProgress<SharedStoreProgress>
    {
        private readonly Action<SharedStoreProgress> _report;

        public ImmediateProgress(Action<SharedStoreProgress> report) => _report = report;

        public void Report(SharedStoreProgress value) => _report(value);
    }
}
