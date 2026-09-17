using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using STlauncher.Core.Nbt;

namespace STlauncher.Core.Instances;

/// <summary>
/// One entry of servers.dat.
/// </summary>
/// <param name="AcceptTextures">
/// 1 accepts the server resource pack, 0 means the player chose "Never".
/// </param>
public sealed record ServerEntry(
    string Name,
    string Address,
    int AcceptTextures = 1,
    bool Hidden = false,
    string? Icon = null);

/// <summary>How a servers.dat file turned out when we tried to read it.</summary>
public enum ServerListState
{
    /// <summary>No file yet - safe to create one from scratch.</summary>
    Missing,

    /// <summary>Read successfully.</summary>
    Loaded,

    /// <summary>
    /// The file exists but could not be parsed. It must never be overwritten: it holds the
    /// player's own server list and we would be replacing it with data we failed to read.
    /// </summary>
    Unreadable,
}

public static class ServerList
{
    public const string FileName = "servers.dat";

    /// <summary>
    /// Adds the server if it is missing, refreshes its name and makes sure the server
    /// resource pack is accepted. Without the last part the game keeps the "Never"
    /// choice and never downloads the pack.
    /// </summary>
    /// <returns>True when the file was changed.</returns>
    public static bool EnsureServer(string serversDatPath, string name, string address)
    {
        var state = TryReadRoot(serversDatPath, out var root);

        // Refuse to touch a file we could not parse. Writing here would silently replace
        // every server the player had with the single one we manage.
        if (state == ServerListState.Unreadable)
        {
            return false;
        }

        root ??= new NbtCompound();

        if (root.Get("servers") is not NbtList list)
        {
            list = new NbtList(NbtTagType.Compound);
            root.Set("servers", list);
        }

        // Mutating the parsed entry in place keeps every field we do not model (icons,
        // resource-pack settings, and whatever a future game version adds).
        var match = list.Items
            .OfType<NbtCompound>()
            .FirstOrDefault(c => string.Equals(
                c.GetString("ip"), address, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            var nameUnchanged = string.Equals(match.GetString("name"), name, StringComparison.Ordinal);
            var texturesAccepted = match.GetByte("acceptTextures") is 1;

            if (nameUnchanged && texturesAccepted)
            {
                return false;
            }

            match.Set("name", new NbtString(name));
            match.Set("acceptTextures", new NbtByte(1));
        }
        else
        {
            var entry = new NbtCompound();
            entry.Set("name", new NbtString(name));
            entry.Set("ip", new NbtString(address));
            entry.Set("acceptTextures", new NbtByte(1));
            entry.Set("hidden", new NbtByte(0));
            list.Items.Add(entry);
        }

        SaveRoot(serversDatPath, root);
        return true;
    }

    /// <summary>
    /// Read-only convenience overload. An unreadable file yields an empty list; use
    /// <see cref="TryReadRoot"/> when the difference matters.
    /// </summary>
    public static List<ServerEntry> Load(string serversDatPath)
    {
        TryReadRoot(serversDatPath, out var root);
        return ToEntries(root);
    }

    /// <summary>
    /// Reads servers.dat, transparently handling the gzip wrapper Minecraft itself accepts.
    /// Returns the root compound so callers can rewrite the file without losing fields.
    /// </summary>
    public static ServerListState TryReadRoot(string serversDatPath, out NbtCompound? root)
    {
        root = null;

        if (!File.Exists(serversDatPath))
        {
            return ServerListState.Missing;
        }

        try
        {
            var bytes = File.ReadAllBytes(serversDatPath);

            // A zero-length file is how the game represents "no servers", not corruption.
            if (bytes.Length == 0)
            {
                return ServerListState.Missing;
            }

            using var file = new MemoryStream(bytes);
            using var stream = IsGzip(bytes)
                ? new GZipStream(file, CompressionMode.Decompress)
                : (Stream)file;

            root = NbtReader.Read(stream);
            return ServerListState.Loaded;
        }
        catch (Exception)
        {
            return ServerListState.Unreadable;
        }
    }

    private static bool IsGzip(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b;

    private static List<ServerEntry> ToEntries(NbtCompound? root)
    {
        var result = new List<ServerEntry>();

        if (root?.Get("servers") is not NbtList list)
        {
            return result;
        }

        foreach (var item in list.Items.OfType<NbtCompound>())
        {
            var name = item.GetString("name") ?? string.Empty;
            var ip = item.GetString("ip") ?? string.Empty;

            if (string.IsNullOrEmpty(ip))
            {
                continue;
            }

            result.Add(new ServerEntry(
                name,
                ip,
                item.GetByte("acceptTextures") is { } textures ? textures : 1,
                item.GetByte("hidden") is 1,
                item.GetString("icon")));
        }

        return result;
    }

    /// <summary>Rebuilds the file from a plain entry list. Creates a fresh file.</summary>
    public static void Save(string serversDatPath, IReadOnlyList<ServerEntry> servers)
    {
        var root = new NbtCompound();
        var list = new NbtList(NbtTagType.Compound);

        foreach (var server in servers)
        {
            var entry = new NbtCompound();
            entry.Set("name", new NbtString(server.Name));
            entry.Set("ip", new NbtString(server.Address));
            entry.Set("acceptTextures", new NbtByte((sbyte)server.AcceptTextures));
            entry.Set("hidden", new NbtByte(server.Hidden ? (sbyte)1 : (sbyte)0));

            if (!string.IsNullOrEmpty(server.Icon))
            {
                entry.Set("icon", new NbtString(server.Icon!));
            }

            list.Items.Add(entry);
        }

        root.Set("servers", list);

        SaveRoot(serversDatPath, root);
    }

    /// <summary>
    /// Writes through a temporary file so an interrupted save cannot truncate the real
    /// servers.dat, and keeps the previous contents as .bak.
    /// </summary>
    private static void SaveRoot(string serversDatPath, NbtCompound root)
    {
        var directory = Path.GetDirectoryName(serversDatPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = serversDatPath + ".tmp";

        using (var stream = File.Create(temp))
        {
            NbtWriter.Write(stream, root);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(serversDatPath))
        {
            File.Replace(temp, serversDatPath, serversDatPath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, serversDatPath);
        }
    }
}
