using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Friends;

/// <summary>
/// A friend's server the player accepted an invite to: what it is, how to reach it and
/// which build to play it with.
/// </summary>
public sealed class FriendServer
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = string.Empty;

    [JsonPropertyName("loader")]
    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;

    [JsonPropertyName("loaderVersion")]
    public string? LoaderVersion { get; set; }

    [JsonPropertyName("hostNickname")]
    public string HostNickname { get; set; } = string.Empty;

    [JsonPropertyName("direct")]
    public string? Direct { get; set; }

    [JsonPropertyName("relay")]
    public string? Relay { get; set; }

    [JsonPropertyName("roomKey")]
    public string? RoomKey { get; set; }

    [JsonPropertyName("public")]
    public string? Public { get; set; }

    /// <summary>The UDP port of the server's voice chat, when the invite named one.</summary>
    [JsonPropertyName("voicePort")]
    public int? VoicePort { get; set; }

    /// <summary>Id of the build the invite brought, or of the one made for this server. Null when it is gone.</summary>
    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }

    /// <summary>
    /// The local port the relay way used last time. Asked for again so that the address
    /// the game remembers for this server stays the same.
    /// </summary>
    [JsonPropertyName("localPort")]
    public int LocalPort { get; set; }

    [JsonPropertyName("addedAt")]
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastPlayedAt")]
    public DateTimeOffset? LastPlayedAt { get; set; }

    [JsonIgnore]
    public ServerInviteEndpoints Endpoints => new(Direct, Relay, RoomKey, Public) { VoicePort = VoicePort is >= 1 and <= 65535 ? VoicePort : null };
}

/// <summary>
/// The friends' servers a player has accepted, kept in <c>friends.json</c> next to the
/// settings. The file holds addresses and room keys from other people's invites and
/// nothing of the player's own; a damaged one is set aside rather than overwritten.
/// </summary>
public sealed class FriendServerStore
{
    public const string FileName = "friends.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public FriendServerStore(LauncherPaths paths)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        FilePath = Path.Combine(paths.Root, FileName);
    }

    public string FilePath { get; }

    /// <summary>The remembered servers, oldest first. Never throws: no file, or one that cannot be read, is an empty list.</summary>
    public List<FriendServer> Load()
    {
        if (!File.Exists(FilePath))
        {
            return new List<FriendServer>();
        }

        try
        {
            var servers = JsonSerializer.Deserialize<List<FriendServer>>(File.ReadAllText(FilePath), JsonOptions);

            if (servers is not null)
            {
                return servers
                    .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.GameVersion))
                    .OrderBy(s => s.AddedAt)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Kept under another name: the next save must not bury what was there.
            SetAside();
        }
        catch (IOException)
        {
            // Locked right now; it is probably fine and will read next time.
        }

        return new List<FriendServer>();
    }

    public void Save(IEnumerable<FriendServer> servers)
    {
        var directory = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(servers.ToList(), JsonOptions));
    }

    /// <summary>
    /// The entry an invite belongs to, when the player already has this server. The room
    /// key is the server's identity where there is one: it stays the same for as long as
    /// the host keeps the server, while addresses change with the host's connection.
    /// Without a room, the host's nickname and the server's name have to do.
    /// </summary>
    public static FriendServer? FindSame(IEnumerable<FriendServer> servers, ServerInvite invite)
    {
        if (invite.Endpoints.RoomKey is { Length: > 0 } roomKey)
        {
            var byRoom = servers.FirstOrDefault(s => string.Equals(s.RoomKey, roomKey, StringComparison.OrdinalIgnoreCase));

            if (byRoom is not null)
            {
                return byRoom;
            }
        }

        return servers.FirstOrDefault(s =>
            string.Equals(s.HostNickname, invite.HostNickname, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Name, invite.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.GameVersion, invite.GameVersion, StringComparison.OrdinalIgnoreCase) &&
            s.Loader == invite.Loader);
    }

    /// <summary>
    /// Puts an invite into the list: a new entry, or fresh addresses for the entry that
    /// is already there. An existing entry keeps its id, its build and its local port.
    /// </summary>
    /// <param name="instanceId">The build to play with; null leaves an existing entry's build as it is.</param>
    public static FriendServer Remember(List<FriendServer> servers, ServerInvite invite, string? instanceId)
    {
        if (servers is null)
        {
            throw new ArgumentNullException(nameof(servers));
        }

        if (invite is null)
        {
            throw new ArgumentNullException(nameof(invite));
        }

        var server = FindSame(servers, invite);

        if (server is null)
        {
            server = new FriendServer { Id = Guid.NewGuid().ToString("N"), AddedAt = DateTimeOffset.UtcNow };
            servers.Add(server);
        }

        server.Name = invite.Name;
        server.GameVersion = invite.GameVersion;
        server.Loader = invite.Loader;
        server.LoaderVersion = invite.LoaderVersion;
        server.HostNickname = invite.HostNickname;
        server.Direct = invite.Endpoints.Direct;
        server.Relay = invite.Endpoints.Relay;
        server.RoomKey = invite.Endpoints.RoomKey;
        server.Public = invite.Endpoints.Public;
        server.VoicePort = invite.Endpoints.VoicePort;

        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            server.InstanceId = instanceId;
        }

        return server;
    }

    private void SetAside()
    {
        try
        {
            File.Move(FilePath, $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
        }
        catch (Exception)
        {
            // Best effort: the list is empty for this session either way.
        }
    }
}
