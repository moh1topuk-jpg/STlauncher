using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using STlauncher.Core.Auth;

namespace STlauncher.Core.Hosting;

/// <summary>A player as the server's lists know them: the nickname and the id derived from it.</summary>
public sealed record ServerPlayer(string Name, Guid Uuid);

/// <summary>
/// <c>whitelist.json</c> and <c>ops.json</c>. A server in offline mode identifies a
/// player by the id it computes from the nickname alone - the same one
/// <see cref="OfflineAuth.ComputeUuid"/> gives - so that id is what goes into the lists.
/// The nickname is case-sensitive there: "Steve" and "steve" are two different players.
/// </summary>
/// <remarks>
/// The game reads these files at start. While the server runs, a changed whitelist
/// needs the console command <c>whitelist reload</c>, and operators are changed with
/// <c>op</c> / <c>deop</c> rather than through the file.
/// </remarks>
public static class ServerAccessLists
{
    public const string WhitelistFileName = "whitelist.json";
    public const string OpsFileName = "ops.json";

    /// <summary>The level of a server owner: every command, including stop.</summary>
    public const int OwnerOpLevel = 4;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static IReadOnlyList<ServerPlayer> ReadWhitelist(string serverDirectory)
        => ReadPlayers(Path.Combine(serverDirectory, WhitelistFileName));

    public static IReadOnlyList<ServerPlayer> ReadOps(string serverDirectory)
        => ReadPlayers(Path.Combine(serverDirectory, OpsFileName));

    /// <summary>Adds a nickname to the whitelist. False when it was already there or is not a valid nickname.</summary>
    public static bool AddToWhitelist(string serverDirectory, string name)
        => Add(Path.Combine(serverDirectory, WhitelistFileName), name, entry => entry);

    public static bool RemoveFromWhitelist(string serverDirectory, string name)
        => Remove(Path.Combine(serverDirectory, WhitelistFileName), name);

    /// <summary>Makes a nickname an operator. An entry that is already there keeps its level.</summary>
    public static bool AddOp(string serverDirectory, string name, int level = OwnerOpLevel)
        => Add(Path.Combine(serverDirectory, OpsFileName), name, entry =>
        {
            entry["level"] = Math.Clamp(level, 1, 4);
            entry["bypassesPlayerLimit"] = false;
            return entry;
        });

    public static bool RemoveOp(string serverDirectory, string name)
        => Remove(Path.Combine(serverDirectory, OpsFileName), name);

    /// <summary>The whitelist entry for a nickname, as the game writes it.</summary>
    public static ServerPlayer PlayerFor(string name) => new(name, OfflineAuth.ComputeUuid(name));

    private static IReadOnlyList<ServerPlayer> ReadPlayers(string path)
    {
        var result = new List<ServerPlayer>();

        foreach (var entry in ReadEntries(path))
        {
            var name = entry["name"]?.GetValueKind() == JsonValueKind.String ? entry["name"]!.GetValue<string>() : null;
            var uuid = entry["uuid"]?.GetValueKind() == JsonValueKind.String ? entry["uuid"]!.GetValue<string>() : null;

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            result.Add(new ServerPlayer(name, Guid.TryParse(uuid, out var parsed) ? parsed : OfflineAuth.ComputeUuid(name)));
        }

        return result;
    }

    private static bool Add(string path, string name, Func<JsonObject, JsonObject> fill)
    {
        name = name?.Trim() ?? string.Empty;

        if (!OfflineAuth.IsValidUsername(name))
        {
            return false;
        }

        var entries = ReadEntries(path);

        if (entries.Any(e => IsPlayer(e, name)))
        {
            return false;
        }

        entries.Add(fill(new JsonObject
        {
            ["uuid"] = OfflineAuth.ComputeUuid(name).ToString("D"),
            ["name"] = name
        }));

        Write(path, entries);
        return true;
    }

    private static bool Remove(string path, string name)
    {
        name = name?.Trim() ?? string.Empty;
        var entries = ReadEntries(path);
        var kept = entries.Where(e => !IsPlayer(e, name)).ToList();

        if (kept.Count == entries.Count)
        {
            return false;
        }

        Write(path, kept);
        return true;
    }

    private static bool IsPlayer(JsonObject entry, string name)
        => entry["name"]?.GetValueKind() == JsonValueKind.String &&
           string.Equals(entry["name"]!.GetValue<string>(), name, StringComparison.Ordinal);

    /// <summary>
    /// The entries as they are in the file, unknown fields and all: an operator's level
    /// set in the game must survive the launcher adding somebody else.
    /// </summary>
    private static List<JsonObject> ReadEntries(string path)
    {
        var result = new List<JsonObject>();

        try
        {
            if (!File.Exists(path))
            {
                return result;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array)
            {
                return result;
            }

            foreach (var node in array.ToList())
            {
                if (node is JsonObject entry)
                {
                    // Detached so it can be put into the array that is written back.
                    array.Remove(entry);
                    result.Add(entry);
                }
            }
        }
        catch (JsonException)
        {
            // A damaged list reads as an empty one; the game does the same.
        }

        return result;
    }

    private static void Write(string path, IEnumerable<JsonObject> entries)
    {
        var array = new JsonArray();

        foreach (var entry in entries)
        {
            array.Add(entry);
        }

        AtomicFile.WriteAllText(path, array.ToJsonString(WriteOptions));
    }
}
