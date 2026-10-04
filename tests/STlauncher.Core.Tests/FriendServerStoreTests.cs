using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Friends;
using STlauncher.Core.Hosting;
using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

public class FriendServerStoreTests
{
    private const string RoomKey = "0123456789abcdef0123456789abcdef";

    private static ServerInvite Invite(string? direct = "203.0.113.7:25565", string? roomKey = RoomKey, string name = "Наш мир") => new(
        name,
        "1.21.1",
        LoaderKind.Fabric,
        "0.16.14",
        "Steve",
        null,
        new ServerInviteEndpoints(direct, roomKey is null ? null : "relay.example.org:25580", roomKey, null));

    private static FriendServerStore NewStore(out string root)
    {
        root = HostingTemp.Directory();
        return new FriendServerStore(new LauncherPaths(root));
    }

    [Fact]
    public void A_missing_file_is_an_empty_list()
    {
        var store = NewStore(out _);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void Servers_round_trip_through_the_file()
    {
        var store = NewStore(out var root);
        var servers = store.Load();

        var added = FriendServerStore.Remember(servers, Invite(), "our-world");
        added.LocalPort = 51234;
        store.Save(servers);

        Assert.True(File.Exists(Path.Combine(root, "friends.json")));

        var read = Assert.Single(store.Load());

        Assert.Equal(added.Id, read.Id);
        Assert.Equal("Наш мир", read.Name);
        Assert.Equal("1.21.1", read.GameVersion);
        Assert.Equal(LoaderKind.Fabric, read.Loader);
        Assert.Equal("0.16.14", read.LoaderVersion);
        Assert.Equal("Steve", read.HostNickname);
        Assert.Equal("our-world", read.InstanceId);
        Assert.Equal(51234, read.LocalPort);
        Assert.Equal(new ServerInviteEndpoints("203.0.113.7:25565", "relay.example.org:25580", RoomKey, null), read.Endpoints);
    }

    [Fact]
    public void The_same_room_updates_the_entry_instead_of_adding_one()
    {
        var store = NewStore(out _);
        var servers = store.Load();

        var first = FriendServerStore.Remember(servers, Invite(), "our-world");
        first.LocalPort = 51234;

        // The host's address changed overnight and the server was renamed; the room is the same.
        var second = FriendServerStore.Remember(servers, Invite(direct: "198.51.100.20:25565", name: "Наш мир 2"), null);

        Assert.Same(first, second);
        Assert.Single(servers);
        Assert.Equal("198.51.100.20:25565", second.Direct);
        Assert.Equal("Наш мир 2", second.Name);
        Assert.Equal("our-world", second.InstanceId);
        Assert.Equal(51234, second.LocalPort);
    }

    [Fact]
    public void Without_a_room_the_host_and_the_name_identify_the_server()
    {
        var servers = new System.Collections.Generic.List<FriendServer>();

        var first = FriendServerStore.Remember(servers, Invite(roomKey: null), "a");
        var same = FriendServerStore.Remember(servers, Invite(direct: "198.51.100.20:25565", roomKey: null), null);
        var other = FriendServerStore.Remember(servers, Invite(roomKey: null, name: "Другой мир"), "b");

        Assert.Same(first, same);
        Assert.NotSame(first, other);
        Assert.Equal(2, servers.Count);
    }

    [Fact]
    public void A_damaged_file_is_set_aside_not_overwritten()
    {
        var store = NewStore(out var root);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Empty(store.Load());
        Assert.False(File.Exists(store.FilePath));
        Assert.Single(Directory.EnumerateFiles(root, "friends.json.corrupt-*"));
    }

    [Fact]
    public void Entries_without_an_id_or_a_version_are_dropped_on_load()
    {
        var store = NewStore(out _);
        File.WriteAllText(
            store.FilePath,
            "[{\"id\":\"a\",\"name\":\"ok\",\"gameVersion\":\"1.21.1\"},{\"id\":\"\",\"gameVersion\":\"1.21.1\"},{\"id\":\"c\"}]");

        Assert.Equal("a", Assert.Single(store.Load()).Id);
    }

    [Fact]
    public void The_host_key_and_the_switches_survive_the_server_definition()
    {
        var root = HostingTemp.Directory();
        var store = new HostedServerStore(new LauncherPaths(root));

        var server = store.Create("Friends", "1.21.1");
        server.HostKey = RelayKeys.NewHostKey();
        server.FriendsRelay = true;
        store.Save(server);

        var read = store.List().Single();

        Assert.Equal(server.HostKey, read.HostKey);
        Assert.True(read.FriendsRelay);
        Assert.False(read.FriendsDirect);
    }
}
