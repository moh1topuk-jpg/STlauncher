using System;
using System.Collections.Generic;
using System.IO;
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

public static class ServerList
{
    public const string FileName = "servers.dat";

    /// <summary>
    /// Adds the server if it is missing, refreshes its name and makes sure the server
    /// resource pack is accepted. Without the last part the game keeps the "Never"
    /// choice and never downloads the pack.
    /// </summary>
    public static bool EnsureServer(string serversDatPath, string name, string address)
    {
        var servers = Load(serversDatPath);
        var existing = servers.FirstOrDefault(
            s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal) &&
                existing.AcceptTextures == 1)
            {
                return false;
            }

            var index = servers.IndexOf(existing);
            servers[index] = existing with { Name = name, AcceptTextures = 1 };
            Save(serversDatPath, servers);
            return true;
        }

        servers.Add(new ServerEntry(name, address, AcceptTextures: 1));
        Save(serversDatPath, servers);
        return true;
    }

    public static List<ServerEntry> Load(string serversDatPath)
    {
        var result = new List<ServerEntry>();

        if (!File.Exists(serversDatPath))
        {
            return result;
        }

        try
        {
            using var stream = File.OpenRead(serversDatPath);
            var root = NbtReader.Read(stream);

            if (root.Get("servers") is not NbtList list)
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
        }
        catch (Exception)
        {
            return new List<ServerEntry>();
        }

        return result;
    }

    public static void Save(string serversDatPath, IReadOnlyList<ServerEntry> servers)
    {
        var directory = Path.GetDirectoryName(serversDatPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

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

        using var stream = File.Create(serversDatPath);
        NbtWriter.Write(stream, root);
    }
}