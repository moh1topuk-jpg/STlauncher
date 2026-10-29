using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>"Prepare in advance": fetched into its own folder, never into the build, and taken from there later without a download.</summary>
public class PreparedFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-prepared-" + Guid.NewGuid().ToString("N"));
    private readonly Server _http = new();

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

    private sealed class Server : HttpMessageHandler
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

    [Fact]
    public async Task A_prepared_file_is_taken_later_without_a_download_and_the_build_is_untouched_until_then()
    {
        var bytes = new byte[50_000];
        new Random(7).NextBytes(bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

        var destination = Path.Combine(_root, "instances", "build", "mods", "sodium.jar");
        var item = new DownloadItem(_http.Serve("sodium.jar", bytes), destination, sha1, bytes.Length);
        var prepared = new PreparedFiles(Path.Combine(_root, "prepared"));
        var downloader = new DownloadClient(new HttpClient(_http), maxAttempts: 1, cache: new VerifiedFileCache()) { Prepared = prepared };

        Assert.True(await prepared.PrepareAsync(downloader, item));
        Assert.False(File.Exists(destination));
        Assert.Equal((1, (long)bytes.Length), prepared.Measure());
        Assert.False(await prepared.PrepareAsync(downloader, item));

        var requests = _http.Requests;
        Assert.True(await downloader.EnsureFileAsync(item));

        Assert.Equal(requests, _http.Requests);
        Assert.Equal(bytes, File.ReadAllBytes(destination));
        Assert.Equal((0, 0L), prepared.Measure());
    }

    [Fact]
    public async Task A_prepared_file_that_changed_since_is_thrown_away_and_fetched()
    {
        var bytes = new byte[10_000];
        new Random(8).NextBytes(bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

        var destination = Path.Combine(_root, "instances", "build", "mods", "iris.jar");
        var item = new DownloadItem(_http.Serve("iris.jar", bytes), destination, sha1, bytes.Length);
        var prepared = new PreparedFiles(Path.Combine(_root, "prepared"));
        var downloader = new DownloadClient(new HttpClient(_http), maxAttempts: 1, cache: new VerifiedFileCache()) { Prepared = prepared };

        await prepared.PrepareAsync(downloader, item);
        File.WriteAllText(prepared.PathFor(item), "damaged");

        Assert.True(await downloader.EnsureFileAsync(item));

        Assert.Equal(bytes, File.ReadAllBytes(destination));
        Assert.False(File.Exists(prepared.PathFor(item)));
    }

    [Fact]
    public void Clear_empties_the_folder()
    {
        var prepared = new PreparedFiles(Path.Combine(_root, "prepared"));
        Directory.CreateDirectory(prepared.Directory);
        File.WriteAllText(Path.Combine(prepared.Directory, "0123"), "x");

        prepared.Clear();

        Assert.Equal((0, 0L), prepared.Measure());
    }
}
