using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Friends;

/// <summary>
/// The guest's end of the relay way: a listener on 127.0.0.1 that Minecraft joins as if
/// the server were on this machine. Every connection it takes is carried to the host
/// through the relay. The listener is on the loopback address only, so nothing outside
/// this machine can use it. When the host's server has voice chat, a UDP port on the
/// loopback address carries that too (<see cref="StartVoice"/>).
/// </summary>
public sealed class RelayGuest : IAsyncDisposable
{
    // Minecraft opens one connection to play and a short one per server-list refresh.
    // Past this many something else on the machine is hammering the port, and passing
    // that on would only get this address limited by the relay.
    private const int MaxConnections = 16;

    private readonly RelayClientOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private TcpListener? _listener;
    private VoiceGuestSide? _voice;
    private Task? _loop;
    private RelayFailure _failure = RelayFailure.None;
    private int _active;

    public RelayGuest(RelayEndpoint relay, string roomKey, RelayClientOptions? options = null)
    {
        Relay = relay ?? throw new ArgumentNullException(nameof(relay));

        if (!RelayKeys.IsKey(roomKey))
        {
            throw new ArgumentException("A room key is 32 hex digits.", nameof(roomKey));
        }

        RoomKey = roomKey.ToLowerInvariant();
        _options = options ?? new RelayClientOptions();
    }

    public RelayEndpoint Relay { get; }

    public string RoomKey { get; }

    /// <summary>The port on 127.0.0.1 to join. Zero until <see cref="Start"/>.</summary>
    public int LocalPort { get; private set; }

    /// <summary>The address to give Minecraft.</summary>
    public string LocalAddress => "127.0.0.1:" + LocalPort;

    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _listener is not null && !_stop.IsCancellationRequested;
            }
        }
    }

    /// <summary>
    /// How the most recent connection fared: None once it reached the host, otherwise
    /// why it did not. The listener itself cannot know whether the host is there until
    /// something connects, so ping the local address to find out.
    /// </summary>
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

    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>Raised on a background thread when <see cref="LastFailure"/> changes.</summary>
    public event Action<RelayFailure>? FailureChanged;

    /// <summary>
    /// Opens the local port. Throws a <see cref="SocketException"/> if the system has none to give.
    /// </summary>
    /// <param name="preferredPort">
    /// The port used last time for this friend's server, so an entry saved in the game's
    /// server list keeps working. If it is taken, or zero, the system picks one.
    /// </param>
    public void Start(int preferredPort = 0)
    {
        lock (_gate)
        {
            if (_listener is not null || _stop.IsCancellationRequested)
            {
                return;
            }

            TcpListener listener;

            try
            {
                listener = new TcpListener(IPAddress.Loopback, preferredPort is > 0 and <= 65535 ? preferredPort : 0);
                listener.Start();
            }
            catch (SocketException) when (preferredPort != 0)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
            }

            _listener = listener;
            LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptAsync(listener));
        }
    }

    /// <summary>The UDP port on 127.0.0.1 that carries voice chat, or 0 when there is none.</summary>
    public int VoicePort
    {
        get
        {
            lock (_gate)
            {
                return _voice?.Port ?? 0;
            }
        }
    }

    /// <summary>Voice tunnels open right now: one per voice chat connection of the game.</summary>
    public int ActiveVoiceTunnels
    {
        get
        {
            lock (_gate)
            {
                return _voice?.ActiveTunnels ?? 0;
            }
        }
    }

    /// <summary>
    /// Also carries the host's voice chat: listens on UDP 127.0.0.1:<paramref name="port"/>,
    /// the port the voice chat mod in the game will send to, and takes each sender to the
    /// host through a tunnel of its own. False when another program holds that port; the
    /// game itself is not affected.
    /// </summary>
    public bool StartVoice(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        lock (_gate)
        {
            if (_voice is not null)
            {
                return _voice.Port == port;
            }

            if (_stop.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                _voice = new VoiceGuestSide(port, token => OpenAsync(RelayChannel.Voice, report: false, token), _options.VoiceIdleTimeout);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        TcpListener? listener;
        VoiceGuestSide? voice;

        lock (_gate)
        {
            loop = _loop;
            listener = _listener;
            voice = _voice;
        }

        _stop.Cancel();
        listener?.Stop();

        if (voice is not null)
        {
            await voice.DisposeAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptAsync(TcpListener listener)
    {
        var token = _stop.Token;

        while (!token.IsCancellationRequested)
        {
            TcpClient local;

            try
            {
                local = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (Interlocked.Increment(ref _active) > MaxConnections)
            {
                Interlocked.Decrement(ref _active);
                local.Dispose();
                continue;
            }

            _ = Task.Run(() => CarryAsync(local, token));
        }
    }

    private async Task CarryAsync(TcpClient local, CancellationToken token)
    {
        try
        {
            using (local)
            {
                local.NoDelay = true;

                using var relay = await OpenAsync(RelayChannel.Game, report: true, token).ConfigureAwait(false);

                if (relay is not null)
                {
                    await StreamSplice.RunAsync(local.GetStream(), relay.GetStream(), token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // A connection that broke on the way is over; the next one starts clean.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>
    /// Opens one tunnel to the host and says what it is for. Null when the relay or the
    /// host could not be reached.
    /// </summary>
    /// <param name="report">
    /// Whether the outcome goes into <see cref="LastFailure"/>. Only the game's tunnels
    /// do: that is what the join is judged by, and voice has its own word in the status.
    /// </param>
    private async Task<TcpClient?> OpenAsync(byte channel, bool report, CancellationToken token)
    {
        TcpClient relay;

        try
        {
            relay = await RelayWire.DialAsync(Relay, _options.ConnectTimeout, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (report)
            {
                SetFailure(RelayFailure.RelayUnreachable);
            }

            return null;
        }

        try
        {
            var relayStream = relay.GetStream();
            string? answer;

            try
            {
                answer = await RelayWire.AskAsync(relayStream, "JOIN " + RoomKey, _options.AnswerTimeout, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                answer = null;
            }

            if (answer != "OK")
            {
                if (report)
                {
                    SetFailure(RelayWire.FailureOf(answer));
                }

                relay.Dispose();
                return null;
            }

            await relayStream.WriteAsync(new[] { channel }, token).ConfigureAwait(false);

            if (report)
            {
                SetFailure(RelayFailure.None);
            }

            return relay;
        }
        catch (Exception)
        {
            relay.Dispose();
            return null;
        }
    }

    private void SetFailure(RelayFailure failure)
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            if (_failure == failure)
            {
                return;
            }

            _failure = failure;
        }

        FailureChanged?.Invoke(failure);
    }
}
