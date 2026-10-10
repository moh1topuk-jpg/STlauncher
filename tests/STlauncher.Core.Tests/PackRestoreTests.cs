using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// What the "bring back the previous version" entry of the resource pack and shader lists
/// stands on: an updated pack has a kept file to go back to, found by the pack's folder
/// and name, and a pack that was never updated has none - so its row gets no menu.
/// </summary>
public class PackRestoreTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "stl-packrestore-" + Guid.NewGuid().ToString("N"));
    private readonly Files _http = new();

    public PackRestoreTests()
    {
        Directory.CreateDirectory(_game);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_game, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private sealed class Files : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public string Serve(string name, byte[] bytes)
        {
            var url = "https://files.test/" + name;
            _files[url] = bytes;
            return url;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_files.TryGetValue(request.RequestUri!.ToString(), out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static byte[] Content(int seed)
    {
        var bytes = new byte[20_000];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha1Of(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    [Theory]
    [InlineData("resourcepacks", "Faithful-1.2.zip", "Faithful-1.3.zip")]
    [InlineData("shaderpacks", "ComplementaryReimagined_r5.3.zip", "ComplementaryReimagined_r5.4.zip")]
    [InlineData("resourcepacks", "SamePack.zip", "SamePack.zip")]
    public async Task UpdatedPack_HasAPreviousVersion_AndItComesBack(string folder, string oldName, string newName)
    {
        var directory = Path.Combine(_game, folder);
        Directory.CreateDirectory(directory);

        var old = Content(1);
        var fresh = Content(2);
        var oldPath = Path.Combine(directory, oldName);
        File.WriteAllBytes(oldPath, old);
        File.WriteAllBytes(Path.Combine(directory, "Untouched.zip"), Content(3));

        var replacer = new ModFileReplacer(new DownloadClient(new HttpClient(_http), maxAttempts: 1));

        await replacer.ReplaceAsync(new ModReplaceRequest(_game, folder, oldPath, newName, _http.Serve(newName, fresh), Sha1Of(fresh), fresh.Length)
        {
            Key = "pack",
            OldLabel = "1.2"
        });

        // The entry is offered on the updated pack, and names what comes back...
        var previous = replacer.FindPrevious(_game, folder, newName);
        Assert.NotNull(previous);
        Assert.Equal("1.2", previous!.Label);
        Assert.Equal(oldName, previous.FileName);

        // ...and nowhere else: not on a pack that was never updated, not on a file of
        // the same name in another list.
        Assert.Null(replacer.FindPrevious(_game, folder, "Untouched.zip"));
        Assert.Null(replacer.FindPrevious(_game, "mods", newName));

        var result = replacer.Restore(_game, previous, "1.3");

        Assert.Equal(oldName, result.NewFileName);
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(directory, oldName)));

        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(File.Exists(Path.Combine(directory, newName)));
        }

        // The newer file is kept in its turn, so the step can be taken back the same way.
        var newer = replacer.FindPrevious(_game, folder, oldName);
        Assert.NotNull(newer);
        Assert.Equal("1.3", newer!.Label);
        Assert.Equal(fresh, File.ReadAllBytes(newer.StoredPath));
    }
}
