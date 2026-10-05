using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Friends;
using STlauncher.Core.Hosting;
using STlauncher.Core.Loaders;
using STlauncher.Relay;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Simple Voice Chat over the relay way: the relay as it is, the host's and the guest's
/// launchers, a UDP echo standing in for the voice server and a TCP echo for Minecraft,
/// all on the loopback address. The guest listens on a port of its own here, because the
/// voice server's real port is taken on this same machine by the stand-in.
/// </summary>
public class VoiceChatRelayTests
{
    private const string RoomKey = "0123456789abcdef0123456789abcdef";

    private static RelayServer StartRelay(Action<RelayOptions>? tweak = null)
    {
        var options = new RelayOptions
        {
            Port = 0,
            Bind = IPAddress.Loopback,
            StatsInterval = TimeSpan.Zero,
            RateBytesPerSecond = 0,
            MaxAttemptsPerMinute = 10_000
        };

        tweak?.Invoke(options);

        var relay = new RelayServer(options);
        relay.Start();
        return relay;
    }

    private static RelayClientOptions Quick() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        AnswerTimeout = TimeSpan.FromSeconds(5),
        PingInterval = TimeSpan.FromMilliseconds(150),
        PongTimeout = TimeSpan.FromSeconds(2),
        MinBackoff = TimeSpan.FromMilliseconds(50),
        MaxBackoff = TimeSpan.FromMilliseconds(200)
    };

    private static RelayEndpoint EndpointOf(RelayServer relay) => new("127.0.0.1", relay.Port);

    private static async Task Until(Func<bool> condition, int milliseconds = 8000)
    {
        var watch = Stopwatch.StartNew();

        while (!condition() && watch.ElapsedMilliseconds < milliseconds)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The expected state was not reached in time.");
    }

    private static async Task<RelayHost> OnlineHost(RelayServer relay, int serverPort, int? voicePort)
    {
        var host = new RelayHost(EndpointOf(relay), serverPort, null, Quick(), voicePort: voicePort);
        host.Start();
        await Until(() => host.State == RelayHostState.Online);
        return host;
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static UdpClient LocalVoiceClient() => new(new IPEndPoint(IPAddress.Loopback, 0));

    /// <summary>Sends one datagram to the guest's voice port and waits for the echo; null when none came.</summary>
    private static async Task<byte[]?> RoundTrip(UdpClient client, int port, byte[] payload, int milliseconds = 10_000)
    {
        await client.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port));

        using var limit = new CancellationTokenSource(milliseconds);

        try
        {
            return (await client.ReceiveAsync(limit.Token)).Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<byte[]> TcpEcho(int port, byte[] payload)
    {
        using var game = new TcpClient { NoDelay = true };
        await game.ConnectAsync(IPAddress.Loopback, port);
        var stream = game.GetStream();

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await stream.WriteAsync(payload, limit.Token);
        var back = new byte[payload.Length];
        await stream.ReadExactlyAsync(back, limit.Token);

        return back;
    }

    /// <summary>Joins the room by hand, as a launcher would, and leaves the connection right after the OK.</summary>
    private static async Task<TcpClient> JoinByHand(RelayServer relay, string roomKey)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, relay.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("JOIN " + roomKey + "\n"));

        var line = new StringBuilder();
        var one = new byte[1];

        while (await stream.ReadAsync(one) == 1 && one[0] != '\n')
        {
            line.Append((char)one[0]);
        }

        Assert.Equal("OK", line.ToString());
        return client;
    }

    /// <summary>True when the other side hangs up within the time; false when it keeps the connection.</summary>
    private static async Task<bool> HangsUp(NetworkStream stream, int milliseconds = 8000)
    {
        using var limit = new CancellationTokenSource(milliseconds);
        var buffer = new byte[256];

        try
        {
            while (await stream.ReadAsync(buffer, limit.Token) > 0)
            {
            }

            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] Frame(byte[] datagram)
    {
        var frame = new byte[datagram.Length + 2];
        frame[0] = (byte)(datagram.Length >> 8);
        frame[1] = (byte)datagram.Length;
        datagram.CopyTo(frame, 2);
        return frame;
    }

    [Fact]
    public async Task A_guests_voice_reaches_the_voice_server_and_the_answers_come_back()
    {
        await using var relay = StartRelay();
        await using var server = new TcpEchoServer();
        using var voice = new UdpEchoServer();
        await using var host = await OnlineHost(relay, server.Port, voice.Port);

        await using var guest = new RelayGuest(EndpointOf(relay), host.RoomKey, Quick());
        guest.Start();
        Assert.True(guest.StartVoice(FreeUdpPort()));

        using var mod = LocalVoiceClient();

        for (var i = 0; i < 5; i++)
        {
            var packet = RandomNumberGenerator.GetBytes(40 + i * 200);
            Assert.Equal(packet, await RoundTrip(mod, guest.VoicePort, packet));
        }

        Assert.Equal(1, guest.ActiveVoiceTunnels);
        Assert.Single(voice.Senders);
        Assert.Equal(1, relay.Stats.Tunnels);

        // The game goes through the same room alongside, with its own tunnel.
        var payload = RandomNumberGenerator.GetBytes(50_000);
        Assert.Equal(payload, await TcpEcho(guest.LocalPort, payload));
        Assert.Equal(RelayFailure.None, guest.LastFailure);
    }

    [Fact]
    public async Task Two_local_senders_get_two_tunnels_and_the_voice_server_tells_them_apart()
    {
        await using var relay = StartRelay();
        await using var server = new TcpEchoServer();
        using var voice = new UdpEchoServer();
        await using var host = await OnlineHost(relay, server.Port, voice.Port);

        await using var guest = new RelayGuest(EndpointOf(relay), host.RoomKey, Quick());
        guest.Start();
        Assert.True(guest.StartVoice(FreeUdpPort()));

        using var first = LocalVoiceClient();
        using var second = LocalVoiceClient();

        var a = Encoding.ASCII.GetBytes("first speaker");
        var b = Encoding.ASCII.GetBytes("second speaker");

        Assert.Equal(a, await RoundTrip(first, guest.VoicePort, a));
        Assert.Equal(b, await RoundTrip(second, guest.VoicePort, b));
        Assert.Equal(a, await RoundTrip(first, guest.VoicePort, a));

        Assert.Equal(2, guest.ActiveVoiceTunnels);
        Assert.Equal(2, voice.Senders.Count);
        await Until(() => relay.Stats.Tunnels == 2);
    }

    [Fact]
    public async Task A_datagram_too_big_for_voice_is_dropped_and_the_tunnel_stays()
    {
        await using var relay = StartRelay();
        await using var server = new TcpEchoServer();
        using var voice = new UdpEchoServer();
        await using var host = await OnlineHost(relay, server.Port, voice.Port);

        await using var guest = new RelayGuest(EndpointOf(relay), host.RoomKey, Quick());
        guest.Start();
        Assert.True(guest.StartVoice(FreeUdpPort()));

        using var mod = LocalVoiceClient();
        var small = Encoding.ASCII.GetBytes("hello");

        Assert.Equal(small, await RoundTrip(mod, guest.VoicePort, small));
        Assert.Null(await RoundTrip(mod, guest.VoicePort, new byte[2000], milliseconds: 1000));
        Assert.Equal(small, await RoundTrip(mod, guest.VoicePort, small));
        Assert.Equal(1, guest.ActiveVoiceTunnels);
    }

    [Fact]
    public async Task A_malformed_frame_closes_only_that_voice_tunnel()
    {
        await using var relay = StartRelay();
        await using var server = new TcpEchoServer();
        using var voice = new UdpEchoServer();
        await using var host = await OnlineHost(relay, server.Port, voice.Port);

        await using var guest = new RelayGuest(EndpointOf(relay), host.RoomKey, Quick());
        guest.Start();
        Assert.True(guest.StartVoice(FreeUdpPort()));

        using var mod = LocalVoiceClient();
        var packet = Encoding.ASCII.GetBytes("before");
        Assert.Equal(packet, await RoundTrip(mod, guest.VoicePort, packet));

        // A voice tunnel opened by hand: one good frame comes back, then a length no
        // voice packet has ends it.
        using (var byHand = await JoinByHand(relay, host.RoomKey))
        {
            var stream = byHand.GetStream();
            await stream.WriteAsync(new[] { (byte)'V' });
            await stream.WriteAsync(Frame(Encoding.ASCII.GetBytes("ping")));

            var back = new byte[6];
            using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await stream.ReadExactlyAsync(back, limit.Token);
            }

            Assert.Equal(Frame(Encoding.ASCII.GetBytes("ping")), back);

            await stream.WriteAsync(new byte[] { 0xFF, 0xFF, 1, 2, 3 });
            Assert.True(await HangsUp(stream));
        }

        // A zero length is no voice packet either.
        using (var empty = await JoinByHand(relay, host.RoomKey))
        {
            var stream = empty.GetStream();
            await stream.WriteAsync(new byte[] { (byte)'V', 0, 0 });
            Assert.True(await HangsUp(stream));
        }

        // The guest's own voice tunnel and the game are untouched.
        packet = Encoding.ASCII.GetBytes("after");
        Assert.Equal(packet, await RoundTrip(mod, guest.VoicePort, packet));

        var payload = RandomNumberGenerator.GetBytes(4096);
        Assert.Equal(payload, await TcpEcho(guest.LocalPort, payload));
        Assert.Equal(RelayHostState.Online, host.State);
    }

    [Fact]
    public async Task A_tunnel_with_an_unknown_tag_or_voice_to_a_server_without_it_is_closed()
    {
        await using var relay = StartRelay();
        await using var server = new TcpEchoServer();
        await using var host = await OnlineHost(relay, server.Port, voicePort: null);

        using (var stranger = await JoinByHand(relay, host.RoomKey))
        {
            var stream = stranger.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("Xhello"));
            Assert.True(await HangsUp(stream));
        }

        using (var noVoice = await JoinByHand(relay, host.RoomKey))
        {
            var stream = noVoice.GetStream();
            await stream.WriteAsync(new[] { (byte)'V' });
            await stream.WriteAsync(Frame(Encoding.ASCII.GetBytes("anyone?")));
            Assert.True(await HangsUp(stream));
        }

        // The game's tag still leads to the server.
        using (var game = await JoinByHand(relay, host.RoomKey))
        {
            var stream = game.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("Gecho"));

            var back = new byte[4];
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await stream.ReadExactlyAsync(back, limit.Token);
            Assert.Equal("echo", Encoding.ASCII.GetString(back));
        }
    }

    [Fact]
    public async Task A_silent_voice_tunnel_is_closed_by_the_guest_and_the_next_packet_opens_another()
    {
        await using var relay = StartRelay();
        await using var server = new TcpEchoServer();
        using var voice = new UdpEchoServer();
        await using var host = await OnlineHost(relay, server.Port, voice.Port);

        var options = Quick();
        options.VoiceIdleTimeout = TimeSpan.FromMilliseconds(400);

        await using var guest = new RelayGuest(EndpointOf(relay), host.RoomKey, options);
        guest.Start();
        Assert.True(guest.StartVoice(FreeUdpPort()));

        using var mod = LocalVoiceClient();
        var packet = Encoding.ASCII.GetBytes("one");

        Assert.Equal(packet, await RoundTrip(mod, guest.VoicePort, packet));
        Assert.Equal(1, guest.ActiveVoiceTunnels);

        await Until(() => guest.ActiveVoiceTunnels == 0);
        await Until(() => relay.Stats.Tunnels == 0);

        Assert.Equal(packet, await RoundTrip(mod, guest.VoicePort, packet));
        Assert.Equal(1, guest.ActiveVoiceTunnels);
    }

    [Fact]
    public async Task A_voice_port_taken_on_the_guests_machine_leaves_the_game_working()
    {
        await using var relay = StartRelay();
        await using var server = new TcpEchoServer();

        // The stand-in voice server holds the port on this machine, as another program would.
        using var voice = new UdpEchoServer();

        await using var session = new FriendsHostSession(server.Port, EndpointOf(relay), null, Quick(), voicePort: voice.Port);
        Assert.True(session.StartRelay());
        await Until(() => session.Status.Relay == FriendsWayState.Ready);
        Assert.Equal(voice.Port, session.Status.Endpoints.VoicePort);

        await using var guest = new RelayGuest(EndpointOf(relay), session.Status.RoomKey!, Quick());
        guest.Start();

        Assert.False(guest.StartVoice(voice.Port));
        Assert.Equal(0, guest.VoicePort);

        var payload = RandomNumberGenerator.GetBytes(2048);
        Assert.Equal(payload, await TcpEcho(guest.LocalPort, payload));
    }

    [Fact]
    public async Task A_public_room_still_carries_plain_clients_without_a_tag()
    {
        var port = FreeTcpPort();
        await using var relay = StartRelay(o => { o.PublicPortFrom = port; o.PublicPortCount = 1; });
        await using var server = new TcpEchoServer();
        using var voice = new UdpEchoServer();

        await using var session = new FriendsHostSession(server.Port, EndpointOf(relay), null, Quick(), voicePort: voice.Port);
        Assert.True(session.StartPublic());
        await Until(() => session.Status.Public == FriendsWayState.Ready);

        // 'V' first, as a Minecraft handshake of 86 bytes would begin: it must reach the server as it is.
        var payload = new byte[] { (byte)'V', 1, 2, 3, (byte)'G' };
        Assert.Equal(payload, await TcpEcho(port, payload));
    }

    [Fact]
    public void The_voice_port_travels_in_the_invite_and_old_invites_still_read()
    {
        var endpoints = new ServerInviteEndpoints(null, "relay.example.org:25580", RoomKey, null) { VoicePort = 24454 };
        var invite = new ServerInvite("Наш мир", "1.21.11", LoaderKind.Fabric, "0.19.5", "Steve", null, endpoints);

        Assert.True(ServerInviteCode.TryDecode(ServerInviteCode.Encode(invite), out var back));
        Assert.Equal(24454, back!.Endpoints.VoicePort);
        Assert.Equal(endpoints, back.Endpoints);

        var without = invite with { Endpoints = endpoints with { VoicePort = null } };
        Assert.True(ServerInviteCode.TryDecode(ServerInviteCode.Encode(without), out back));
        Assert.Null(back!.Endpoints.VoicePort);

        // Written by a launcher that knew nothing of voice.
        Assert.True(ServerInviteCode.TryDecode(Pack("{\"v\":\"1.21.11\",\"r\":\"relay.example.org:25580\",\"k\":\"" + RoomKey + "\"}"), out back));
        Assert.Null(back!.Endpoints.VoicePort);
        Assert.True(back.Endpoints.HasRelay);

        // A port that cannot be one is dropped; the rest of the invite stands.
        Assert.True(ServerInviteCode.TryDecode(Pack("{\"v\":\"1.21.11\",\"r\":\"relay.example.org:25580\",\"k\":\"" + RoomKey + "\",\"voicePort\":70000}"), out back));
        Assert.Null(back!.Endpoints.VoicePort);
        Assert.True(back.Endpoints.HasRelay);

        // The friend's list keeps it, so "Play" next time knows to listen.
        var servers = new List<FriendServer>();
        var saved = FriendServerStore.Remember(servers, invite, "build");
        Assert.Equal(24454, saved.VoicePort);
        Assert.Equal(endpoints, saved.Endpoints);
    }

    [Fact]
    public void The_voice_port_is_read_from_the_servers_own_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "stl-voice-" + Guid.NewGuid().ToString("N"));

        try
        {
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(mods);

            Assert.Null(ServerVoiceChat.FindPort(root, 25565));

            // Never started: no config yet, the mod's default.
            File.WriteAllBytes(Path.Combine(mods, "voicechat-fabric-2.6.22.jar"), Array.Empty<byte>());
            Assert.Equal(24454, ServerVoiceChat.FindPort(root, 25565));

            var config = ServerVoiceChat.ConfigPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);

            File.WriteAllText(config, "# Simple Voice Chat server config v1.0.0\nport=25001\nbind_address=\nvoice_host=\n");
            Assert.Equal(25001, ServerVoiceChat.FindPort(root, 25565));

            // -1 is the mod's word for "the Minecraft server's port".
            File.WriteAllText(config, "port=-1\n");
            Assert.Equal(25570, ServerVoiceChat.FindPort(root, 25570));

            File.WriteAllText(config, "port=banana\n");
            Assert.Equal(24454, ServerVoiceChat.FindPort(root, 25565));

            // Switched off in the launcher, or removed: the config left behind does not count.
            File.Move(Path.Combine(mods, "voicechat-fabric-2.6.22.jar"), Path.Combine(mods, "voicechat-fabric-2.6.22.jar.disabled"));
            Assert.Null(ServerVoiceChat.FindPort(root, 25565));

            // Renamed, it is still found by the id it carries.
            using (var jar = ZipFile.Open(Path.Combine(mods, "svc.jar"), ZipArchiveMode.Create))
            using (var writer = new StreamWriter(jar.CreateEntry("fabric.mod.json").Open()))
            {
                writer.Write("{\"schemaVersion\":1,\"id\":\"voicechat\",\"version\":\"2.6.22\"}");
            }

            File.WriteAllText(config, "port=25002\n");
            Assert.Equal(25002, ServerVoiceChat.FindPort(root, 25565));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Pack(string json)
    {
        using var buffer = new MemoryStream();

        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(json));
        }

        return ServerInviteCode.Prefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Sends every datagram back where it came from, and remembers who sent.</summary>
    private sealed class UdpEchoServer : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public UdpEchoServer()
        {
            Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
            _loop = Task.Run(EchoAsync);
        }

        public int Port { get; }

        public ConcurrentDictionary<IPEndPoint, int> Senders { get; } = new();

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        private async Task EchoAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var received = await _socket.ReceiveAsync(_stop.Token);
                    Senders.AddOrUpdate(received.RemoteEndPoint, 1, (_, n) => n + 1);
                    await _socket.SendAsync(received.Buffer, received.RemoteEndPoint, _stop.Token);
                }
                catch (SocketException)
                {
                    // An ICMP report for a sender that has gone; the next one is still served.
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Sends back whatever it receives over TCP.</summary>
    private sealed class TcpEchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public TcpEchoServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);

                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                var buffer = new byte[8192];
                                int read;

                                while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                                {
                                    await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                                }
                            }
                            catch (Exception)
                            {
                            }
                        }
                    });
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
