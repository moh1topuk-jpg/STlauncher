using System;
using System.Collections.Generic;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The line shapes are taken from game logs. Those marked "captured" were copied from
/// real logs on a player's machine, with third-party addresses swapped for documentation
/// ones; the rest are the same message under the prefix their version is known to print.
/// </summary>
public class GameLocationTests
{
    [Theory]
    // Captured: Fabric 1.21.11.
    [InlineData("[20:43:22] [Render thread/INFO]: Connecting to mc.showtime.su, 25565", "mc.showtime.su")]
    // Captured: a trailing dot typed into the address, and a port of its own.
    [InlineData("[01:08:21] [Render thread/INFO]: Connecting to mc.showtime.su., 25592", "mc.showtime.su:25592")]
    // Captured: Forge 1.20.1, which prints the date its own way and the logger's name.
    [InlineData("[09июн.2026 22:01:33.908] [Render thread/INFO] [net.minecraft.client.gui.screens.ConnectScreen/]: Connecting to 203.0.113.28, 25124", "203.0.113.28:25124")]
    // Captured: Forge 1.16.5, other class name.
    [InlineData("[09июн.2026 22:22:45.168] [Render thread/INFO] [net.minecraft.client.gui.screen.ConnectingScreen/]: Connecting to 203.0.113.28, 25117", "203.0.113.28:25117")]
    // 1.7 - 1.12.
    [InlineData("[12:01:07] [Client thread/INFO]: Connecting to Play.Example.ORG, 25565", "play.example.org")]
    // 1.6 and older.
    [InlineData("2013-09-21 14:02:11 [CLIENT] [INFO] Connecting to play.example.org, 25565", "play.example.org")]
    // What the launcher's own log4j filter rebuilds, without a time.
    [InlineData("[Render thread/INFO]: Connecting to 2001:db8::7, 25566", "[2001:db8::7]:25566")]
    public void Connecting_lines_give_a_canonical_address(string line, string expected)
    {
        var seen = GameLocation.Recognise(line);

        Assert.Equal(GameLocationSignal.Connecting, seen.Signal);
        Assert.Equal(expected, seen.Address);
    }

    [Theory]
    [InlineData("[12:00:01] [Server thread/INFO]: Starting integrated minecraft server version 1.21.11", GameLocationSignal.SinglePlayerStarted)]
    [InlineData("[12:00:01] [Server thread/INFO] [net.minecraft.client.server.IntegratedServer/]: Starting integrated minecraft server version 1.20.1", GameLocationSignal.SinglePlayerStarted)]
    [InlineData("[12:30:00] [Server thread/INFO]: Stopping server", GameLocationSignal.SinglePlayerStopped)]
    [InlineData("[12:30:00] [Server thread/INFO]: Stopping singleplayer server as player logged out", GameLocationSignal.SinglePlayerStopped)]
    [InlineData("[12:30:00] [Render thread/ERROR]: Couldn't connect to server", GameLocationSignal.Disconnected)]
    // Captured: Fabric 1.21.11, kicked by the proxy.
    [InlineData("[05:10:11] [Render thread/WARN]: Client disconnected with reason: Вы были кикнуты с сервера hub-1: Режим отключился", GameLocationSignal.Disconnected)]
    // Captured: Simple Voice Chat.
    [InlineData("[21:50:04] [Render thread/INFO]: [voicechat] Disconnecting voicechat", GameLocationSignal.MaybeLeft)]
    [InlineData("[20:43:35] [Render thread/INFO]: [voicechat] Connecting to voice chat server: '203.0.113.175:25592'", GameLocationSignal.StillThere)]
    public void Other_lines_are_told_apart(string line, GameLocationSignal expected)
        => Assert.Equal(expected, GameLocation.Recognise(line).Signal);

    [Theory]
    // Captured neighbours of the lines above: none of them says where the player is.
    [InlineData("[10:23:09] [IO-Worker-1/ERROR]: Couldn't connect to realms")]
    [InlineData("[10:23:34] [Render thread/INFO]: Stopping!")]
    [InlineData("[20:43:33] [Render thread/INFO]: [voicechat] Disconnecting from previous connection due to server change")]
    [InlineData("[09июн.2026 22:01:33.807] [Render thread/INFO] [EMI/]: [EMI] Disconnecting from server, EMI data cleared")]
    // Anyone can type this into the chat.
    [InlineData("[20:50:00] [Render thread/INFO]: [System] [CHAT] <Griefer> Connecting to evil.example, 25565")]
    [InlineData("[20:50:00] [Render thread/INFO]: [CHAT] Connecting to evil.example, 25565")]
    [InlineData("2013-09-21 14:02:11 [CLIENT] [INFO] [CHAT] Connecting to evil.example, 25565")]
    [InlineData("[20:50:00] [Render thread/INFO]: Connecting to , 25565")]
    [InlineData("")]
    [InlineData(null)]
    public void Lookalikes_are_ignored(string? line)
        => Assert.Equal(GameLocationSignal.None, GameLocation.Recognise(line).Signal);

    [Theory]
    [InlineData("MC.Showtime.su", "mc.showtime.su")]
    [InlineData("mc.showtime.su:25565", "mc.showtime.su")]
    [InlineData("mc.showtime.su.:25565", "mc.showtime.su")]
    [InlineData(" mc.showtime.su:25592 ", "mc.showtime.su:25592")]
    [InlineData("[2001:DB8::7]:25565", "[2001:db8::7]")]
    [InlineData("2001:db8::7", "[2001:db8::7]")]
    [InlineData("192.168.1.5:25566", "192.168.1.5:25566")]
    public void Addresses_have_one_spelling(string written, string expected)
        => Assert.Equal(expected, GameLocation.CanonicalAddress(written));

    [Fact]
    public void Same_server_ignores_case_and_the_default_port()
    {
        Assert.True(GameLocation.SameServer("mc.showtime.su", "MC.SHOWTIME.SU:25565"));
        Assert.False(GameLocation.SameServer("mc.showtime.su", "mc.showtime.su:25592"));
        Assert.False(GameLocation.SameServer(null, null));
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Time_is_split_between_the_menu_a_server_and_a_world()
    {
        var tracker = new GameLocationTracker(Start);

        Assert.False(tracker.Feed("[12:00:30] [Render thread/INFO]: Sound engine started", Start.AddSeconds(30)));
        Assert.True(tracker.Feed("[12:01:00] [Render thread/INFO]: Connecting to mc.showtime.su, 25565", Start.AddMinutes(1)));
        Assert.Equal(GamePlace.Server("mc.showtime.su"), tracker.Place);

        // Kicked: the ten minutes after it on the "disconnected" screen are nobody's.
        Assert.True(tracker.Feed("[12:31:00] [Render thread/WARN]: Client disconnected with reason: Timed out", Start.AddMinutes(31)));
        Assert.True(tracker.Feed("[12:41:00] [Server thread/INFO]: Starting integrated minecraft server version 1.21.11", Start.AddMinutes(41)));
        Assert.True(tracker.Feed("[12:51:00] [Server thread/INFO]: Stopping singleplayer server as player logged out", Start.AddMinutes(51)));

        var spent = tracker.Finish(Start.AddMinutes(60));

        Assert.Equal(TimeSpan.FromMinutes(30), spent[GamePlace.Server("mc.showtime.su")]);
        Assert.Equal(TimeSpan.FromMinutes(10), spent[GamePlace.SinglePlayer]);
        Assert.Equal(2, spent.Count);
    }

    [Fact]
    public void A_mod_letting_go_counts_as_leaving_only_when_it_does_not_reconnect()
    {
        var tracker = new GameLocationTracker(Start);
        tracker.Feed("[Render thread/INFO]: Connecting to mc.showtime.su, 25565", Start);

        // The proxy moves the player to another of its servers: four seconds, then back.
        tracker.Feed("[Render thread/INFO]: [voicechat] Disconnecting voicechat", Start.AddMinutes(10));
        tracker.Feed("[Render thread/INFO]: [voicechat] Connecting to voice chat server: '203.0.113.175:25592'", Start.AddMinutes(10).AddSeconds(4));
        tracker.Feed("[Render thread/INFO]: anything", Start.AddMinutes(15));
        Assert.Equal(GamePlaceKind.Server, tracker.Place.Kind);

        // "Disconnect" pressed: the mod lets go and nothing follows. Three hours in the menu.
        tracker.Feed("[Render thread/INFO]: [voicechat] Disconnecting voicechat", Start.AddMinutes(20));
        Assert.True(tracker.Feed("[Render thread/INFO]: Reloading ResourceManager: vanilla", Start.AddMinutes(21)));
        Assert.Equal(GamePlace.Menu, tracker.Place);

        var spent = tracker.Finish(Start.AddHours(3));

        Assert.Equal(TimeSpan.FromMinutes(20), spent[GamePlace.Server("mc.showtime.su")]);
    }

    [Fact]
    public void Quitting_the_game_from_a_server_ends_the_stay_at_the_last_word()
    {
        var tracker = new GameLocationTracker(Start);
        tracker.Feed("[Render thread/INFO]: Connecting to friend.example, 25565", Start);
        tracker.Feed("[Render thread/INFO]: [voicechat] Disconnecting voicechat", Start.AddMinutes(5));

        var spent = tracker.Finish(Start.AddMinutes(5).AddSeconds(3));

        Assert.Equal(TimeSpan.FromMinutes(5), spent[GamePlace.Server("friend.example")]);
    }

    // Lines below are from real logs of Minecraft 1.21.11 on Fabric with Sodium 0.8.12,
    // Xaero's Minimap 26.5.0, Xaero's World Map 1.46.0 and Simple Voice Chat 2.6.22, in
    // the order they were printed; only the times and the addresses are made up.

    [Theory]
    [InlineData("[11:22:16] [Render thread/INFO]: [voicechat] Disconnecting voicechat", GameLocationSignal.MaybeLeft)]
    [InlineData("[11:22:16] [Render thread/INFO]: Xaero hud session finalized.", GameLocationSignal.MaybeLeft)]
    [InlineData("[11:22:16] [Render thread/INFO]: World map session finalized.", GameLocationSignal.MaybeLeft)]
    [InlineData("[11:22:16] [Render thread/INFO]: Stopping worker threads", GameLocationSignal.MaybeLeft)]
    [InlineData("[11:22:04] [Render thread/INFO]: New Xaero hud session initialized!", GameLocationSignal.StillThere)]
    [InlineData("[11:22:04] [Render thread/INFO]: New world map session initialized!", GameLocationSignal.StillThere)]
    [InlineData("[11:22:04] [Render thread/INFO]: Started 10 worker threads", GameLocationSignal.StillThere)]
    [InlineData("[11:22:04] [Render thread/INFO]: [voicechat] Sending secret request to the server", GameLocationSignal.StillThere)]
    [InlineData("[12:50:16] [Render thread/INFO]: [voicechat] Connecting to voice chat server: '203.0.113.175:25592'", GameLocationSignal.StillThere)]
    // Printed around the same moments, and saying nothing about where the player is.
    [InlineData("[11:22:16] [Render thread/INFO]: Finalizing world map session...", GameLocationSignal.None)]
    [InlineData("[11:22:16] [Thread-13/INFO]: World map cleaned normally!", GameLocationSignal.None)]
    [InlineData("[11:22:16] [Render thread/INFO]: [voicechat] Clearing audio channels", GameLocationSignal.None)]
    [InlineData("[11:22:16] [Render thread/INFO]: [voicechat] Stopping microphone thread", GameLocationSignal.None)]
    [InlineData("[11:22:04] [Render thread/INFO]: [voicechat] Disconnecting from previous connection due to server change", GameLocationSignal.None)]
    [InlineData("[11:22:36] [Render thread/INFO]: Sound engine started", GameLocationSignal.None)]
    [InlineData("[11:22:36] [Render thread/INFO]: Reloading ResourceManager: vanilla, fabric-api", GameLocationSignal.None)]
    [InlineData("[09:04:01] [Render thread/INFO]: Stopping!", GameLocationSignal.None)]
    // A player or a server typing a mod's line into the chat moves nobody.
    [InlineData("[11:22:16] [Render thread/INFO]: [System] [CHAT] Stopping worker threads", GameLocationSignal.None)]
    [InlineData("[11:22:16] [Render thread/INFO]: [CHAT] <someone> World map session finalized.", GameLocationSignal.None)]
    public void Mods_say_when_the_world_goes_and_when_one_arrives(string line, GameLocationSignal expected)
    {
        Assert.Equal(expected, GameLocation.Recognise(line).Signal);
    }

    private static void FeedAll(GameLocationTracker tracker, DateTimeOffset at, params string[] lines)
    {
        foreach (var line in lines)
        {
            tracker.Feed("[Render thread/INFO]: " + line, at);
        }
    }

    [Theory]
    [InlineData("Stopping worker threads")]
    [InlineData("Xaero hud session finalized.")]
    [InlineData("World map session finalized.")]
    public void Without_voice_chat_another_mod_still_marks_leaving(string leaving)
    {
        var tracker = new GameLocationTracker(Start);
        FeedAll(tracker, Start, "Connecting to play.example.org, 25565", "New Xaero hud session initialized!", "New world map session initialized!", "Started 10 worker threads");

        tracker.Feed("[Render thread/INFO]: " + leaving, Start.AddMinutes(20));

        // Back on the title screen for two hours; the next line of any kind settles it.
        Assert.True(tracker.Feed("[Render thread/INFO]: Sound engine started", Start.AddMinutes(21)));
        Assert.Equal(GamePlace.Menu, tracker.Place);

        var spent = tracker.Finish(Start.AddHours(2));

        Assert.Equal(TimeSpan.FromMinutes(20), spent[GamePlace.Server("play.example.org")]);
    }

    [Fact]
    public void A_proxy_moving_the_player_is_not_leaving_even_where_the_new_server_has_no_voice_chat()
    {
        var tracker = new GameLocationTracker(Start);
        tracker.Feed("[Render thread/INFO]: Connecting to mc.showtime.su, 25565", Start);

        // The whole of one move, as printed. Voice chat lets go last and never connects:
        // the lobby has none. It only asks for a secret.
        FeedAll(
            tracker,
            Start.AddMinutes(10),
            "Stopping worker threads",
            "Previous hud session still active. Probably using MenuMobs. Forcing it to end...",
            "Xaero hud session finalized.",
            "Minimap required item set to nothing.",
            "New Xaero hud session initialized!",
            "Previous world map session still active. Probably using MenuMobs. Forcing it to end...",
            "Finalizing world map session...",
            "World map session finalized.",
            "Fullscreen map required item set to nothing.",
            "New world map session initialized!",
            "Started 10 worker threads",
            "[voicechat] Disconnecting from previous connection due to server change",
            "[voicechat] Clearing audio channels",
            "[voicechat] Stopping microphone thread",
            "[voicechat] Disconnecting voicechat",
            "[voicechat] Sending secret request to the server",
            "Loaded 38 advancements",
            "Stopping worker threads",
            "Started 10 worker threads");

        Assert.False(tracker.Feed("[Render thread/INFO]: Loaded 766 advancements", Start.AddMinutes(30)));
        Assert.Equal(GamePlace.Server("mc.showtime.su"), tracker.Place);

        // And then the real thing: every mod lets go and none takes it back.
        FeedAll(
            tracker,
            Start.AddMinutes(40),
            "[voicechat] Clearing audio channels",
            "Xaero hud session finalized.",
            "Finalizing world map session...",
            "World map session finalized.",
            "Stopping worker threads");

        var spent = tracker.Finish(Start.AddHours(5));

        Assert.Equal(TimeSpan.FromMinutes(40), spent[GamePlace.Server("mc.showtime.su")]);
    }

    [Fact]
    public void A_slow_move_between_a_proxys_servers_brings_the_player_back_to_the_same_server()
    {
        var tracker = new GameLocationTracker(Start);
        tracker.Feed("[Render thread/INFO]: Connecting to mc.showtime.su, 25565", Start);

        // The world is taken away, a resource pack reloads for a minute, the world returns.
        tracker.Feed("[Render thread/INFO]: Stopping worker threads", Start.AddMinutes(10));
        Assert.True(tracker.Feed("[Render thread/INFO]: Reloading ResourceManager: vanilla", Start.AddMinutes(10).AddSeconds(30)));
        Assert.Equal(GamePlace.Menu, tracker.Place);

        Assert.True(tracker.Feed("[Render thread/INFO]: Started 10 worker threads", Start.AddMinutes(11)));
        Assert.Equal(GamePlace.Server("mc.showtime.su"), tracker.Place);

        var spent = tracker.Finish(Start.AddMinutes(31));

        // Ten minutes before, twenty after; the minute of loading is nobody's.
        Assert.Equal(TimeSpan.FromMinutes(30), spent[GamePlace.Server("mc.showtime.su")]);
    }

    [Fact]
    public void A_world_arriving_long_after_leaving_is_not_the_old_server()
    {
        var tracker = new GameLocationTracker(Start);
        tracker.Feed("[Render thread/INFO]: Connecting to mc.showtime.su, 25565", Start);
        tracker.Feed("[Render thread/INFO]: Stopping worker threads", Start.AddMinutes(10));

        tracker.Feed("[Render thread/INFO]: Started 10 worker threads", Start.AddMinutes(10) + GameLocationTracker.ReturnWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(GamePlace.Menu, tracker.Place);

        Assert.Equal(TimeSpan.FromMinutes(10), tracker.Finish(Start.AddHours(1))[GamePlace.Server("mc.showtime.su")]);
    }

    [Fact]
    public void A_kick_after_the_world_was_taken_away_is_final()
    {
        var tracker = new GameLocationTracker(Start);
        tracker.Feed("[Render thread/INFO]: Connecting to mc.showtime.su, 25565", Start);

        // As it happened in a real log: the move hung, and half a minute later the connection timed out.
        tracker.Feed("[Render thread/INFO]: Stopping worker threads", Start.AddMinutes(10));
        tracker.Feed("[Render thread/WARN]: Client disconnected with reason: Timed out", Start.AddMinutes(10).AddSeconds(34));
        tracker.Feed("[Render thread/INFO]: Started 10 worker threads", Start.AddMinutes(11));

        Assert.Equal(GamePlace.Menu, tracker.Place);
        Assert.Equal(TimeSpan.FromMinutes(10), tracker.Finish(Start.AddHours(1))[GamePlace.Server("mc.showtime.su")]);
    }

    [Fact]
    public void A_dedicated_server_stopping_elsewhere_does_not_end_a_multiplayer_stay()
    {
        var tracker = new GameLocationTracker(Start);
        tracker.Feed("[Render thread/INFO]: Connecting to mc.showtime.su, 25565", Start);

        Assert.False(tracker.Feed("[Server thread/INFO]: Stopping server", Start.AddMinutes(1)));
        Assert.Equal(GamePlaceKind.Server, tracker.Place.Kind);
    }

    [Fact]
    public void Places_add_up_in_the_build_and_the_list_is_capped()
    {
        var instance = new Instance();
        var showtime = GamePlace.Server("mc.showtime.su");

        Assert.True(Playtime.RecordPlaces(instance, new Dictionary<GamePlace, TimeSpan>
        {
            [showtime] = TimeSpan.FromHours(2),
            [GamePlace.SinglePlayer] = TimeSpan.FromMinutes(30),
            [GamePlace.Server("joined.and.left")] = TimeSpan.FromMilliseconds(300)
        }, Start));

        Playtime.RecordPlaces(instance, new Dictionary<GamePlace, TimeSpan> { [showtime] = TimeSpan.FromHours(1) }, Start.AddDays(1));

        var top = Playtime.TopPlaces(instance, 4);

        Assert.Equal(2, top.Count);
        Assert.Equal("mc.showtime.su", top[0].Address);
        Assert.Equal(3 * 3600, top[0].Seconds);
        Assert.Null(top[1].Address);

        for (var i = 0; i < Playtime.MaxPlaces + 10; i++)
        {
            Playtime.RecordPlaces(
                instance,
                new Dictionary<GamePlace, TimeSpan> { [GamePlace.Server($"s{i}.example")] = TimeSpan.FromMinutes(1) },
                Start.AddDays(2).AddMinutes(i));
        }

        Assert.Equal(Playtime.MaxPlaces, instance.PlayPlaces.Count);
        Assert.DoesNotContain(instance.PlayPlaces, p => p.Address == "s0.example");
    }

    [Fact]
    public void Places_survive_the_build_file()
    {
        var instance = new Instance();
        Playtime.RecordPlaces(instance, new Dictionary<GamePlace, TimeSpan> { [GamePlace.SinglePlayer] = TimeSpan.FromMinutes(5) }, Start);

        var json = System.Text.Json.JsonSerializer.Serialize(instance);
        var back = System.Text.Json.JsonSerializer.Deserialize<Instance>(json)!;

        Assert.Single(back.PlayPlaces);
        Assert.Null(back.PlayPlaces[0].Address);
        Assert.Equal(300, back.PlayPlaces[0].Seconds);

        // A file written before this field existed.
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<Instance>("{\"id\":\"x\"}")!.PlayPlaces);
    }
}
