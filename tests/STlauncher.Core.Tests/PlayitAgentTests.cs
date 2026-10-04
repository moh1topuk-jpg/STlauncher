using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Friends;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The lines here are the ones the agent's source prints at v0.17.1, and the JSON has
/// the shape of its API types. The agent itself is never downloaded or started by a test.
/// </summary>
public class PlayitAgentTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Theory]
    [InlineData("2026-10-04T10:15:02.118431Z  INFO playit_cli::ui: Visit link to setup https://playit.gg/claim/3fa9c01b7e")]
    [InlineData("2026-10-04T10:15:09.330210Z  INFO playit_cli::ui: Approve program at https://playit.gg/claim/3fa9c01b7e")]
    [InlineData("\u001b[2m2026-10-04T10:15:02Z\u001b[0m \u001b[32m INFO\u001b[0m playit_cli::ui: Visit link to setup https://playit.gg/claim/3fa9c01b7e\u001b[0m")]
    [InlineData("https://playit.gg/claim/3fa9c01b7e")]
    public void The_claim_link_is_found_in_the_agents_output(string line)
    {
        var parsed = PlayitOutput.Parse(line);

        Assert.Equal(PlayitEventKind.ClaimLink, parsed!.Kind);
        Assert.Equal("https://playit.gg/claim/3fa9c01b7e", parsed.Url);
    }

    [Theory]
    [InlineData("2026-10-04T10:15:20Z  INFO playit_cli::ui: Program approved :). Secret code being setup.", PlayitEventKind.ClaimApproved)]
    [InlineData("2026-10-04T10:15:20Z  INFO playit_cli::ui: Program rejected :(", PlayitEventKind.ClaimRejected)]
    [InlineData("Error: AgentClaimRejected", PlayitEventKind.ClaimRejected)]
    [InlineData("2026-10-04T10:15:31Z  INFO playit_cli::autorun: tunnel running", PlayitEventKind.Connected)]
    [InlineData("2026-10-04T10:15:34Z  INFO playit_cli::ui: playit (v0.17.1): 1791108934512 tunnel running, 1 tunnels registered", PlayitEventKind.Connected)]
    [InlineData("2026-10-04T10:15:20Z  INFO playit_cli::ui: Invalid secret, do you want to reset (Y/n)? ", PlayitEventKind.SecretInvalid)]
    public void The_agents_progress_is_recognised(string line, PlayitEventKind kind)
    {
        Assert.Equal(kind, PlayitOutput.Parse(line)!.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-04T10:15:01Z  INFO playit_cli::ui: checking if secret key is valid")]
    [InlineData("2026-10-04T10:15:05Z  INFO playit_cli::ui: secret key valid, agent has 1 tunnels")]
    [InlineData("see https://playit.gg/claim/ for details")]
    [InlineData("https://evil.example/playit.gg/claim/3fa9c01b7e")]
    public void Everything_else_is_just_talk(string? line)
    {
        Assert.Null(PlayitOutput.Parse(line));
    }

    [Fact]
    public void The_secret_is_read_from_either_form_of_the_file()
    {
        Assert.Equal("0a1b2c3d4e5f60718293a4b5c6d7e8f9", PlayitOutput.ReadSecret("secret_key = \"0a1b2c3d4e5f60718293a4b5c6d7e8f9\"\n"));
        Assert.Equal("0a1b2c3d4e5f60718293a4b5c6d7e8f9", PlayitOutput.ReadSecret("  0a1b2c3d4e5f60718293a4b5c6d7e8f9\r\n"));
        Assert.Null(PlayitOutput.ReadSecret(""));
        Assert.Null(PlayitOutput.ReadSecret("secret_key = \"not hex\""));
        Assert.Null(PlayitOutput.ReadSecret("[other]\nvalue = 1"));
    }

    [Fact]
    public void Run_data_gives_the_tunnels_and_where_they_lead()
    {
        const string json = """
            {
              "status": "success",
              "data": {
                "agent_id": "5b0cd2d4-6d0e-4b0b-9a57-0d8f6c1f2a11",
                "tunnels": [
                  {
                    "id": "c0a8d0a2-1111-4222-8333-444455556666",
                    "internal_id": 41,
                    "name": "Minecraft",
                    "display_address": "our-world.gl.joinmc.link",
                    "port_type": "tcp",
                    "port_count": 1,
                    "tunnel_type": "minecraft-java",
                    "tunnel_type_display": "Minecraft Java",
                    "agent_config": { "fields": [ { "name": "local_ip", "value": "127.0.0.1" }, { "name": "local_port", "value": "25565" } ] },
                    "disabled_reason": null
                  },
                  {
                    "id": "c0a8d0a2-1111-4222-8333-444455557777",
                    "internal_id": 42,
                    "name": "Voice",
                    "display_address": "147.185.221.20:24454",
                    "port_type": "udp",
                    "port_count": 1,
                    "tunnel_type": null,
                    "tunnel_type_display": "Custom UDP",
                    "agent_config": { "fields": [] },
                    "disabled_reason": "over limit"
                  },
                  {
                    "id": "c0a8d0a2-1111-4222-8333-444455558888",
                    "internal_id": 43,
                    "name": "Odd",
                    "display_address": "not an address; rm -rf",
                    "port_type": "tcp",
                    "port_count": 1,
                    "tunnel_type": null,
                    "tunnel_type_display": "Custom TCP",
                    "agent_config": { "fields": [] },
                    "disabled_reason": null
                  }
                ],
                "pending": [ { "id": "c0a8d0a2-1111-4222-8333-444455559999", "name": "new", "status_msg": "allocating" } ],
                "notices": [],
                "permissions": { "is_self_managed": false, "has_premium": false, "account_status": "guest" }
              }
            }
            """;

        var data = PlayitOutput.ParseRunData(json);

        Assert.NotNull(data);
        Assert.False(data!.InvalidKey);
        Assert.Equal("5b0cd2d4-6d0e-4b0b-9a57-0d8f6c1f2a11", data.AgentId);
        Assert.Equal(1, data.PendingTunnels);
        Assert.Equal(2, data.Tunnels.Count);

        var minecraft = data.Tunnels[0];
        Assert.Equal("our-world.gl.joinmc.link", minecraft.PublicAddress);
        Assert.Equal(25565, minecraft.LocalPort);
        Assert.Equal("minecraft-java", minecraft.Kind);
        Assert.True(minecraft.CarriesTcp);
        Assert.True(minecraft.Enabled);

        // No local port of its own: it leads to the port its public address names.
        var voice = data.Tunnels[1];
        Assert.Equal("147.185.221.20:24454", voice.PublicAddress);
        Assert.Equal(24454, voice.LocalPort);
        Assert.False(voice.CarriesTcp);
        Assert.False(voice.Enabled);
    }

    [Fact]
    public void A_refused_key_and_a_stranger_answer_are_told_apart()
    {
        var refused = PlayitOutput.ParseRunData("""{"status":"error","data":{"type":"auth","message":"InvalidAgentKey"}}""");

        Assert.True(refused!.InvalidKey);
        Assert.Empty(refused.Tunnels);

        Assert.Null(PlayitOutput.ParseRunData("""{"status":"error","data":{"type":"internal","message":{"trace_id":"x"}}}"""));
        Assert.Null(PlayitOutput.ParseRunData("<html>blocked</html>"));
        Assert.Null(PlayitOutput.ParseRunData("[]"));
        Assert.Null(PlayitOutput.ParseRunData(null));

        // An id that is not an id never becomes part of a link.
        var odd = PlayitOutput.ParseRunData("""{"status":"success","data":{"agent_id":"../../x","tunnels":[],"pending":[]}}""");
        Assert.Null(odd!.AgentId);
    }

    [Fact]
    public void Downloads_are_pinned_to_one_release_of_the_official_repository()
    {
        Assert.NotEmpty(PlayitAgent.KnownDownloads);

        foreach (var download in PlayitAgent.KnownDownloads)
        {
            Assert.Equal(PlayitAgent.Version, download.Version);
            Assert.Equal(
                $"https://github.com/playit-cloud/playit-agent/releases/download/v{PlayitAgent.Version}/{download.FileName}",
                download.Url);
            Assert.Equal(64, download.Sha256.Length);
            Assert.All(download.Sha256, c => Assert.True(Uri.IsHexDigit(c)));
            Assert.InRange(download.Size, 1_000_000, 20_000_000);
        }

        Assert.Equal(PlayitAgent.KnownDownloads.Count, PlayitAgent.KnownDownloads.Select(d => d.Sha256).Distinct().Count());

        Assert.Equal("playit-windows-x86_64-signed.exe", PlayitAgent.DownloadFor(true, false, Architecture.X64)!.FileName);
        Assert.Equal("playit-windows-x86-signed.exe", PlayitAgent.DownloadFor(true, false, Architecture.X86)!.FileName);
        Assert.Equal("playit-linux-aarch64", PlayitAgent.DownloadFor(false, true, Architecture.Arm64)!.FileName);
        Assert.Null(PlayitAgent.DownloadFor(false, false, Architecture.Arm64));
    }

    [Fact]
    public async Task A_download_that_is_not_the_pinned_file_is_thrown_away()
    {
        var root = TempRoot();
        using var http = new HttpClient(new Canned(new byte[4096]));
        await using var agent = new PlayitAgent(new LauncherPaths(root), http);

        if (agent.Download is null)
        {
            return;
        }

        Assert.Equal(PlayitState.NotInstalled, agent.Status.State);
        Assert.StartsWith(root, agent.Folder);
        Assert.False(agent.Start(25565));

        Assert.False(await agent.InstallAsync());

        Assert.Equal(PlayitState.Failed, agent.Status.State);
        Assert.Equal(PlayitFailure.HashMismatch, agent.Status.Failure);
        Assert.False(agent.IsInstalled);
        Assert.False(File.Exists(agent.ExecutablePath));
        Assert.Empty(Directory.GetFiles(agent.Folder));
    }

    [Fact]
    public async Task A_file_of_the_right_size_and_the_wrong_content_is_never_started()
    {
        var root = TempRoot();
        using var http = new HttpClient(new Canned(Array.Empty<byte>()));
        await using var agent = new PlayitAgent(new LauncherPaths(root), http);

        if (agent.Download is null)
        {
            return;
        }

        // Something put a file where the agent goes. It has the right length, so only
        // the hash can tell it is not the agent.
        Directory.CreateDirectory(agent.Folder);
        File.WriteAllBytes(agent.ExecutablePath, new byte[agent.Download.Size]);

        try
        {
            Assert.True(agent.IsInstalled);
            Assert.False(agent.Start(25565));
            Assert.False(agent.IsRunning);
            Assert.Equal(PlayitFailure.HashMismatch, agent.Status.Failure);
        }
        finally
        {
            // Five megabytes of zeroes per run would add up in the temp folder.
            File.Delete(agent.ExecutablePath);
        }
    }

    [Fact]
    public async Task Unlinking_sets_the_secret_aside_instead_of_deleting_it()
    {
        var root = TempRoot();
        using var http = new HttpClient(new Canned(Array.Empty<byte>()));
        await using var agent = new PlayitAgent(new LauncherPaths(root), http);

        Assert.False(agent.IsClaimed);
        Assert.False(agent.Unlink());

        Directory.CreateDirectory(agent.Folder);
        File.WriteAllText(agent.SecretPath, "secret_key = \"0a1b2c3d4e5f60718293a4b5c6d7e8f9\"\n");

        Assert.True(agent.IsClaimed);
        Assert.True(agent.Unlink());
        Assert.False(agent.IsClaimed);

        var aside = Assert.Single(Directory.GetFiles(agent.Folder));
        Assert.Contains("playit.toml.unlinked-", aside);
        Assert.Contains("0a1b2c3d4e5f60718293a4b5c6d7e8f9", File.ReadAllText(aside));
    }

    private sealed class Canned : HttpMessageHandler
    {
        private readonly byte[] _body;

        public Canned(byte[] body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
    }
}
