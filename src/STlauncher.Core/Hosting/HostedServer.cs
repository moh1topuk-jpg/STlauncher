using System;
using System.Text.Json.Serialization;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Hosting;

/// <summary>
/// A Minecraft server the player runs on their own machine for friends. This is the
/// launcher's own description of it (<c>server.json</c>); the game's files -
/// server.properties, the whitelist, the world - sit beside it in the same folder and
/// stay the source of truth for what the game reads.
/// </summary>
public sealed class HostedServer
{
    public const int DefaultPort = 25565;
    public const int DefaultMemoryMb = 2048;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = string.Empty;

    [JsonPropertyName("loader")]
    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;

    /// <summary>Null for vanilla, and for a loader until the installer has picked a build.</summary>
    [JsonPropertyName("loaderVersion")]
    public string? LoaderVersion { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = DefaultPort;

    /// <summary>The Java heap limit (-Xmx), in megabytes.</summary>
    [JsonPropertyName("memoryMb")]
    public int MemoryMb { get; set; } = DefaultMemoryMb;

    /// <summary>Id of the build this server was made from; null for one made from scratch.</summary>
    [JsonPropertyName("sourceInstanceId")]
    public string? SourceInstanceId { get; set; }

    /// <summary>
    /// True only after the player said yes to Mojang's EULA on the screen. The launcher
    /// never sets it on its own: without it the server is not started at all.
    /// </summary>
    [JsonPropertyName("eulaAccepted")]
    public bool EulaAccepted { get; set; }

    [JsonPropertyName("eulaAcceptedAt")]
    public DateTimeOffset? EulaAcceptedAt { get; set; }

    /// <summary>
    /// The jar that starts the server, relative to its folder. Null until the server
    /// has been installed, which is also how "not installed yet" is told apart.
    /// </summary>
    [JsonPropertyName("launchJar")]
    public string? LaunchJar { get; set; }

    /// <summary>
    /// The Java the game version asks for, remembered at install time so that starting
    /// the server needs no network. Zero until installed.
    /// </summary>
    [JsonPropertyName("javaMajor")]
    public int JavaMajor { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastStartedAt")]
    public DateTimeOffset? LastStartedAt { get; set; }

    /// <summary>
    /// The host's secret for the relay room, kept so that invites already sent keep
    /// working after a restart. Never shown and never put into an invite: the invite
    /// carries the room key derived from it.
    /// </summary>
    [JsonPropertyName("hostKey")]
    public string? HostKey { get; set; }

    /// <summary>
    /// The ways of reaching the server the player switched on. They are remembered with
    /// the server and come up together with it; nothing here starts a way by itself.
    /// </summary>
    [JsonPropertyName("friendsRelay")]
    public bool FriendsRelay { get; set; }

    [JsonPropertyName("friendsDirect")]
    public bool FriendsDirect { get; set; }

    /// <summary>
    /// The last full invite that was put together for this server. It is left with the
    /// relay again whenever the server starts, so the short code a friend already has
    /// keeps working after a restart.
    /// </summary>
    public string? LastInvite { get; set; }
}
