using System;
using System.IO;
using STlauncher.Core.Instances;
using STlauncher.Core.Nbt;
using Xunit;

namespace STlauncher.Core.Tests;

public class NbtTests
{
    [Fact]
    public void RoundTrip_PreservesAllTagTypes()
    {
        var root = new NbtCompound();
        root.Set("byte", new NbtByte(-5));
        root.Set("short", new NbtShort(-1234));
        root.Set("int", new NbtInt(123456));
        root.Set("long", new NbtLong(9876543210));
        root.Set("float", new NbtFloat(1.5f));
        root.Set("double", new NbtDouble(2.25));
        root.Set("string", new NbtString("Hello NBT"));
        root.Set("bytes", new NbtByteArray(new byte[] { 1, 2, 3 }));
        root.Set("ints", new NbtIntArray(new[] { 7, 8, 9 }));

        var list = new NbtList(NbtTagType.Compound);
        list.Items.Add(new NbtCompound().Set("name", new NbtString("server")));
        root.Set("list", list);

        var nested = new NbtCompound();
        nested.Set("child", new NbtInt(42));
        root.Set("compound", nested);

        using var stream = new MemoryStream();
        NbtWriter.Write(stream, root, "root");

        stream.Position = 0;
        var read = NbtReader.Read(stream);

        Assert.Equal((sbyte)-5, ((NbtByte)read.Get("byte")!).Value);
        Assert.Equal((short)-1234, ((NbtShort)read.Get("short")!).Value);
        Assert.Equal(123456, ((NbtInt)read.Get("int")!).Value);
        Assert.Equal(9876543210L, ((NbtLong)read.Get("long")!).Value);
        Assert.Equal(1.5f, ((NbtFloat)read.Get("float")!).Value);
        Assert.Equal(2.25, ((NbtDouble)read.Get("double")!).Value);
        Assert.Equal("Hello NBT", read.GetString("string"));
        Assert.Equal(new byte[] { 1, 2, 3 }, ((NbtByteArray)read.Get("bytes")!).Value);
        Assert.Equal(new[] { 7, 8, 9 }, ((NbtIntArray)read.Get("ints")!).Value);
        Assert.Equal(42, ((NbtInt)((NbtCompound)read.Get("compound")!).Get("child")!).Value);

        var readList = (NbtList)read.Get("list")!;
        Assert.Single(readList.Items);
        Assert.Equal("server", ((NbtCompound)readList.Items[0]).GetString("name"));
    }
}

public class ServerListTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"), "servers.dat");

    [Fact]
    public void EnsureServer_CreatesFileAndAddsEntry()
    {
        var path = TempFile();

        var changed = ServerList.EnsureServer(path, "My Server", "play.example.com");

        Assert.True(changed);
        Assert.True(File.Exists(path));
        Assert.Equal(new[] { new ServerEntry("My Server", "play.example.com") }, ServerList.Load(path));
    }

    [Fact]
    public void EnsureServer_IsIdempotent()
    {
        var path = TempFile();
        ServerList.EnsureServer(path, "My Server", "play.example.com");

        Assert.False(ServerList.EnsureServer(path, "My Server", "play.example.com"));
        Assert.False(ServerList.EnsureServer(path, "My Server", "PLAY.EXAMPLE.COM"));
        Assert.Single(ServerList.Load(path));
    }

    [Fact]
    public void EnsureServer_PreservesExistingEntries()
    {
        var path = TempFile();
        ServerList.Save(path, new[]
        {
            new ServerEntry("Existing", "other.example.com")
        });

        ServerList.EnsureServer(path, "My Server", "play.example.com");
        var servers = ServerList.Load(path);

        Assert.Equal(2, servers.Count);
        Assert.Contains(new ServerEntry("Existing", "other.example.com"), servers);
        Assert.Contains(new ServerEntry("My Server", "play.example.com"), servers);
    }

    [Fact]
    public void EnsureServer_UpdatesNameWhenChanged()
    {
        var path = TempFile();
        ServerList.EnsureServer(path, "Old Name", "play.example.com");
        ServerList.EnsureServer(path, "New Name", "play.example.com");

        var server = Assert.Single(ServerList.Load(path));
        Assert.Equal("New Name", server.Name);
    }

    [Fact]
    public void EnsureServer_ReadsGzipCompressedFile()
    {
        // Minecraft's own NbtIo accepts a gzipped servers.dat, so we have to as well -
        // otherwise we read an empty list and overwrite the player's servers.
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var root = new NbtCompound();
        var list = new NbtList(NbtTagType.Compound);
        var entry = new NbtCompound();
        entry.Set("name", new NbtString("Existing"));
        entry.Set("ip", new NbtString("other.example.com"));
        entry.Set("acceptTextures", new NbtByte(1));
        list.Items.Add(entry);
        root.Set("servers", list);

        using (var file = File.Create(path))
        using (var gzip = new System.IO.Compression.GZipStream(
                   file, System.IO.Compression.CompressionMode.Compress))
        {
            NbtWriter.Write(gzip, root);
        }

        ServerList.EnsureServer(path, "My Server", "play.example.com");
        var servers = ServerList.Load(path);

        Assert.Equal(2, servers.Count);
        Assert.Contains(new ServerEntry("Existing", "other.example.com"), servers);
        Assert.Contains(new ServerEntry("My Server", "play.example.com"), servers);
    }

    [Fact]
    public void EnsureServer_DoesNotTouchAnUnreadableFile()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Not valid NBT in any encoding.
        var original = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(path, original);

        var changed = ServerList.EnsureServer(path, "My Server", "play.example.com");

        Assert.False(changed);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void EnsureServer_KeepsFieldsItDoesNotModel()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var root = new NbtCompound();
        var list = new NbtList(NbtTagType.Compound);
        var entry = new NbtCompound();
        entry.Set("name", new NbtString("Existing"));
        entry.Set("ip", new NbtString("play.example.com"));
        entry.Set("acceptTextures", new NbtByte(0));
        entry.Set("icon", new NbtString("data:image/png;base64,AAAA"));
        entry.Set("futureField", new NbtString("keep me"));
        list.Items.Add(entry);
        root.Set("servers", list);

        using (var stream = File.Create(path))
        {
            NbtWriter.Write(stream, root);
        }

        // Renaming forces a rewrite; the untouched fields must survive it.
        Assert.True(ServerList.EnsureServer(path, "Renamed", "play.example.com"));

        var reread = ServerList.Load(path);
        var server = Assert.Single(reread);
        Assert.Equal("Renamed", server.Name);
        Assert.Equal("data:image/png;base64,AAAA", server.Icon);

        ServerList.TryReadRoot(path, out var written);
        var writtenEntry = (NbtCompound)((NbtList)written!.Get("servers")!).Items[0];
        Assert.Equal("keep me", writtenEntry.GetString("futureField"));
    }

    [Fact]
    public void EnsureServer_TreatsAnEmptyFileAsNoServers()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());

        Assert.True(ServerList.EnsureServer(path, "My Server", "play.example.com"));
        Assert.Single(ServerList.Load(path));
    }
}
