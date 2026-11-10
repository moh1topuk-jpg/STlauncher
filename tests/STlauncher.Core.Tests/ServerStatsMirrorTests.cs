using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Server;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Players whose provider cuts workers.dev saw "the collector is unavailable": the same
/// collector now also comes through the owner's server under /stats/.
/// </summary>
public sealed class ServerStatsMirrorTests : IDisposable
{
    private const string Collector = "https://showtime-stats.moh1topuk.workers.dev/";
    private const string Mirror = "https://2.26.80.201/";

    private readonly string _root = Directory.CreateTempSubdirectory("stats-mirror-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("https://showtime-stats.moh1topuk.workers.dev/", "https://2.26.80.201/stats/")]
    [InlineData("https://showtime-stats.moh1topuk.workers.dev", "https://2.26.80.201/stats/")]
    [InlineData("https://SHOWTIME-STATS.moh1topuk.workers.dev/", "https://2.26.80.201/stats/")]
    public void MirrorOf_TheCollector_IsUnderStats(string statsUrl, string expected)
        => Assert.Equal(expected, ServerStatsClient.MirrorOf(statsUrl, Collector.TrimEnd('/'), Mirror));

    [Theory]
    [InlineData("https://other-collector.example/")]
    [InlineData("")]
    [InlineData(null)]
    public void MirrorOf_AnotherCollector_HasNone(string? statsUrl)
        => Assert.Null(ServerStatsClient.MirrorOf(statsUrl, Collector, Mirror));

    [Fact]
    public async Task Load_CollectorCut_ComesThroughTheMirror()
    {
        var asked = new List<string>();
        var client = new ServerStatsClient(new HttpClient(new Answer(asked, url => url.StartsWith(Mirror))), new LauncherPaths(_root))
        {
            StatsUrl = Collector,
            FallbackUrl = Mirror + "stats/"
        };

        var result = await client.LoadAsync();

        Assert.Equal(ServerStatsOrigin.Remote, result.Origin);
        Assert.Equal(new[] { Collector, Mirror + "stats/" }, asked);
    }

    [Fact]
    public async Task Load_CollectorAnswering_DoesNotAskTheMirror()
    {
        var asked = new List<string>();
        var client = new ServerStatsClient(new HttpClient(new Answer(asked, _ => true)), new LauncherPaths(_root))
        {
            StatsUrl = Collector,
            FallbackUrl = Mirror + "stats/"
        };

        await client.LoadAsync();

        Assert.Equal(new[] { Collector }, asked);
    }

    private sealed class Answer : HttpMessageHandler
    {
        private readonly List<string> _asked;
        private readonly Func<string, bool> _answers;

        public Answer(List<string> asked, Func<string, bool> answers)
        {
            _asked = asked;
            _answers = answers;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            _asked.Add(url);

            if (!_answers(url))
            {
                throw new HttpRequestException("connection reset");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "schemaVersion": 1, "updatedAt": "2026-11-10T00:00:00Z" }""")
            });
        }
    }
}
