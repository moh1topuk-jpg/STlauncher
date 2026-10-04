using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Friends;

public enum Reachability
{
    /// <summary>The relay connected to the port from outside: friends can too.</summary>
    Reachable,

    /// <summary>The relay tried and nothing answered: the port is not open to the internet.</summary>
    Unreachable,

    /// <summary>Nobody could check; <see cref="ReachabilityResult.Failure"/> says why.</summary>
    Unknown
}

/// <param name="PublicAddress">This machine's address as the relay saw it, when the relay answered.</param>
public sealed record ReachabilityResult(Reachability Outcome, string? PublicAddress, RelayFailure Failure);

/// <summary>
/// Asks the relay to connect back to this machine on a port: the only honest way to tell
/// whether a port mapping works is to try it from outside. The relay only ever tries the
/// address the question came from.
/// </summary>
public static class RelayProbe
{
    public static async Task<ReachabilityResult> CheckAsync(
        RelayEndpoint? relay,
        int port,
        RelayClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (relay is null)
        {
            return new ReachabilityResult(Reachability.Unknown, null, RelayFailure.NotConfigured);
        }

        options ??= new RelayClientOptions();

        try
        {
            using var client = await RelayWire.DialAsync(relay, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);

            var answer = await RelayWire.AskAsync(
                client.GetStream(),
                "PROBE " + port.ToString(CultureInfo.InvariantCulture),
                options.AnswerTimeout,
                cancellationToken).ConfigureAwait(false);

            return Parse(answer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ReachabilityResult(Reachability.Unknown, null, RelayFailure.RelayUnreachable);
        }
    }

    /// <summary>Reads the relay's answer to PROBE: "REACHABLE address", "UNREACHABLE address" or an error.</summary>
    public static ReachabilityResult Parse(string? answer)
    {
        if (answer is null)
        {
            return new ReachabilityResult(Reachability.Unknown, null, RelayFailure.RelayUnreachable);
        }

        var space = answer.IndexOf(' ');
        var verdict = space < 0 ? answer : answer[..space];
        var address = space < 0 ? null : answer[(space + 1)..].Trim();

        // The address is shown to the player and goes into the invite, so it has to be
        // an address and not whatever the other end felt like sending.
        if (address is not null && !IPAddress.TryParse(address, out _))
        {
            address = null;
        }

        return verdict switch
        {
            "REACHABLE" => new ReachabilityResult(Reachability.Reachable, address, RelayFailure.None),
            "UNREACHABLE" => new ReachabilityResult(Reachability.Unreachable, address, RelayFailure.None),
            _ => new ReachabilityResult(Reachability.Unknown, null, RelayWire.FailureOf(answer))
        };
    }
}
