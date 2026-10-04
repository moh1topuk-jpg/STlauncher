using System;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Friends;

public enum FriendsWayState
{
    /// <summary>Not asked for.</summary>
    Off,

    /// <summary>Being set up, or being restored after a drop.</summary>
    Working,

    /// <summary>Friends can connect this way.</summary>
    Ready,

    /// <summary>Did not work; the failure next to it says why.</summary>
    Failed
}

/// <summary>Where the host's side stands on each way of being reached.</summary>
/// <param name="DirectAddress">"address:port" for friends, while the direct way is ready.</param>
/// <param name="DirectVerified">True when the relay connected to that address from outside. False when there was no relay to ask.</param>
/// <param name="RelayAddress">The relay in use, or null when none is configured.</param>
/// <param name="PublicAddress">The public address given through <see cref="FriendsHostSession.SetPublicAddress"/>.</param>
public sealed record FriendsHostStatus(
    FriendsWayState Direct,
    UpnpFailure DirectFailure,
    string? DirectAddress,
    bool DirectVerified,
    FriendsWayState Relay,
    RelayFailure RelayFailure,
    string? RelayAddress,
    string? RoomKey,
    string? PublicAddress)
{
    /// <summary>The public address given by the relay itself: off, being asked for, there, or not to be had.</summary>
    public FriendsWayState Public { get; init; }

    /// <summary>
    /// Why <see cref="Public"/> failed. None with a failed state means the relay answered
    /// but has no public port to give.
    /// </summary>
    public RelayFailure PublicFailure { get; init; }

    /// <summary>
    /// What goes into an invite right now. The relay is included while it is only being
    /// reconnected: the invite is read later than it is written, and the room keeps its key.
    /// </summary>
    public ServerInviteEndpoints Endpoints
    {
        get
        {
            var relay = (Relay is FriendsWayState.Ready or FriendsWayState.Working) && RoomKey is not null;

            return new ServerInviteEndpoints(
                Direct == FriendsWayState.Ready ? DirectAddress : null,
                relay ? RelayAddress : null,
                relay ? RoomKey : null,
                PublicAddress);
        }
    }
}

/// <summary>
/// The host's side of "a server for friends" in one place: the relay room, the port
/// mapping with its check from outside, and the public address, gathered into the
/// endpoints of an invite. Each way starts only when asked for, and everything this
/// object opened is closed again by <see cref="StopAsync"/>.
/// </summary>
public sealed class FriendsHostSession : IAsyncDisposable
{
    private readonly RelayEndpoint? _relay;
    private readonly RelayClientOptions? _options;
    private readonly object _gate = new();

    private RelayHost? _host;
    private RelayHost? _publicHost;
    private UpnpPortMapper? _mapper;
    private FriendsHostStatus _status;

    /// <param name="relay">From <see cref="RelayLocation.Resolve(Content.ContentCatalog?)"/>; null when there is none.</param>
    /// <param name="hostKey">The key saved from an earlier session, so invites already sent keep working.</param>
    public FriendsHostSession(int serverPort, RelayEndpoint? relay, string? hostKey = null, RelayClientOptions? options = null)
    {
        if (serverPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(serverPort));
        }

        ServerPort = serverPort;
        HostKey = RelayKeys.IsKey(hostKey) ? hostKey!.ToLowerInvariant() : RelayKeys.NewHostKey();

        _relay = relay;
        _options = options;

        _status = new FriendsHostStatus(
            FriendsWayState.Off, UpnpFailure.None, null, false,
            FriendsWayState.Off, relay is null ? RelayFailure.NotConfigured : RelayFailure.None, relay?.ToString(), null,
            null);
    }

    public int ServerPort { get; }

    /// <summary>The host's secret for the relay. Save it with the server's settings; never show or share it.</summary>
    public string HostKey { get; }

    public FriendsHostStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event Action<FriendsHostStatus>? Changed;

    /// <summary>
    /// Opens the room on the relay and keeps it open. Returns false when no relay is
    /// configured; the status then says <see cref="RelayFailure.NotConfigured"/>.
    /// </summary>
    public bool StartRelay()
    {
        if (_relay is null)
        {
            Update(s => s with { Relay = FriendsWayState.Failed, RelayFailure = RelayFailure.NotConfigured });
            return false;
        }

        RelayHost host;

        lock (_gate)
        {
            if (_host is not null)
            {
                return true;
            }

            host = _host = new RelayHost(_relay, ServerPort, HostKey, _options);
        }

        host.StateChanged += _ => OnRelayChanged(host);
        host.Start();
        OnRelayChanged(host);
        return true;
    }

    public async Task StopRelayAsync()
    {
        RelayHost? host;

        lock (_gate)
        {
            host = _host;
            _host = null;
        }

        if (host is not null)
        {
            await host.StopAsync().ConfigureAwait(false);
        }

        Update(s => s with { Relay = FriendsWayState.Off, RelayFailure = _relay is null ? RelayFailure.NotConfigured : RelayFailure.None, RoomKey = null });
    }

    /// <summary>
    /// Asks the relay for a public address: "relay-host:port", which anyone can type into
    /// Minecraft with no launcher and no account anywhere. Returns false when no relay is
    /// configured. The address arrives in <see cref="FriendsHostStatus.PublicAddress"/>.
    /// </summary>
    public bool StartPublic()
    {
        if (_relay is null)
        {
            Update(s => s with { Public = FriendsWayState.Failed, PublicFailure = RelayFailure.NotConfigured });
            return false;
        }

        RelayHost host;

        lock (_gate)
        {
            if (_publicHost is not null)
            {
                return true;
            }

            host = _publicHost = new RelayHost(_relay, ServerPort, RelayKeys.PublicHostKeyFor(HostKey), _options, wantPublic: true);
        }

        host.StateChanged += _ => OnPublicChanged(host);
        host.Start();
        OnPublicChanged(host);
        return true;
    }

    public async Task StopPublicAsync()
    {
        RelayHost? host;

        lock (_gate)
        {
            host = _publicHost;
            _publicHost = null;
        }

        if (host is null)
        {
            return;
        }

        await host.StopAsync().ConfigureAwait(false);
        Update(s => s with { Public = FriendsWayState.Off, PublicFailure = RelayFailure.None, PublicAddress = null });
    }

    /// <summary>
    /// Tries to make the server reachable straight from the internet, and says whether it
    /// is. Call it while the server is running: the check from outside needs something to
    /// answer on the port. It first asks the relay whether the port is open as things
    /// stand (a public address, a port forwarded by hand), and only then asks the router
    /// for a mapping, which is checked the same way and removed again if it does not work.
    /// </summary>
    public async Task<FriendsHostStatus> OpenDirectAsync(CancellationToken cancellationToken = default)
    {
        Update(s => s with { Direct = FriendsWayState.Working, DirectFailure = UpnpFailure.None, DirectAddress = null, DirectVerified = false });

        try
        {
            if (_relay is not null)
            {
                var asIs = await RelayProbe.CheckAsync(_relay, ServerPort, _options, cancellationToken).ConfigureAwait(false);

                if (asIs.Outcome == Reachability.Reachable && asIs.PublicAddress is not null)
                {
                    return DirectReady(asIs.PublicAddress, ServerPort, verified: true);
                }
            }

            UpnpPortMapper mapper;

            lock (_gate)
            {
                mapper = _mapper ??= new UpnpPortMapper();
            }

            var mapped = await mapper.MapAsync(ServerPort, null, cancellationToken).ConfigureAwait(false);

            if (!mapped.Ok)
            {
                return DirectFailed(mapped.Failure);
            }

            var mapping = mapped.Mapping!;
            var address = mapping.ExternalAddress;
            var verified = false;

            if (_relay is not null)
            {
                var outside = await RelayProbe.CheckAsync(_relay, mapping.ExternalPort, _options, cancellationToken).ConfigureAwait(false);

                if (outside.Outcome == Reachability.Unreachable)
                {
                    // A mapping that does not let anyone in is only a hole in the
                    // router's list: it goes back out.
                    await mapper.UnmapAsync().ConfigureAwait(false);
                    return DirectFailed(UpnpFailure.NotReachable);
                }

                if (outside.Outcome == Reachability.Reachable)
                {
                    verified = true;
                    address = outside.PublicAddress ?? address;
                }
            }

            if (address is null)
            {
                await mapper.UnmapAsync().ConfigureAwait(false);
                return DirectFailed(UpnpFailure.Error);
            }

            return DirectReady(address, mapping.ExternalPort, verified);
        }
        catch (OperationCanceledException)
        {
            await CloseDirectAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Removes the port mapping, if this session made one.</summary>
    public async Task CloseDirectAsync()
    {
        UpnpPortMapper? mapper;

        lock (_gate)
        {
            mapper = _mapper;
        }

        if (mapper is not null)
        {
            await mapper.UnmapAsync().ConfigureAwait(false);
        }

        Update(s => s with { Direct = FriendsWayState.Off, DirectFailure = UpnpFailure.None, DirectAddress = null, DirectVerified = false });
    }

    /// <summary>Records the public address (from <see cref="PlayitAgent"/>) for the invite. Null or malformed clears it.</summary>
    public void SetPublicAddress(string? address)
    {
        var normalized = HostPort.TryNormalize(address, out var value) ? value : null;
        Update(s => s with { PublicAddress = normalized });
    }

    /// <summary>The invite as things stand now. Hand it to <see cref="ServerInviteCode.Encode"/>.</summary>
    public ServerInvite BuildInvite(string name, string gameVersion, LoaderKind loader, string? loaderVersion, string hostNickname, string? buildCode)
        => new(name, gameVersion, loader, loaderVersion, hostNickname, buildCode, Status.Endpoints);

    public async Task StopAsync()
    {
        await StopRelayAsync().ConfigureAwait(false);
        await StopPublicAsync().ConfigureAwait(false);
        await CloseDirectAsync().ConfigureAwait(false);

        UpnpPortMapper? mapper;

        lock (_gate)
        {
            mapper = _mapper;
            _mapper = null;
        }

        if (mapper is not null)
        {
            await mapper.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private FriendsHostStatus DirectReady(string address, int port, bool verified)
        => Update(s => s with
        {
            Direct = FriendsWayState.Ready,
            DirectFailure = UpnpFailure.None,
            DirectAddress = HostPort.Format(address, port),
            DirectVerified = verified
        });

    private FriendsHostStatus DirectFailed(UpnpFailure failure)
        => Update(s => s with { Direct = FriendsWayState.Failed, DirectFailure = failure, DirectAddress = null, DirectVerified = false });

    private void OnRelayChanged(RelayHost host)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(host, _host))
            {
                return;
            }
        }

        var state = host.State switch
        {
            RelayHostState.Online => FriendsWayState.Ready,
            RelayHostState.Failed => FriendsWayState.Failed,
            RelayHostState.Stopped => FriendsWayState.Off,
            _ => FriendsWayState.Working
        };

        Update(s => s with { Relay = state, RelayFailure = host.LastFailure, RoomKey = host.RoomKey });
    }

    private void OnPublicChanged(RelayHost host)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(host, _publicHost))
            {
                return;
            }
        }

        var port = host.PublicPort;

        switch (host.State)
        {
            case RelayHostState.Online when port > 0:
                Update(s => s with { Public = FriendsWayState.Ready, PublicFailure = RelayFailure.None, PublicAddress = HostPort.Format(_relay!.Host, port) });
                break;

            case RelayHostState.Online:
                // The relay is there but hands out no public ports, or all are taken.
                Update(s => s with { Public = FriendsWayState.Failed, PublicFailure = RelayFailure.None, PublicAddress = null });
                break;

            case RelayHostState.Failed:
                Update(s => s with { Public = FriendsWayState.Failed, PublicFailure = host.LastFailure, PublicAddress = null });
                break;

            case RelayHostState.Stopped:
                Update(s => s with { Public = FriendsWayState.Off, PublicFailure = RelayFailure.None, PublicAddress = null });
                break;

            default:
                // Reconnecting keeps the address in the invite: the port follows from the
                // key, so the room comes back where it was.
                Update(s => s with { Public = FriendsWayState.Working, PublicFailure = host.LastFailure });
                break;
        }
    }

    private FriendsHostStatus Update(Func<FriendsHostStatus, FriendsHostStatus> change)
    {
        FriendsHostStatus status;

        lock (_gate)
        {
            status = change(_status);

            if (status == _status)
            {
                return status;
            }

            _status = status;
        }

        Changed?.Invoke(status);
        return status;
    }
}
