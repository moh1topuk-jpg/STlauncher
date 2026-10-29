using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using Xunit;

namespace STlauncher.Core.Tests;

public sealed class CdnFallbackTests : IDisposable
{
    private const string Mirror = "https://2.26.80.201/";
    private const string ModrinthFile = "https://cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/sodium-fabric-0.9.3-beta.1%2Bmc26.3.jar";
    private const string MirroredModrinthFile = "https://2.26.80.201/mr/data/AANobbMI/versions/g6ju16VJ/sodium-fabric-0.9.3-beta.1%2Bmc26.3.jar";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-cdnfallback-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    public CdnFallbackTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ---- Which addresses go the second way ------------------------------------------

    [Theory]
    [InlineData(ModrinthFile, MirroredModrinthFile)]
    [InlineData("https://cdn.modrinth.com/data/uXXizFIs/versions/Ii0gP3D8/ferritecore-8.2.0-fabric.jar",
                "https://2.26.80.201/mr/data/uXXizFIs/versions/Ii0gP3D8/ferritecore-8.2.0-fabric.jar")]
    [InlineData("https://cdn.modrinth.com/data/uXXizFIs/versions/Ii0gP3D8/Some%20Mod%20%5B1.0%5D.JAR",
                "https://2.26.80.201/mr/data/uXXizFIs/versions/Ii0gP3D8/Some%20Mod%20%5B1.0%5D.JAR")]
    [InlineData("https://cdn.modrinth.com/data/1KVo5zza/versions/AbCd1234/Fabulously.Optimized-9.0.mrpack",
                "https://2.26.80.201/mr/data/1KVo5zza/versions/AbCd1234/Fabulously.Optimized-9.0.mrpack")]
    [InlineData("https://cdn.modrinth.com/data/YL57xq9U/versions/AbCd1234/ComplementaryReimagined_r5.5.zip",
                "https://2.26.80.201/mr/data/YL57xq9U/versions/AbCd1234/ComplementaryReimagined_r5.5.zip")]
    [InlineData("https://edge.forgecdn.net/files/8907/912/appleskin-fabric-mc26.3-3.0.10.jar",
                "https://2.26.80.201/cf/files/8907/912/appleskin-fabric-mc26.3-3.0.10.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/5900/3/Climber%20Mod+1.2.jar",
                "https://2.26.80.201/cf/files/5900/3/Climber%20Mod+1.2.jar")]
    [InlineData("https://EDGE.forgecdn.net/files/5900/4/server-pack.zip",
                "https://2.26.80.201/cf/files/5900/4/server-pack.zip")]
    [InlineData("https://api.modrinth.com/v2/project/ferrite-core/version?loaders=%5B%22fabric%22%5D&game_versions=%5B%221.21.11%22%5D",
                "https://2.26.80.201/mrapi/v2/project/ferrite-core/version?loaders=%5B%22fabric%22%5D&game_versions=%5B%221.21.11%22%5D")]
    [InlineData("https://api.modrinth.com/v2/project/fabric-api", "https://2.26.80.201/mrapi/v2/project/fabric-api")]
    [InlineData("https://api.modrinth.com/v2/version/Ii0gP3D8", "https://2.26.80.201/mrapi/v2/version/Ii0gP3D8")]
    [InlineData("https://api.modrinth.com/v2/search?query=sodium&limit=20", "https://2.26.80.201/mrapi/v2/search?query=sodium&limit=20")]
    [InlineData("https://api.modrinth.com/v2/tag/category", "https://2.26.80.201/mrapi/v2/tag/category")]
    public void A_cdn_file_or_a_known_api_call_is_rewritten_to_the_mirror(string url, string expected)
    {
        var rewritten = new CdnFallback(Mirror).Rewrite(new Uri(url));

        Assert.NotNull(rewritten);
        Assert.Equal(expected, rewritten!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://github.com/moh1topuk-jpg/STlauncher/releases/download/v1/a.jar")]
    [InlineData("https://piston-data.mojang.com/v1/objects/abc/client.jar")]
    [InlineData("https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16/fabric-loader-0.16.jar")]
    [InlineData("https://showtime-updates.moh1topuk.workers.dev/cf/v1/mods/search")]
    [InlineData("http://cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/a.jar")]
    [InlineData("https://cdn.modrinth.com.evil.example/data/AANobbMI/versions/g6ju16VJ/a.jar")]
    [InlineData("https://evilforgecdn.net/files/1/2/a.jar")]
    [InlineData("https://media.forgecdn.net/avatars/thumbnails/29/69/256/256/logo.jpeg")]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/icon.png")]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/a.exe")]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/abc/sodium.jar")]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/sub/a.jar")]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/a%2Fb.jar")]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/a.jar?token=1")]
    [InlineData("https://cdn.modrinth.com:8443/data/AANobbMI/versions/g6ju16VJ/a.jar")]
    [InlineData("https://user@cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/a.jar")]
    [InlineData("https://edge.forgecdn.net/files/abc/1/a.jar")]
    [InlineData("https://edge.forgecdn.net/files/1/2/3/a.jar")]
    [InlineData("https://api.modrinth.com/v2/")]
    [InlineData("https://api.modrinth.com/v2/user")]
    [InlineData("https://api.modrinth.com/v2/version_files")]
    [InlineData("https://api.modrinth.com/v2/project/sodium/members")]
    [InlineData("https://api.modrinth.com/v3/project/sodium")]
    public void Anything_else_is_never_rewritten(string url)
    {
        Assert.Null(new CdnFallback(Mirror).Rewrite(new Uri(url)));
    }

    [Fact]
    public void Without_a_mirror_nothing_is_rewritten()
    {
        Assert.Null(new CdnFallback(null).Rewrite(new Uri(ModrinthFile)));
        Assert.Null(new CdnFallback("http://2.26.80.201/").Rewrite(new Uri(ModrinthFile)));
    }

    // ---- When the second way is taken ----------------------------------------------

    [Fact]
    public async Task A_route_failure_goes_through_the_mirror_and_later_files_go_straight_there()
    {
        var web = new Web(request => request.RequestUri!.Host == "cdn.modrinth.com" ? throw Dns() : Ok("jar"));
        var (fallback, client) = Build(web);
        var announced = new ConcurrentBag<string>();
        fallback.FallbackStarted += announced.Add;

        var first = await client.GetStringAsync(ModrinthFile);
        var second = await client.GetStringAsync(ModrinthFile.Replace("sodium-fabric", "other", StringComparison.Ordinal));

        Assert.Equal("jar", first);
        Assert.Equal("jar", second);
        Assert.Equal(
            new[] { ModrinthFile, MirroredModrinthFile, MirroredModrinthFile.Replace("sodium-fabric", "other", StringComparison.Ordinal) },
            web.Asked);
        Assert.Equal(new[] { "Modrinth" }, announced);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.TimedOut)]
    public async Task Refused_reset_and_timed_out_connections_count_as_route_failures(SocketError error)
    {
        var web = new Web(request => request.RequestUri!.Host == "edge.forgecdn.net"
            ? throw new HttpRequestException("failed", new SocketException((int)error))
            : Ok("jar"));
        var (_, client) = Build(web);

        Assert.Equal("jar", await client.GetStringAsync("https://edge.forgecdn.net/files/8907/912/a.jar"));
        Assert.Equal("https://2.26.80.201/cf/files/8907/912/a.jar", web.Asked.Last());
    }

    [Fact]
    public async Task A_broken_handshake_counts_as_a_route_failure()
    {
        var web = new Web(request => request.RequestUri!.Host == "cdn.modrinth.com"
            ? throw new HttpRequestException("The SSL connection could not be established", new System.Security.Authentication.AuthenticationException("handshake", new IOException("reset")))
            : Ok("jar"));
        var (_, client) = Build(web);

        Assert.Equal("jar", await client.GetStringAsync(ModrinthFile));
        Assert.Equal(MirroredModrinthFile, web.Asked.Last());
    }

    [Fact]
    public async Task A_direct_route_that_never_answers_goes_the_second_way_in_time()
    {
        var web = new Web(async (request, token) =>
        {
            if (request.RequestUri!.Host == "cdn.modrinth.com")
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return Ok("jar");
        });
        var (_, client) = Build(web, TimeSpan.FromMilliseconds(150));

        Assert.Equal("jar", await client.GetStringAsync(ModrinthFile));
        Assert.Equal(MirroredModrinthFile, web.Asked.Last());
    }

    [Fact]
    public async Task A_404_is_the_file_and_never_goes_the_second_way()
    {
        var web = new Web(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var (fallback, client) = Build(web);

        using var response = await client.GetAsync(ModrinthFile);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(new[] { ModrinthFile }, web.Asked);
        Assert.False(fallback.ShouldUseMirror("cdn.modrinth.com"));
    }

    [Fact]
    public async Task A_403_counts_only_when_the_mirror_has_the_file()
    {
        var web = new Web(request => request.RequestUri!.Host == "cdn.modrinth.com"
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Ok("jar"));
        var (fallback, client) = Build(web);

        Assert.Equal("jar", await client.GetStringAsync(ModrinthFile));
        Assert.True(fallback.ShouldUseMirror("cdn.modrinth.com"));
    }

    [Fact]
    public async Task A_403_from_both_ways_is_the_site_and_marks_nothing()
    {
        var web = new Web(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var (fallback, client) = Build(web);

        using var response = await client.GetAsync("https://mediafilez.forgecdn.net/files/5846/880/missing.jar");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(2, web.Asked.Count);
        Assert.False(fallback.ShouldUseMirror("mediafilez.forgecdn.net"));
    }

    [Fact]
    public async Task When_neither_way_answers_the_direct_failure_is_told_and_nothing_is_marked()
    {
        var web = new Web(_ => throw Dns());
        var (fallback, client) = Build(web);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(ModrinthFile));

        Assert.Equal(NetworkFailureCause.NameNotResolved, NetworkFailures.Classify(error).Cause);
        Assert.Equal(2, web.Asked.Count);
        Assert.False(fallback.ShouldUseMirror("cdn.modrinth.com"));
    }

    [Fact]
    public async Task Other_addresses_fail_as_they_are_and_never_reach_the_mirror()
    {
        var web = new Web(_ => throw Dns());
        var (_, client) = Build(web);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://piston-data.mojang.com/v1/objects/abc/client.jar"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://cdn.modrinth.com/data/AANobbMI/icon.png"));

        Assert.DoesNotContain(web.Asked, u => u.StartsWith(Mirror, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_reads_go_the_second_way()
    {
        var web = new Web(_ => throw Dns());
        var (_, client) = Build(web);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("https://api.modrinth.com/v2/project/sodium", new StringContent("{}")));

        Assert.Single(web.Asked);
    }

    [Fact]
    public async Task The_range_header_goes_along()
    {
        string? range = null;
        var web = new Web(request =>
        {
            if (request.RequestUri!.Host == "cdn.modrinth.com")
            {
                throw Dns();
            }

            range = request.Headers.Range?.ToString();
            return Ok("j");
        });
        var (_, client) = Build(web);
        using var request = new HttpRequestMessage(HttpMethod.Get, ModrinthFile);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199);

        using var _ = await client.SendAsync(request);

        Assert.Equal("bytes=100-199", range);
    }

    [Fact]
    public async Task The_direct_route_is_tried_again_after_ten_minutes()
    {
        var blocked = true;
        var web = new Web(request => blocked && request.RequestUri!.Host == "cdn.modrinth.com" ? throw Dns() : Ok("jar"));
        var (fallback, client) = Build(web);
        var announced = 0;
        fallback.FallbackStarted += _ => Interlocked.Increment(ref announced);

        await client.GetStringAsync(ModrinthFile);
        _now += TimeSpan.FromMinutes(9);
        await client.GetStringAsync(ModrinthFile);
        Assert.Equal(new[] { ModrinthFile, MirroredModrinthFile, MirroredModrinthFile }, web.Asked);

        // Ten minutes on, still blocked: one more direct try, then back to the mirror.
        _now += TimeSpan.FromMinutes(2);
        await client.GetStringAsync(ModrinthFile);
        Assert.Equal(new[] { ModrinthFile, MirroredModrinthFile }, web.Asked.Skip(3));
        Assert.True(fallback.ShouldUseMirror("cdn.modrinth.com"));

        // And once the direct route works again, it is used again.
        blocked = false;
        _now += TimeSpan.FromMinutes(11);
        web.Asked.Clear();
        await client.GetStringAsync(ModrinthFile);
        await client.GetStringAsync(ModrinthFile);
        Assert.Equal(new[] { ModrinthFile, ModrinthFile }, web.Asked);

        // Told once per session, not on every re-test.
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task Modrinth_and_CurseForge_are_remembered_apart()
    {
        var web = new Web(request => request.RequestUri!.Host == "cdn.modrinth.com" ? throw Dns() : Ok("jar"));
        var (fallback, client) = Build(web);
        var announced = new ConcurrentBag<string>();
        fallback.FallbackStarted += announced.Add;

        await client.GetStringAsync(ModrinthFile);
        await client.GetStringAsync("https://edge.forgecdn.net/files/8907/912/a.jar");

        Assert.Equal("https://edge.forgecdn.net/files/8907/912/a.jar", web.Asked.Last());
        Assert.Equal(new[] { "Modrinth" }, announced);
    }

    // ---- Through the download client -----------------------------------------------

    [Fact]
    public async Task A_wrong_hash_never_sends_the_file_the_second_way()
    {
        var web = new Web(_ => Ok("not the promised file"));
        var (fallback, http) = Build(web);
        var client = new DownloadClient(http, maxAttempts: 2, fallback: fallback);

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => client.EnsureFileAsync(new DownloadItem(ModrinthFile, Path.Combine(_root, "a.jar"), new string('0', 40))));

        Assert.Equal(NetworkFailureCause.HashMismatch, error.Failure.Cause);
        Assert.Equal(new[] { ModrinthFile, ModrinthFile }, web.Asked);
        Assert.False(fallback.ShouldUseMirror("cdn.modrinth.com"));
    }

    [Fact]
    public async Task A_file_through_the_mirror_is_checked_as_strictly()
    {
        var web = new Web(request => request.RequestUri!.Host == "cdn.modrinth.com" ? throw Dns() : Ok("tampered"));
        var (fallback, http) = Build(web);
        var client = new DownloadClient(http, maxAttempts: 1, fallback: fallback);

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => client.EnsureFileAsync(new DownloadItem(ModrinthFile, Path.Combine(_root, "a.jar"), Sha1("the real jar"))));

        Assert.Equal(NetworkFailureCause.HashMismatch, error.Failure.Cause);
        Assert.False(File.Exists(Path.Combine(_root, "a.jar")));
    }

    [Fact]
    public async Task A_file_whose_body_stops_coming_is_fetched_again_through_the_mirror()
    {
        var web = new Web(request => request.RequestUri!.Host == "cdn.modrinth.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()), RequestMessage = request }
            : Ok("the real jar", request));
        var (fallback, http) = Build(web);
        var client = new DownloadClient(http, maxAttempts: 2, fallback: fallback) { StallTimeout = TimeSpan.FromMilliseconds(150) };
        var path = Path.Combine(_root, "a.jar");

        await client.EnsureFileAsync(new DownloadItem(ModrinthFile, path, Sha1("the real jar")));

        Assert.Equal("the real jar", File.ReadAllText(path));
        Assert.Equal(new[] { ModrinthFile, MirroredModrinthFile }, web.Asked);
    }

    // ---- Helpers ---------------------------------------------------------------------

    private (CdnFallback Fallback, HttpClient Client) Build(Web web, TimeSpan? directTimeout = null)
    {
        var fallback = new CdnFallback(Mirror, now: () => _now);
        var handler = new CdnFallbackHandler(fallback)
        {
            InnerHandler = web,
            DirectAnswerTimeout = directTimeout ?? TimeSpan.FromSeconds(20)
        };

        return (fallback, new HttpClient(handler));
    }

    private static HttpRequestException Dns()
        => new("No such host is known.", new SocketException((int)SocketError.HostNotFound));

    private static HttpResponseMessage Ok(string body, HttpRequestMessage? request = null)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)), RequestMessage = request };

    private static string Sha1(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class Web : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;

        public Web(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request)))
        {
        }

        public Web(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        public System.Collections.Generic.List<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(request.RequestUri!.AbsoluteUri);
            }

            return _answer(request, cancellationToken);
        }
    }

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
