using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Diagnostics;

/// <summary>Something the launcher needs to reach, and how to probe it.</summary>
/// <param name="Url">An https address for a GET, or "tcp://host:port" for a plain connect.</param>
/// <param name="RejectHtml">
/// True for hosts that never answer with a web page: a 200 with text/html there is a
/// provider's block page standing in for the service, which is a failure, not a pass.
/// </param>
public sealed record NetworkTarget(string Key, string Url, bool RejectHtml = false);

/// <summary>One probe's outcome: reached in N ms, or what went wrong.</summary>
public sealed record NetworkCheckResult(NetworkTarget Target, bool Ok, int Milliseconds, string? Error)
{
    public override string ToString()
        => Ok ? $"{Target.Key}: ok, {Milliseconds} ms" : $"{Target.Key}: FAIL ({Error})";
}

/// <summary>
/// Probes each place the launcher talks to, so a player who cannot download anything
/// gets a list of what answers and what does not instead of a guess. Everything runs at
/// once, with one timeout per target.
/// </summary>
public sealed class NetworkDiagnostics
{
    private readonly HttpClient _http;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public NetworkDiagnostics(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<NetworkCheckResult>> RunAsync(
        IEnumerable<NetworkTarget> targets,
        IProgress<NetworkCheckResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = targets.Select(async target =>
        {
            var result = await ProbeAsync(target, cancellationToken).ConfigureAwait(false);
            progress?.Report(result);
            return result;
        }).ToList();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<NetworkCheckResult> ProbeAsync(NetworkTarget target, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            if (target.Url.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(target.Url);
                using var socket = new TcpClient();
                await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
                return new NetworkCheckResult(target, true, (int)watch.ElapsedMilliseconds, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            // Any answer from the host proves the path is open; a 404 on a probe path is
            // still an answer. Only 5xx means the service itself is down.
            var ok = (int)response.StatusCode < 500;

            if (ok && target.RejectHtml &&
                string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                return new NetworkCheckResult(target, false, (int)watch.ElapsedMilliseconds, "html-stub");
            }

            return new NetworkCheckResult(target, ok, (int)watch.ElapsedMilliseconds, ok ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NetworkCheckResult(target, false, (int)watch.ElapsedMilliseconds, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new NetworkCheckResult(target, false, (int)watch.ElapsedMilliseconds, Describe(ex));
        }
        catch (SocketException ex)
        {
            return new NetworkCheckResult(target, false, (int)watch.ElapsedMilliseconds, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return new NetworkCheckResult(target, false, (int)watch.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    /// <summary>The innermost reason, in one word where possible: DNS, TLS, refused, reset.</summary>
    private static string Describe(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData => "DNS",
                        SocketError.ConnectionRefused => "refused",
                        SocketError.ConnectionReset => "reset",
                        SocketError.TimedOut => "timeout",
                        _ => socket.SocketErrorCode.ToString()
                    };
                case System.Security.Authentication.AuthenticationException:
                    return "TLS";
                case System.IO.IOException io when io.Message.Contains("reset", StringComparison.OrdinalIgnoreCase):
                    return "reset";
            }
        }

        return ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
    }
}
