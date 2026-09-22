using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core;
using STlauncher.Core.Content;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>The catalog fetch races the main address against the mirrors.</summary>
public class CatalogRaceTests
{
    private const string Catalog = """{ "schemaVersion": 1, "name": "Race", "sections": [] }""";

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<Uri, CancellationToken, Task<HttpResponseMessage>> _respond;

        public Handler(Func<Uri, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request.RequestUri!, cancellationToken);
    }

    private static ContentCatalogService Service(Func<Uri, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        var paths = new LauncherPaths(root);
        paths.EnsureCreated();

        return new ContentCatalogService(new HttpClient(new Handler(respond)), paths)
        {
            CatalogUrl = "https://blocked.example/catalog.json",
            FallbackUrls = new[] { "https://mirror.example/catalog.json" },
            RequestTimeout = TimeSpan.FromSeconds(2),
            MirrorDelay = TimeSpan.FromMilliseconds(200)
        };
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(Catalog) };

    [Fact]
    public async Task MainAddressWins_WhenItAnswers()
    {
        var mirrorAsked = 0;

        var service = Service((uri, _) =>
        {
            if (uri.Host == "mirror.example")
            {
                Interlocked.Increment(ref mirrorAsked);
            }

            return Task.FromResult(Ok());
        });

        var result = await service.LoadAsync();

        Assert.Equal(CatalogOrigin.Remote, result.Origin);
        Assert.Equal("Race", result.Catalog!.Name);
        Assert.Equal(0, mirrorAsked);
    }

    [Fact]
    public async Task Mirror_AnswersWhileTheMainAddressHangs()
    {
        var service = Service(async (uri, token) =>
        {
            if (uri.Host == "blocked.example")
            {
                // A blackholed host: nothing ever comes back.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return Ok();
        });

        var clock = Stopwatch.StartNew();
        var result = await service.LoadAsync();

        Assert.Equal(CatalogOrigin.Remote, result.Origin);
        Assert.Null(result.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task AllAddressesDown_FallsBackToTheCache_WithEveryHostNamed()
    {
        var service = Service((uri, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        Directory.CreateDirectory(Path.GetDirectoryName(service.CachePath)!);
        File.WriteAllText(service.CachePath, Catalog);

        var result = await service.LoadAsync();

        Assert.Equal(CatalogOrigin.Cache, result.Origin);
        Assert.Contains("blocked.example", result.Error);
        Assert.Contains("mirror.example", result.Error);
    }
}
