using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using STlauncher.Core.Diagnostics;
using STlauncher.Relay;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>"Why does it lag?": the conclusion from made-up numbers, and the probes on loopback.</summary>
public sealed class LagCheckTests
{
    private static readonly LagTarget Server = new("server", LagProbeKind.GameServer, "mc.showtime.su");
    private static readonly LagTarget Dns = new("dns", LagProbeKind.Dns, "mc.showtime.su");
    private static readonly LagTarget Relay = new("relay", LagProbeKind.Relay, "2.26.80.201:25580");
    private static readonly LagTarget Mirror1 = new("mirror", LagProbeKind.Mirror, "https://one.example/health");
    private static readonly LagTarget Mirror2 = new("mirror", LagProbeKind.Mirror, "https://two.example/health");
    private static readonly LagTarget Modrinth = new("modrinth", LagProbeKind.Modrinth, "https://api.modrinth.com/");
    private static readonly LagTarget CurseForge = new("curseforge", LagProbeKind.CurseForge, "https://one.example/cf/ping");

    /// <summary>Server, DNS, relay, two mirrors, Modrinth, CurseForge: null milliseconds is "no answer".</summary>
    private static List<LagMeasurement> Sample(int? server, int? relay = 40, int? mirror1 = 35, int? mirror2 = 45, int? modrinth = 50, int? curseForge = 38, int? dns = 3)
    {
        LagMeasurement M(LagTarget t, int? ms) => ms is { } v ? new LagMeasurement(t, true, v) : new LagMeasurement(t, false, null, "timeout");

        return new List<LagMeasurement>
        {
            M(Server, server), M(Dns, dns), M(Relay, relay), M(Mirror1, mirror1), M(Mirror2, mirror2), M(Modrinth, modrinth), M(CurseForge, curseForge)
        };
    }

    [Fact]
    public void Everything_quick_is_all_good()
    {
        var conclusion = LagCheck.Conclude(Sample(server: 48));

        Assert.Equal(LagVerdict.AllGood, conclusion.Verdict);
        Assert.Equal(48, conclusion.ServerMs);
        Assert.Equal(40, conclusion.OthersMs);
        Assert.Empty(conclusion.Failed);
    }

    [Fact]
    public void A_slow_server_among_quick_others_is_the_way_to_the_server()
    {
        var conclusion = LagCheck.Conclude(Sample(server: 180));

        Assert.Equal(LagVerdict.ServerPathSlow, conclusion.Verdict);
        Assert.Equal(180, conclusion.ServerMs);
        Assert.Equal(40, conclusion.OthersMs);
    }

    [Fact]
    public void A_server_a_little_further_than_the_rest_is_not_called_slow()
    {
        Assert.Equal(LagVerdict.AllGood, LagCheck.Conclude(Sample(server: 90)).Verdict);
        Assert.Equal(LagVerdict.AllGood, LagCheck.Conclude(Sample(server: 160, relay: 110, mirror1: 100, mirror2: 120, modrinth: 105, curseForge: 115)).Verdict);
    }

    [Fact]
    public void Everything_slow_is_the_players_connection()
    {
        var conclusion = LagCheck.Conclude(Sample(server: 420, relay: 380, mirror1: 300, mirror2: 350, modrinth: 410, curseForge: 290));

        Assert.Equal(LagVerdict.EverythingSlow, conclusion.Verdict);
    }

    [Fact]
    public void A_silent_server_while_the_rest_answers_is_the_server()
    {
        var conclusion = LagCheck.Conclude(Sample(server: null));

        Assert.Equal(LagVerdict.ServerDown, conclusion.Verdict);
        Assert.Null(conclusion.ServerMs);
        Assert.Equal(new[] { Server }, conclusion.Failed);
    }

    [Fact]
    public void Modrinth_alone_not_opening_is_the_provider()
    {
        var conclusion = LagCheck.Conclude(Sample(server: 52, modrinth: null));

        Assert.Equal(LagVerdict.ModrinthBlocked, conclusion.Verdict);
        Assert.Equal(new[] { Modrinth }, conclusion.Failed);
    }

    [Fact]
    public void Other_places_not_opening_are_named()
    {
        var conclusion = LagCheck.Conclude(Sample(server: 52, mirror1: null, modrinth: null));

        Assert.Equal(LagVerdict.SomethingBlocked, conclusion.Verdict);
        Assert.Equal(new[] { Mirror1, Modrinth }, conclusion.Failed);
    }

    [Fact]
    public void Nothing_answering_is_offline()
    {
        Assert.Equal(LagVerdict.Offline, LagCheck.Conclude(Sample(null, null, null, null, null, null, null)).Verdict);

        // DNS from the cache answering does not make the internet work.
        Assert.Equal(LagVerdict.Offline, LagCheck.Conclude(Sample(null, null, null, null, null, null, dns: 1)).Verdict);

        // The server silent and most of the rest too: the connection, not the server.
        Assert.Equal(LagVerdict.Offline, LagCheck.Conclude(Sample(server: null, relay: null, mirror1: null, mirror2: null, modrinth: 60, curseForge: null)).Verdict);
    }

    [Fact]
    public void A_server_name_that_does_not_resolve_is_dns()
    {
        Assert.Equal(LagVerdict.DnsFailed, LagCheck.Conclude(Sample(server: null, dns: null)).Verdict);
    }

    [Fact]
    public void Slow_and_silent_together_say_slow_first()
    {
        Assert.Equal(LagVerdict.EverythingSlow, LagCheck.Conclude(Sample(server: 400, relay: 350, mirror1: 300, mirror2: 320, modrinth: null, curseForge: 330)).Verdict);
    }

    [Fact]
    public void Without_a_relay_or_curseforge_the_rest_still_concludes()
    {
        var measurements = Sample(server: 200).Where(m => m.Target.Kind is not (LagProbeKind.Relay or LagProbeKind.CurseForge)).ToList();

        Assert.Equal(LagVerdict.ServerPathSlow, LagCheck.Conclude(measurements).Verdict);
    }

    [Theory]
    [InlineData("mc.showtime.su", "mc.showtime.su", 25565)]
    [InlineData("mc.showtime.su:25570", "mc.showtime.su", 25570)]
    [InlineData("[2001:db8::1]:25566", "2001:db8::1", 25566)]
    [InlineData("2001:db8::1", "2001:db8::1", 25565)]
    public void Addresses_split_into_host_and_port(string address, string host, int port)
    {
        Assert.Equal((host, port), LagProbe.SplitHostPort(address, 25565));
    }

    [Fact]
    public async Task The_relay_is_timed_by_a_line_there_and_back_and_a_closed_port_is_no_answer()
    {
        await using var relay = new RelayServer(new RelayOptions { Port = 0, Bind = IPAddress.Loopback, StatsInterval = TimeSpan.Zero });
        relay.Start();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var http = new HttpClient();
        var probe = new LagProbe(http, TimeSpan.FromSeconds(3));

        var results = await probe.RunAsync(new[]
        {
            new LagTarget("relay", LagProbeKind.Relay, $"127.0.0.1:{relay.Port}"),
            new LagTarget("server", LagProbeKind.GameServer, $"127.0.0.1:{closedPort}"),
            new LagTarget("dns", LagProbeKind.Dns, "localhost")
        });

        Assert.True(results[0].Ok);
        Assert.InRange(results[0].Milliseconds!.Value, 0, 3000);
        Assert.False(results[1].Ok);
        Assert.True(results[2].Ok);
    }
}
