using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace STlauncher.Core.Friends;

/// <summary>
/// The first byte a guest's launcher sends into a tunnel of the invite room, right after
/// the relay's OK. The relay passes it on like any other byte; the host's launcher reads
/// it to know where the tunnel goes. Rooms with a public port carry plain Minecraft
/// clients, which send no such byte, so only the invite room uses it.
/// </summary>
internal static class RelayChannel
{
    /// <summary>The game: spliced to the Minecraft server's TCP port.</summary>
    public const byte Game = (byte)'G';

    /// <summary>Voice chat: datagrams in frames, handed to the voice server's UDP port.</summary>
    public const byte Voice = (byte)'V';
}

/// <summary>
/// Datagrams carried over a TCP tunnel: each one as a two-byte big-endian length and the
/// bytes. The relay only splices streams, so the edges of a datagram have to travel with
/// it. A frame that cannot be a voice packet ends the tunnel it came in.
/// </summary>
internal static class VoiceFrames
{
    /// <summary>
    /// The largest datagram carried. Simple Voice Chat keeps its packets under the usual
    /// Ethernet MTU; anything bigger is not voice and is not passed on.
    /// </summary>
    public const int MaxDatagram = 1500;

    public static bool IsCarried(int length) => length is > 0 and <= MaxDatagram;

    /// <summary>Writes one frame. Header and bytes go in one write so a frame is never split between two writers.</summary>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
    {
        if (!IsCarried(datagram.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(datagram));
        }

        var frame = new byte[datagram.Length + 2];
        frame[0] = (byte)(datagram.Length >> 8);
        frame[1] = (byte)datagram.Length;
        datagram.CopyTo(frame.AsMemory(2));

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame into <paramref name="buffer"/> and returns the datagram's length:
    /// 0 when the tunnel ended cleanly between frames. A length that is zero or over the
    /// limit, or a frame cut off half way, throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static async Task<int> ReadAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        var got = await stream.ReadAtLeastAsync(header, 2, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

        if (got == 0)
        {
            return 0;
        }

        if (got < 2)
        {
            throw new InvalidDataException("The tunnel ended inside a frame header.");
        }

        var length = (header[0] << 8) | header[1];

        if (!IsCarried(length) || length > buffer.Length)
        {
            throw new InvalidDataException("A frame of " + length + " bytes is not a voice packet.");
        }

        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The tunnel ended inside a frame.");
        }

        return length;
    }
}

/// <summary>UDP sockets on the loopback address, as both ends of the voice way use them.</summary>
internal static class LoopbackUdp
{
    // A whole UDP datagram fits; anything over the frame limit is then dropped by length
    // instead of failing the receive, which is what Windows does with a short buffer.
    public const int ReceiveBuffer = 64 * 1024;

    // SIO_UDP_CONNRESET: IOC_IN | IOC_VENDOR | 12.
    private const int UdpConnReset = unchecked((int)0x9800000C);

    /// <param name="exclusive">
    /// Refuse the port when anything else holds it, including on the wildcard address:
    /// a listener that shared the port with another program would get half its packets.
    /// </param>
    public static Socket Open(int port, bool exclusive)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            if (exclusive && OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = true;
            }

            IgnoreConnectionResets(socket);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Windows reports an ICMP "port unreachable" for an earlier send as an error on the
    /// next receive. The voice server not being up yet is not a reason to stop listening,
    /// so the report is switched off; where that cannot be done, the receive loops skip it.
    /// </summary>
    private static void IgnoreConnectionResets(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            socket.IOControl(UdpConnReset, new byte[4], null);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// The host's end of one guest's voice tunnel: a UDP socket of its own that talks to the
/// voice server on this machine. One socket per tunnel, so the voice server sees every
/// guest at a port of their own and tells them apart the way it would over the internet.
/// </summary>
internal static class VoiceHostSide
{
    public static async Task RunAsync(NetworkStream tunnel, int voicePort, CancellationToken cancellationToken)
    {
        var server = new IPEndPoint(IPAddress.Loopback, voicePort);

        using var udp = LoopbackUdp.Open(0, exclusive: false);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var up = Task.Run(async () =>
        {
            var buffer = new byte[VoiceFrames.MaxDatagram];

            try
            {
                while (true)
                {
                    var length = await VoiceFrames.ReadAsync(tunnel, buffer, cancel.Token).ConfigureAwait(false);

                    if (length == 0)
                    {
                        break;
                    }

                    await udp.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, server, cancel.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Closed, reset, or a frame that is not voice: this tunnel is over.
            }
            finally
            {
                cancel.Cancel();
            }
        });

        var down = Task.Run(async () =>
        {
            var buffer = new byte[LoopbackUdp.ReceiveBuffer];
            var any = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (true)
                {
                    SocketReceiveFromResult received;

                    try
                    {
                        received = await udp.ReceiveFromAsync(buffer, SocketFlags.None, any, cancel.Token).ConfigureAwait(false);
                    }
                    catch (SocketException) when (!cancel.IsCancellationRequested)
                    {
                        // An ICMP report of an earlier send, where it could not be switched off.
                        continue;
                    }

                    // Only the voice server's answers go back to the guest: the socket is
                    // on loopback, but other programs on this machine are on it too.
                    if (!server.Equals(received.RemoteEndPoint) || !VoiceFrames.IsCarried(received.ReceivedBytes))
                    {
                        continue;
                    }

                    await VoiceFrames.WriteAsync(tunnel, buffer.AsMemory(0, received.ReceivedBytes), cancel.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Cancelled with the tunnel, or the tunnel could not be written to.
            }
            finally
            {
                cancel.Cancel();
            }
        });

        await Task.WhenAll(up, down).ConfigureAwait(false);
    }
}

/// <summary>
/// The guest's end of the voice way: a UDP port on 127.0.0.1 with the number the host's
/// voice server has. The voice chat mod in the game sends to the address it joined the
/// game by, which on the relay way is this machine, so it finds this port. Each local
/// sender gets a tunnel of its own through the relay, opened on its first packet and
/// closed when it falls silent.
/// </summary>
internal sealed class VoiceGuestSide : IAsyncDisposable
{
    // One game has one voice connection; a few more cover the mod reconnecting from a
    // new port while the old tunnel has not timed out yet.
    private const int MaxTunnels = 4;

    // Packets that come while the tunnel is still being opened wait here. Voice older
    // than this is worth nothing, so the oldest goes first when it is full.
    private const int QueueLength = 64;

    // After a tunnel could not be opened, new ones wait this long: the mod sends every
    // second or so, and each try would count against this address's limit at the relay.
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<TcpClient?>> _open;
    private readonly TimeSpan _idle;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<IPEndPoint, Link> _links = new();
    private readonly Task _loop;

    private long _quietUntil;

    /// <param name="open">Opens a voice tunnel through the relay, the tag already sent; null when it could not.</param>
    /// <exception cref="SocketException">The port is taken by another program.</exception>
    public VoiceGuestSide(int port, Func<CancellationToken, Task<TcpClient?>> open, TimeSpan idle)
    {
        _open = open;
        _idle = idle;
        _socket = LoopbackUdp.Open(port, exclusive: true);
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _loop = Task.Run(ReceiveAsync);
    }

    public int Port { get; }

    public int ActiveTunnels
    {
        get
        {
            lock (_links)
            {
                return _links.Count;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket.Dispose();
        await _loop.ConfigureAwait(false);

        List<Link> links;

        lock (_links)
        {
            links = _links.Values.ToList();
            _links.Clear();
        }

        foreach (var link in links)
        {
            link.Stop.Cancel();
        }

        await Task.WhenAll(links.Select(l => l.Run)).ConfigureAwait(false);
    }

    private async Task ReceiveAsync()
    {
        var token = _stop.Token;
        var buffer = new byte[LoopbackUdp.ReceiveBuffer];
        var any = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult received;

            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            if (!VoiceFrames.IsCarried(received.ReceivedBytes) || received.RemoteEndPoint is not IPEndPoint from)
            {
                continue;
            }

            var link = LinkFor(from);

            if (link is null)
            {
                continue;
            }

            link.Touch();
            link.Outgoing.Writer.TryWrite(buffer.AsSpan(0, received.ReceivedBytes).ToArray());
        }
    }

    private Link? LinkFor(IPEndPoint client)
    {
        Link? evicted = null;
        Link link;

        lock (_links)
        {
            if (_links.TryGetValue(client, out var known))
            {
                return known;
            }

            if (Environment.TickCount64 < Volatile.Read(ref _quietUntil))
            {
                return null;
            }

            if (_links.Count >= MaxTunnels)
            {
                // The one silent longest is the one a reconnecting mod has left behind.
                evicted = _links.Values.OrderBy(l => l.LastActive).First();
                _links.Remove(evicted.Client);
            }

            link = new Link(client, CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
            _links[client] = link;
        }

        evicted?.Stop.Cancel();
        link.Run = Task.Run(() => CarryAsync(link));
        return link;
    }

    private async Task CarryAsync(Link link)
    {
        var token = link.Stop.Token;

        try
        {
            using var relay = await _open(token).ConfigureAwait(false);

            if (relay is null)
            {
                Volatile.Write(ref _quietUntil, Environment.TickCount64 + (long)RetryPause.TotalMilliseconds);
                return;
            }

            var stream = relay.GetStream();
            link.Touch();

            var up = Task.Run(async () =>
            {
                try
                {
                    await foreach (var datagram in link.Outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
                    {
                        await VoiceFrames.WriteAsync(stream, datagram, token).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                }
            });

            var down = Task.Run(async () =>
            {
                var buffer = new byte[VoiceFrames.MaxDatagram];

                try
                {
                    while (true)
                    {
                        var length = await VoiceFrames.ReadAsync(stream, buffer, token).ConfigureAwait(false);

                        if (length == 0)
                        {
                            break;
                        }

                        link.Touch();
                        await _socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, link.Client, token).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // The host or the relay hung up, or sent something that is not voice.
                }
            });

            var idle = WatchIdleAsync(link, token);

            await Task.WhenAny(up, down, idle).ConfigureAwait(false);
            link.Stop.Cancel();
            await Task.WhenAll(up, down, idle).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // One tunnel failing is that tunnel's business; the next packet opens another.
        }
        finally
        {
            link.Stop.Cancel();

            lock (_links)
            {
                if (_links.TryGetValue(link.Client, out var current) && ReferenceEquals(current, link))
                {
                    _links.Remove(link.Client);
                }
            }
        }
    }

    private async Task WatchIdleAsync(Link link, CancellationToken token)
    {
        var limit = (long)_idle.TotalMilliseconds;
        var period = TimeSpan.FromMilliseconds(Math.Clamp(limit / 4, 20, 5000));

        try
        {
            while (Environment.TickCount64 - link.LastActive <= limit)
            {
                await Task.Delay(period, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class Link
    {
        private long _lastActive = Environment.TickCount64;

        public Link(IPEndPoint client, CancellationTokenSource stop)
        {
            Client = client;
            Stop = stop;
        }

        public IPEndPoint Client { get; }

        public CancellationTokenSource Stop { get; }

        public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueLength)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        public Task Run { get; set; } = Task.CompletedTask;

        public long LastActive => Volatile.Read(ref _lastActive);

        public void Touch() => Volatile.Write(ref _lastActive, Environment.TickCount64);
    }
}
