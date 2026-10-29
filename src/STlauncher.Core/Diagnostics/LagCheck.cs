using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Friends;
using STlauncher.Core.Server;

namespace STlauncher.Core.Diagnostics;

/// <summary>What a row of the connection check measures.</summary>
public enum LagProbeKind
{
    /// <summary>The Showtime server, by the game's own status ping.</summary>
    GameServer,

    /// <summary>The launcher's relay: a TCP connect and one line there and back.</summary>
    Relay,

    /// <summary>An update mirror, by HTTPS.</summary>
    Mirror,

    Modrinth,

    /// <summary>The CurseForge proxy on the updates mirror.</summary>
    CurseForge,

    /// <summary>How long the name of the game server takes to resolve.</summary>
    Dns
}

/// <param name="Address">
/// "host:port" for the game server and the relay, an https address for the web ones, a
/// bare host name for DNS.
/// </param>
public sealed record LagTarget(string Key, LagProbeKind Kind, string Address);

/// <summary>One row's outcome. Milliseconds is the round trip when it answered.</summary>
public sealed record LagMeasurement(LagTarget Target, bool Ok, int? Milliseconds, string? Error = null)
{
    public override string ToString()
        => Ok ? $"{Target.Key}: {Milliseconds} ms" : $"{Target.Key}: no answer ({Error})";
}

/// <summary>What the numbers add up to, for the page to put into one or two sentences.</summary>
public enum LagVerdict
{
    /// <summary>Every place answers, and quickly.</summary>
    AllGood,

    /// <summary>The game server is far slower than everything else: the delay is on the way to it, not at home.</summary>
    ServerPathSlow,

    /// <summary>Everything is slow alike: the player's own connection or Wi-Fi.</summary>
    EverythingSlow,

    /// <summary>The game server does not answer while the rest does.</summary>
    ServerDown,

    /// <summary>Only Modrinth does not open: a provider is in the way; mods go the spare way.</summary>
    ModrinthBlocked,

    /// <summary>The game server is fine, but some of the launcher's other places do not open.</summary>
    SomethingBlocked,

    /// <summary>The game server's name does not resolve: the provider's DNS.</summary>
    DnsFailed,

    /// <summary>Nothing (or almost nothing) answers: no internet, or something cuts it off.</summary>
    Offline
}

/// <param name="ServerMs">The game server's round trip, when it answered.</param>
/// <param name="OthersMs">The middle of the other places' round trips, when any answered.</param>
/// <param name="Failed">The places that did not answer, DNS aside.</param>
public sealed record LagConclusion(LagVerdict Verdict, int? ServerMs, int? OthersMs, IReadOnlyList<LagTarget> Failed);

/// <summary>
/// "Why does it lag?": a handful of probes at once, each with a short timeout, and a
/// conclusion from their numbers. Nothing is sent anywhere but the probes themselves.
/// </summary>
public static class LagCheck
{
    /// <summary>A round trip above this is slow for anything nearby.</summary>
    public const int SlowMs = 200;

    /// <summary>The game server is called slow from here up, and only if it stands out.</summary>
    public const int ServerSlowMs = 150;

    /// <summary>How long each probe may take before its row says "no answer".</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Reads the measurements. Pure, so it can be tested on made-up numbers; the order of
    /// the checks is the order of what matters most to a player who asks why it lags.
    /// </summary>
    public static LagConclusion Conclude(IReadOnlyList<LagMeasurement> measurements)
    {
        var server = measurements.FirstOrDefault(m => m.Target.Kind == LagProbeKind.GameServer);
        var dns = measurements.FirstOrDefault(m => m.Target.Kind == LagProbeKind.Dns);
        var others = measurements.Where(m => m.Target.Kind is not (LagProbeKind.GameServer or LagProbeKind.Dns)).ToList();
        var answered = others.Where(m => m.Ok && m.Milliseconds is not null).ToList();
        var failed = measurements.Where(m => !m.Ok && m.Target.Kind != LagProbeKind.Dns).Select(m => m.Target).ToList();

        int? othersMs = answered.Count == 0 ? null : Median(answered.Select(m => m.Milliseconds!.Value));
        int? serverMs = server is { Ok: true } ? server.Milliseconds : null;

        LagConclusion Say(LagVerdict verdict) => new(verdict, serverMs, othersMs, failed);

        if (!measurements.Any(m => m.Ok && m.Target.Kind != LagProbeKind.Dns))
        {
            return Say(LagVerdict.Offline);
        }

        if (server is { Ok: false })
        {
            if (dns is { Ok: false })
            {
                return Say(LagVerdict.DnsFailed);
            }

            // Most of the rest silent too: it is the connection, not the server.
            return answered.Count * 2 < others.Count ? Say(LagVerdict.Offline) : Say(LagVerdict.ServerDown);
        }

        if (othersMs is { } baseline)
        {
            if (baseline >= SlowMs)
            {
                return Say(LagVerdict.EverythingSlow);
            }

            // Slow in itself, and slow next to the rest: the way to the server, not the home line.
            if (serverMs is { } ms && ms >= ServerSlowMs && ms >= baseline * 2 && ms - baseline >= 80)
            {
                return Say(LagVerdict.ServerPathSlow);
            }
        }

        if (failed.Count > 0)
        {
            return failed.All(t => t.Kind == LagProbeKind.Modrinth)
                ? Say(LagVerdict.ModrinthBlocked)
                : Say(LagVerdict.SomethingBlocked);
        }

        // The game server alone answering, and slowly, with nothing to compare it against.
        if (serverMs is { } alone && othersMs is null && alone >= SlowMs)
        {
            return Say(LagVerdict.EverythingSlow);
        }

        return Say(LagVerdict.AllGood);
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}

/// <summary>Runs the probes of <see cref="LagCheck"/>: all at once, each within <see cref="LagCheck.Timeout"/>.</summary>
public sealed class LagProbe
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public LagProbe(HttpClient http, TimeSpan? timeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? LagCheck.Timeout;
    }

    public async Task<IReadOnlyList<LagMeasurement>> RunAsync(
        IEnumerable<LagTarget> targets,
        IProgress<LagMeasurement>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = targets.Select(async target =>
        {
            var result = await MeasureAsync(target, cancellationToken).ConfigureAwait(false);
            progress?.Report(result);
            return result;
        }).ToList();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<LagMeasurement> MeasureAsync(LagTarget target, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);

        try
        {
            return target.Kind switch
            {
                LagProbeKind.GameServer => await GameServerAsync(target, limit.Token).ConfigureAwait(false),
                LagProbeKind.Relay => await RelayAsync(target, limit.Token).ConfigureAwait(false),
                LagProbeKind.Dns => await DnsAsync(target, limit.Token).ConfigureAwait(false),
                _ => await WebAsync(target, limit.Token).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LagMeasurement(target, false, null, "timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LagMeasurement(target, false, null, Describe(ex));
        }
    }

    private async Task<LagMeasurement> GameServerAsync(LagTarget target, CancellationToken token)
    {
        var (host, port) = SplitHostPort(target.Address, ServerPinger.DefaultPort);
        var (status, ms) = await ServerPinger.MeasureAsync(host, port, _timeout, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        return status is not null && ms is not null
            ? new LagMeasurement(target, true, ms)
            : new LagMeasurement(target, false, null, "no answer");
    }

    /// <summary>
    /// "PROBE 0" is refused at once with ERR BADPORT, by this relay and every older one: a
    /// line there and back with no connect-back behind it.
    /// </summary>
    private static async Task<LagMeasurement> RelayAsync(LagTarget target, CancellationToken token)
    {
        var (host, port) = SplitHostPort(target.Address, 25580);
        using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, token).ConfigureAwait(false);

        var stream = client.GetStream();
        var watch = Stopwatch.StartNew();
        await RelayWire.WriteLineAsync(stream, "PROBE 0", token).ConfigureAwait(false);
        var answer = await RelayWire.ReadLineAsync(stream, token).ConfigureAwait(false);

        return answer is null
            ? new LagMeasurement(target, false, null, "no answer")
            : new LagMeasurement(target, true, (int)watch.ElapsedMilliseconds);
    }

    private static async Task<LagMeasurement> DnsAsync(LagTarget target, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var addresses = await Dns.GetHostAddressesAsync(target.Address, token).ConfigureAwait(false);

        return addresses.Length > 0
            ? new LagMeasurement(target, true, (int)watch.ElapsedMilliseconds)
            : new LagMeasurement(target, false, null, "DNS");
    }

    /// <summary>
    /// Two HEADs: the first opens the connection (name, TCP, TLS), the second is timed and
    /// rides on it, so the number is one round trip like the others and not four. Any
    /// answer at all means the way is open; a web page from an API is a provider's stub.
    /// </summary>
    private async Task<LagMeasurement> WebAsync(LagTarget target, CancellationToken token)
    {
        var first = await HeadAsync(target, token).ConfigureAwait(false);

        if (!first.Ok)
        {
            return first;
        }

        try
        {
            var second = await HeadAsync(target, token).ConfigureAwait(false);
            return second.Ok ? second : first;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return first;
        }
    }

    private async Task<LagMeasurement> HeadAsync(LagTarget target, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Head, target.Address);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var ms = (int)watch.ElapsedMilliseconds;

        if (target.Kind == LagProbeKind.Modrinth &&
            string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            return new LagMeasurement(target, false, null, "html-stub");
        }

        return new LagMeasurement(target, true, ms);
    }

    /// <summary>"host:port", "[v6]:port" or a bare host.</summary>
    public static (string Host, int Port) SplitHostPort(string address, int defaultPort)
    {
        var text = address.Trim();

        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');

            if (close > 0)
            {
                var host = text[1..close];
                return close + 2 < text.Length && text[close + 1] == ':' &&
                       int.TryParse(text[(close + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var v6Port)
                    ? (host, v6Port)
                    : (host, defaultPort);
            }
        }

        var colon = text.LastIndexOf(':');

        return colon > 0 && text.IndexOf(':') == colon &&
               int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? (text[..colon], port)
            : (text, defaultPort);
    }

    private static string Describe(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case System.Net.Sockets.SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData => "DNS",
                        System.Net.Sockets.SocketError.ConnectionRefused => "refused",
                        System.Net.Sockets.SocketError.ConnectionReset => "reset",
                        System.Net.Sockets.SocketError.TimedOut => "timeout",
                        _ => socket.SocketErrorCode.ToString()
                    };
                case System.Security.Authentication.AuthenticationException:
                    return "TLS";
            }
        }

        return ex.GetType().Name;
    }
}
