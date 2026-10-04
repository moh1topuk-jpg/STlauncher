using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace STlauncher.Relay;

/// <summary>What the relay is doing right now. Counters only: no addresses, no keys.</summary>
public sealed record RelayStats(int Rooms, int Connections, int Tunnels);

/// <summary>
/// Joins a guest to a host when neither can accept a connection from the other. Both
/// dial out to the relay and it copies bytes between them.
///
/// The protocol is one ASCII line, then raw bytes:
///   HOST hostKey          the host's control connection; answered OK, then kept alive
///                         with PING / PONG. The relay sends OPEN connId on it when a
///                         guest arrives.
///   JOIN roomKey          a guest; answered OK once the host has picked it up.
///   DATA hostKey connId   the host picking a guest up; answered OK, then spliced.
///   PROBE port            answered REACHABLE or UNREACHABLE after a connect back to
///                         the caller's own address.
///
/// The room key is derived from the host key (see <see cref="RoomKeyFor"/>), so the
/// invite a host hands out lets a friend join the room but not take it over.
///
/// It listens on the open internet, so everything a client can make it do is bounded:
/// line length, time to say the first line, rooms, guests per room, connections per
/// address, new connections per address per minute, bytes per second, idle time.
/// Addresses live in memory only for as long as those limits need them.
/// </summary>
public sealed class RelayServer : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;
    private const int KeyLength = 32;
    private const int MaxLingeringRejects = 64;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LingerTime = TimeSpan.FromSeconds(2);

    private readonly RelayOptions _options;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    /// <summary>How many rooms each address holds; read and written under the lock on <see cref="_rooms"/>.</summary>
    private readonly Dictionary<IPAddress, int> _roomsByOwner = new();

    /// <summary>Public ports in use, guarded by the <see cref="_rooms"/> lock.</summary>
    private readonly HashSet<int> _publicPorts = new();

    private readonly ConcurrentDictionary<Socket, byte> _sockets = new();
    private readonly IpGate _gate;

    private Socket? _listener;
    private Task? _acceptLoop;
    private int _connections;
    private int _tunnels;
    private int _lingering;

    public RelayServer(RelayOptions options, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _gate = new IpGate(options.MaxConnectionsPerIp, options.MaxAttemptsPerMinute);
    }

    /// <summary>The port actually bound, which differs from the option when that was zero.</summary>
    public int Port { get; private set; }

    public RelayStats Stats
    {
        get
        {
            int rooms;

            lock (_rooms)
            {
                rooms = _rooms.Count;
            }

            return new RelayStats(rooms, Volatile.Read(ref _connections), Volatile.Read(ref _tunnels));
        }
    }

    /// <summary>Starts listening. Throws a <see cref="SocketException"/> when the port is taken.</summary>
    public void Start()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The relay is already running.");
        }

        _listener = Listen(_options.Bind, _options.Port);
        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log?.Invoke($"listening on port {Port}");
    }

    /// <summary>Stops accepting, drops every connection and waits for the handlers to unwind.</summary>
    public async Task StopAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        Close(_listener);

        lock (_rooms)
        {
            foreach (var room in _rooms.Values)
            {
                room.Closed.Cancel();
            }

            _rooms.Clear();
            _roomsByOwner.Clear();
        }

        foreach (var socket in _sockets.Keys)
        {
            Close(socket);
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        // Handlers end on their own once their sockets are closed. A stuck one must not
        // keep the process alive, so the wait is short and then it is over either way.
        var deadline = Environment.TickCount64 + 3000;

        while (Volatile.Read(ref _connections) > 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        _log?.Invoke("stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// The key guests use for a host's room: the first half of SHA-256 over the host
    /// key's bytes. Knowing it does not give the host key back, so a guest cannot open
    /// or replace the room it was invited to.
    /// </summary>
    public static string RoomKeyFor(string hostKey)
    {
        var hash = SHA256.HashData(Convert.FromHexString(hostKey));
        return Convert.ToHexString(hash, 0, KeyLength / 2).ToLowerInvariant();
    }

    private static Socket Listen(IPAddress? bind, int port)
    {
        // One dual-mode socket takes IPv4 and IPv6 alike; a machine with IPv6 switched
        // off falls back to plain IPv4.
        if (bind is null && Socket.OSSupportsIPv6)
        {
            var dual = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                dual.DualMode = true;
                AllowQuickRestart(dual);
                dual.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                dual.Listen(128);
                return dual;
            }
            catch (SocketException)
            {
                dual.Dispose();
            }
        }

        var address = bind ?? IPAddress.Any;
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            AllowQuickRestart(socket);
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(128);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// On Linux a port stays "in use" for a minute after a restart while old connections
    /// time out, unless the listener says it does not mind. On Windows the same option
    /// means something else - sharing the port with another process - and is left alone.
    /// </summary>
    private static void AllowQuickRestart(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
    }

    private async Task AcceptLoopAsync()
    {
        var token = _stop.Token;

        while (!token.IsCancellationRequested)
        {
            Socket socket;

            try
            {
                socket = await _listener!.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // Out of handles, or a client that reset during the accept: neither is a
                // reason to stop taking the next one, but spinning on it would be.
                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            IPAddress address;

            try
            {
                address = Normalize(((IPEndPoint)socket.RemoteEndPoint!).Address);
            }
            catch (Exception)
            {
                Close(socket);
                continue;
            }

            if (Volatile.Read(ref _connections) >= _options.MaxConnections)
            {
                Reject(socket, "ERR BUSY");
                continue;
            }

            var gateKey = GateKey(address);

            if (!_gate.TryEnter(gateKey))
            {
                Reject(socket, "ERR LIMIT");
                continue;
            }

            Interlocked.Increment(ref _connections);
            _sockets.TryAdd(socket, 0);
            _ = Task.Run(() => ServeAsync(socket, address, gateKey));
        }
    }

    private async Task ServeAsync(Socket socket, IPAddress address, IPAddress gateKey)
    {
        try
        {
            socket.NoDelay = true;

            var line = await ReadLineAsync(socket, _options.HandshakeTimeout).ConfigureAwait(false);

            if (line is null)
            {
                return;
            }

            var parts = line.Split(' ');

            switch (parts[0])
            {
                case "HOST" when parts.Length == 2:
                    await ServeHostAsync(socket, address, parts[1], wantPublic: false).ConfigureAwait(false);
                    break;

                case "HOST" when parts.Length == 3 && parts[2] == "PUBLIC":
                    await ServeHostAsync(socket, address, parts[1], wantPublic: true).ConfigureAwait(false);
                    break;

                case "JOIN" when parts.Length == 2:
                    await ServeGuestAsync(socket, parts[1]).ConfigureAwait(false);
                    break;

                case "DATA" when parts.Length == 3:
                    await ServeDataAsync(socket, parts[1], parts[2]).ConfigureAwait(false);
                    break;

                case "PROBE" when parts.Length == 2:
                    await ServeProbeAsync(socket, address, parts[1]).ConfigureAwait(false);
                    break;

                default:
                    await SendLineAsync(socket, "ERR BADCMD").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception)
        {
            // A peer that vanishes in the middle of anything is how most connections end.
        }
        finally
        {
            _sockets.TryRemove(socket, out _);
            Close(socket);
            _gate.Leave(gateKey);
            Interlocked.Decrement(ref _connections);
        }
    }

    private async Task ServeHostAsync(Socket socket, IPAddress address, string hostKeyText, bool wantPublic)
    {
        if (!TryKey(hostKeyText, out var hostKey))
        {
            await SendLineAsync(socket, "ERR BADKEY").ConfigureAwait(false);
            return;
        }

        var roomKey = RoomKeyFor(hostKey);
        var owner = RoomOwner(address);
        Room? room;
        Socket? replaced = null;
        var refusal = "ERR FULL";

        lock (_rooms)
        {
            if (_rooms.TryGetValue(roomKey, out room))
            {
                // The host is back before the relay noticed its old connection die. The
                // same secret key proves it is the same host, so the room carries on
                // with the new connection and the old socket goes.
                replaced = room.Control;
                room.Control = socket;
            }
            else if (_rooms.Count < _options.MaxRooms)
            {
                _roomsByOwner.TryGetValue(owner, out var held);

                // Rooms are few and cost their owner next to nothing to keep: one address
                // must not be able to sit on all of them.
                if (held < _options.MaxRoomsPerIp)
                {
                    room = new Room(socket, owner);
                    _rooms.Add(roomKey, room);
                    _roomsByOwner[owner] = held + 1;
                }
                else
                {
                    refusal = "ERR LIMIT";
                }
            }
        }

        if (room is null)
        {
            await SendLineAsync(socket, refusal).ConfigureAwait(false);
            return;
        }

        Close(replaced);

        try
        {
            // "OK <port>" when the room has a public address, plain "OK" when it asked for
            // none or there is none to give: the host then simply has no public way.
            var publicPort = wantPublic ? OpenPublic(room, roomKey) : 0;

            if (!await SendControlAsync(room, socket, publicPort > 0 ? "OK " + publicPort.ToString(CultureInfo.InvariantCulture) : "OK").ConfigureAwait(false))
            {
                return;
            }

            while (true)
            {
                var line = await ReadLineAsync(socket, _options.ControlTimeout).ConfigureAwait(false);

                // The control connection only ever pings. Anything else is a broken
                // client, and silence past the timeout is a host that is gone.
                if (line != "PING" || !await SendControlAsync(room, socket, "PONG").ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        finally
        {
            var removed = false;

            lock (_rooms)
            {
                if (ReferenceEquals(room.Control, socket))
                {
                    removed = _rooms.Remove(roomKey);
                }

                // Counted against whoever opened the room, wherever its host has moved since.
                if (removed && _roomsByOwner.TryGetValue(room.Owner, out var held))
                {
                    if (held > 1)
                    {
                        _roomsByOwner[room.Owner] = held - 1;
                    }
                    else
                    {
                        _roomsByOwner.Remove(room.Owner);
                    }
                }
            }

            // Guests still waiting for this host get their answer now instead of at the
            // accept timeout. Tunnels already running are left to end by themselves.
            if (removed)
            {
                room.Closed.Cancel();
            }
        }
    }

    private async Task ServeGuestAsync(Socket guest, string roomKeyText)
    {
        if (!TryKey(roomKeyText, out var roomKey))
        {
            await SendLineAsync(guest, "ERR BADKEY").ConfigureAwait(false);
            return;
        }

        Room? room;

        lock (_rooms)
        {
            _rooms.TryGetValue(roomKey, out room);
        }

        if (room is null)
        {
            await SendLineAsync(guest, "ERR NOROOM").ConfigureAwait(false);
            return;
        }

        await BridgeGuestAsync(guest, room, speaksProtocol: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings a guest and the room's host together. A guest that came through the room's
    /// public port is a plain Minecraft client: it is told nothing in the relay's own
    /// words, a refusal is simply the connection closing.
    /// </summary>
    private async Task BridgeGuestAsync(Socket guest, Room room, bool speaksProtocol)
    {
        Task Say(string line) => speaksProtocol ? SendLineAsync(guest, line) : Task.CompletedTask;

        if (Interlocked.Increment(ref room.Guests) > _options.MaxGuestsPerRoom)
        {
            Interlocked.Decrement(ref room.Guests);
            await Say("ERR FULL").ConfigureAwait(false);
            return;
        }

        var connId = NewKey();
        var pending = new Pending();
        room.Pending[connId] = pending;

        try
        {
            Socket host;

            try
            {
                if (!await SendControlAsync(room, null, "OPEN " + connId).ConfigureAwait(false))
                {
                    await Say("ERR NOHOST").ConfigureAwait(false);
                    return;
                }

                host = await pending.Host.Task.WaitAsync(_options.AcceptTimeout, room.Closed.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await Say("ERR TIMEOUT").ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                await Say("ERR NOHOST").ConfigureAwait(false);
                return;
            }

            if (!await SendLineAsync(host, "OK").ConfigureAwait(false) ||
                (speaksProtocol && !await SendLineAsync(guest, "OK").ConfigureAwait(false)))
            {
                return;
            }

            Interlocked.Increment(ref _tunnels);

            try
            {
                await SpliceAsync(guest, host).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _tunnels);
            }
        }
        finally
        {
            room.Pending.TryRemove(connId, out _);

            // Cancelling loses to a host socket that got in first, which is fine: that
            // side is released by Done either way and closes its own socket.
            pending.Host.TrySetCanceled();
            pending.Done.TrySetResult();
            Interlocked.Decrement(ref room.Guests);
        }
    }

    /// <summary>
    /// Gives the room a port of its own and starts taking connections on it. The port
    /// follows from the room key, so a host that comes back gets the address it had and
    /// what it told its friends stays true. Returns 0 when public addresses are off or
    /// every port is taken.
    /// </summary>
    private int OpenPublic(Room room, string roomKey)
    {
        var count = _options.PublicPortFrom <= 0 ? 0 : Math.Min(_options.PublicPortCount, 65536 - _options.PublicPortFrom);

        if (count <= 0)
        {
            return 0;
        }

        Socket? listener = null;
        var port = 0;

        lock (_rooms)
        {
            if (room.PublicPort > 0)
            {
                return room.PublicPort;
            }

            if (room.Closed.IsCancellationRequested)
            {
                return 0;
            }

            var first = (int)(Convert.ToUInt32(roomKey[..8], 16) % (uint)count);

            for (var i = 0; i < count && listener is null; i++)
            {
                var candidate = _options.PublicPortFrom + ((first + i) % count);

                if (_publicPorts.Contains(candidate))
                {
                    continue;
                }

                try
                {
                    listener = Listen(_options.Bind, candidate);
                    port = candidate;
                }
                catch (SocketException)
                {
                    // Taken by something else on the machine: the next one.
                }
            }

            if (listener is null)
            {
                return 0;
            }

            _publicPorts.Add(port);
            room.PublicPort = port;
        }

        _ = Task.Run(() => PublicAcceptLoopAsync(room, listener, port));
        return port;
    }

    private async Task PublicAcceptLoopAsync(Room room, Socket listener, int port)
    {
        var token = room.Closed.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket socket;

                try
                {
                    socket = await listener.AcceptAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    try
                    {
                        await Task.Delay(50, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                IPAddress address;

                try
                {
                    address = Normalize(((IPEndPoint)socket.RemoteEndPoint!).Address);
                }
                catch (Exception)
                {
                    Close(socket);
                    continue;
                }

                // The same doors as on the main port: a public address is found by
                // scanners within minutes.
                if (Volatile.Read(ref _connections) >= _options.MaxConnections)
                {
                    Close(socket);
                    continue;
                }

                var gateKey = GateKey(address);

                if (!_gate.TryEnter(gateKey))
                {
                    Close(socket);
                    continue;
                }

                Interlocked.Increment(ref _connections);
                _sockets.TryAdd(socket, 0);
                _ = Task.Run(() => ServePublicAsync(socket, room, gateKey));
            }
        }
        finally
        {
            Close(listener);

            lock (_rooms)
            {
                _publicPorts.Remove(port);
            }
        }
    }

    private async Task ServePublicAsync(Socket socket, Room room, IPAddress gateKey)
    {
        try
        {
            socket.NoDelay = true;
            await BridgeGuestAsync(socket, room, speaksProtocol: false).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            _sockets.TryRemove(socket, out _);
            Close(socket);
            _gate.Leave(gateKey);
            Interlocked.Decrement(ref _connections);
        }
    }

    private async Task ServeDataAsync(Socket socket, string hostKeyText, string connIdText)
    {
        if (!TryKey(hostKeyText, out var hostKey) || !TryKey(connIdText, out var connId))
        {
            await SendLineAsync(socket, "ERR BADKEY").ConfigureAwait(false);
            return;
        }

        Room? room;

        // Finding the room by the key derived from the host key is the proof that this
        // is the host: a guest only ever sees the derived one.
        lock (_rooms)
        {
            _rooms.TryGetValue(RoomKeyFor(hostKey), out room);
        }

        if (room is null)
        {
            await SendLineAsync(socket, "ERR NOROOM").ConfigureAwait(false);
            return;
        }

        if (!room.Pending.TryRemove(connId, out var pending) || !pending.Host.TrySetResult(socket))
        {
            await SendLineAsync(socket, "ERR NOCONN").ConfigureAwait(false);
            return;
        }

        // The guest's side runs the splice. This side only has to keep its socket open
        // until that is over.
        await pending.Done.Task.ConfigureAwait(false);
    }

    private async Task ServeProbeAsync(Socket socket, IPAddress address, string portText)
    {
        // Privileged ports are never a game server, and there is no reason to let the
        // relay knock on them.
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port < 1024 || port > 65535)
        {
            await SendLineAsync(socket, "ERR BADPORT").ConfigureAwait(false);
            return;
        }

        var reachable = false;

        // Only ever the address this very connection came from: the relay must not be a
        // way to knock on somebody else's door.
        using (var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            limit.CancelAfter(_options.ProbeTimeout);

            try
            {
                await probe.ConnectAsync(new IPEndPoint(address, port), limit.Token).ConfigureAwait(false);
                reachable = true;
            }
            catch (Exception)
            {
            }
        }

        await SendLineAsync(socket, (reachable ? "REACHABLE " : "UNREACHABLE ") + address).ConfigureAwait(false);
    }

    private async Task SpliceAsync(Socket guest, Socket host)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var activity = new Activity();

        var up = PumpAsync(guest, host, activity, cancel.Token);
        var down = PumpAsync(host, guest, activity, cancel.Token);
        var idle = WatchIdleAsync(activity, cancel);

        await Task.WhenAny(up, down).ConfigureAwait(false);

        // One side has finished or failed. What the other has already read still gets
        // through, and then both go.
        cancel.CancelAfter(LingerTime);
        await Task.WhenAll(up, down).ConfigureAwait(false);

        cancel.Cancel();
        await idle.ConfigureAwait(false);
    }

    private async Task PumpAsync(Socket from, Socket to, Activity activity, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var bucket = new TokenBucket(_options.RateBytesPerSecond, _options.BurstSeconds);

        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, BufferSize), SocketFlags.None, token).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                activity.Touch();

                var wait = bucket.Take(read);

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }

                var sent = 0;

                while (sent < read)
                {
                    sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, token).ConfigureAwait(false);
                }

                activity.Touch();
            }

            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            // Reset, closed or cancelled: the tunnel is over whichever it was.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WatchIdleAsync(Activity activity, CancellationTokenSource cancel)
    {
        var idle = (long)_options.IdleTimeout.TotalMilliseconds;
        var period = TimeSpan.FromMilliseconds(Math.Clamp(idle / 4, 50, 5000));

        try
        {
            while (true)
            {
                await Task.Delay(period, cancel.Token).ConfigureAwait(false);

                if (Environment.TickCount64 - activity.Last > idle)
                {
                    cancel.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Reads up to the line break one byte at a time, so nothing past the line is taken
    /// off the socket: after the handshake the same socket carries the tunnel's bytes.
    /// Null means the line never came: closed, too long, not ASCII, or out of time.
    /// </summary>
    private async Task<string?> ReadLineAsync(Socket socket, TimeSpan timeout)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(timeout);

        var line = new byte[_options.MaxLineLength];
        var one = new byte[1];
        var length = 0;

        while (true)
        {
            int read;

            try
            {
                read = await socket.ReceiveAsync(one.AsMemory(), SocketFlags.None, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (read == 0)
            {
                return null;
            }

            var value = one[0];

            if (value == '\n')
            {
                break;
            }

            if (value == '\r')
            {
                continue;
            }

            // Keys and numbers are printable ASCII. Anything else is not a client of ours.
            if (value < 0x20 || value > 0x7E || length == line.Length)
            {
                return null;
            }

            line[length++] = value;
        }

        return Encoding.ASCII.GetString(line, 0, length);
    }

    private async Task<bool> SendLineAsync(Socket socket, string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(SendTimeout);

        try
        {
            var sent = 0;

            while (sent < bytes.Length)
            {
                sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, limit.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a line on a room's control connection. Pongs and OPENs come from different
    /// handlers, so they take turns. With <paramref name="expected"/> set the line is
    /// only sent if that socket is still the room's control connection.
    /// </summary>
    private async Task<bool> SendControlAsync(Room room, Socket? expected, string line)
    {
        try
        {
            await room.WriteLock.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            var control = room.Control;

            if (expected is not null && !ReferenceEquals(control, expected))
            {
                return false;
            }

            return await SendLineAsync(control, line).ConfigureAwait(false);
        }
        finally
        {
            room.WriteLock.Release();
        }
    }

    /// <summary>
    /// Tells a refused client why and lets it read that before the socket goes: closing
    /// with the client's own first line still unread would reset the connection and take
    /// the answer with it. Under a flood the courtesy is dropped.
    /// </summary>
    private void Reject(Socket socket, string line)
    {
        if (Interlocked.Increment(ref _lingering) > MaxLingeringRejects)
        {
            Interlocked.Decrement(ref _lingering);
            Close(socket);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.SendAsync(Encoding.ASCII.GetBytes(line + "\n").AsMemory(), SocketFlags.None, limit.Token).ConfigureAwait(false);
                socket.Shutdown(SocketShutdown.Send);

                var scratch = new byte[256];

                while (await socket.ReceiveAsync(scratch.AsMemory(), SocketFlags.None, limit.Token).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                Close(socket);
                Interlocked.Decrement(ref _lingering);
            }
        });
    }

    private static void Close(Socket? socket)
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// What the per-address limits count by. One IPv6 customer owns a whole /64, so
    /// counting single addresses there would be no limit at all.
    /// </summary>
    private static IPAddress GateKey(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// What the rooms-per-address limit counts by. Wider than <see cref="GateKey"/> for
    /// IPv6: a home line is commonly handed a /56, which is 256 of the /64s the connection
    /// limits count by - and a room, unlike a connection, is one of only a few hundred.
    /// </summary>
    private static IPAddress RoomOwner(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 7, 9);
        return new IPAddress(bytes);
    }

    private static string NewKey()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyLength / 2)).ToLowerInvariant();

    /// <summary>A key is 128 bits as 32 hex digits. Anything else is refused before it is looked up.</summary>
    private static bool TryKey(string text, out string key)
    {
        key = string.Empty;

        if (text.Length != KeyLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        key = text.ToLowerInvariant();
        return true;
    }

    private sealed class Room
    {
        public Room(Socket control, IPAddress owner)
        {
            Control = control;
            Owner = owner;
        }

        /// <summary>The address the room is counted against: the one that opened it.</summary>
        public IPAddress Owner { get; }

        public volatile Socket Control;
        public int Guests;

        /// <summary>The room's public port, or 0. Set once, under the rooms lock.</summary>
        public volatile int PublicPort;

        public ConcurrentDictionary<string, Pending> Pending { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public CancellationTokenSource Closed { get; } = new();
    }

    /// <summary>A guest waiting for the host: the host's socket comes in, "the tunnel is over" goes back.</summary>
    private sealed class Pending
    {
        public TaskCompletionSource<Socket> Host { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Activity
    {
        private long _last = Environment.TickCount64;

        public long Last => Volatile.Read(ref _last);

        public void Touch() => Volatile.Write(ref _last, Environment.TickCount64);
    }

    /// <summary>
    /// The bandwidth cap of one direction of one tunnel. Bytes are paid for after they
    /// are read, and a debt is slept off before they are sent on, so a sender is slowed
    /// by its own TCP window rather than by dropped data.
    /// </summary>
    private sealed class TokenBucket
    {
        private readonly double _rate;
        private readonly double _capacity;
        private double _tokens;
        private long _stamp;

        public TokenBucket(int bytesPerSecond, double burstSeconds)
        {
            _rate = bytesPerSecond;
            _capacity = Math.Max(bytesPerSecond * burstSeconds, 1);
            _tokens = _capacity;
            _stamp = Stopwatch.GetTimestamp();
        }

        public TimeSpan Take(int bytes)
        {
            if (_rate <= 0)
            {
                return TimeSpan.Zero;
            }

            var now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(_capacity, _tokens + (now - _stamp) / (double)Stopwatch.Frequency * _rate);
            _stamp = now;
            _tokens -= bytes;

            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
    }

    /// <summary>
    /// Counts open connections and new connections per minute for each address. An entry
    /// is forgotten a minute after its last connection closes: nothing here is written
    /// anywhere, and nothing outlives the limit it serves.
    /// </summary>
    private sealed class IpGate
    {
        private const long Window = 60_000;

        private readonly int _maxActive;
        private readonly int _maxPerWindow;
        private readonly Dictionary<IPAddress, Entry> _entries = new();
        private long _lastSweep = Environment.TickCount64;

        public IpGate(int maxActive, int maxPerWindow)
        {
            _maxActive = maxActive;
            _maxPerWindow = maxPerWindow;
        }

        public bool TryEnter(IPAddress key)
        {
            var now = Environment.TickCount64;

            lock (_entries)
            {
                Sweep(now);

                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry { WindowStart = now };
                    _entries[key] = entry;
                }

                if (now - entry.WindowStart >= Window)
                {
                    entry.WindowStart = now;
                    entry.Attempts = 0;
                }

                // Refused attempts count too: hammering the door does not reopen it.
                if (entry.Attempts < int.MaxValue)
                {
                    entry.Attempts++;
                }

                if (entry.Attempts > _maxPerWindow || entry.Active >= _maxActive)
                {
                    return false;
                }

                entry.Active++;
                return true;
            }
        }

        public void Leave(IPAddress key)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(key, out var entry) && entry.Active > 0)
                {
                    entry.Active--;
                }
            }
        }

        private void Sweep(long now)
        {
            if (now - _lastSweep < Window)
            {
                return;
            }

            _lastSweep = now;

            foreach (var (key, entry) in _entries.ToArray())
            {
                if (entry.Active == 0 && now - entry.WindowStart >= Window)
                {
                    _entries.Remove(key);
                }
            }
        }

        private sealed class Entry
        {
            public int Active;
            public int Attempts;
            public long WindowStart;
        }
    }
}
