using System;
using System.IO;
using System.Security.Cryptography;
using STlauncher.Core;
using STlauncher.Core.Http;
using Xunit;

namespace STlauncher.Core.Tests;

public class VerifiedFileCacheTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Sha1Of(string path)
        => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void UnchangedFile_IsVerifiedWithoutHashing()
    {
        var root = TempRoot();
        var file = Path.Combine(root, "a.jar");
        File.WriteAllText(file, "content");
        var hash = Sha1Of(file);

        var cache = new VerifiedFileCache();
        Assert.False(cache.IsVerified(file, hash, new FileInfo(file)));

        cache.Remember(file, hash, new FileInfo(file));

        Assert.True(cache.IsVerified(file, hash, new FileInfo(file)));
        Assert.False(cache.IsVerified(file, "0000", new FileInfo(file)));
    }

    [Fact]
    public void ChangedFile_IsNotVerified()
    {
        var root = TempRoot();
        var file = Path.Combine(root, "a.jar");
        File.WriteAllText(file, "content");
        var cache = new VerifiedFileCache();
        cache.Remember(file, Sha1Of(file), new FileInfo(file));

        File.WriteAllText(file, "content!");

        Assert.False(cache.IsVerified(file, Sha1Of(file), new FileInfo(file)));
    }

    [Fact]
    public void Cache_SurvivesARestart()
    {
        var root = TempRoot();
        var paths = new LauncherPaths(root);
        var file = Path.Combine(root, "a.jar");
        File.WriteAllText(file, "content");
        var hash = Sha1Of(file);

        var first = new VerifiedFileCache(paths);
        first.Remember(file, hash, new FileInfo(file));
        first.Save();

        var second = new VerifiedFileCache(paths);

        Assert.True(second.IsVerified(file, hash, new FileInfo(file)));

        second.Clear();
        Assert.False(new VerifiedFileCache(paths).IsVerified(file, hash, new FileInfo(file)));
    }

    [Fact]
    public async System.Threading.Tasks.Task DownloadClient_HashesOnce_ThenTrustsTheCache()
    {
        var root = TempRoot();
        var file = Path.Combine(root, "obj");
        File.WriteAllText(file, "asset");
        var hash = Sha1Of(file);
        var cache = new VerifiedFileCache();

        var client = new DownloadClient(new System.Net.Http.HttpClient(), cache: cache);
        var item = new DownloadItem("http://127.0.0.1:9/never", file, hash, new FileInfo(file).Length);

        // Present and correct: nothing is downloaded, and the verdict is remembered.
        Assert.False(await client.EnsureFileAsync(item));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.IsVerified(file, hash, new FileInfo(file)));

        // Tampered file with the same size and time is caught by the size check or the
        // hash; here the size differs, so the cache entry is dropped and a download is
        // attempted - which fails against a closed port, as it should.
        File.WriteAllText(file, "asset!!");
        await Assert.ThrowsAnyAsync<Exception>(() => client.EnsureFileAsync(item));
        Assert.False(cache.IsVerified(file, hash, new FileInfo(file)));
    }
}
