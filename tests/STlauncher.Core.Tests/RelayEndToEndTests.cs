using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Friends;
using STlauncher.Core.Loaders;
using STlauncher.Core.Server;
using STlauncher.Relay;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The relay, the host's side and the guest's side together, all on the loopback
/// address: the relay on a port the system picks, a small echo server standing in for
/// Minecraft.
/// </summary>
public class RelayEndToEndTests
{
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

    private static async Task<RelayHost> OnlineHost(RelayServer relay, int serverPort)
    {
        var host = new RelayHost(EndpointOf(relay), serverPort, null, Quick());
        host.Start();
        await Until(() => host.State == RelayHostState.Online);
        return host;
    }

    /// <summary>Connects, says one line and reads the one-line answer. The connection stays open for the caller.</summary>
    private static async Task<(TcpClient Client, string? Answer)> Ask(int port, string? line)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);

        if (line is not null)
        {
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(line + "\n"));
        }

        return (client, await ReadLine(client.GetStream()));
    }

    /// <summary>One line, or null when the other side closed or reset without saying anything.</summary>
    private static async Task<string?> ReadLine(NetworkStream stream, int milliseconds = 8000)
    {
        using var limit = new CancellationTokenSource(milliseconds);
        var text = new StringBuilder();
        var one = new byte[1];

        try
        {
            while (true)
            {
                if (await stream.ReadAsync(one.AsMemory(0, 1), limit.Token) == 0)
                {
                    return null;
                }

                if (one[0] == '\n')
                {
                    return text.ToString();
                }

                text.Append((char)one[0]);
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<byte[]> Echo(int port, byte[] payload)
    {
        using var game = new TcpClient { NoDelay = true };
        await game.ConnectAsync(IPAddress.Loopback, port);
        var stream = game.GetStream();

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var write = stream.WriteAsync(payload, limit.Token).AsTask();
        var back = new byte[payload.Length];
        await stream.ReadExactlyAsync(back, limit.Token);
        await write;

        return back;
    }

    [Fact]
    public async Task Bytes_travel_both_ways_between_a_guest_and_the_hosts_server()
    {
        await using var relay = StartRelay();
        await using var server = new EchoServer();
        await using var host = await OnlineHost(relay, server.Port);

        await using var guest = new RelayGuest(EndpointOf(relay), host.RoomKey, Quick());
        guest.Start();

        using var game = new TcpClient { NoDelay = true };
        await game.ConnectAsync(IPAddress.Loopback, guest.LocalPort);
        var stream = game.GetStream();

        var sent = RandomNumberGenerator.GetBytes(200_000);
        var write = stream.WriteAsync(sent).AsTask();
        var back = new byte[sent.Length];

        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            await stream.ReadExactlyAsync(back, limit.Token);
        }

        await write;

        Assert.Equal(sent, back);
        Assert.Equal(RelayFailure.None, guest.LastFailure);
        Assert.Equal(1, relay.Stats.Rooms);
        Assert.Equal(1, relay.Stats.Tunnels);
        Assert.Equal(1, host.ActiveConnections);
    }

    [Fact]
    public async Task A_guest_is_told_when_the_room_is_not_there_and_nonsense_is_refused()
    {
        await using var relay = StartRelay();

        var (unknown, noRoom) = await Ask(relay.Port, "JOIN " + RelayKeys.NewHostKey());
        var (malformed, badKey) = await Ask(relay.Port, "JOIN not-a-key");
        var (stranger, badCommand) = await Ask(relay.Port, "GET / HTTP/1.1");

        using (unknown)
        using (malformed)
        using (stranger)
        {
            Assert.Equal("ERR NOROOM", noRoom);
            Assert.Equal("ERR BADKEY", badKey);
            Assert.Equal("ERR BADCMD", badCommand);
        }

        // The launcher's side turns the same answer into "the host is offline".
        await using var guest = new RelayGuest(EndpointOf(relay), RelayKeys.NewHostKey(), Quick());
        guest.Start();

        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, guest.LocalPort);

        await Until(() => guest.LastFailure == RelayFailure.HostOffline);
    }

    [Fact]
    public async Task A_room_takes_only_as_many_guests_as_the_limit_allows()
    {
        await using var relay = StartRelay(o => o.MaxGuestsPerRoom = 1);
        await using var server = new EchoServer();
        await using var host = await OnlineHost(relay, server.Port);

        var (first, firstAnswer) = await Ask(relay.Port, "JOIN " + host.RoomKey);
        var (second, secondAnswer) = await Ask(relay.Port, "JOIN " + host.RoomKey);

        using (first)
        using (second)
        {
            Assert.Equal("OK", firstAnswer);
            Assert.Equal("ERR FULL", secondAnswer);
        }
    }

    [Fact]
    public async Task The_key_in_an_invite_does_not_open_the_hosts_room()
    {
        await using var relay = StartRelay();
        await using var server = new EchoServer();
        await using var host = await OnlineHost(relay, server.Port);

        Assert.NotEqual(host.HostKey, host.RoomKey);
        Assert.Equal(RelayServer.RoomKeyFor(host.HostKey), host.RoomKey);

        // Somebody who only has the invite tries to take the room over with its key.
        var (imposter, answer) = await Ask(relay.Port, "HOST " + host.RoomKey);

        using (imposter)
        {
            // They get a room, but a different one: the host's is untouched.
            Assert.Equal("OK", answer);
            Assert.Equal(2, relay.Stats.Rooms);

            await using var guest = new RelayGuest(EndpointOf(relay), host.RoomKey, Quick());
            guest.Start();

            var payload = RandomNumberGenerator.GetBytes(4096);
            Assert.Equal(payload, await Echo(guest.LocalPort, payload));
            Assert.Equal(RelayHostState.Online, host.State);
        }
    }

    [Fact]
    public async Task A_guest_is_let_go_when_the_host_never_picks_it_up()
    {
        await using var relay = StartRelay(o => o.AcceptTimeout = TimeSpan.FromMilliseconds(300));

        // A host that opens the room and then ignores what the relay tells it.
        var hostKey = RelayKeys.NewHostKey();
        var (control, opened) = await Ask(relay.Port, "HOST " + hostKey);

        using (control)
        {
            Assert.Equal("OK", opened);

            var (guest, answer) = await Ask(relay.Port, "JOIN " + RelayKeys.RoomKeyFor(hostKey));

            using (guest)
            {
                Assert.Equal("ERR TIMEOUT", answer);
            }

            var notice = await ReadLine(control.GetStream());
            Assert.StartsWith("OPEN ", notice);
            Assert.True(RelayKeys.IsKey(notice![5..]));
        }
    }

    [Fact]
    public async Task A_line_that_is_too_long_or_never_comes_gets_no_answer()
    {
        await using var relay = StartRelay(o =>
        {
            o.MaxLineLength = 64;
            o.HandshakeTimeout = TimeSpan.FromMilliseconds(300);
        });

        var (talker, tooLong) = await Ask(relay.Port, "JOIN " + new string('a', 500));
        var (mute, nothing) = await Ask(relay.Port, null);

        using (talker)
        using (mute)
        {
            Assert.Null(tooLong);
            Assert.Null(nothing);
        }

        await Until(() => relay.Stats.Connections == 0);
    }

    [Fact]
    public async Task One_address_gets_only_so_many_connections_and_attempts()
    {
        await using var crowded = StartRelay(o => o.MaxConnectionsPerIp = 2);
        await using var hammered = StartRelay(o => o.MaxAttemptsPerMinute = 3);

        var held = new List<TcpClient>();

        try
        {
            for (var i = 0; i < 2; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, crowded.Port);
                held.Add(client);
            }

            await Until(() => crowded.Stats.Connections == 2);

            var (third, thirdAnswer) = await Ask(crowded.Port, null);
            held.Add(third);
            Assert.Equal("ERR LIMIT", thirdAnswer);

            // Attempts are counted whether or not the earlier ones are still open.
            for (var i = 0; i < 3; i++)
            {
                var (client, answer) = await Ask(hammered.Port, "JOIN " + RelayKeys.NewHostKey());
                client.Dispose();
                Assert.Equal("ERR NOROOM", answer);
            }

            var (fourth, fourthAnswer) = await Ask(hammered.Port, "JOIN " + RelayKeys.NewHostKey());
            held.Add(fourth);
            Assert.Equal("ERR LIMIT", fourthAnswer);
        }
        finally
        {
            foreach (var client in held)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task One_address_holds_only_so_many_rooms()
    {
        await using var relay = StartRelay(o => o.MaxRoomsPerIp = 2);

        var (first, firstAnswer) = await Ask(relay.Port, "HOST " + RelayKeys.NewHostKey());
        var (second, secondAnswer) = await Ask(relay.Port, "HOST " + RelayKeys.NewHostKey());
        var (third, thirdAnswer) = await Ask(relay.Port, "HOST " + RelayKeys.NewHostKey());

        using (second)
        using (third)
        {
            Assert.Equal("OK", firstAnswer);
            Assert.Equal("OK", secondAnswer);

            // Not "full": the relay has rooms, this address has had its share of them.
            Assert.Equal("ERR LIMIT", thirdAnswer);
            Assert.Equal(2, relay.Stats.Rooms);

            // A room given back is a room that can be taken again.
            first.Dispose();
            await Until(() => relay.Stats.Rooms == 1);

            var (fourth, fourthAnswer) = await Ask(relay.Port, "HOST " + RelayKeys.NewHostKey());

            using (fourth)
            {
                Assert.Equal("OK", fourthAnswer);
            }
        }
    }

    [Fact]
    public async Task A_silent_tunnel_is_closed_after_the_idle_time()
    {
        await using var relay = StartRelay(o => o.IdleTimeout = TimeSpan.FromMilliseconds(400));
        await using var server = new EchoServer();
        await using var host = await OnlineHost(relay, server.Port);

        var (guest, answer) = await Ask(relay.Port, "JOIN " + host.RoomKey);

        using (guest)
        {
            Assert.Equal("OK", answer);
            await Until(() => relay.Stats.Tunnels == 1);

            // Nothing is said, and the relay hangs up.
            Assert.Null(await ReadLine(guest.GetStream()));
            await Until(() => relay.Stats.Tunnels == 0);
        }
    }

    [Fact]
    public async Task The_bandwidth_cap_slows_a_tunnel_down()
    {
        await using var relay = StartRelay(o =>
        {
            o.RateBytesPerSecond = 40_000;
            o.BurstSeconds = 0;
        });

        await using var server = new EchoServer();
        await using var host = await OnlineHost(relay, server.Port);

        await using var guest = new RelayGuest(EndpointOf(relay), host.RoomKey, Quick());
        guest.Start();

        var payload = RandomNumberGenerator.GetBytes(60_000);
        var watch = Stopwatch.StartNew();
        var back = await Echo(guest.LocalPort, payload);
        watch.Stop();

        // 60 KB at 40 KB/s is a second and a half; without the cap it is milliseconds.
        Assert.Equal(payload, back);
        Assert.True(watch.ElapsedMilliseconds >= 1000, $"The echo took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task The_host_comes_back_by_itself_when_the_relay_does()
    {
        var first = StartRelay();
        var port = first.Port;

        await using var server = new EchoServer();
        await using var host = await OnlineHost(first, server.Port);

        await first.StopAsync();

        await Until(() => host.State == RelayHostState.Reconnecting);
        Assert.Equal(RelayFailure.RelayUnreachable, host.LastFailure);

        await using var second = StartRelay(o => o.Port = port);
        await Until(() => host.State == RelayHostState.Online);

        // The room has the same key as before, so an invite sent earlier still works.
        await using var guest = new RelayGuest(EndpointOf(second), host.RoomKey, Quick());
        guest.Start();

        var payload = RandomNumberGenerator.GetBytes(2048);
        Assert.Equal(payload, await Echo(guest.LocalPort, payload));
    }

    [Fact]
    public async Task Probe_says_whether_the_callers_own_port_is_open()
    {
        await using var relay = StartRelay();
        await using var open = new EchoServer();

        var closedPort = FreePort();

        var reachable = await RelayProbe.CheckAsync(EndpointOf(relay), open.Port, Quick());
        var unreachable = await RelayProbe.CheckAsync(EndpointOf(relay), closedPort, Quick());
        var privileged = await RelayProbe.CheckAsync(EndpointOf(relay), 80, Quick());
        var noRelay = await RelayProbe.CheckAsync(null, open.Port, Quick());
        var deadRelay = await RelayProbe.CheckAsync(new RelayEndpoint("127.0.0.1", closedPort), open.Port, Quick());

        Assert.Equal(Reachability.Reachable, reachable.Outcome);
        Assert.Equal("127.0.0.1", reachable.PublicAddress);
        Assert.Equal(Reachability.Unreachable, unreachable.Outcome);
        Assert.Equal(Reachability.Unknown, privileged.Outcome);
        Assert.Equal(RelayFailure.Rejected, privileged.Failure);
        Assert.Equal(RelayFailure.NotConfigured, noRelay.Failure);
        Assert.Equal(RelayFailure.RelayUnreachable, deadRelay.Failure);
    }

    [Fact]
    public async Task An_invite_from_a_hosting_session_leads_a_friend_to_the_server()
    {
        await using var relay = StartRelay();
        await using var server = new StatusServer("hello from the host");

        await using var session = new FriendsHostSession(server.Port, EndpointOf(relay), null, Quick());
        Assert.True(session.StartRelay());
        await Until(() => session.Status.Relay == FriendsWayState.Ready);

        var code = ServerInviteCode.Encode(session.BuildInvite("Наш мир", "1.21.11", LoaderKind.Fabric, "0.19.5", "Steve", null));

        Assert.True(ServerInviteCode.TryDecode(code, out var invite));
        Assert.Equal(EndpointOf(relay).ToString(), invite!.Endpoints.Relay);
        Assert.Null(invite.Endpoints.Direct);

        await using var join = await FriendsJoin.ConnectAsync(invite.Endpoints, Quick());

        Assert.Equal(FriendsWay.Relay, join.Way);
        Assert.StartsWith("127.0.0.1:", join.Address);
        Assert.Equal("hello from the host", join.Server!.Motd);

        // The address is one the game can join: a second ping through it works too.
        Assert.True(HostPort.TryParse(join.Address, out var host, out var port));
        Assert.NotNull(await ServerPinger.PingAsync(host, port!.Value, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_public_address_from_the_relay_leads_a_plain_client_to_the_server()
    {
        var port = FreePort();
        await using var relay = StartRelay(o => { o.PublicPortFrom = port; o.PublicPortCount = 1; });
        await using var server = new StatusServer("public hello");

        await using var session = new FriendsHostSession(server.Port, EndpointOf(relay), null, Quick());
        Assert.True(session.StartPublic());
        await Until(() => session.Status.Public == FriendsWayState.Ready);

        // No launcher on this side: the address is pinged the way Minecraft would.
        Assert.Equal("127.0.0.1:" + port, session.Status.PublicAddress);
        Assert.Equal("127.0.0.1:" + port, session.Status.Endpoints.Public);

        var answer = await ServerPinger.PingAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));
        Assert.Equal("public hello", answer!.Motd);

        // The one port is taken, so a second server gets none and says so.
        await using var second = new FriendsHostSession(server.Port, EndpointOf(relay), null, Quick());
        Assert.True(second.StartPublic());
        await Until(() => second.Status.Public == FriendsWayState.Failed);
        Assert.Equal(RelayFailure.None, second.Status.PublicFailure);
        Assert.Null(second.Status.PublicAddress);

        // Switched off, the address stops answering and the port is free again.
        await session.StopPublicAsync();
        Assert.Null(session.Status.PublicAddress);
        await Until(() => relay.Stats.Rooms == 1);
    }

    [Fact]
    public async Task A_relay_without_public_ports_gives_none()
    {
        await using var relay = StartRelay(o => o.PublicPortFrom = 0);
        await using var session = new FriendsHostSession(25565, EndpointOf(relay), null, Quick());

        Assert.True(session.StartPublic());
        await Until(() => session.Status.Public == FriendsWayState.Failed);
        Assert.Null(session.Status.PublicAddress);
    }

    [Fact]
    public async Task Without_a_relay_the_session_says_so_and_a_dead_invite_leads_nowhere()
    {
        await using var session = new FriendsHostSession(25565, null);

        Assert.False(session.StartRelay());
        Assert.Equal(FriendsWayState.Failed, session.Status.Relay);
        Assert.Equal(RelayFailure.NotConfigured, session.Status.RelayFailure);
        Assert.False(session.Status.Endpoints.HasAny);

        session.SetPublicAddress("friends.example.org");
        Assert.Equal("friends.example.org", session.Status.Endpoints.Public);

        var nowhere = new ServerInviteEndpoints(
            "127.0.0.1:" + FreePort(),
            "127.0.0.1:" + FreePort(),
            RelayKeys.NewHostKey(),
            null);

        await using var join = await FriendsJoin.ConnectAsync(nowhere, Quick());

        Assert.Equal(FriendsWay.None, join.Way);
        Assert.Null(join.Address);
        Assert.Equal(RelayFailure.RelayUnreachable, join.RelayFailure);
    }

    [Fact]
    public void Options_come_from_the_command_line_over_the_environment()
    {
        var environment = new Dictionary<string, string?>
        {
            ["STRELAY_MAX_ROOMS"] = "7",
            ["STRELAY_PORT"] = "4000"
        };

        Assert.True(RelayOptions.TryParse(
            new[] { "--port", "5000", "--rate-kbit=2000", "--bind", "127.0.0.1" },
            name => environment.GetValueOrDefault(name),
            out var options,
            out var error));

        Assert.Null(error);
        Assert.Equal(5000, options.Port);
        Assert.Equal(7, options.MaxRooms);
        Assert.Equal(250_000, options.RateBytesPerSecond);
        Assert.Equal(IPAddress.Loopback, options.Bind);
        Assert.Equal(16, options.MaxGuestsPerRoom);

        Assert.False(RelayOptions.TryParse(new[] { "--max-rooms", "many" }, _ => null, out _, out error));
        Assert.Contains("--max-rooms", error);

        Assert.False(RelayOptions.TryParse(new[] { "--no-such-thing", "1" }, _ => null, out _, out error));
        Assert.False(RelayOptions.TryParse(Array.Empty<string>(), name => name == "STRELAY_MAX_GUESTS" ? "0" : null, out _, out error));
        Assert.Contains("STRELAY_MAX_GUESTS", error);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Sends back whatever it receives.</summary>
    private class EchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public EchoServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        protected CancellationToken Stopping => _stop.Token;

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
        }

        protected virtual async Task ServeAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            int read;

            while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
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
                                await ServeAsync(client.GetStream());
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

    /// <summary>Answers a Minecraft status ping with a fixed description, which is all the launcher asks of a server.</summary>
    private sealed class StatusServer : EchoServer
    {
        private readonly string _motd;

        public StatusServer(string motd)
        {
            _motd = motd;
        }

        protected override async Task ServeAsync(NetworkStream stream)
        {
            // The handshake and the status request arrive together; their content does
            // not matter here, only that the client has spoken.
            var scratch = new byte[512];

            if (await stream.ReadAsync(scratch, Stopping) == 0)
            {
                return;
            }

            var json = Encoding.UTF8.GetBytes(
                "{\"version\":{\"name\":\"1.21.11\",\"protocol\":774},\"players\":{\"max\":8,\"online\":1},\"description\":\"" + _motd + "\"}");

            using var body = new MemoryStream();
            ServerPinger.WriteVarInt(body, 0x00);
            ServerPinger.WriteVarInt(body, json.Length);
            body.Write(json);

            using var packet = new MemoryStream();
            ServerPinger.WriteVarInt(packet, (int)body.Length);
            body.WriteTo(packet);

            await stream.WriteAsync(packet.ToArray(), Stopping);
            await stream.FlushAsync(Stopping);

            // Hold the connection until the client has read the answer and hung up.
            while (await stream.ReadAsync(scratch, Stopping) > 0)
            {
            }
        }
    }
}
