using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Nbt;

namespace STlauncher.Core.Instances;

public sealed record ServerEntry(string Name, string Address);

public static class ServerList
{
    public const string FileName = "servers.dat";

    public static bool EnsureServer(string serversDatPath, string name, string address)
    {
        var servers = Load(serversDatPath);
        var existing = servers.FirstOrDefault(
            s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                return false;
            }

            var index = servers.IndexOf(existing);
            servers[index] = existing with { Name = name };
            Save(serversDatPath, servers);
            return true;
        }

        servers.Add(new ServerEntry(name, address));
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

                if (!string.IsNullOrEmpty(ip))
                {
                    result.Add(new ServerEntry(name, ip));
                }
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
            entry.Set("hidden", new NbtByte(0));
            entry.Set("acceptTextures", new NbtByte(0));
            list.Items.Add(entry);
        }

        root.Set("servers", list);

        using var stream = File.Create(serversDatPath);
        NbtWriter.Write(stream, root);
    }
}