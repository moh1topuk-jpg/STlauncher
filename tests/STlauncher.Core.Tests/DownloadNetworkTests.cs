using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The system proxy changes while the launcher runs: the handler has to ask again.
/// These go through a real <see cref="SocketsHttpHandler"/> and real sockets, because
/// the question is what the runtime does, not what a stub says it does.
/// </summary>
public class SystemProxyFollowerTests
{
    /// <summary>A "proxy" that answers every request itself with its own name.</summary>
    private sealed class FakeProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _name;

        public FakeProxy(string name)
        {
            _name = name;
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Uri Address => new($"http://127.0.0.1:{Port}/");

        public int Requests;

        /// <summary>The request line of the last request: a proxied one names the full address.</summary>
        public string LastRequestLine = string.Empty;

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var text = new StringBuilder();

                    while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);

                        if (read == 0)
                        {
                            return;
                        }

                        text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    LastRequestLine = text.ToString().Split("\r\n")[0];
                    Interlocked.Increment(ref Requests);

                    var body = Encoding.ASCII.GetBytes(_name);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task One_handler_follows_the_proxy_as_it_changes_and_when_it_goes_away()
    {
        using var first = new FakeProxy("first");
        using var second = new FakeProxy("second");
        using var site = new FakeProxy("direct");

        var settings = "first";
        IWebProxy current = new WebProxy(first.Address);

        var follower = new SystemProxyFollower(() => settings, () => current, new WebProxy(first.Address), TimeSpan.Zero);

        // The launcher's own handler, as it is built for the whole session.
        using var http = new HttpClient(LauncherHttp.CreateHandler(follower)) { Timeout = TimeSpan.FromSeconds(20) };

        Assert.Equal("first", await http.GetStringAsync("http://stlauncher-proxy-test.invalid/a"));
        Assert.StartsWith("GET http://stlauncher-proxy-test.invalid/a", first.LastRequestLine);

        // The player switches a VPN on: another proxy in the Windows settings.
        settings = "second";
        current = new WebProxy(second.Address);

        Assert.Equal("second", await http.GetStringAsync("http://stlauncher-proxy-test.invalid/b"));
        Assert.Equal(1, first.Requests);

        // And off: no proxy at all. The same client now goes straight to the site.
        settings = "none";
        current = new WebProxy();

        Assert.Equal("direct", await http.GetStringAsync($"http://127.0.0.1:{site.Port}/c"));
        Assert.StartsWith("GET /c", site.LastRequestLine);
        Assert.Equal(1, first.Requests);
        Assert.Equal(1, second.Requests);
        Assert.Equal(2, follower.Changes);
    }

    [Fact]
    public async Task A_handler_given_a_fixed_proxy_object_stays_with_it_which_is_why_the_follower_exists()
    {
        // What the launcher did before: the proxy object is read once and kept. Changing
        // "the settings" (here: nothing can change them) leaves the route where it was.
        using var first = new FakeProxy("first");

        using var handler = new SocketsHttpHandler { Proxy = new WebProxy(first.Address), UseProxy = true };
        using var http = new HttpClient(handler);

        Assert.Equal("first", await http.GetStringAsync("http://stlauncher-proxy-test.invalid/a"));

        // The property cannot even be set again once the handler has been used.
        Assert.Throws<InvalidOperationException>(() => handler.Proxy = new WebProxy());
    }

    [Fact]
    public void The_settings_are_not_read_more_often_than_asked_and_not_rebuilt_without_a_change()
    {
        var now = 0L;
        var reads = 0;
        var builds = 0;
        var settings = "a";

        var follower = new SystemProxyFollower(
            () => { reads++; return settings; },
            () => { builds++; return new WebProxy("http://127.0.0.1:1/"); },
            new WebProxy(),
            TimeSpan.FromSeconds(2),
            () => now);

        var uri = new Uri("https://example.com/");
        reads = 0;

        follower.GetProxy(uri);
        follower.IsBypassed(uri);
        Assert.Equal(0, reads);

        now = 2500;
        follower.GetProxy(uri);
        Assert.Equal(1, reads);
        Assert.Equal(0, builds);

        settings = "b";
        now = 5000;
        Assert.Equal(new Uri("http://127.0.0.1:1/"), follower.GetProxy(uri));
        Assert.Equal(1, builds);
    }

    [Fact]
    public void A_reader_that_fails_or_has_nothing_keeps_the_proxy_in_use()
    {
        var settings = "a";
        Func<IWebProxy?> build = () => throw new InvalidOperationException("moved in a runtime update");
        var follower = new SystemProxyFollower(() => settings, () => build(), new WebProxy("http://127.0.0.1:7/"), TimeSpan.Zero);
        var uri = new Uri("https://example.com/");

        settings = "b";
        Assert.Equal(new Uri("http://127.0.0.1:7/"), follower.GetProxy(uri));

        build = () => null;
        settings = "c";
        Assert.Equal(new Uri("http://127.0.0.1:7/"), follower.GetProxy(uri));

        // And a signature that cannot be read is not a change.
        var broken = new SystemProxyFollower(() => throw new IOException("registry"), () => new WebProxy(), new WebProxy("http://127.0.0.1:7/"), TimeSpan.Zero);
        Assert.Equal(new Uri("http://127.0.0.1:7/"), broken.GetProxy(uri));
        Assert.Equal(0, broken.Changes);
    }

    [Fact]
    public void The_runtime_still_has_the_reader_the_follower_asks_for()
    {
        // Reached by reflection: if a runtime update renames it, this is where it shows
        // (the follower then falls back to the plain address from the settings).
        var proxy = SystemProxyFollower.ConstructRuntimeSystemProxy();

        Assert.NotNull(proxy);

        // A fresh one each time, not the cached default.
        Assert.NotSame(proxy, SystemProxyFollower.ConstructRuntimeSystemProxy());
    }

    [Theory]
    [InlineData("127.0.0.1:10809", "http://127.0.0.1:10809/")]
    [InlineData("http=10.0.0.1:3128;https=10.0.0.2:3129", "http://10.0.0.2:3129/")]
    [InlineData("http=10.0.0.1:3128", "http://10.0.0.1:3128/")]
    [InlineData("socks=127.0.0.1:1080", null)]
    [InlineData("", null)]
    public void The_manual_address_of_the_windows_settings_is_read(string server, string? expected)
    {
        var proxy = SystemProxyFollower.ParseManual(true, server, "localhost;*.lan;<local>");

        Assert.Equal(expected is null ? null : new Uri(expected), ProxyFor(proxy, "https://cdn.modrinth.com/x"));
    }

    [Fact]
    public void A_manual_proxy_that_is_switched_off_or_bypassed_sends_directly()
    {
        Assert.Null(ProxyFor(SystemProxyFollower.ParseManual(false, "127.0.0.1:10809", null), "https://cdn.modrinth.com/x"));

        var proxy = SystemProxyFollower.ParseManual(true, "127.0.0.1:10809", "*.lan;<local>");

        Assert.Null(ProxyFor(proxy, "http://nas.lan/file"));
        Assert.Null(ProxyFor(proxy, "http://router/"));
        Assert.NotNull(ProxyFor(proxy, "https://api.modrinth.com/v2/"));
    }

    /// <summary>Asked the way the handler asks: bypass first, then the address.</summary>
    private static Uri? ProxyFor(IWebProxy proxy, string url)
    {
        var uri = new Uri(url);
        return proxy.IsBypassed(uri) ? null : proxy.GetProxy(uri) is { } address && address != uri ? address : null;
    }

    [Fact]
    public void The_launcher_handler_recycles_connections_bounds_redirects_and_leaves_trust_to_the_system()
    {
        using var handler = LauncherHttp.CreateHandler();

        Assert.InRange(handler.PooledConnectionLifetime, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        Assert.InRange(handler.PooledConnectionIdleTimeout, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
        Assert.True(handler.UseProxy);
        Assert.IsType<SystemProxyFollower>(handler.Proxy);

        // Against this machine's real settings, read-only: asking must simply work.
        Assert.Null(Record.Exception(() => handler.Proxy!.GetProxy(new Uri("https://api.modrinth.com/v2/"))));
        Assert.Null(Record.Exception(() => handler.Proxy!.IsBypassed(new Uri("https://api.modrinth.com/v2/"))));

        Assert.True(handler.AllowAutoRedirect);
        Assert.Equal(LauncherHttp.MaxRedirects, handler.MaxAutomaticRedirections);

        // Nothing of ours between a site's certificate and the Windows trust store: no
        // callback, no pinned chain policy, no client-side list of roots.
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.CertificateChainPolicy);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck, handler.SslOptions.CertificateRevocationCheckMode);
    }
}

public class NetworkFailureCauseTests
{
    [Fact]
    public void A_name_that_does_not_resolve()
    {
        var failure = NetworkFailures.Classify(new HttpRequestException(
            "No such host is known. (cdn.modrinth.com:443)",
            new SocketException((int)SocketError.HostNotFound)));

        Assert.Equal(NetworkFailureCause.NameNotResolved, failure.Cause);
        Assert.Equal(NetworkFailureKind.NoConnection, failure.Kind);
    }

    [Fact]
    public void A_name_that_does_not_resolve_told_by_the_runtime_alone()
    {
        var failure = NetworkFailures.Classify(new HttpRequestException(HttpRequestError.NameResolutionError, "Name or service not known"));

        Assert.Equal(NetworkFailureCause.NameNotResolved, failure.Cause);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, NetworkFailureCause.ConnectionRefused)]
    [InlineData(SocketError.ConnectionReset, NetworkFailureCause.ConnectionReset)]
    [InlineData(SocketError.ConnectionAborted, NetworkFailureCause.ConnectionReset)]
    [InlineData(SocketError.NetworkUnreachable, NetworkFailureCause.Unreachable)]
    [InlineData(SocketError.TimedOut, NetworkFailureCause.TimedOut)]
    public void A_socket_error_names_its_cause(SocketError error, NetworkFailureCause expected)
    {
        var failure = NetworkFailures.Classify(new HttpRequestException("wrapper", new SocketException((int)error)));

        Assert.Equal(expected, failure.Cause);
    }

    [Fact]
    public void A_certificate_windows_does_not_trust_is_told_apart_from_a_cut_handshake()
    {
        // What an antivirus that re-signs HTTPS with a root missing from the store produces.
        var inspected = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));

        var cut = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "Authentication failed because the remote party closed the transport stream.",
                new SocketException((int)SocketError.ConnectionReset)));

        Assert.Equal(NetworkFailureCause.CertificateNotTrusted, NetworkFailures.Classify(inspected).Cause);
        Assert.Equal(NetworkFailureKind.Blocked, NetworkFailures.Classify(inspected).Kind);
        Assert.Equal(NetworkFailureCause.ConnectionReset, NetworkFailures.Classify(cut).Cause);

        Assert.Contains("antivirus", NetworkFailures.Describe(NetworkFailures.Classify(inspected)));
    }

    [Fact]
    public void A_native_certificate_code_is_recognised_without_reading_the_message()
    {
        var failure = NetworkFailures.Classify(new HttpRequestException(
            "wrapper",
            new AuthenticationException("Authentication failed, see inner exception.", new System.ComponentModel.Win32Exception(unchecked((int)0x80090325)))));

        Assert.Equal(NetworkFailureCause.CertificateNotTrusted, failure.Cause);
    }

    [Fact]
    public void A_timeout_and_an_error_status()
    {
        Assert.Equal(
            NetworkFailureCause.TimedOut,
            NetworkFailures.Classify(new TaskCanceledException("timeout", new TimeoutException())).Cause);

        var status = NetworkFailures.Classify(new HttpRequestException("404 Not Found", null, HttpStatusCode.NotFound));

        Assert.Equal(NetworkFailureCause.HttpStatus, status.Cause);
        Assert.Contains("404", NetworkFailures.Describe(status));
    }
}

public class DownloadFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-download-" + Guid.NewGuid().ToString("N"));

    public DownloadFailureTests() => Directory.CreateDirectory(_root);

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

    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public Scripted(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = _answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    /// <summary>A body that starts and then says nothing more, as a route that died does.</summary>
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

    private static string Sha1Of(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private string[] Leftovers() => Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).ToArray()!;

    [Fact]
    public async Task The_message_puts_the_cause_first_and_the_address_last()
    {
        const string url = "https://cdn.modrinth.com/data/AANobbMI/versions/abc/sodium.jar";
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new DownloadClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => client.EnsureFileAsync(new DownloadItem(url, Path.Combine(_root, "sodium.jar"))));

        Assert.Equal(NetworkFailureCause.HttpStatus, error.Failure.Cause);
        Assert.StartsWith("The site answered with an error (404", error.Message);
        Assert.EndsWith(url, error.Message);
        Assert.Equal(url, error.Url);

        // Asking again for a file that is not there changes nothing: once is enough.
        Assert.Equal(1, handler.Requests);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_file_with_another_digest_is_named_as_such_and_leaves_nothing_behind()
    {
        var bytes = Encoding.UTF8.GetBytes("not the promised file");
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 2);

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => client.EnsureFileAsync(new DownloadItem("https://files.test/a.jar", Path.Combine(_root, "a.jar"), new string('0', 40))));

        Assert.Equal(NetworkFailureCause.HashMismatch, error.Failure.Cause);
        Assert.Equal(2, handler.Requests);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_redirect_the_handler_did_not_follow_is_refused_by_name_and_not_retried()
    {
        // The handler hands back the redirect itself when it leads from https to http, or
        // when the chain is longer than allowed.
        var handler = new Scripted(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://mirror.example/a.jar");
            return response;
        });

        var client = new DownloadClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => client.EnsureFileAsync(new DownloadItem("https://files.test/a.jar", Path.Combine(_root, "a.jar"))));

        Assert.Equal(NetworkFailureCause.RedirectRefused, error.Failure.Cause);
        Assert.Contains("http://mirror.example", error.Message);
        Assert.Equal(1, handler.Requests);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task An_https_download_that_landed_on_plain_http_is_not_kept()
    {
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://mirror.example/a.jar")
        });

        var client = new DownloadClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => client.EnsureFileAsync(new DownloadItem("https://files.test/a.jar", Path.Combine(_root, "a.jar"))));

        Assert.Equal(NetworkFailureCause.RedirectRefused, error.Failure.Cause);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_body_that_stops_coming_is_given_up_as_a_timeout()
    {
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) });
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1) { StallTimeout = TimeSpan.FromMilliseconds(200) };

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => client.EnsureFileAsync(new DownloadItem("https://files.test/a.jar", Path.Combine(_root, "a.jar"))));

        Assert.Equal(NetworkFailureCause.TimedOut, error.Failure.Cause);
        Assert.Equal(NetworkFailureKind.Timeout, error.Failure.Kind);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Cancelling_is_still_cancelling_not_a_failed_download()
    {
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) });
        var client = new DownloadClient(new HttpClient(handler));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.EnsureFileAsync(new DownloadItem("https://files.test/a.jar", Path.Combine(_root, "a.jar")), cancel.Token));

        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Staging_brings_the_verified_file_to_the_hidden_name_only()
    {
        var bytes = Encoding.UTF8.GetBytes("the new version");
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var client = new DownloadClient(new HttpClient(handler));
        var staging = Path.Combine(_root, ".a.jar.stlnew");

        await client.StageAsync(new DownloadItem("https://files.test/a.jar", Path.Combine(_root, "a.jar"), Sha1Of(bytes)), staging);

        Assert.Equal(new[] { ".a.jar.stlnew" }, Leftovers());
        Assert.Equal(bytes, File.ReadAllBytes(staging));
    }
}
