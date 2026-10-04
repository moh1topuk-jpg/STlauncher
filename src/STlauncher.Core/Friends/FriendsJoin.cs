using System;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Server;

namespace STlauncher.Core.Friends;

public enum FriendsWay
{
    /// <summary>None of the invite's addresses answered.</summary>
    None,

    /// <summary>Straight to the host's own address.</summary>
    Direct,

    /// <summary>Through the relay: Minecraft joins a port on this machine.</summary>
    Relay,

    /// <summary>The host's public address.</summary>
    Public
}

/// <summary>
/// The guest's side: takes the endpoints of an invite and finds the first way that
/// actually answers, in the order of preference - direct, relay, public. A way counts
/// as working when a Minecraft server answers a status ping through it, so the address
/// handed back is one the game can join right now.
///
/// For the relay way this object keeps the local port open; dispose it when the player
/// is done playing.
/// </summary>
public sealed class FriendsJoin : IAsyncDisposable
{
    private static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PublicTimeout = TimeSpan.FromSeconds(5);

    // The relay waits ten seconds for the host before giving up; the ping must outlast that.
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(13);

    private readonly RelayGuest? _guest;

    private FriendsJoin(FriendsWay way, string? address, ServerStatus? server, RelayFailure relayFailure, RelayGuest? guest)
    {
        Way = way;
        Address = address;
        Server = server;
        RelayFailure = relayFailure;
        _guest = guest;
    }

    public FriendsWay Way { get; }

    /// <summary>What Minecraft joins: "host:port", or a bare host name for a public address that carries its port in DNS.</summary>
    public string? Address { get; }

    /// <summary>The server's answer to the ping that proved the way works. Null when nothing could be checked.</summary>
    public ServerStatus? Server { get; }

    /// <summary>Why the relay way did not work, when the invite offered it and it was tried.</summary>
    public RelayFailure RelayFailure { get; }

    /// <param name="preferredLocalPort">
    /// For the relay way: the local port used for this server last time, so the address
    /// the game remembers stays the same. Zero lets the system pick.
    /// </param>
    public static async Task<FriendsJoin> ConnectAsync(
        ServerInviteEndpoints endpoints,
        RelayClientOptions? options = null,
        int preferredLocalPort = 0,
        CancellationToken cancellationToken = default)
    {
        if (endpoints is null)
        {
            throw new ArgumentNullException(nameof(endpoints));
        }

        if (HostPort.TryParse(endpoints.Direct, out var directHost, out var directPort) && directPort is not null)
        {
            var status = await ServerPinger.PingAsync(directHost, directPort.Value, DirectTimeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (status is not null)
            {
                return new FriendsJoin(FriendsWay.Direct, HostPort.Format(directHost, directPort), status, RelayFailure.None, null);
            }
        }

        var relayFailure = RelayFailure.None;

        if (endpoints.HasRelay && RelayEndpoint.TryParse(endpoints.Relay, out var relay) && RelayKeys.IsKey(endpoints.RoomKey))
        {
            var guest = new RelayGuest(relay!, endpoints.RoomKey!, options);
            var keep = false;

            try
            {
                guest.Start(preferredLocalPort);

                var status = await ServerPinger.PingAsync("127.0.0.1", guest.LocalPort, RelayTimeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (status is not null)
                {
                    keep = true;
                    return new FriendsJoin(FriendsWay.Relay, guest.LocalAddress, status, RelayFailure.None, guest);
                }

                // The tunnel opened and nothing spoke Minecraft through it: the host's
                // launcher is there, its server is not.
                relayFailure = guest.LastFailure == RelayFailure.None ? RelayFailure.HostOffline : guest.LastFailure;
            }
            catch (System.Net.Sockets.SocketException)
            {
                relayFailure = RelayFailure.RelayUnreachable;
            }
            finally
            {
                if (!keep)
                {
                    await guest.StopAsync().ConfigureAwait(false);
                }
            }
        }

        if (HostPort.TryParse(endpoints.Public, out var publicHost, out var publicPort))
        {
            var address = HostPort.Format(publicHost, publicPort);

            // A name without a port keeps it in a DNS record only the game looks up, so
            // there is nothing here to ping: it is handed over as it is.
            if (publicPort is null)
            {
                return new FriendsJoin(FriendsWay.Public, address, null, relayFailure, null);
            }

            var status = await ServerPinger.PingAsync(publicHost, publicPort.Value, PublicTimeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (status is not null)
            {
                return new FriendsJoin(FriendsWay.Public, address, status, relayFailure, null);
            }
        }

        return new FriendsJoin(FriendsWay.None, null, null, relayFailure, null);
    }

    /// <summary>Closes the local port of the relay way. The other ways hold nothing open.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_guest is not null)
        {
            await _guest.StopAsync().ConfigureAwait(false);
        }
    }
}
