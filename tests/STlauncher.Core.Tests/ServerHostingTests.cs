using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Auth;
using STlauncher.Core.Hosting;
using STlauncher.Core.Http;
using STlauncher.Core.Java;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

internal static class HostingTemp
{
    public static string Directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Jar(string directory, string fileName, params (string Entry, string Content)[] entries)
    {
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }
}

public class ServerPropertiesTests
{
    private const string Sample =
        "#Minecraft server properties\n" +
        "#Sat Oct 03 12:00:00 MSK 2026\n" +
        "\n" +
        "# my own note, do not lose\n" +
        "allow-flight=false\n" +
        "motd=A Minecraft Server\n" +
        "some-mod-key : spaced value\n" +
        "resource-pack=https\\://example.com/pack.zip\n" +
        "server-port=25565\n" +
        "level-seed=\n";

    [Fact]
    public void RoundTrip_UntouchedFileComesBackByteForByte()
        => Assert.Equal(Sample, ServerProperties.Parse(Sample).ToString());

    [Fact]
    public void Parse_ReadsSeparatorsAndEscapes()
    {
        var properties = ServerProperties.Parse(Sample);

        Assert.Equal("false", properties.Get("allow-flight"));
        Assert.Equal("spaced value", properties.Get("some-mod-key"));
        Assert.Equal("https://example.com/pack.zip", properties.Get("resource-pack"));
        Assert.Equal(string.Empty, properties.Get("level-seed"));
        Assert.Equal(25565, properties.GetInt(ServerProperties.PortKey, 0));
        Assert.Null(properties.Get("no-such-key"));
    }

    [Fact]
    public void Set_RewritesOnlyTheLineItChanges_AndAppendsNewKeys()
    {
        var properties = ServerProperties.Parse(Sample);

        properties.Set(ServerProperties.PortKey, 25570);
        properties.Set("white-list", true);

        var expected = Sample.Replace("server-port=25565", "server-port=25570") + "white-list=true\n";
        Assert.Equal(expected, properties.ToString());
    }

    [Fact]
    public void Set_NonAsciiValueSurvivesAsEscapes()
    {
        var properties = new ServerProperties();
        properties.Set(ServerProperties.MotdKey, "Сервер: для друзей");

        var text = properties.ToString();

        // Plain ASCII on disk, so every version of the game reads the same thing.
        Assert.All(text, ch => Assert.True(ch < 0x7F));
        Assert.Equal("Сервер: для друзей", ServerProperties.Parse(text).Get(ServerProperties.MotdKey));
    }

    [Fact]
    public void ApplyFriendsDefaults_KeepsUnknownKeysAndSetsTheLaunchersOwn()
    {
        var properties = ServerProperties.Parse(Sample + "online-mode=true\n");

        properties.ApplyFriendsDefaults("Дача", 25570);

        Assert.False(properties.GetBool(ServerProperties.OnlineModeKey, true));
        Assert.True(properties.GetBool(ServerProperties.WhitelistKey, false));
        Assert.True(properties.GetBool(ServerProperties.EnforceWhitelistKey, false));

        // The list is by nickname, so the answer to a ping must not hand the nicknames out.
        Assert.True(properties.GetBool(ServerProperties.HideOnlinePlayersKey, false));
        Assert.Equal(25570, properties.GetInt(ServerProperties.PortKey, 0));
        Assert.Equal("Дача", properties.Get(ServerProperties.MotdKey));
        Assert.Equal("spaced value", properties.Get("some-mod-key"));
        Assert.Contains("# my own note, do not lose", properties.ToString());
    }

    [Fact]
    public void Parse_KeepsWindowsLineEndingsAndContinuationLines()
    {
        const string text = "a=1\r\nlong=first \\\r\n    second\r\n#end\r\n";
        var properties = ServerProperties.Parse(text);

        Assert.Equal("first second", properties.Get("long"));
        Assert.Equal(text, properties.ToString());
    }

    [Fact]
    public void SaveAndLoad_GoThroughTheFile()
    {
        var path = Path.Combine(HostingTemp.Directory(), ServerProperties.FileName);

        Assert.Empty(ServerProperties.Load(path).Keys);

        var properties = ServerProperties.Parse(Sample);
        properties.Set("max-players", 8);
        properties.Save(path);

        Assert.Equal(8, ServerProperties.Load(path).GetInt("max-players", 0));
        Assert.Equal("A Minecraft Server", ServerProperties.Load(path).Get(ServerProperties.MotdKey));
    }
}

public class ServerAccessListsTests
{
    [Fact]
    public void Whitelist_IsWrittenTheWayTheGameReadsIt()
    {
        var dir = HostingTemp.Directory();

        Assert.True(ServerAccessLists.AddToWhitelist(dir, "Steve"));
        Assert.True(ServerAccessLists.AddToWhitelist(dir, "Alex_2"));
        Assert.False(ServerAccessLists.AddToWhitelist(dir, "Steve"));

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ServerAccessLists.WhitelistFileName)));
        var entries = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal("Steve", entries[0].GetProperty("name").GetString());

        // The id an offline-mode server computes for the same nickname, dashed.
        Assert.Equal(OfflineAuth.ComputeUuid("Steve").ToString("D"), entries[0].GetProperty("uuid").GetString());
        Assert.Equal(36, entries[0].GetProperty("uuid").GetString()!.Length);

        var read = ServerAccessLists.ReadWhitelist(dir);
        Assert.Equal(new[] { "Steve", "Alex_2" }, read.Select(p => p.Name));
        Assert.Equal(OfflineAuth.ComputeUuid("Alex_2"), read[1].Uuid);
    }

    [Fact]
    public void Whitelist_IsCaseSensitiveAndRejectsBadNicknames()
    {
        var dir = HostingTemp.Directory();

        Assert.True(ServerAccessLists.AddToWhitelist(dir, "Steve"));
        Assert.True(ServerAccessLists.AddToWhitelist(dir, "steve"));
        Assert.False(ServerAccessLists.AddToWhitelist(dir, "no spaces"));
        Assert.False(ServerAccessLists.AddToWhitelist(dir, ""));

        Assert.True(ServerAccessLists.RemoveFromWhitelist(dir, "steve"));
        Assert.False(ServerAccessLists.RemoveFromWhitelist(dir, "steve"));
        Assert.Equal(new[] { "Steve" }, ServerAccessLists.ReadWhitelist(dir).Select(p => p.Name));
    }

    [Fact]
    public void Ops_KeepWhatTheGameWroteAboutOthers()
    {
        var dir = HostingTemp.Directory();
        var path = Path.Combine(dir, ServerAccessLists.OpsFileName);

        File.WriteAllText(path,
            "[{\"uuid\":\"" + OfflineAuth.ComputeUuid("Friend").ToString("D") +
            "\",\"name\":\"Friend\",\"level\":2,\"bypassesPlayerLimit\":true}]");

        Assert.True(ServerAccessLists.AddOp(dir, "Owner"));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entries = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries[0].GetProperty("level").GetInt32());
        Assert.True(entries[0].GetProperty("bypassesPlayerLimit").GetBoolean());
        Assert.Equal("Owner", entries[1].GetProperty("name").GetString());
        Assert.Equal(4, entries[1].GetProperty("level").GetInt32());
        Assert.False(entries[1].GetProperty("bypassesPlayerLimit").GetBoolean());

        Assert.True(ServerAccessLists.RemoveOp(dir, "Friend"));
        Assert.Equal(new[] { "Owner" }, ServerAccessLists.ReadOps(dir).Select(p => p.Name));
    }

    [Fact]
    public void DamagedList_ReadsAsEmpty()
    {
        var dir = HostingTemp.Directory();
        File.WriteAllText(Path.Combine(dir, ServerAccessLists.WhitelistFileName), "{ not a list");

        Assert.Empty(ServerAccessLists.ReadWhitelist(dir));
    }
}

public class ServerEulaTests
{
    [Fact]
    public void Eula_IsWrittenOnlyWhenThePlayerAccepted()
    {
        var dir = HostingTemp.Directory();

        Assert.False(ServerEula.Accept(dir, playerAccepted: false));
        Assert.False(File.Exists(Path.Combine(dir, ServerEula.FileName)));
        Assert.False(ServerEula.IsAccepted(dir));

        Assert.True(ServerEula.Accept(dir, playerAccepted: true));
        Assert.Contains("eula=true", File.ReadAllText(Path.Combine(dir, ServerEula.FileName)));
        Assert.True(ServerEula.IsAccepted(dir));
    }

    [Fact]
    public void Eula_TheGamesOwnRefusalIsNotAcceptance()
    {
        var dir = HostingTemp.Directory();
        File.WriteAllText(Path.Combine(dir, ServerEula.FileName), "#comment\neula=false\n");

        Assert.False(ServerEula.IsAccepted(dir));
    }
}

public class HostedServerStoreTests
{
    private static HostedServerStore NewStore(out string root)
    {
        root = HostingTemp.Directory();
        return new HostedServerStore(new LauncherPaths(root));
    }

    [Fact]
    public void Create_List_Get_RoundTrip()
    {
        var store = NewStore(out var root);

        var first = store.Create("Наш сервер", "1.21.1");
        var second = store.Create("Friends SMP", "1.20.1", LoaderKind.Fabric, "0.16.14", sourceInstanceId: "my-build");
        var third = store.Create("Friends SMP", "1.20.1");

        // Cyrillic leaves nothing for an ASCII id; the name itself is kept as typed.
        Assert.Equal("server", first.Id);
        Assert.Equal("friends-smp", second.Id);
        Assert.Equal("friends-smp-2", third.Id);
        Assert.True(File.Exists(Path.Combine(root, "servers", "friends-smp", "server.json")));

        var listed = store.List();
        Assert.Equal(3, listed.Count);

        var read = store.Get("friends-smp")!;
        Assert.Equal("Friends SMP", read.Name);
        Assert.Equal("1.20.1", read.GameVersion);
        Assert.Equal(LoaderKind.Fabric, read.Loader);
        Assert.Equal("0.16.14", read.LoaderVersion);
        Assert.Equal("my-build", read.SourceInstanceId);
        Assert.Equal(HostedServer.DefaultPort, read.Port);
        Assert.Equal(HostedServer.DefaultMemoryMb, read.MemoryMb);
        Assert.False(read.EulaAccepted);
        Assert.Null(read.LaunchJar);
        Assert.Equal("Наш сервер", store.Get("server")!.Name);
    }

    [Fact]
    public void Rename_KeepsTheFolder_AndMovesTheMotdOnlyWhileItIsStillTheName()
    {
        var store = NewStore(out _);
        var server = store.Create("Old name", "1.21.1");
        store.WriteStartingFiles(server, "Owner");

        var renamed = store.Rename(server.Id, "New name");
        var path = ServerProperties.PathIn(store.ServerDirectory(server));

        Assert.Equal(server.Id, renamed.Id);
        Assert.Equal("New name", store.Get(server.Id)!.Name);
        Assert.Equal("New name", ServerProperties.Load(path).Get(ServerProperties.MotdKey));

        var properties = ServerProperties.Load(path);
        properties.Set(ServerProperties.MotdKey, "Заходите в субботу");
        properties.Save(path);

        store.Rename(server.Id, "Third name");
        Assert.Equal("Заходите в субботу", ServerProperties.Load(path).Get(ServerProperties.MotdKey));
    }

    [Fact]
    public void WriteStartingFiles_GivesAFriendsServer_WithTheOwnerLetIn()
    {
        var store = NewStore(out _);
        var server = store.Create("Home", "1.21.1");
        server.Port = 25570;
        store.Save(server);

        store.WriteStartingFiles(server, "Owner");

        var dir = store.ServerDirectory(server);
        var properties = ServerProperties.Load(ServerProperties.PathIn(dir));

        Assert.False(properties.GetBool(ServerProperties.OnlineModeKey, true));
        Assert.True(properties.GetBool(ServerProperties.WhitelistKey, false));
        Assert.True(properties.GetBool(ServerProperties.EnforceWhitelistKey, false));
        Assert.Equal(25570, properties.GetInt(ServerProperties.PortKey, 0));
        Assert.Equal("Home", properties.Get(ServerProperties.MotdKey));
        Assert.Equal(new[] { "Owner" }, ServerAccessLists.ReadWhitelist(dir).Select(p => p.Name));
        Assert.Equal(new[] { "Owner" }, ServerAccessLists.ReadOps(dir).Select(p => p.Name));

        // Nothing here agrees to the EULA for the player.
        Assert.False(File.Exists(Path.Combine(dir, ServerEula.FileName)));
    }

    [Fact]
    public void AcceptEula_NeedsThePlayersWord()
    {
        var store = NewStore(out _);
        var server = store.Create("Home", "1.21.1");

        Assert.False(store.AcceptEula(server, playerAccepted: false));
        Assert.False(store.Get(server.Id)!.EulaAccepted);
        Assert.False(ServerEula.IsAccepted(store.ServerDirectory(server)));

        Assert.True(store.AcceptEula(server, playerAccepted: true));

        var saved = store.Get(server.Id)!;
        Assert.True(saved.EulaAccepted);
        Assert.NotNull(saved.EulaAcceptedAt);
        Assert.True(ServerEula.IsAccepted(store.ServerDirectory(server)));
    }

    [Fact]
    public void Remove_MovesTheFolderAside_AndDeletesNothing()
    {
        var store = NewStore(out _);
        var server = store.Create("Home", "1.21.1");
        var world = Path.Combine(store.ServerDirectory(server), "world");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "level.dat"), "hours of somebody's life");

        var parked = store.Remove(server.Id);

        Assert.Empty(store.List());
        Assert.False(Directory.Exists(store.ServerDirectory(server)));
        Assert.StartsWith(store.RemovedRoot, parked);
        Assert.Equal("hours of somebody's life", File.ReadAllText(Path.Combine(parked, "world", "level.dat")));

        // The id is free again, and the parked folder is not mistaken for a server.
        Assert.Equal("home", store.Create("Home", "1.21.1").Id);
        Assert.Single(store.List());
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".removed")]
    [InlineData("a/../../instances")]
    [InlineData("")]
    public void Remove_RefusesAnythingThatIsNotAServerId(string id)
    {
        var store = NewStore(out _);
        Assert.ThrowsAny<Exception>(() => store.Remove(id));
    }
}

public class ServerContentTests
{
    private static string Fabric(string environment)
        => "{\"schemaVersion\":1,\"id\":\"m\",\"name\":\"Some Mod\",\"version\":\"1.0\",\"environment\":\"" + environment + "\"}";

    private static (string Instance, string Server) NewPair()
    {
        var root = HostingTemp.Directory();
        var instance = Path.Combine(root, "instance");
        var server = Path.Combine(root, "server");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        Directory.CreateDirectory(server);
        return (instance, server);
    }

    [Fact]
    public void IsClientOnly_ReadsEveryLoadersWayOfSayingIt()
    {
        var dir = HostingTemp.Directory();

        Assert.True(ServerContent.IsClientOnly(HostingTemp.Jar(dir, "a.jar", ("fabric.mod.json", Fabric("client")))));
        Assert.False(ServerContent.IsClientOnly(HostingTemp.Jar(dir, "b.jar", ("fabric.mod.json", Fabric("*")))));
        Assert.False(ServerContent.IsClientOnly(HostingTemp.Jar(dir, "c.jar", ("fabric.mod.json", Fabric("server")))));
        Assert.False(ServerContent.IsClientOnly(HostingTemp.Jar(dir, "d.jar", ("fabric.mod.json", "{\"id\":\"m\"}"))));

        Assert.True(ServerContent.IsClientOnly(HostingTemp.Jar(dir, "e.jar",
            ("quilt.mod.json", "{\"quilt_loader\":{\"id\":\"q\"},\"minecraft\":{\"environment\":\"client\"}}"))));

        Assert.True(ServerContent.IsClientOnly(HostingTemp.Jar(dir, "f.jar",
            ("META-INF/mods.toml", "modLoader=\"javafml\"\nclientSideOnly=true\n[[mods]]\nmodId=\"f\"\n"))));
        Assert.False(ServerContent.IsClientOnly(HostingTemp.Jar(dir, "g.jar",
            ("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"g\"\n"))));

        // Not a jar at all: nothing says client-only.
        var junk = Path.Combine(dir, "junk.jar");
        File.WriteAllText(junk, "not a zip");
        Assert.False(ServerContent.IsClientOnly(junk));
    }

    [Fact]
    public void CopyMods_LeavesOutClientOnlyAndDisabled_AndTouchesNothingInTheBuild()
    {
        var (instance, server) = NewPair();
        var mods = Path.Combine(instance, "mods");

        HostingTemp.Jar(mods, "lithium.jar", ("fabric.mod.json", Fabric("*")));
        HostingTemp.Jar(mods, "sodium.jar", ("fabric.mod.json", Fabric("client")));
        HostingTemp.Jar(mods, "off.jar.disabled", ("fabric.mod.json", Fabric("*")));
        HostingTemp.Jar(mods, "library.jar", ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n"));
        File.WriteAllText(Path.Combine(mods, "notes.txt"), "x");

        HostingTemp.Jar(Path.Combine(server, "mods"), "server-side-only.jar", ("fabric.mod.json", Fabric("server")));

        var before = Directory.GetFiles(mods).OrderBy(f => f).Select(f => (f, new FileInfo(f).Length)).ToList();

        // The plan says the same thing and writes nothing.
        var plan = ServerContent.PlanMods(instance, server);
        Assert.Equal(new[] { "library.jar", "lithium.jar" }, plan.Copied.Select(c => c.FileName));
        Assert.False(File.Exists(Path.Combine(server, "mods", "lithium.jar")));

        var result = ServerContent.CopyMods(instance, server);

        Assert.Equal(new[] { "library.jar", "lithium.jar" }, result.Copied.Select(c => c.FileName));
        Assert.All(result.Copied, c => Assert.False(c.AlreadyThere));
        Assert.Equal("Some Mod", result.Copied[1].Name);
        Assert.Null(result.Copied[0].Name);

        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.FileName == "sodium.jar" && s.Reason == ModSkipReason.ClientOnly);
        Assert.Contains(result.Skipped, s => s.FileName == "off.jar.disabled" && s.Reason == ModSkipReason.Disabled);
        Assert.Equal(new[] { "server-side-only.jar" }, result.ServerOnly);

        Assert.True(File.Exists(Path.Combine(server, "mods", "lithium.jar")));
        Assert.False(File.Exists(Path.Combine(server, "mods", "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(server, "mods", "server-side-only.jar")));

        // Copied, not moved: the build is exactly as it was.
        Assert.Equal(before, Directory.GetFiles(mods).OrderBy(f => f).Select(f => (f, new FileInfo(f).Length)).ToList());

        // A second run finds everything in place.
        Assert.All(ServerContent.CopyMods(instance, server).Copied, c => Assert.True(c.AlreadyThere));
    }

    [Fact]
    public void CopyWorld_CopiesWithoutTheLock_AndNeverReplacesUnasked()
    {
        var (instance, server) = NewPair();
        var save = Path.Combine(instance, "saves", "My World");
        Directory.CreateDirectory(Path.Combine(save, "region"));
        File.WriteAllText(Path.Combine(save, "level.dat"), "level");
        File.WriteAllText(Path.Combine(save, "region", "r.0.0.mca"), "chunks");
        File.WriteAllText(Path.Combine(save, "session.lock"), "lock");

        Assert.Equal("My World", Assert.Single(ServerContent.ListWorlds(instance)).FolderName);
        Assert.False(ServerContent.HasWorld(server));

        var first = ServerContent.CopyWorld(instance, "My World", server);

        Assert.Equal(WorldCopyStatus.Copied, first.Status);
        Assert.Equal(2, first.Files);
        Assert.Equal(Path.Combine(server, "world"), first.WorldPath);
        Assert.Equal("chunks", File.ReadAllText(Path.Combine(server, "world", "region", "r.0.0.mca")));
        Assert.False(File.Exists(Path.Combine(server, "world", "session.lock")));
        Assert.True(ServerContent.HasWorld(server));

        // The original is still there, lock and all.
        Assert.Equal("level", File.ReadAllText(Path.Combine(save, "level.dat")));
        Assert.True(File.Exists(Path.Combine(save, "session.lock")));

        File.WriteAllText(Path.Combine(server, "world", "level.dat"), "played on the server");

        Assert.Equal(WorldCopyStatus.DestinationExists, ServerContent.CopyWorld(instance, "My World", server).Status);
        Assert.Equal("played on the server", File.ReadAllText(Path.Combine(server, "world", "level.dat")));

        var replaced = ServerContent.CopyWorld(instance, "My World", server, replaceExisting: true);

        Assert.Equal(WorldCopyStatus.Copied, replaced.Status);
        Assert.Equal("level", File.ReadAllText(Path.Combine(server, "world", "level.dat")));
        Assert.NotNull(replaced.PreviousWorldPath);
        Assert.Equal("played on the server", File.ReadAllText(Path.Combine(replaced.PreviousWorldPath!, "level.dat")));
    }

    [Fact]
    public void CopyWorld_SaysSoWhenTheWorldIsMissingOrOpen()
    {
        var (instance, server) = NewPair();

        Assert.Equal(WorldCopyStatus.SourceMissing, ServerContent.CopyWorld(instance, "Nope", server).Status);
        Assert.Equal(WorldCopyStatus.SourceMissing, ServerContent.CopyWorld(instance, "..", server).Status);

        var save = Path.Combine(instance, "saves", "Open");
        Directory.CreateDirectory(save);
        File.WriteAllText(Path.Combine(save, "level.dat"), "level");
        File.WriteAllText(Path.Combine(save, "session.lock"), "lock");

        if (OperatingSystem.IsWindows())
        {
            // The game keeps the lock file open while the world is loaded.
            using var held = new FileStream(Path.Combine(save, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Assert.Equal(WorldCopyStatus.SourceInUse, ServerContent.CopyWorld(instance, "Open", server).Status);
        }

        Assert.Equal(WorldCopyStatus.Copied, ServerContent.CopyWorld(instance, "Open", server).Status);
    }

    [Fact]
    public void CopyWorld_CutShort_LeavesNothingThatLooksLikeAWorld()
    {
        var (instance, server) = NewPair();
        var save = Path.Combine(instance, "saves", "Big");
        Directory.CreateDirectory(Path.Combine(save, "region"));
        File.WriteAllText(Path.Combine(save, "level.dat"), "level");
        File.WriteAllText(Path.Combine(save, "region", "r.0.0.mca"), "chunks");

        if (OperatingSystem.IsWindows())
        {
            // A file that cannot be read stops the copy part way, as a full disk would.
            using (new FileStream(Path.Combine(save, "region", "r.0.0.mca"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => ServerContent.CopyWorld(instance, "Big", server));
            }

            // level.dat did get across, but not into the world's place: the server has no world.
            Assert.False(ServerContent.HasWorld(server));
            Assert.False(Directory.Exists(Path.Combine(server, "world")));
        }

        // The next attempt is a whole copy, not "already there".
        var again = ServerContent.CopyWorld(instance, "Big", server);

        Assert.Equal(WorldCopyStatus.Copied, again.Status);
        Assert.Equal(2, again.Files);
        Assert.Equal("chunks", File.ReadAllText(Path.Combine(server, "world", "region", "r.0.0.mca")));
        Assert.False(Directory.Exists(Path.Combine(server, "world" + ServerContent.CopyingSuffix)));
    }
}

public class ServerCommandLineTests
{
    [Fact]
    public void Vanilla_IsJavaXmxJarNogui_InTheServersFolder()
    {
        var server = new HostedServer { Id = "home", GameVersion = "1.21.1", MemoryMb = 3072, LaunchJar = "server.jar" };

        var command = ServerCommandLine.Build(server, @"C:\data\servers\home", @"C:\java\bin\java.exe");

        Assert.Equal(@"C:\java\bin\java.exe", command.FileName);
        Assert.Equal(@"C:\data\servers\home", command.WorkingDirectory);
        Assert.Equal("-Xmx3072M", command.Arguments[0]);
        Assert.Equal(new[] { "-jar", "server.jar", "nogui" }, command.Arguments.TakeLast(3));
        Assert.DoesNotContain(command.Arguments, a => a.StartsWith("-Dfabric", StringComparison.Ordinal));

        // Every JVM option comes before -jar; anything after it would go to the game.
        var jar = command.Arguments.ToList().IndexOf("-jar");
        Assert.All(command.Arguments.Take(jar), a => Assert.StartsWith("-", a));
    }

    [Fact]
    public void Fabric_StartsItsLauncher_AndPointsItAtTheVerifiedGameJar()
    {
        var server = new HostedServer
        {
            Id = "modded",
            GameVersion = "1.21.1",
            Loader = LoaderKind.Fabric,
            LaunchJar = "fabric-server-mc.1.21.1-loader.0.19.5-launcher.1.1.2.jar"
        };

        var command = ServerCommandLine.Build(server, "/srv", "/usr/bin/java");

        Assert.Contains("-Dfabric.installer.server.gameJar=server.jar", command.Arguments);
        Assert.Equal(new[] { "-jar", server.LaunchJar, "nogui" }, command.Arguments.TakeLast(3));
    }

    [Fact]
    public void Memory_HasAFloor_AndAnUninstalledServerHasNoCommand()
    {
        var server = new HostedServer { Id = "tiny", MemoryMb = 64, LaunchJar = "server.jar" };
        Assert.Equal("-Xmx512M", ServerCommandLine.Build(server, "/srv", "/usr/bin/java").Arguments[0]);

        server.LaunchJar = null;
        Assert.Throws<InvalidOperationException>(() => ServerCommandLine.Build(server, "/srv", "/usr/bin/java"));
    }
}

public class ServerOutputTests
{
    [Theory]
    [InlineData("[12:00:05] [Server thread/INFO]: Done (3.456s)! For help, type \"help\"", 3.456)]
    [InlineData("[12:00:05] [Server thread/INFO]: Done (12,5s)! For help, type \"help\" or \"?\"", 12.5)]
    [InlineData("[12:00:05] [Server thread/INFO] [minecraft/DedicatedServer]: Done (7.1s)! For help, type \"help\"", 7.1)]
    [InlineData("[12:00:05 INFO]: Done (2.0s)! For help, type \"help\"", 2.0)]
    [InlineData("2013-06-01 12:00:05 [INFO] Done (1.25s)! For help, type \"help\" or \"?\"", 1.25)]
    public void Ready_IsTheDoneLine(string line, double seconds)
    {
        var parsed = ServerOutput.Parse(line);

        Assert.Equal(ServerLineKind.Ready, parsed.Kind);
        Assert.Equal(seconds, parsed.StartupSeconds!.Value, 3);
        Assert.Equal(line, parsed.Text);
    }

    [Theory]
    [InlineData("[12:01:00] [Server thread/INFO]: Steve joined the game", ServerLineKind.PlayerJoined, "Steve")]
    [InlineData("[12:09:00] [Server thread/INFO]: Alex_2 left the game", ServerLineKind.PlayerLeft, "Alex_2")]
    [InlineData("2013-06-01 12:00:05 [INFO] Steve joined the game", ServerLineKind.PlayerJoined, "Steve")]
    public void Players_AreRecognisedByName(string line, ServerLineKind kind, string player)
    {
        var parsed = ServerOutput.Parse(line);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(player, parsed.Player);
    }

    [Theory]
    [InlineData("[12:01:00] [Server thread/INFO]: <Steve> joined the game")]
    [InlineData("[12:01:00] [Server thread/INFO]: <Steve> Alex joined the game")]
    [InlineData("[12:01:00] [Server thread/INFO]: [Not Secure] <Steve> Alex joined the game")]
    [InlineData("[12:01:00] [Server thread/INFO]: [Server] Alex left the game")]
    [InlineData("[12:01:00] [Server thread/INFO]: * Steve joined the game")]
    [InlineData("[12:01:00] [Server thread/INFO]: <Steve> Done (1.0s)! For help, type \"help\"")]
    [InlineData("[12:01:00] [Server thread/INFO]: <Steve> **** FAILED TO BIND TO PORT!")]
    [InlineData("[12:01:00] [Server thread/INFO]: Steve[/127.0.0.1:50000] logged in with entity id 1 at (0.5, 64.0, 0.5)")]
    [InlineData("Done (1.0s)! For help, type \"help\"")]
    [InlineData("")]
    public void Chat_CannotPassItselfOffAsTheServer(string line)
        => Assert.Equal(ServerLineKind.Plain, ServerOutput.Parse(line).Kind);

    [Theory]
    [InlineData("[12:00:02] [Server thread/WARN]: **** FAILED TO BIND TO PORT!", ServerLineKind.PortInUse)]
    [InlineData("[12:00:02] [Server thread/WARN]: The exception was: java.net.BindException: Address already in use: bind", ServerLineKind.PortInUse)]
    [InlineData("[12:00:01] [main/INFO]: You need to agree to the EULA in order to run the server. Go to eula.txt for more info.", ServerLineKind.EulaRequired)]
    [InlineData("Error: LinkageError occurred while loading main class net.minecraft.bundler.Main", ServerLineKind.Plain)]
    [InlineData("\tjava.lang.UnsupportedClassVersionError: net/minecraft/bundler/Main has been compiled by a more recent version of the Java Runtime (class file version 65.0)", ServerLineKind.WrongJava)]
    [InlineData("Error occurred during initialization of VM", ServerLineKind.Plain)]
    [InlineData("Could not reserve enough space for 8388608KB object heap", ServerLineKind.NotEnoughMemory)]
    [InlineData("Invalid maximum heap size: -Xmx64000M", ServerLineKind.NotEnoughMemory)]
    [InlineData("[12:00:02] [Server thread/ERROR]: Failed to start the minecraft server", ServerLineKind.Crash)]
    [InlineData("net.minecraft.util.DirectoryLock$LockException: ./world/session.lock: already locked (possibly by other Minecraft instance?)", ServerLineKind.WorldInUse)]
    [InlineData("[12:30:00] [Server thread/ERROR]: This crash report has been saved to: /srv/crash-reports/crash-2026-10-03_12.30.00-server.txt", ServerLineKind.Crash)]
    [InlineData("Error: Unable to access jarfile server.jar", ServerLineKind.Crash)]
    [InlineData("[12:40:00] [Server thread/INFO]: Stopping the server", ServerLineKind.Stopping)]
    [InlineData("[12:40:00] [Server thread/INFO]: Stopping server", ServerLineKind.Stopping)]
    [InlineData("[12:40:00] [Server thread/INFO]: Saving chunks for level 'ServerLevel[world]'/minecraft:overworld", ServerLineKind.Plain)]
    public void StartupFailuresAndShutdown_AreNamed(string line, ServerLineKind kind)
        => Assert.Equal(kind, ServerOutput.Parse(line).Kind);

    [Fact]
    public void ErrorStream_IsMarked()
        => Assert.True(ServerOutput.Parse("Exception in thread \"main\" java.lang.RuntimeException", isError: true).IsError);
}

public class ServerInstallerTests
{
    private static readonly byte[] ServerJar = Encoding.ASCII.GetBytes("this stands in for fifty megabytes of server");
    private static readonly byte[] FabricJar = Encoding.ASCII.GetBytes("fabric server launcher");

    private const string FabricLauncherUrl =
        "https://meta.fabricmc.net/v2/versions/loader/1.21.1/0.19.5/1.1.2/server/jar";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses;

        public StubHandler(Dictionary<string, byte[]> responses) => _responses = responses;

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            lock (Requests)
            {
                Requests.Add(url);
            }

            return Task.FromResult(_responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (ServerInstaller Installer, HostedServerStore Store, StubHandler Handler) NewInstaller(string? sha1 = null)
    {
        sha1 ??= Convert.ToHexString(SHA1.HashData(ServerJar)).ToLowerInvariant();

        var responses = new Dictionary<string, byte[]>
        {
            [MetadataClient.VersionManifestUrl] = Json(
                "{\"latest\":{\"release\":\"1.21.1\",\"snapshot\":\"1.21.1\"},\"versions\":[" +
                "{\"id\":\"1.21.1\",\"type\":\"release\",\"url\":\"https://meta.test/1.21.1.json\"}," +
                "{\"id\":\"1.0\",\"type\":\"release\",\"url\":\"https://meta.test/1.0.json\"}]}"),
            ["https://meta.test/1.21.1.json"] = Json(
                "{\"id\":\"1.21.1\",\"javaVersion\":{\"component\":\"java-runtime-delta\",\"majorVersion\":21}," +
                "\"downloads\":{\"server\":{\"sha1\":\"" + sha1 + "\",\"size\":" + ServerJar.Length +
                ",\"url\":\"https://data.test/server.jar\"}}}"),
            ["https://meta.test/1.0.json"] = Json("{\"id\":\"1.0\",\"downloads\":{\"client\":{\"url\":\"https://data.test/client.jar\"}}}"),
            ["https://data.test/server.jar"] = ServerJar,
            ["https://meta.fabricmc.net/v2/versions/installer"] = Json(
                "[{\"version\":\"1.1.3\",\"stable\":false},{\"version\":\"1.1.2\",\"stable\":true}]"),
            ["https://meta.fabricmc.net/v2/versions/loader/1.21.1"] = Json(
                "[{\"loader\":{\"version\":\"0.20.0-beta\",\"stable\":false}},{\"loader\":{\"version\":\"0.19.5\",\"stable\":true}}]"),
            [FabricLauncherUrl] = FabricJar
        };

        var handler = new StubHandler(responses);
        var http = new HttpClient(handler);
        var paths = new LauncherPaths(HostingTemp.Directory());
        var downloader = new DownloadClient(http, maxAttempts: 1);
        var store = new HostedServerStore(paths);

        var java = new JavaManager(paths, downloader, http);

        var installer = new ServerInstaller(
            http,
            new MetadataClient(http, paths),
            downloader,
            new LoaderService(http, paths, downloader, java),
            store,
            new ServerJava(java, http));

        return (installer, store, handler);
    }

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Plan_QuiltIsUnsupported_WithoutAskingTheNetwork()
    {
        var (installer, _, handler) = NewInstaller();

        var plan = await installer.PlanAsync("1.21.1", LoaderKind.Quilt);

        Assert.Equal(ServerInstallStatus.UnsupportedLoader, plan.Status);
        Assert.False(plan.CanInstall);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Plan_Vanilla_SaysWhatFromWhereAndHowBig_AndDownloadsNothing()
    {
        var (installer, store, handler) = NewInstaller();

        var plan = await installer.PlanAsync("1.21.1", LoaderKind.Vanilla);

        Assert.Equal(ServerInstallStatus.Ready, plan.Status);
        Assert.Equal(21, plan.JavaMajor);

        var download = Assert.Single(plan.Downloads);
        Assert.Equal(ServerDownloadKind.ServerJar, download.Kind);
        Assert.Equal("data.test", download.Host);
        Assert.Equal(ServerJar.Length, download.SizeBytes);
        Assert.True(plan.SizeIsExact);
        Assert.Equal(ServerJar.Length, plan.TotalBytes);

        Assert.DoesNotContain("https://data.test/server.jar", handler.Requests);
        Assert.False(Directory.Exists(store.Root));
    }

    [Fact]
    public async Task Plan_NamesWhatIsMissing()
    {
        var (installer, _, _) = NewInstaller();

        Assert.Equal(ServerInstallStatus.UnknownGameVersion, (await installer.PlanAsync("9.99", LoaderKind.Vanilla)).Status);
        Assert.Equal(ServerInstallStatus.NoServerForVersion, (await installer.PlanAsync("1.0", LoaderKind.Vanilla)).Status);
    }

    [Fact]
    public async Task Plan_Fabric_PicksStableBuilds_AndListsWhatFabricFetchesItself()
    {
        var (installer, _, handler) = NewInstaller();

        var plan = await installer.PlanAsync("1.21.1", LoaderKind.Fabric);

        Assert.Equal(ServerInstallStatus.Ready, plan.Status);
        Assert.Equal("0.19.5", plan.LoaderVersion);
        Assert.Equal("1.1.2", plan.InstallerVersion);
        Assert.False(plan.SizeIsExact);

        Assert.Equal(
            new[] { ServerDownloadKind.ServerJar, ServerDownloadKind.LoaderLauncher, ServerDownloadKind.LoaderLibraries },
            plan.Downloads.Select(d => d.Kind));

        Assert.Equal(FabricLauncherUrl, plan.Downloads[1].Url);
        Assert.Equal("meta.fabricmc.net", plan.Downloads[1].Host);
        Assert.True(plan.Downloads[2].AtFirstStart);
        Assert.Equal("maven.fabricmc.net", plan.Downloads[2].Host);
        Assert.DoesNotContain(FabricLauncherUrl, handler.Requests);
    }

    [Fact]
    public async Task Install_Vanilla_DownloadsVerifiesAndRecordsHowToStart()
    {
        var (installer, store, _) = NewInstaller();
        var server = store.Create("Home", "1.21.1");
        var plan = await installer.PlanAsync(server);

        Assert.False(installer.IsInstalled(server));

        var result = await installer.InstallAsync(server, plan);

        Assert.True(result.Succeeded);
        Assert.Equal(ServerJar, File.ReadAllBytes(Path.Combine(store.ServerDirectory(server), "server.jar")));

        var saved = store.Get(server.Id)!;
        Assert.Equal("server.jar", saved.LaunchJar);
        Assert.Equal(21, saved.JavaMajor);
        Assert.True(installer.IsInstalled(saved));

        // Installing downloads the server and nothing else: no agreement, no settings.
        Assert.False(File.Exists(Path.Combine(store.ServerDirectory(server), ServerEula.FileName)));
        Assert.False(saved.EulaAccepted);
    }

    [Fact]
    public async Task Install_Fabric_KeepsBothJars_AndStartsTheLauncher()
    {
        var (installer, store, _) = NewInstaller();
        var server = store.Create("Modded", "1.21.1", LoaderKind.Fabric);

        var result = await installer.InstallAsync(server, await installer.PlanAsync(server));

        Assert.True(result.Succeeded);

        var dir = store.ServerDirectory(server);
        var saved = store.Get(server.Id)!;

        Assert.Equal("fabric-server-mc.1.21.1-loader.0.19.5-launcher.1.1.2.jar", saved.LaunchJar);
        Assert.Equal("0.19.5", saved.LoaderVersion);
        Assert.Equal(FabricJar, File.ReadAllBytes(Path.Combine(dir, saved.LaunchJar!)));
        Assert.Equal(ServerJar, File.ReadAllBytes(Path.Combine(dir, "server.jar")));
    }

    [Fact]
    public async Task Install_RefusesAJarThatDoesNotMatchItsChecksum()
    {
        var (installer, store, _) = NewInstaller(sha1: new string('0', 40));
        var server = store.Create("Home", "1.21.1");

        var result = await installer.InstallAsync(server, await installer.PlanAsync(server));

        Assert.Equal(ServerInstallOutcome.DownloadFailed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(store.ServerDirectory(server), "server.jar")));
        Assert.Null(store.Get(server.Id)!.LaunchJar);
    }

    [Fact]
    public async Task Install_RefusesAPlanMadeForSomethingElse()
    {
        var (installer, store, handler) = NewInstaller();
        var server = store.Create("Home", "1.21.1");

        var unsupported = await installer.PlanAsync("1.21.1", LoaderKind.Quilt);
        var fabric = await installer.PlanAsync("1.21.1", LoaderKind.Fabric);

        Assert.Equal(ServerInstallOutcome.PlanNotReady, (await installer.InstallAsync(server, unsupported)).Outcome);
        Assert.Equal(ServerInstallOutcome.PlanNotReady, (await installer.InstallAsync(server, fabric)).Outcome);
        Assert.DoesNotContain("https://data.test/server.jar", handler.Requests);
    }
}

public class ServerRunnerTests
{
    [Fact]
    public void Check_NamesWhatStandsInTheWay_BeforeAnythingIsStarted()
    {
        var store = new HostedServerStore(new LauncherPaths(HostingTemp.Directory()));
        var runner = new ServerRunner(store);
        var server = store.Create("Home", "1.21.1");

        Assert.Equal(ServerStartStatus.NotInstalled, runner.Check(server));

        server.LaunchJar = "server.jar";
        File.WriteAllText(Path.Combine(store.ServerDirectory(server), "server.jar"), "jar");
        Assert.Equal(ServerStartStatus.EulaNotAccepted, runner.Check(server));
        Assert.Equal(ServerStartStatus.EulaNotAccepted, runner.Start(server, "java").Status);

        store.AcceptEula(server, playerAccepted: true);

        // On the loopback address: a listener on every address would make Windows ask
        // whether the test host may accept connections.
        using (var taken = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            taken.Start();
            server.Port = ((IPEndPoint)taken.LocalEndpoint).Port;

            Assert.False(ServerRunner.IsPortFree(server.Port));
            Assert.Equal(ServerStartStatus.PortInUse, runner.Check(server));

            taken.Stop();
        }

        Assert.Equal(ServerStartStatus.Started, runner.Check(server));
        Assert.Equal(ServerStartStatus.JavaNotFound, runner.Start(server, Path.Combine(store.Root, "no-java-here.exe")).Status);
        Assert.Empty(runner.Running);
    }
}
