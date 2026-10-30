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
/// A player whose provider cut meta.fabricmc.net could not start an installed build: the
/// loader profile was fetched on every launch. A profile already on disk is now used as it is.
/// </summary>
public class LoaderProfileOfflineTests
{
    private const string Profile = """
        { "id": "fabric-loader-0.19.5-1.21.11", "inheritsFrom": "1.21.11", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient" }
        """;

    private sealed class Handler : HttpMessageHandler
    {
        public bool Cut { get; set; }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            if (Cut)
            {
                throw new HttpRequestException("The SSL connection could not be established.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Profile) });
        }
    }

    private static (LoaderService Service, Handler Handler, LauncherPaths Paths) Build()
    {
        var paths = new LauncherPaths(Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N")));
        var handler = new Handler();
        var http = new HttpClient(handler);
        var downloader = new DownloadClient(http);
        return (new LoaderService(http, paths, downloader, new JavaManager(paths, downloader, http)), handler, paths);
    }

    [Fact]
    public async Task InstalledProfile_StartsWithTheSiteCut()
    {
        var (service, handler, _) = Build();
        Assert.Equal("fabric-loader-0.19.5-1.21.11", await service.InstallAsync(LoaderKind.Fabric, "1.21.11", "0.19.5"));

        handler.Cut = true;
        var calls = handler.Calls;

        Assert.Equal("fabric-loader-0.19.5-1.21.11", await service.InstallAsync(LoaderKind.Fabric, "1.21.11", "0.19.5"));
        Assert.Equal(calls, handler.Calls);
    }

    [Fact]
    public async Task BrokenProfile_IsFetchedAgain()
    {
        var (service, handler, paths) = Build();
        Directory.CreateDirectory(paths.VersionDirectory("fabric-loader-0.19.5-1.21.11"));
        File.WriteAllText(paths.VersionJsonPath("fabric-loader-0.19.5-1.21.11"), "{ \"id\": \"fabric-lo");

        Assert.Equal("fabric-loader-0.19.5-1.21.11", await service.InstallAsync(LoaderKind.Fabric, "1.21.11", "0.19.5"));
        Assert.Equal(1, handler.Calls);
        Assert.Contains("inheritsFrom", File.ReadAllText(paths.VersionJsonPath("fabric-loader-0.19.5-1.21.11")));
    }
}
