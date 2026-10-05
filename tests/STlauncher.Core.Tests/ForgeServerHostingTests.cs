using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Hosting;
using STlauncher.Core.Http;
using STlauncher.Core.Java;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

public class ForgeServerTests
{
    [Fact]
    public void Addresses_FollowEachMavensLayout()
    {
        // Builds remember Forge's version without the game's in front; the maven needs both.
        Assert.Equal("1.21.1-52.1.16", ForgeServer.MavenVersion(LoaderKind.Forge, "1.21.1", "52.1.16"));
        Assert.Equal("1.21.1-52.1.16", ForgeServer.MavenVersion(LoaderKind.Forge, "1.21.1", "1.21.1-52.1.16"));
        Assert.Equal("21.1.255", ForgeServer.MavenVersion(LoaderKind.NeoForge, "1.21.1", "21.1.255"));

        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.16/forge-1.21.1-52.1.16-installer.jar",
            ForgeServer.InstallerUrl(LoaderKind.Forge, "1.21.1-52.1.16"));
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.255/neoforge-21.1.255-installer.jar",
            ForgeServer.InstallerUrl(LoaderKind.NeoForge, "21.1.255"));

        // NeoForge for 1.20.1 still went by Forge's name and numbering.
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar",
            ForgeServer.InstallerUrl(LoaderKind.NeoForge, "1.20.1-47.1.106"));

        Assert.Equal("libraries/net/minecraftforge/forge/1.21.1-52.1.16", ForgeServer.ArgsDirectory(LoaderKind.Forge, "1.21.1-52.1.16"));
        Assert.Equal("libraries/net/neoforged/neoforge/21.1.255", ForgeServer.ArgsDirectory(LoaderKind.NeoForge, "21.1.255"));
    }

    [Fact]
    public void LaunchTarget_IsTheArgsFileForTheSystem_OrTheJarOfTheOldLayout()
    {
        var modern = HostingTemp.Directory();
        var args = Path.Combine(modern, "libraries", "net", "minecraftforge", "forge", "1.21.1-52.1.16");
        Directory.CreateDirectory(args);
        File.WriteAllText(Path.Combine(args, "win_args.txt"), "-p x");
        File.WriteAllText(Path.Combine(args, "unix_args.txt"), "-p x");

        // The shim jar beside them is not what Forge's own run.bat uses.
        File.WriteAllText(Path.Combine(modern, "forge-1.21.1-52.1.16-shim.jar"), "jar");

        Assert.Equal(
            "libraries/net/minecraftforge/forge/1.21.1-52.1.16/win_args.txt",
            ForgeServer.FindLaunchTarget(modern, LoaderKind.Forge, "1.21.1-52.1.16", windows: true));
        Assert.Equal(
            "libraries/net/minecraftforge/forge/1.21.1-52.1.16/unix_args.txt",
            ForgeServer.FindLaunchTarget(modern, LoaderKind.Forge, "1.21.1-52.1.16", windows: false));

        var legacy = HostingTemp.Directory();
        File.WriteAllText(Path.Combine(legacy, "forge-1.16.5-36.2.39.jar"), "jar");
        Assert.Equal("forge-1.16.5-36.2.39.jar", ForgeServer.FindLaunchTarget(legacy, LoaderKind.Forge, "1.16.5-36.2.39"));

        var older = HostingTemp.Directory();
        File.WriteAllText(Path.Combine(older, "forge-1.12.2-14.23.5.2860.jar"), "jar");
        Assert.Equal("forge-1.12.2-14.23.5.2860.jar", ForgeServer.FindLaunchTarget(older, LoaderKind.Forge, "1.12.2-14.23.5.2860"));

        // A folder the installer left nothing in has nothing to start.
        Assert.Null(ForgeServer.FindLaunchTarget(HostingTemp.Directory(), LoaderKind.Forge, "1.21.1-52.1.16"));
    }

    [Fact]
    public void HeapLimit_CountsOnlyWhenTheOwnerUncommentedIt()
    {
        var dir = HostingTemp.Directory();
        var path = Path.Combine(dir, ForgeServer.UserJvmArgsFile);

        // As the installer writes it: comments only, "-Xmx4G" among them.
        File.WriteAllText(path, "# Uncomment the next line to set it.\n# -Xmx4G\n");
        Assert.False(ForgeServer.SetsHeapLimit(path));

        File.WriteAllText(path, "# comment\n-Xms1G -Xmx6G\n");
        Assert.True(ForgeServer.SetsHeapLimit(path));

        Assert.False(ForgeServer.SetsHeapLimit(Path.Combine(dir, "missing.txt")));
    }
}

public class ForgeServerCommandLineTests
{
    private const string ArgsDir = "libraries/net/minecraftforge/forge/1.21.1-52.1.16";

    /// <summary>Exactly what the installer of Forge 1.21.1-52.1.16 writes, as seen on a real install.</summary>
    private const string InstallersUserJvmArgs =
        "# Note: Not all server panels support this file. You may need to set these options in the panel itself.\n\n" +
        "# Xmx and Xms set the maximum and minimum RAM usage, respectively.\n" +
        "# They can take any number, followed by an M (for megabyte) or a G (for gigabyte).\n" +
        "# For example, to set the maximum to 3GB: -Xmx3G\n# To set the minimum to 2.5GB: -Xms2500M\n\n" +
        "# A good default for a modded server is 4GB. Do not allocate excessive amounts of RAM as too much may cause lag or crashes.\n" +
        "# Uncomment the next line to set it. To uncomment, remove the # at the beginning of the line.\n# -Xmx4G\n";

    private static (HostedServer Server, string Directory) Modern(string userJvmArgs = InstallersUserJvmArgs)
    {
        var dir = HostingTemp.Directory();
        File.WriteAllText(Path.Combine(dir, ForgeServer.UserJvmArgsFile), userJvmArgs);

        var server = new HostedServer
        {
            Id = "forge",
            GameVersion = "1.21.1",
            Loader = LoaderKind.Forge,
            LoaderVersion = "52.1.16",
            MemoryMb = 4096,
            LaunchJar = ArgsDir + "/win_args.txt"
        };

        return (server, dir);
    }

    [Fact]
    public void Modern_OnWindows_IsUserArgsThenWinArgs_WithTheLaunchersMemory()
    {
        var (server, dir) = Modern();

        var command = ServerCommandLine.Build(server, dir, @"C:\java\bin\java.exe", windows: true);

        Assert.Equal(dir, command.WorkingDirectory);
        Assert.Equal("@user_jvm_args.txt", command.Arguments[0]);
        Assert.Equal(new[] { "@" + ArgsDir + "/win_args.txt", "nogui" }, command.Arguments.TakeLast(2));
        Assert.Equal(new[] { "-Xmx4096M" }, command.Arguments.Where(a => a.StartsWith("-Xmx", StringComparison.Ordinal)));
        Assert.DoesNotContain("-jar", command.Arguments);

        // JVM options all come before the argument file that names the main class.
        var main = command.Arguments.ToList().IndexOf("@" + ArgsDir + "/win_args.txt");
        Assert.All(command.Arguments.Take(main), a => Assert.True(a.StartsWith('-') || a.StartsWith('@')));
    }

    [Fact]
    public void Modern_OnLinuxAndMac_TakesUnixArgs_EvenWhenInstalledOnWindows()
    {
        var (server, dir) = Modern();

        var command = ServerCommandLine.Build(server, dir, "/usr/bin/java", windows: false);

        Assert.Equal(new[] { "@" + ArgsDir + "/unix_args.txt", "nogui" }, command.Arguments.TakeLast(2));
    }

    [Fact]
    public void Modern_AHeapLimitTheOwnerWroteIsNotDoubled()
    {
        var (server, dir) = Modern("-Xmx6G\n");

        var command = ServerCommandLine.Build(server, dir, "java", windows: true);

        Assert.Equal("@user_jvm_args.txt", command.Arguments[0]);
        Assert.DoesNotContain(command.Arguments, a => a.StartsWith("-Xmx", StringComparison.Ordinal));
    }

    [Fact]
    public void Modern_WithoutUserArgsFile_StillStarts()
    {
        var (server, dir) = Modern();
        File.Delete(Path.Combine(dir, ForgeServer.UserJvmArgsFile));

        var command = ServerCommandLine.Build(server, dir, "java", windows: true);

        Assert.Equal("-Xmx4096M", command.Arguments[0]);
        Assert.DoesNotContain("@user_jvm_args.txt", command.Arguments);
    }

    [Fact]
    public void Legacy_IsJavaXmxJarNogui()
    {
        var server = new HostedServer
        {
            Id = "old",
            GameVersion = "1.16.5",
            Loader = LoaderKind.Forge,
            MemoryMb = 3072,
            LaunchJar = "forge-1.16.5-36.2.39.jar"
        };

        var command = ServerCommandLine.Build(server, HostingTemp.Directory(), "java", windows: true);

        Assert.Equal("-Xmx3072M", command.Arguments[0]);
        Assert.Equal(new[] { "-jar", "forge-1.16.5-36.2.39.jar", "nogui" }, command.Arguments.TakeLast(3));
        Assert.DoesNotContain(command.Arguments, a => a.StartsWith('@'));
    }
}

public class ForgeServerOutputTests
{
    // Lines as Forge 1.21.1-52.1.16 printed them on a real start in the launcher's pipe.
    [Theory]
    [InlineData("[14:29:47] [main/INFO] [minecraft/Main]: You need to agree to the EULA in order to run the server. Go to eula.txt for more info.", ServerLineKind.EulaRequired)]
    [InlineData("[14:29:47] [main/WARN] [minecraft/Eula]: Failed to load eula.txt", ServerLineKind.Plain)]
    [InlineData("[14:29:46] [main/INFO] [cp.mo.mo.Launcher/MODLAUNCHER]: ModLauncher running: args [--launchTarget, forge_server, nogui]", ServerLineKind.Plain)]
    [InlineData("[14:29:47] [main/INFO] [cp.mo.mo.LaunchServiceHandler/MODLAUNCHER]: Launching target 'forge_server' with arguments [nogui]", ServerLineKind.Plain)]
    [InlineData("WARN StatusConsoleListener Advanced terminal features are not available in this environment", ServerLineKind.Plain)]
    // And NeoForge 21.1.255: milliseconds in the time, a space at the end of the early lines.
    [InlineData("[14:33:14.704] [main/INFO] [Launcher/MODLAUNCHER]: ModLauncher running: args [--launchTarget, forgeserver, nogui] ", ServerLineKind.Plain)]
    [InlineData("[14:33:16] [main/INFO] [minecraft/Main]: You need to agree to the EULA in order to run the server. Go to eula.txt for more info.", ServerLineKind.EulaRequired)]
    [InlineData("\t\tNeoForge 21.1.255 (neoforge) ", ServerLineKind.Plain)]
    public void RealForgeStartLines_AreRead(string line, ServerLineKind kind)
        => Assert.Equal(kind, ServerOutput.Parse(line).Kind);

    // The game's own messages in the same frame: Forge adds one bracket for the logger.
    // Not seen live (the live run stopped at the EULA); the frame is the one above.
    [Theory]
    [InlineData("[14:31:02] [Server thread/INFO] [minecraft/DedicatedServer]: Done (9.874s)! For help, type \"help\"", ServerLineKind.Ready, null)]
    [InlineData("[14:32:10] [Server thread/INFO] [minecraft/MinecraftServer]: Steve joined the game", ServerLineKind.PlayerJoined, "Steve")]
    [InlineData("[14:32:10.123] [Server thread/INFO] [minecraft/MinecraftServer]: Alex joined the game ", ServerLineKind.PlayerJoined, "Alex")]
    [InlineData("[14:40:00] [Server thread/INFO] [minecraft/MinecraftServer]: Steve left the game", ServerLineKind.PlayerLeft, "Steve")]
    [InlineData("[14:41:00] [Server thread/INFO] [minecraft/MinecraftServer]: Stopping server", ServerLineKind.Stopping, null)]
    [InlineData("[14:32:30] [Server thread/INFO] [minecraft/MinecraftServer]: <Steve> Alex joined the game", ServerLineKind.Plain, null)]
    [InlineData("[14:32:10] [Server thread/INFO] [minecraft/PlayerList]: Steve[/127.0.0.1:50000] logged in with entity id 1 at (0.5, 64.0, 0.5)", ServerLineKind.Plain, null)]
    public void GameMessages_InForgesFrame_AreRecognised(string line, ServerLineKind kind, string? player)
    {
        var parsed = ServerOutput.Parse(line);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(player, parsed.Player);
    }
}

public class ForgeModSideTests
{
    private static string Toml(string top, params (string ModId, string Side)[] dependencies)
    {
        var text = new StringBuilder("modLoader=\"javafml\"\nloaderVersion=\"[52,)\"\nlicense=\"MIT\"\n" + top + "\n");
        text.Append("[[mods]]\nmodId=\"thing\"\ndisplayName=\"Thing\"\n");

        foreach (var (modId, side) in dependencies)
        {
            text.Append("[[dependencies.thing]]\n")
                .Append("    modId=\"").Append(modId).Append("\"\n")
                .Append("    mandatory=true\n    versionRange=\"[1,)\"\n    ordering=\"NONE\"\n")
                .Append("    side=\"").Append(side).Append("\"\n");
        }

        return text.ToString();
    }

    [Fact]
    public void ClientSideOnly_AtTheTop_IsAStatement()
    {
        var dir = HostingTemp.Directory();

        Assert.Equal(ModClientSide.Stated, ServerContent.ClientSide(
            HostingTemp.Jar(dir, "a.jar", ("META-INF/mods.toml", Toml("clientSideOnly=true", ("forge", "BOTH"))))));
        Assert.Equal(ModClientSide.Stated, ServerContent.ClientSide(
            HostingTemp.Jar(dir, "b.jar", ("META-INF/neoforge.mods.toml", Toml("clientSideOnly = true")))));

        // Inside a table it is some other key, not the one the loader reads.
        Assert.Equal(ModClientSide.NotStated, ServerContent.ClientSide(
            HostingTemp.Jar(dir, "c.jar", ("META-INF/mods.toml", Toml("", ("forge", "BOTH")) + "[[mods]]\nmodId=\"x\"\nclientSideOnly=true\n"))));
        Assert.Equal(ModClientSide.NotStated, ServerContent.ClientSide(
            HostingTemp.Jar(dir, "d.jar", ("META-INF/mods.toml", Toml("clientSideOnly=false")))));
    }

    [Fact]
    public void GameNeededOnlyOnTheClient_IsImplied_AndAnythingLessIsNothing()
    {
        var dir = HostingTemp.Directory();

        Assert.Equal(ModClientSide.Implied, ServerContent.ClientSide(HostingTemp.Jar(dir, "a.jar",
            ("META-INF/mods.toml", Toml("", ("forge", "CLIENT"), ("minecraft", "CLIENT"), ("jei", "BOTH"))))));
        Assert.Equal(ModClientSide.Implied, ServerContent.ClientSide(HostingTemp.Jar(dir, "b.jar",
            ("META-INF/neoforge.mods.toml", Toml("", ("neoforge", "CLIENT"))))));

        // One of them needed on both sides: the mod says nothing about being client-only.
        Assert.Equal(ModClientSide.NotStated, ServerContent.ClientSide(HostingTemp.Jar(dir, "c.jar",
            ("META-INF/mods.toml", Toml("", ("forge", "CLIENT"), ("minecraft", "BOTH"))))));

        // Only another mod wanted on the client: says nothing about this one.
        Assert.Equal(ModClientSide.NotStated, ServerContent.ClientSide(HostingTemp.Jar(dir, "d.jar",
            ("META-INF/mods.toml", Toml("", ("forge", "BOTH"), ("jei", "CLIENT"))))));

        // What most mods are: no side anywhere, or none at all.
        Assert.Equal(ModClientSide.NotStated, ServerContent.ClientSide(HostingTemp.Jar(dir, "e.jar",
            ("META-INF/mods.toml", Toml("")))));
        Assert.Equal(ModClientSide.NotStated, ServerContent.ClientSide(HostingTemp.Jar(dir, "f.jar",
            ("META-INF/mods.toml", Toml("", ("forge", "BOTH"), ("minecraft", "BOTH"))))));

        // displayTest is not a side: it is set the same way by client-only and server-only mods.
        Assert.Equal(ModClientSide.NotStated, ServerContent.ClientSide(HostingTemp.Jar(dir, "g.jar",
            ("META-INF/mods.toml", Toml("") + "displayTest=\"IGNORE_SERVER_VERSION\"\n"))));
    }

    [Fact]
    public void AJarForSeveralLoaders_IsClientOnlyOnlyWhenTheyAllSaySo()
    {
        var dir = HostingTemp.Directory();
        const string fabricBoth = "{\"schemaVersion\":1,\"id\":\"m\",\"environment\":\"*\"}";
        const string fabricClient = "{\"schemaVersion\":1,\"id\":\"m\",\"environment\":\"client\"}";

        Assert.Equal(ModClientSide.NotStated, ServerContent.ClientSide(HostingTemp.Jar(dir, "a.jar",
            ("fabric.mod.json", fabricBoth), ("META-INF/mods.toml", Toml("clientSideOnly=true")))));
        Assert.Equal(ModClientSide.Stated, ServerContent.ClientSide(HostingTemp.Jar(dir, "b.jar",
            ("fabric.mod.json", fabricClient), ("META-INF/mods.toml", Toml("clientSideOnly=true")))));
    }

    [Fact]
    public void CopyMods_SaysWhichWereLeftOutOnTheirWord_AndWhichByTheirDependencies()
    {
        var root = HostingTemp.Directory();
        var instance = Path.Combine(root, "instance");
        var server = Path.Combine(root, "server");
        var mods = Path.Combine(instance, "mods");

        HostingTemp.Jar(mods, "jei.jar", ("META-INF/mods.toml", Toml("", ("forge", "BOTH"), ("minecraft", "BOTH"))));
        HostingTemp.Jar(mods, "plain.jar", ("META-INF/mods.toml", Toml("")));
        HostingTemp.Jar(mods, "zoom.jar", ("META-INF/mods.toml", Toml("clientSideOnly=true")));
        HostingTemp.Jar(mods, "shaders.jar", ("META-INF/mods.toml", Toml("", ("forge", "CLIENT"), ("minecraft", "CLIENT"))));

        var result = ServerContent.CopyMods(instance, server);

        Assert.Equal(new[] { "jei.jar", "plain.jar" }, result.Copied.Select(c => c.FileName));
        Assert.Contains(result.Skipped, s => s.FileName == "zoom.jar" && s.Reason == ModSkipReason.ClientOnly);
        Assert.Contains(result.Skipped, s => s.FileName == "shaders.jar" && s.Reason == ModSkipReason.LikelyClientOnly);
        Assert.False(File.Exists(Path.Combine(server, "mods", "shaders.jar")));
    }
}

public class ForgeServerInstallerTests
{
    private const string Version = "1.21.1-52.1.16";
    private const string InstallerUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/" + Version + "/forge-" + Version + "-installer.jar";

    private static readonly byte[] InstallerJar = Encoding.ASCII.GetBytes("this stands in for the forge installer");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses;

        public StubHandler(Dictionary<string, byte[]> responses) => _responses = responses;

        public List<(HttpMethod Method, string Url)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            lock (Requests)
            {
                Requests.Add((request.Method, url));
            }

            return Task.FromResult(_responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed record Setup(
        HostedServerStore Store,
        StubHandler Handler,
        HttpClient Http,
        MetadataClient Metadata,
        DownloadClient Downloader,
        LoaderService Loaders,
        ServerJava Java);

    private static Setup NewSetup(string? installerSha1 = null, bool publishSha1 = true)
    {
        var responses = new Dictionary<string, byte[]>
        {
            [MetadataClient.VersionManifestUrl] = Bytes(
                "{\"latest\":{\"release\":\"1.21.1\",\"snapshot\":\"1.21.1\"},\"versions\":[" +
                "{\"id\":\"1.21.1\",\"type\":\"release\",\"url\":\"https://meta.test/1.21.1.json\"}]}"),
            ["https://meta.test/1.21.1.json"] = Bytes(
                "{\"id\":\"1.21.1\",\"javaVersion\":{\"component\":\"java-runtime-delta\",\"majorVersion\":21}," +
                "\"downloads\":{\"server\":{\"sha1\":\"" + new string('a', 40) + "\",\"size\":51627615," +
                "\"url\":\"https://piston-data.mojang.com/v1/objects/x/server.jar\"}}}"),
            ["https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"] = Bytes(
                "<metadata><versioning><versions><version>" + Version + "</version><version>1.20.1-47.3.0</version>" +
                "</versions></versioning></metadata>"),
            [InstallerUrl] = InstallerJar
        };

        if (publishSha1)
        {
            responses[InstallerUrl + ".sha1"] = Bytes(installerSha1 ?? Convert.ToHexString(SHA1.HashData(InstallerJar)).ToLowerInvariant());
        }

        var handler = new StubHandler(responses);
        var http = new HttpClient(handler);
        var paths = new LauncherPaths(HostingTemp.Directory());
        var downloader = new DownloadClient(http, maxAttempts: 1);
        var java = new JavaManager(paths, downloader, http);

        return new Setup(
            new HostedServerStore(paths),
            handler,
            http,
            new MetadataClient(http, paths),
            downloader,
            new LoaderService(http, paths, downloader, java),
            new ServerJava(java, http));
    }

    private static ServerInstaller Installer(
        Setup setup,
        Func<LoaderInstallerCommand, Action<string>, CancellationToken, Task<LoaderInstallerExit>>? run = null)
        => new(setup.Http, setup.Metadata, setup.Downloader, setup.Loaders, setup.Store, setup.Java)
        {
            ResolveJava = (_, _) => Task.FromResult(@"C:\java\bin\java.exe"),
            RunInstaller = run ?? ((_, _, _) => throw new InvalidOperationException("The installer was not to be run."))
        };

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>What a real Forge 1.21.1 installer leaves behind, minus the libraries' contents.</summary>
    private static Task<LoaderInstallerExit> FakeForge(LoaderInstallerCommand command, Action<string> onLine)
    {
        var args = Path.Combine(command.WorkingDirectory, "libraries", "net", "minecraftforge", "forge", Version);
        Directory.CreateDirectory(args);
        File.WriteAllText(Path.Combine(args, "win_args.txt"), "-p libraries/x.jar");
        File.WriteAllText(Path.Combine(args, "unix_args.txt"), "-p libraries/x.jar");
        File.WriteAllText(Path.Combine(command.WorkingDirectory, "user_jvm_args.txt"), "# -Xmx4G\n");
        File.WriteAllText(Path.Combine(command.WorkingDirectory, "run.bat"), "java @user_jvm_args.txt ...");
        File.WriteAllText(Path.Combine(command.WorkingDirectory, $"forge-{Version}-shim.jar"), "jar");

        onLine("Extracting main jar:");
        onLine("The server installed successfully");
        return Task.FromResult(new LoaderInstallerExit(LoaderInstallerEnd.Exited, 0));
    }

    [Fact]
    public async Task Plan_SaysWhatTheLauncherFetches_AndWhatTheInstallerFetchesItself_WithoutDownloadingEither()
    {
        var setup = NewSetup();
        var installer = Installer(setup);

        var plan = await installer.PlanAsync("1.21.1", LoaderKind.Forge);

        Assert.Equal(ServerInstallStatus.Ready, plan.Status);
        Assert.Equal("1.21.1-52.1.16", ForgeServer.MavenVersion(plan.Loader, plan.GameVersion, plan.LoaderVersion!));
        Assert.Equal(21, plan.JavaMajor);
        Assert.True(plan.RunsInstaller);
        Assert.False(plan.SizeIsExact);

        Assert.Equal(
            new[] { ServerDownloadKind.LoaderInstaller, ServerDownloadKind.ServerJar, ServerDownloadKind.LoaderLibraries },
            plan.Downloads.Select(d => d.Kind));

        var jar = plan.Downloads[0];
        Assert.Equal(InstallerUrl, jar.Url);
        Assert.Equal("maven.minecraftforge.net", jar.Host);
        Assert.Equal(InstallerJar.Length, jar.SizeBytes);
        Assert.True(jar.SizeIsExact);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(InstallerJar)).ToLowerInvariant(), jar.Sha1);
        Assert.False(jar.ByInstaller);

        // Mojang's server, exact, and Forge's libraries, estimated: both fetched by the installer.
        Assert.True(plan.Downloads[1].ByInstaller);
        Assert.Equal("piston-data.mojang.com", plan.Downloads[1].Host);
        Assert.Equal(51627615, plan.Downloads[1].SizeBytes);
        Assert.True(plan.Downloads[2].ByInstaller);
        Assert.False(plan.Downloads[2].SizeIsExact);
        Assert.Equal("maven.minecraftforge.net", plan.Downloads[2].Host);
        Assert.Equal(plan.Downloads[1].SizeBytes + plan.Downloads[2].SizeBytes, plan.InstallerFetchesBytes);

        // Asked about, never fetched.
        Assert.Contains((HttpMethod.Head, InstallerUrl), setup.Handler.Requests);
        Assert.DoesNotContain((HttpMethod.Get, InstallerUrl), setup.Handler.Requests);
        Assert.False(Directory.Exists(setup.Store.Root));
    }

    [Fact]
    public async Task Plan_ABuildOfForgeTheMavenDoesNotHave_IsUnavailable()
    {
        var installer = Installer(NewSetup());

        var plan = await installer.PlanAsync("1.21.1", LoaderKind.Forge, "52.0.1");

        Assert.Equal(ServerInstallStatus.LoaderUnavailable, plan.Status);
    }

    [Fact]
    public async Task Plan_WithoutAChecksumOnTheMaven_StillPlans_Unverified()
    {
        var plan = await Installer(NewSetup(publishSha1: false)).PlanAsync("1.21.1", LoaderKind.Forge, "52.1.16");

        Assert.Equal(ServerInstallStatus.Ready, plan.Status);
        Assert.Null(plan.Downloads[0].Sha1);
    }

    [Fact]
    public async Task Install_RunsTheInstallerInTheServersFolder_AndRecordsTheArgsFile()
    {
        var setup = NewSetup();
        LoaderInstallerCommand? ran = null;
        var lines = new List<string>();

        var installer = Installer(setup, (command, onLine, _) =>
        {
            ran = command;
            return FakeForge(command, onLine);
        });

        var server = setup.Store.Create("Forge", "1.21.1", LoaderKind.Forge);
        var plan = await installer.PlanAsync(server);
        var result = await installer.InstallAsync(server, plan, new SyncProgress(p =>
        {
            if (p.InstallerLine is { } line)
            {
                lines.Add(line);
            }
        }));

        Assert.True(result.Succeeded, result.Detail);

        var dir = setup.Store.ServerDirectory(server);
        Assert.NotNull(ran);
        Assert.Equal(@"C:\java\bin\java.exe", ran!.JavaPath);
        Assert.Equal(dir, ran.WorkingDirectory);
        Assert.Equal("-jar", ran.Arguments[0]);
        Assert.Equal("--installServer", ran.Arguments[^1]);

        // The installer's output reached the caller line by line.
        Assert.Equal(new[] { "Extracting main jar:", "The server installed successfully" }, lines);

        var saved = setup.Store.Get(server.Id)!;
        var expected = $"libraries/net/minecraftforge/forge/{Version}/" + (OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt");
        Assert.Equal(expected, saved.LaunchJar);
        Assert.Equal("52.1.16", saved.LoaderVersion);
        Assert.Equal(21, saved.JavaMajor);
        Assert.True(installer.IsInstalled(saved));

        // The installer is not needed once it has done its work.
        Assert.False(File.Exists(ran.Arguments[1]));
    }

    [Fact]
    public async Task Install_AFailedInstaller_LeavesTheServerNotInstalled_AndNothingThatStarts()
    {
        var setup = NewSetup();

        var installer = Installer(setup, (command, onLine, _) =>
        {
            // Half-way: the start scripts are written early, the argument files late.
            File.WriteAllText(Path.Combine(command.WorkingDirectory, "run.bat"), "java ...");
            File.WriteAllText(Path.Combine(command.WorkingDirectory, $"forge-{Version}-shim.jar"), "jar");
            onLine("Downloading library from https://maven.minecraftforge.net/x.jar");
            onLine("These libraries failed to download. Try again.");
            onLine("\tat net.minecraftforge.installer.Something.run(Something.java:1)");
            return Task.FromResult(new LoaderInstallerExit(LoaderInstallerEnd.Exited, 1));
        });

        var server = setup.Store.Create("Forge", "1.21.1", LoaderKind.Forge);
        var result = await installer.InstallAsync(server, await installer.PlanAsync(server));

        Assert.Equal(ServerInstallOutcome.InstallerFailed, result.Outcome);
        Assert.Equal("These libraries failed to download. Try again.", result.Detail);
        Assert.NotNull(result.InstallerLog);

        var dir = setup.Store.ServerDirectory(server);
        Assert.Null(setup.Store.Get(server.Id)!.LaunchJar);
        Assert.False(File.Exists(Path.Combine(dir, "run.bat")));
        Assert.False(File.Exists(Path.Combine(dir, $"forge-{Version}-shim.jar")));
    }

    [Fact]
    public async Task Install_AnInstallerThatSaysSuccessButLeavesNothing_IsAFailure()
    {
        var setup = NewSetup();
        var installer = Installer(setup, (_, _, _) => Task.FromResult(new LoaderInstallerExit(LoaderInstallerEnd.Exited, 0)));

        var server = setup.Store.Create("Forge", "1.21.1", LoaderKind.Forge);
        var result = await installer.InstallAsync(server, await installer.PlanAsync(server));

        Assert.Equal(ServerInstallOutcome.InstallerFailed, result.Outcome);
        Assert.Null(setup.Store.Get(server.Id)!.LaunchJar);
    }

    [Fact]
    public async Task Install_AnInstallerThatDoesNotMatchTheMavensChecksum_IsNeverRun()
    {
        var setup = NewSetup(installerSha1: new string('0', 40));
        var installer = Installer(setup);

        var server = setup.Store.Create("Forge", "1.21.1", LoaderKind.Forge);
        var result = await installer.InstallAsync(server, await installer.PlanAsync(server));

        Assert.Equal(ServerInstallOutcome.DownloadFailed, result.Outcome);
        Assert.Null(setup.Store.Get(server.Id)!.LaunchJar);
    }

    [Fact]
    public async Task Install_NoJava_SaysSo_AndRunsNothing()
    {
        var setup = NewSetup();
        var installer = new ServerInstaller(setup.Http, setup.Metadata, setup.Downloader, setup.Loaders, setup.Store, setup.Java)
        {
            ResolveJava = (_, _) => throw new HttpRequestException("adoptium is down"),
            RunInstaller = (_, _, _) => throw new InvalidOperationException("The installer was not to be run.")
        };

        var server = setup.Store.Create("Forge", "1.21.1", LoaderKind.Forge);
        var result = await installer.InstallAsync(server, await installer.PlanAsync(server));

        Assert.Equal(ServerInstallOutcome.JavaUnavailable, result.Outcome);
        Assert.Contains("adoptium", result.Detail);
    }

    private sealed class SyncProgress : IProgress<ServerInstallProgress>
    {
        private readonly Action<ServerInstallProgress> _action;

        public SyncProgress(Action<ServerInstallProgress> action) => _action = action;

        public void Report(ServerInstallProgress value) => _action(value);
    }
}

public class LoaderInstallerProcessTests
{
    [Fact]
    public async Task Run_PassesOutputAndExitCode_AndReportsAJavaThatIsNotThere()
    {
        var missing = await LoaderInstallerProcess.RunAsync(
            new LoaderInstallerCommand(Path.Combine(HostingTemp.Directory(), "no-java.exe"), Array.Empty<string>(), HostingTemp.Directory()));

        Assert.Equal(LoaderInstallerEnd.FailedToStart, missing.End);
        Assert.False(missing.Succeeded);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // cmd stands in for Java: a child that prints and exits with a code.
        var lines = new List<string>();
        var exit = await LoaderInstallerProcess.RunAsync(
            new LoaderInstallerCommand("cmd.exe", new[] { "/c", "echo installing& exit 3" }, HostingTemp.Directory()),
            line => lines.Add(line));

        Assert.Equal(LoaderInstallerEnd.Exited, exit.End);
        Assert.Equal(3, exit.ExitCode);
        Assert.Contains("installing", lines);
    }

    [Fact]
    public async Task Run_AnInstallerThatGoesQuiet_IsEnded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exit = await LoaderInstallerProcess.RunAsync(
            new LoaderInstallerCommand("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, HostingTemp.Directory()),
            silenceTimeout: TimeSpan.FromSeconds(1));

        Assert.Equal(LoaderInstallerEnd.WentSilent, exit.End);
    }
}
