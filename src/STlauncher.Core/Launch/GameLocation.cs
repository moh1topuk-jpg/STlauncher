using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Launch;

public enum GamePlaceKind
{
    /// <summary>The title screen, a loading screen, a "connection lost" screen: not in a world.</summary>
    Menu,
    SinglePlayer,
    Server
}

/// <summary>Where the player is. <see cref="Address"/> is canonical and set only for a server.</summary>
public readonly record struct GamePlace(GamePlaceKind Kind, string? Address)
{
    public static readonly GamePlace Menu = new(GamePlaceKind.Menu, null);

    public static readonly GamePlace SinglePlayer = new(GamePlaceKind.SinglePlayer, null);

    public static GamePlace Server(string address) => new(GamePlaceKind.Server, address);
}

public enum GameLocationSignal
{
    None,

    /// <summary>The game is joining a server; the address is in the event.</summary>
    Connecting,

    /// <summary>The join did not work out, or the server dropped the player: back on a menu screen.</summary>
    Disconnected,

    SinglePlayerStarted,
    SinglePlayerStopped,

    /// <summary>
    /// A mod said it let go of the world. The game itself logs nothing when the player
    /// presses "Disconnect", so this is the only trace of it; the same line also appears
    /// when a proxy moves the player between its servers, so it counts only when
    /// <see cref="StillThere"/> does not follow.
    /// </summary>
    MaybeLeft,

    /// <summary>A mod said it has a world again: the player is in one.</summary>
    StillThere
}

public readonly record struct GameLocationEvent(GameLocationSignal Signal, string? Address = null);

/// <summary>
/// Reads where the player is from the game's own output. The launch arguments only say
/// where the game was asked to go; the player then leaves, joins a friend, opens a world.
/// Nothing is asked of the network: these are lines the game prints anyway.
/// </summary>
/// <remarks>
/// The message is the same from 1.3 to the current versions; what differs is what stands
/// before it:
/// <code>
/// [20:43:22] [Render thread/INFO]: Connecting to mc.showtime.su, 25565              vanilla, Fabric 1.13+
/// [12:01:07] [Client thread/INFO]: Connecting to play.example.org, 25565            1.7 - 1.12
/// [09jun.2026 22:01:33.908] [Render thread/INFO] [net.minecraft.client.gui.screens.ConnectScreen/]: Connecting to 87.228.10.28, 25124    Forge
/// 2013-09-21 14:02:11 [CLIENT] [INFO] Connecting to play.example.org, 25565         1.6 and older
/// </code>
/// Up to 1.16 the game resolves the SRV record before it prints the line, so the host
/// may be the record's target rather than what the player typed.
/// </remarks>
public static class GameLocation
{
    public const int DefaultPort = 25565;

    private static readonly Regex Connecting = new(
        @"^Connecting to (?<host>[^\s,]+), (?<port>\d{1,5})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>"2013-09-21 14:02:11 [CLIENT] [INFO] message": no colon after the brackets.</summary>
    private static readonly Regex LegacyPrefix = new(
        @"^(?:\d{4}-\d\d-\d\d \d\d:\d\d:\d\d )?(?:\[[^\]]*\] )+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static GameLocationEvent Recognise(string? line)
    {
        // A chat line carries whatever a server or another player typed.
        if (string.IsNullOrEmpty(line) || line.Contains("[CHAT]", StringComparison.Ordinal))
        {
            return default;
        }

        var message = Message(line);

        if (message.StartsWith("Connecting to ", StringComparison.Ordinal))
        {
            var match = Connecting.Match(message);

            return match.Success &&
                   int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
                   CanonicalAddress(match.Groups["host"].Value, port) is { } address
                ? new GameLocationEvent(GameLocationSignal.Connecting, address)
                : default;
        }

        if (message.StartsWith("Starting integrated minecraft server", StringComparison.Ordinal))
        {
            return new GameLocationEvent(GameLocationSignal.SinglePlayerStarted);
        }

        // "Stopping server" up to 1.19, "Stopping singleplayer server as player logged out" after.
        if (message.StartsWith("Stopping singleplayer server", StringComparison.Ordinal) ||
            message.TrimEnd().Equals("Stopping server", StringComparison.Ordinal))
        {
            return new GameLocationEvent(GameLocationSignal.SinglePlayerStopped);
        }

        if (message.StartsWith("Couldn't connect to server", StringComparison.Ordinal) ||
            message.StartsWith("Client disconnected with reason:", StringComparison.Ordinal))
        {
            return new GameLocationEvent(GameLocationSignal.Disconnected);
        }

        return ModSignal(message);
    }

    /// <summary>
    /// What mods say when the world on screen goes away or a new one arrives. The game
    /// itself prints nothing when the player leaves a server (checked on 1.21.8 Forge and
    /// 1.21.11 with only OptiFine: the log is silent from the last line of play to the
    /// next "Connecting to"), so without one of these mods a stay ends only at the next
    /// join or when the game closes.
    /// </summary>
    /// <remarks>
    /// Each pair was read from real logs of Minecraft 1.21.11 on Fabric and is spelled
    /// exactly as printed there. All of them come in the same two shapes:
    /// <code>
    /// leaving for the menu      "let go" and then nothing
    /// a proxy moving the player "let go", then "got a new one" within a few seconds
    /// </code>
    /// which is why a "let go" alone never ends a stay. Fabric prints no logger name, so
    /// the message is all there is to match on; chat never gets this far.
    /// </remarks>
    private static GameLocationEvent ModSignal(string message)
    {
        message = message.TrimEnd();

        // Simple Voice Chat 2.6. It asks every server it lands on for a secret, with or
        // without voice chat there, and connects only where there is: the request alone
        // already says the player is in a world.
        if (message.StartsWith("[voicechat] ", StringComparison.Ordinal))
        {
            var voice = message["[voicechat] ".Length..];

            return voice.StartsWith("Disconnecting voicechat", StringComparison.Ordinal)
                ? new GameLocationEvent(GameLocationSignal.MaybeLeft)
                : voice.StartsWith("Connecting to voice chat server", StringComparison.Ordinal) ||
                  voice.StartsWith("Sending secret request to the server", StringComparison.Ordinal)
                    ? new GameLocationEvent(GameLocationSignal.StillThere)
                    : default;
        }

        switch (message)
        {
            // Sodium 0.8: its chunk builders stop when the world is taken away and start
            // for the next one; a change of video settings does both at once.
            case "Stopping worker threads":
            // Xaero's Minimap 26.5.
            case "Xaero hud session finalized.":
            // Xaero's World Map 1.46.
            case "World map session finalized.":
                return new GameLocationEvent(GameLocationSignal.MaybeLeft);

            case "New Xaero hud session initialized!":
            case "New world map session initialized!":
                return new GameLocationEvent(GameLocationSignal.StillThere);
        }

        return SodiumStarted.IsMatch(message)
            ? new GameLocationEvent(GameLocationSignal.StillThere)
            : default;
    }

    /// <summary>"Started 10 worker threads": Sodium has a world to build again.</summary>
    private static readonly Regex SodiumStarted = new(
        @"^Started \d{1,3} worker threads$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The line without the time, thread and logger in front of it.</summary>
    private static string Message(string line)
    {
        var colon = line.IndexOf("]: ", StringComparison.Ordinal);

        if (colon >= 0)
        {
            return line[(colon + 3)..];
        }

        var legacy = LegacyPrefix.Match(line);

        return legacy.Success ? line[legacy.Length..] : line;
    }

    /// <summary>
    /// One spelling per server: lower case, no trailing dot, the default port dropped, an
    /// IPv6 address in brackets. Null when there is no host to speak of.
    /// </summary>
    public static string? CanonicalAddress(string? host, int? port)
    {
        host = host?.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();

        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        if (host.Contains(':', StringComparison.Ordinal))
        {
            host = "[" + host + "]";
        }

        return port is null or DefaultPort or <= 0 or > 65535
            ? host
            : host + ":" + port.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The same for an address as people write it: "host", "host:port", "[v6]:port", a bare IPv6.</summary>
    public static string? CanonicalAddress(string? address)
    {
        address = address?.Trim();

        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        if (address[0] == '[')
        {
            var close = address.IndexOf(']', StringComparison.Ordinal);

            if (close < 0)
            {
                return null;
            }

            var rest = address[(close + 1)..];

            return CanonicalAddress(address[1..close], rest.StartsWith(':') ? ParsePort(rest[1..]) : null);
        }

        var first = address.IndexOf(':', StringComparison.Ordinal);

        // More than one colon and no brackets is an IPv6 address with no port.
        if (first < 0 || first != address.LastIndexOf(':'))
        {
            return CanonicalAddress(address, null);
        }

        return CanonicalAddress(address[..first], ParsePort(address[(first + 1)..]));
    }

    public static bool SameServer(string? left, string? right)
        => CanonicalAddress(left) is { } a && string.Equals(a, CanonicalAddress(right), StringComparison.Ordinal);

    private static int? ParsePort(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;
}

/// <summary>
/// Follows one run of the game through its output and adds up how long the player spent
/// in each place. Time is handed in by the caller, so the arithmetic can be tested.
/// </summary>
public sealed class GameLocationTracker
{
    /// <summary>How long a "maybe left" waits for the mod to reconnect before it counts as leaving.</summary>
    public static readonly TimeSpan LeaveGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long after such a leave a mod saying "I have a world again" still means the
    /// same server. A proxy move that reloads a large resource pack on a slow computer
    /// outlasts the grace; nothing announces the server again afterwards, so without this
    /// the rest of the evening would count as the menu.
    /// </summary>
    public static readonly TimeSpan ReturnWindow = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly Dictionary<GamePlace, TimeSpan> _spent = new();
    private GamePlace _place = GamePlace.Menu;
    private DateTimeOffset _since;
    private DateTimeOffset? _maybeLeftAt;

    /// <summary>The place a mod's word took the player out of, and when: where a late "still there" puts them back.</summary>
    private (GamePlace Place, DateTimeOffset At)? _letGo;

    public GameLocationTracker(DateTimeOffset startedAt)
    {
        _since = startedAt;
    }

    public GamePlace Place
    {
        get
        {
            lock (_gate)
            {
                return _place;
            }
        }
    }

    /// <summary>Takes one line of output. True when the place changed. Safe to call from the output threads.</summary>
    public bool Feed(string? line, DateTimeOffset now)
    {
        var seen = GameLocation.Recognise(line);

        lock (_gate)
        {
            var before = _place;
            Settle(now);

            switch (seen.Signal)
            {
                case GameLocationSignal.Connecting:
                    CommitLeave();
                    _letGo = null;
                    MoveTo(GamePlace.Server(seen.Address!), now);
                    break;

                case GameLocationSignal.Disconnected when _place.Kind == GamePlaceKind.Server:
                case GameLocationSignal.SinglePlayerStopped when _place.Kind == GamePlaceKind.SinglePlayer:
                    _maybeLeftAt = null;
                    _letGo = null;
                    MoveTo(GamePlace.Menu, now);
                    break;

                // The game's own word that the stay is over, after a mod already ended it.
                case GameLocationSignal.Disconnected:
                    _letGo = null;
                    break;

                case GameLocationSignal.SinglePlayerStarted:
                    CommitLeave();
                    _letGo = null;
                    MoveTo(GamePlace.SinglePlayer, now);
                    break;

                case GameLocationSignal.MaybeLeft when _place.Kind != GamePlaceKind.Menu:
                    _maybeLeftAt ??= now;
                    break;

                case GameLocationSignal.StillThere:
                    _maybeLeftAt = null;

                    // Only the wait itself stays with the menu; it was a loading screen.
                    if (_letGo is { } back && _place.Kind == GamePlaceKind.Menu && now - back.At <= ReturnWindow)
                    {
                        MoveTo(back.Place, now);
                    }

                    _letGo = null;
                    break;
            }

            return _place != before;
        }
    }

    /// <summary>The game has exited: time per place, the menu left out.</summary>
    public IReadOnlyDictionary<GamePlace, TimeSpan> Finish(DateTimeOffset now)
    {
        lock (_gate)
        {
            // The game closing right after the mod let go is the player quitting from the server.
            CommitLeave();
            MoveTo(GamePlace.Menu, now);

            var result = new Dictionary<GamePlace, TimeSpan>(_spent);
            result.Remove(GamePlace.Menu);
            return result;
        }
    }

    /// <summary>A "maybe left" nobody took back becomes a leave, dated when it was said.</summary>
    private void Settle(DateTimeOffset now)
    {
        if (_maybeLeftAt is { } left && now - left > LeaveGrace)
        {
            CommitLeave();
        }
    }

    /// <summary>Whatever comes next, the place the mod let go of ended when it said so.</summary>
    private void CommitLeave()
    {
        if (_maybeLeftAt is { } left)
        {
            _letGo = (_place, left);
            MoveTo(GamePlace.Menu, left);
            _maybeLeftAt = null;
        }
    }

    private void MoveTo(GamePlace place, DateTimeOffset at)
    {
        if (at > _since)
        {
            _spent[_place] = (_spent.TryGetValue(_place, out var sum) ? sum : TimeSpan.Zero) + (at - _since);
            _since = at;
        }

        _place = place;
    }
}
