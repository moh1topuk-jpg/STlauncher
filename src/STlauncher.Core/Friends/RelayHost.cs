using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Friends;

public enum RelayHostState
{
    Stopped,

    /// <summary>The first connection to the relay is being made.</summary>
    Connecting,

    /// <summary>The room is open: friends with the invite can join.</summary>
    Online,

    /// <summary>The relay was lost and is being dialled again; <see cref="RelayHost.LastFailure"/> says why.</summary>
    Reconnecting,

    /// <summary>The relay refused the room and asking again would not help.</summary>
    Failed
}

/// <summary>
/// The host's end of the relay way. Keeps one control connection to the relay; each time
/// the relay says a friend has arrived, dials a second connection for that friend and
/// splices it to the Minecraft server on this machine. Nothing listens here: both
/// connections go out, which is why no port needs to be open.
/// </summary>
public sealed class RelayHost : IAsyncDisposable
{
    private readonly RelayClientOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private Task? _loop;
    private RelayHostState _state = RelayHostState.Stopped;
    private RelayFailure _failure = RelayFailure.None;
    private readonly bool _wantPublic;
    private int _active;
    private int _publicPort;

    /// <param name="hostKey">
    /// The key of an earlier session, to reopen the same room so invites already sent
    /// keep working. Null or malformed makes a fresh one.
    /// </param>
    /// <param name="wantPublic">
    /// Ask the relay for a public port: an address anyone can type into Minecraft, no
    /// launcher needed. The relay may have none to give; <see cref="PublicPort"/> says.
    /// </param>
    public RelayHost(RelayEndpoint relay, int serverPort, string? hostKey = null, RelayClientOptions? options = null, bool wantPublic = false)
    {
        _wantPublic = wantPublic;

        Relay = relay ?? throw new ArgumentNullException(nameof(relay));

        if (serverPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(serverPort));
        }

        ServerPort = serverPort;
        HostKey = RelayKeys.IsKey(hostKey) ? hostKey!.ToLowerInvariant() : RelayKeys.NewHostKey();
        RoomKey = RelayKeys.RoomKeyFor(HostKey);
        _options = options ?? new RelayClientOptions();
    }

    public RelayEndpoint Relay { get; }

    /// <summary>The port of the Minecraft server on this machine.</summary>
    public int ServerPort { get; }

    /// <summary>The host's secret. Keep it in the host's settings; it never goes into an invite.</summary>
    public string HostKey { get; }

    /// <summary>What goes into the invite.</summary>
    public string RoomKey { get; }

    public RelayHostState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Why the last attempt to reach the relay failed. None while online.</summary>
    public RelayFailure LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>The port on the relay that leads to this server, or 0 when there is none.</summary>
    public int PublicPort => Volatile.Read(ref _publicPort);

    /// <summary>Friends' connections being carried right now.</summary>
    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>Raised on a background thread whenever <see cref="State"/> changes.</summary>
    public event Action<RelayHostState>? StateChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _stop.IsCancellationRequested)
            {
                return;
            }

            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>Closes the room. Friends who are connected through it are dropped.</summary>
    public async Task StopAsync()
    {
        Task? loop;

        lock (_gate)
        {
            loop = _loop;
        }

        _stop.Cancel();

        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync()
    {
        var token = _stop.Token;
        var backoff = _options.MinBackoff;

        SetState(RelayHostState.Connecting, RelayFailure.None);

        while (!token.IsCancellationRequested)
        {
            var (failure, wasOnline) = await RunControlAsync(token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (failure == RelayFailure.Rejected)
            {
                SetState(RelayHostState.Failed, failure);
                return;
            }

            // A connection that worked for a while and then dropped starts the waits
            // over; one that never got through keeps doubling them.
            if (wasOnline)
            {
                backoff = _options.MinBackoff;
            }

            SetState(RelayHostState.Reconnecting, failure);

            try
            {
                await Task.Delay(backoff, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
        }

        SetState(RelayHostState.Stopped, RelayFailure.None);
    }

    /// <summary>One life of the control connection, from dialling to whatever ended it.</summary>
    private async Task<(RelayFailure Failure, bool WasOnline)> RunControlAsync(CancellationToken token)
    {
        var online = false;

        try
        {
            using var client = await RelayWire.DialAsync(Relay, _options.ConnectTimeout, token).ConfigureAwait(false);
            var stream = client.GetStream();

            var answer = await RelayWire.AskAsync(stream, _wantPublic ? "HOST " + HostKey + " PUBLIC" : "HOST " + HostKey, _options.AnswerTimeout, token).ConfigureAwait(false);
            var port = 0;

            // "OK <port>" is a room with a public address.
            if (answer is not null && answer.StartsWith("OK ", StringComparison.Ordinal) &&
                int.TryParse(answer.AsSpan(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var given) &&
                given is > 0 and <= 65535)
            {
                port = given;
            }
            else if (answer != "OK")
            {
                return (RelayWire.FailureOf(answer), false);
            }

            Volatile.Write(ref _publicPort, port);
            online = true;
            SetState(RelayHostState.Online, RelayFailure.None);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pings = PingAsync(stream, session);

            try
            {
                while (true)
                {
                    string? line;

                    using (var limit = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                    {
                        limit.CancelAfter(_options.PongTimeout);
                        line = await RelayWire.ReadLineAsync(stream, limit.Token).ConfigureAwait(false);
                    }

                    if (line is null)
                    {
                        return (RelayFailure.RelayUnreachable, true);
                    }

                    // PONG, and anything a newer relay might add, is simply proof of life.
                    if (line.StartsWith("OPEN ", StringComparison.Ordinal) && RelayKeys.IsKey(line[5..]))
                    {
                        var connId = line[5..];
                        _ = Task.Run(() => CarryAsync(connId, token));
                    }
                }
            }
            finally
            {
                session.Cancel();
                await pings.ConfigureAwait(false);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            return (RelayFailure.None, online);
        }
        catch (Exception)
        {
            // Refused, reset, timed out, a name that does not resolve: all of them mean
            // "not reachable right now", and the loop tries again.
            return (RelayFailure.RelayUnreachable, online);
        }
    }

    private async Task PingAsync(NetworkStream stream, CancellationTokenSource session)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.PingInterval, session.Token).ConfigureAwait(false);
                await RelayWire.WriteLineAsync(stream, "PING", session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A ping that cannot be written means the connection is gone; ending the
            // session now saves waiting out the read timeout to find that out.
            session.Cancel();
        }
    }

    /// <summary>Carries one friend's connection: relay on one side, the local server on the other.</summary>
    private async Task CarryAsync(string connId, CancellationToken token)
    {
        Interlocked.Increment(ref _active);

        try
        {
            using var relay = await RelayWire.DialAsync(Relay, _options.ConnectTimeout, token).ConfigureAwait(false);
            var relayStream = relay.GetStream();

            var answer = await RelayWire.AskAsync(relayStream, $"DATA {HostKey} {connId}", _options.AnswerTimeout, token).ConfigureAwait(false);

            if (answer != "OK")
            {
                return;
            }

            // The server is dialled after the relay on purpose. When it is not running
            // the connect fails here, the relay connection closes, and the friend sees
            // the refusal at once instead of waiting out the relay's timeout.
            using var local = new TcpClient { NoDelay = true };

            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(_options.ConnectTimeout);
                await local.ConnectAsync(IPAddress.Loopback, ServerPort, limit.Token).ConfigureAwait(false);
            }

            await StreamSplice.RunAsync(relayStream, local.GetStream(), token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // One friend's connection failing is that connection's business only.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private void SetState(RelayHostState state, RelayFailure failure)
    {
        lock (_gate)
        {
            if (_state == state && _failure == failure)
            {
                return;
            }

            _state = state;
            _failure = failure;
        }

        StateChanged?.Invoke(state);
    }
}
