using System;
using System.Linq;
using STlauncher.Core.Content;
using STlauncher.Core.Friends;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;
using Xunit;

namespace STlauncher.Core.Tests;

public class ServerInviteTests
{
    private const string RoomKey = "0123456789abcdef0123456789abcdef";

    private static string SampleBuildCode() => BuildCode.Encode(new BuildCodePayload(
        "Showtime plus",
        "1.21.11",
        LoaderKind.Fabric,
        "0.19.5",
        new[] { new BuildCodeFile("mods/sodium.jar", "https://cdn.modrinth.com/data/AANobbMI/versions/abc/sodium.jar", "aa11", 1234) },
        Array.Empty<string>()));

    private static ServerInvite Sample(string? buildCode = null) => new(
        "Наш мир",
        "1.21.11",
        LoaderKind.Fabric,
        "0.19.5",
        "Steve",
        buildCode,
        new ServerInviteEndpoints("203.0.113.7:25565", "relay.example.org:25580", RoomKey, "our-world.gl.joinmc.link"));

    [Fact]
    public void An_invite_round_trips()
    {
        var code = ServerInviteCode.Encode(Sample(SampleBuildCode()));

        Assert.StartsWith("STS1.", code);
        Assert.All(code, c => Assert.True(c < 128, "The code must be plain ASCII."));
        Assert.DoesNotContain("+", code);
        Assert.DoesNotContain("/", code);
        Assert.DoesNotContain("=", code);

        Assert.True(ServerInviteCode.TryDecode(code, out var back));
        Assert.Equal("Наш мир", back!.Name);
        Assert.Equal("1.21.11", back.GameVersion);
        Assert.Equal(LoaderKind.Fabric, back.Loader);
        Assert.Equal("0.19.5", back.LoaderVersion);
        Assert.Equal("Steve", back.HostNickname);
        Assert.Equal("203.0.113.7:25565", back.Endpoints.Direct);
        Assert.Equal("relay.example.org:25580", back.Endpoints.Relay);
        Assert.Equal(RoomKey, back.Endpoints.RoomKey);
        Assert.Equal("our-world.gl.joinmc.link", back.Endpoints.Public);

        // The build inside is the same build, readable by the build importer as it is.
        Assert.True(BuildCode.TryDecode(back.BuildCode, out var build));
        Assert.Equal("Showtime plus", build!.Name);
        Assert.Single(build.Files);
    }

    [Fact]
    public void An_invite_pasted_with_text_around_it_still_reads()
    {
        var message = "заходи ко мне:\n" + ServerInviteCode.Encode(Sample()) + "\nжду";

        Assert.True(ServerInviteCode.TryDecode(message, out var back));
        Assert.Equal("Steve", back!.HostNickname);
        Assert.Null(back.BuildCode);
    }

    [Fact]
    public void An_invite_without_a_build_fits_a_short_message()
    {
        Assert.InRange(ServerInviteCode.Encode(Sample()).Length, 60, 320);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("STS1.")]
    [InlineData("STS1.!!!")]
    [InlineData("STS1.aGVsbG8")]
    public void Garbage_is_refused(string text)
    {
        Assert.False(ServerInviteCode.TryDecode(text, out var invite));
        Assert.Null(invite);
    }

    [Fact]
    public void A_build_code_is_not_an_invite_and_the_other_way_round()
    {
        Assert.False(ServerInviteCode.TryDecode(SampleBuildCode(), out _));
        Assert.False(BuildCode.TryDecode(ServerInviteCode.Encode(Sample()), out _));
    }

    [Fact]
    public void A_pasted_text_is_told_apart_as_a_build_or_an_invite()
    {
        var invite = ServerInviteCode.Encode(Sample(SampleBuildCode()));
        var build = SampleBuildCode();

        Assert.Equal(SharedCodeKind.Server, SharedCode.Read("вот: " + invite, out var noBuild, out var server));
        Assert.Null(noBuild);
        Assert.Equal("Наш мир", server!.Name);

        Assert.Equal(SharedCodeKind.Build, SharedCode.Read(build, out var payload, out var noServer));
        Assert.Null(noServer);
        Assert.Equal("Showtime plus", payload!.Name);

        Assert.Equal(SharedCodeKind.None, SharedCode.Detect("просто текст"));
        Assert.Equal(SharedCodeKind.None, SharedCode.Detect(null));

        // Two codes in one message: the first is the one that was meant, and a broken
        // first one does not hide a good second.
        Assert.Equal(SharedCodeKind.Server, SharedCode.Detect(invite + "\n" + build));
        Assert.Equal(SharedCodeKind.Build, SharedCode.Detect(build + "\n" + invite));
        Assert.Equal(SharedCodeKind.Build, SharedCode.Detect("STS1.broken " + build));
    }

    [Fact]
    public void Addresses_that_are_not_addresses_do_not_survive()
    {
        var invite = Sample() with
        {
            Endpoints = new ServerInviteEndpoints(
                "203.0.113.7",                      // the direct way needs its port
                "relay.example.org:25580",
                "not-a-key",                        // and the relay needs a real room key
                "evil.example.org/path?x=1 --flag") // a public address is a host, not a command line
        };

        Assert.False(ServerInviteCode.TryDecode(ServerInviteCode.Encode(invite), out _));

        var usable = invite with { Endpoints = invite.Endpoints with { Public = "[2001:db8::1]:25565" } };

        Assert.True(ServerInviteCode.TryDecode(ServerInviteCode.Encode(usable), out var back));
        Assert.Null(back!.Endpoints.Direct);
        Assert.Null(back.Endpoints.Relay);
        Assert.Null(back.Endpoints.RoomKey);
        Assert.Equal("[2001:db8::1]:25565", back.Endpoints.Public);
    }

    [Fact]
    public void Names_lose_control_characters_and_excess_length()
    {
        var invite = Sample() with { Name = "Мир\r\n\u0007" + new string('x', 200), HostNickname = "  Alex\t " };

        Assert.True(ServerInviteCode.TryDecode(ServerInviteCode.Encode(invite), out var back));
        Assert.StartsWith("Мир", back!.Name);
        Assert.DoesNotContain(back.Name, c => char.IsControl(c));
        Assert.Equal(64, back.Name.Length);
        Assert.Equal("Alex", back.HostNickname);
    }

    [Fact]
    public void A_version_that_is_not_a_version_refuses_the_invite()
    {
        Assert.False(ServerInviteCode.TryDecode(ServerInviteCode.Encode(Sample() with { GameVersion = "" }), out _));
        Assert.False(ServerInviteCode.TryDecode(ServerInviteCode.Encode(Sample() with { GameVersion = "1.21\"; rm -rf" }), out _));
        Assert.True(ServerInviteCode.TryDecode(ServerInviteCode.Encode(Sample() with { GameVersion = "24w14a" }), out _));
    }

    [Theory]
    [InlineData("example.org", "example.org", null)]
    [InlineData(" example.org:25565 ", "example.org", 25565)]
    [InlineData("203.0.113.7:1", "203.0.113.7", 1)]
    [InlineData("[::1]:25580", "::1", 25580)]
    [InlineData("2001:db8::1", "2001:db8::1", null)]
    public void Host_and_port_are_read(string text, string host, int? port)
    {
        Assert.True(HostPort.TryParse(text, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.org:0")]
    [InlineData("example.org:70000")]
    [InlineData("example.org:port")]
    [InlineData("exa mple.org")]
    [InlineData("http://example.org")]
    [InlineData("example..org")]
    [InlineData("[::1")]
    [InlineData("пример.рф")]
    public void Anything_else_is_not_a_host(string text)
    {
        Assert.False(HostPort.TryParse(text, out _, out _));
    }

    [Fact]
    public void The_relay_comes_from_the_catalog_unless_the_environment_overrides_it()
    {
        var catalog = ContentCatalogService.Parse("""{ "schemaVersion": 1, "friendsRelay": "relay.example.org:4000" }""");

        Assert.Equal("relay.example.org:4000", catalog.FriendsRelay);
        Assert.Equal(new RelayEndpoint("relay.example.org", 4000), RelayLocation.Resolve(null, catalog.FriendsRelay));
        Assert.Equal(new RelayEndpoint("127.0.0.1", 5000), RelayLocation.Resolve("127.0.0.1:5000", catalog.FriendsRelay));

        // A bare host gets the relay's default port; no address anywhere means no relay.
        Assert.Equal(new RelayEndpoint("relay.example.org", RelayEndpoint.DefaultPort), RelayLocation.Resolve(null, "relay.example.org"));
        Assert.Null(RelayLocation.Resolve(null, null));
        Assert.Null(RelayLocation.Resolve("  ", ""));
        Assert.Null(ContentCatalogService.Parse("""{ "schemaVersion": 1 }""").FriendsRelay);

        // A mistyped override is "no relay", not a quiet fall back to the real one.
        Assert.Null(RelayLocation.Resolve("not an address", catalog.FriendsRelay));
    }
}
