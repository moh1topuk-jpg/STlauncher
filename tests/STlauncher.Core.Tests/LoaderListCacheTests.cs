using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Java;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The loader list used to be fetched on every start, one to two seconds of network each
/// time. It is now kept on disk for twelve hours, and an old copy stands in when the
/// fetch fails.
/// </summary>
public class LoaderListCacheTests
{
    private const string FabricJson = """
        [
          { "loader": { "version": "0.19.5", "stable": true } },
          { "loader": { "version": "0.19.4", "stable": false } }
        ]
        """;

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Answer { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FabricJson)
        };

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Answer());
        }
    }

    private static (LoaderService Service, Handler Handler, string Root) Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        var paths = new LauncherPaths(root);
        var handler = new Handler();
        var http = new HttpClient(handler);
        var downloader = new DownloadClient(http);
        var java = new JavaManager(paths, downloader, http);
        return (new LoaderService(http, paths, downloader, java), handler, root);
    }

    private static string CacheFile(string root) => Path.Combine(root, "meta", "loaders", "fabric-1.21.11.json");

    [Fact]
    public async Task SecondCallWithinTwelveHours_DoesNotTouchTheNetwork()
    {
        var (service, handler, root) = Build();

        var first = await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.11");
        var second = await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.11");

        Assert.Equal(2, first.Count);
        Assert.Equal("0.19.5", second[0].Version);
        Assert.True(second[0].Stable);
        Assert.Equal(1, handler.Calls);
        Assert.True(File.Exists(CacheFile(root)));
    }

    [Fact]
    public async Task OldCopy_IsRefreshedWhenTheNetworkAnswers()
    {
        var (service, handler, root) = Build();
        await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.11");
        File.SetLastWriteTimeUtc(CacheFile(root), DateTime.UtcNow.AddDays(-2));

        await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.11");

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task OldCopy_StandsInWhenTheFetchFails()
    {
        var (service, handler, root) = Build();
        await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.11");
        File.SetLastWriteTimeUtc(CacheFile(root), DateTime.UtcNow.AddDays(-2));
        handler.Answer = () => throw new HttpRequestException("offline");

        var stale = await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.11");

        Assert.Equal(2, stale.Count);
    }

    [Fact]
    public async Task NothingCachedAndOffline_GivesAnEmptyList()
    {
        var (service, handler, _) = Build();
        handler.Answer = () => throw new HttpRequestException("offline");

        var result = await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.11");

        Assert.Empty(result);
    }
}
