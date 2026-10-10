using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Java;
using STlauncher.Core.Launch;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// What stands between a launch and four quiet failures: Log4Shell on old versions, a
/// file re-downloaded for ever over a wrong size, a Java that is only an executable, and
/// a command line Windows will not start.
/// </summary>
public class LaunchSafetyTests
{
    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // ---------------------------------------------------------------- Log4Shell

    [Fact]
    public void LogLinesInMojangsXmlComeBackAsPlainLines()
    {
        var filter = new Log4jXmlFilter();
        var seen = new List<string>();

        // As the 1.12-1.16.5 configuration prints them (LegacyXMLLayout).
        foreach (var line in new[]
                 {
                     "<log4j:Event logger=\"net.minecraft.client.Minecraft\" timestamp=\"1610000000000\" level=\"INFO\" thread=\"Render thread\">",
                     "  <log4j:Message><![CDATA[Setting user: Steve]]></log4j:Message>",
                     "</log4j:Event>",
                     "a line the loader printed itself",
                     "<log4j:Event logger=\"x\" timestamp=\"1610000001000\" level=\"ERROR\" thread=\"main\">",
                     "  <log4j:Message><![CDATA[Unreported exception thrown!]]></log4j:Message>",
                     "  <log4j:Throwable><![CDATA[java.lang.IllegalStateException: boom",
                     "\tat net.minecraft.Foo.bar(Foo.java:10)",
                     "]]></log4j:Throwable>",
                     "</log4j:Event>"
                 })
        {
            seen.AddRange(filter.Feed(line));
        }

        Assert.Equal(5, seen.Count);
        Assert.EndsWith("[Render thread/INFO]: Setting user: Steve", seen[0]);
        Assert.Matches(@"^\[\d\d:\d\d:\d\d\] \[Render thread/INFO\]: ", seen[0]);
        Assert.Equal("a line the loader printed itself", seen[1]);
        Assert.EndsWith("[main/ERROR]: Unreported exception thrown!", seen[2]);
        Assert.Equal("java.lang.IllegalStateException: boom", seen[3]);
        Assert.Equal("\tat net.minecraft.Foo.bar(Foo.java:10)", seen[4]);
    }

    [Fact]
    public void AnEventOnOneLine_AndAMessageHoldingTheCdataEnd_AreRead()
    {
        var filter = new Log4jXmlFilter();

        var one = filter.Feed("<log4j:Event logger=\"x\" timestamp=\"1\" level=\"WARN\" thread=\"main\"><log4j:Message><![CDATA[a ]]]]><![CDATA[> b]]></log4j:Message></log4j:Event>");

        Assert.Single(one);
        Assert.EndsWith("[main/WARN]: a ]]> b", one[0]);
    }

    [Fact]
    public void EveryLaunchCarriesTheSwitch_AndOldVersionsTheirPatchedConfiguration()
    {
        var version = new ResolvedVersion
        {
            Id = "1.16.5",
            MainClass = "net.minecraft.client.main.Main",
            ReleaseTime = new DateTimeOffset(2021, 1, 14, 0, 0, 0, TimeSpan.Zero),
            Logging = new LoggingConfig { Client = new LoggingClient { Argument = "-Dlog4j.configurationFile=${path}" } }
        };

        var withConfig = Build(version, loggingConfig: @"C:\data\assets\log_configs\client-1.12.xml", userArgs: new[] { "-Dlog4j2.formatMsgNoLookups=false" });
        var arguments = withConfig.Arguments.ToList();

        Assert.Contains(@"-Dlog4j.configurationFile=C:\data\assets\log_configs\client-1.12.xml", arguments);

        // The player's own "off" comes first, so ours is the one Java keeps.
        Assert.True(arguments.IndexOf("-Dlog4j2.formatMsgNoLookups=false") < arguments.IndexOf(LaunchCommandBuilder.Log4ShellGuard));
        Assert.True(arguments.IndexOf(LaunchCommandBuilder.Log4ShellGuard) < arguments.IndexOf("net.minecraft.client.main.Main"));

        var without = Build(version, loggingConfig: null, userArgs: Array.Empty<string>());

        Assert.Contains(LaunchCommandBuilder.Log4ShellGuard, without.Arguments);
        Assert.DoesNotContain(without.Arguments, a => a.StartsWith("-Dlog4j.configurationFile", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2015, 12, 3, true)]    // 1.8.9
    [InlineData(2021, 11, 30, true)]   // 1.18
    [InlineData(2021, 12, 10, false)]  // 1.18.1, the fixed one (08:23 UTC)
    [InlineData(2025, 12, 9, false)]   // 1.21.11
    public void OnlyVersionsFromBeforeTheFixGetThePatchedConfiguration(int year, int month, int day, bool needs)
    {
        var version = new ResolvedVersion { ReleaseTime = new DateTimeOffset(year, month, day, 8, 23, 0, TimeSpan.Zero) };

        Assert.Equal(needs, LaunchService.NeedsPatchedLogging(version));
    }

    [Fact]
    public void AVersionWithNoDateIsTreatedAsOld()
        => Assert.True(LaunchService.NeedsPatchedLogging(new ResolvedVersion()));

    [Fact]
    public void ALoaderProfileTakesItsDateFromTheGameVersion()
    {
        var game = ResolvedVersion.FromLeaf(new VersionJson { Id = "1.16.5", ReleaseTime = new DateTimeOffset(2021, 1, 14, 0, 0, 0, TimeSpan.Zero) });
        var loader = ResolvedVersion.Merge(game, new VersionJson { Id = "fabric-loader-0.16-1.16.5", InheritsFrom = "1.16.5", ReleaseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.Equal(2021, loader.ReleaseTime!.Value.Year);
    }

    // ---------------------------------------------------------------- command line length

    [Fact]
    public void AShortCommandLineIsLeftAlone()
    {
        var command = new LaunchCommand("java", new[] { "-Xmx2G", "-cp", "a.jar;b.jar", "Main" });

        Assert.Same(command, LaunchCommandBuilder.Shorten(command));
    }

    [Fact]
    public void ALongClasspathTravelsInTheEnvironment()
    {
        var classpath = string.Join(';', Enumerable.Range(0, 400).Select(i => $@"C:\Users\Дмитрий\AppData\Roaming\STlauncher\libraries\group\artifact{i}\1.0\artifact{i}-1.0.jar"));
        var command = new LaunchCommand("java", new[] { "-Xmx2G", "-cp", classpath, "Main", "--username", "Steve" });

        var shortened = LaunchCommandBuilder.Shorten(command);

        Assert.Equal(new[] { "-Xmx2G", "Main", "--username", "Steve" }, shortened.Arguments);
        Assert.Equal(classpath, shortened.Environment!["CLASSPATH"]);
    }

    [Fact]
    public void ALongCommandLineWithNoClasspathArgumentIsNotBroken()
    {
        var command = new LaunchCommand("java", new[] { new string('x', 40_000), "Main" });

        Assert.Same(command, LaunchCommandBuilder.Shorten(command));
    }

    // ---------------------------------------------------------------- downloads

    [Fact]
    public async Task AFileWithTheRightHashIsKept_WhateverSizeTheCatalogStates()
    {
        var directory = Temp();
        var path = Path.Combine(directory, "mod.jar");
        var bytes = new byte[1234];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        var handler = new Counting(bytes);
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

        // The catalog says two bytes more than the file has, as Modrinth's index sometimes does.
        var fetched = await client.EnsureFileAsync(new DownloadItem("https://example.org/mod.jar", path, sha1, 1236));

        Assert.False(fetched);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task WithNoHashTheSizeStillDecides()
    {
        var directory = Temp();
        var path = Path.Combine(directory, "mod.jar");
        File.WriteAllBytes(path, new byte[10]);

        var handler = new Counting(new byte[20]);
        var client = new DownloadClient(new HttpClient(handler), maxAttempts: 1);

        var fetched = await client.EnsureFileAsync(new DownloadItem("https://example.org/mod.jar", path, Size: 20));

        Assert.True(fetched);
        Assert.Equal(20, new FileInfo(path).Length);
    }

    private sealed class Counting : HttpMessageHandler
    {
        private readonly byte[] _body;

        public Counting(byte[] body) => _body = body;

        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
        }
    }

    // ---------------------------------------------------------------- java

    [Fact]
    public void AnExecutableAloneIsNotARuntime()
    {
        var home = Temp();
        var java = Write(home, "bin", JavaManager.JavaExecutableName);

        Assert.False(JavaManager.IsRuntimeComplete(java));

        // Java 17 and later: everything under lib, the JVM under bin/server or lib/server.
        Write(home, "lib", "jvm.cfg");
        Assert.False(JavaManager.IsRuntimeComplete(java));

        Write(home, "lib", "modules");
        Assert.False(JavaManager.IsRuntimeComplete(java));

        Write(home, "bin", "server", "jvm.dll");
        Assert.True(JavaManager.IsRuntimeComplete(java));
    }

    [Fact]
    public void AJavaEightLayoutCounts_AndAnEmptyClassLibraryDoesNot()
    {
        var home = Temp();
        var java = Write(home, "bin", JavaManager.JavaExecutableName);

        Write(home, "lib", "amd64", "jvm.cfg");
        Write(home, "lib", "amd64", "server", "libjvm.so");
        var classes = Write(home, "lib", "rt.jar");

        Assert.True(JavaManager.IsRuntimeComplete(java));

        File.WriteAllBytes(classes, Array.Empty<byte>());
        Assert.False(JavaManager.IsRuntimeComplete(java));
    }

    private static string Write(string root, params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static LaunchCommand Build(ResolvedVersion version, string? loggingConfig, IReadOnlyList<string> userArgs)
        => LaunchCommandBuilder.Build(
            new LaunchOptions
            {
                Version = version,
                Account = Auth.OfflineAuth.Login("Steve"),
                JavaPath = "java",
                GameDirectory = "game",
                AssetsDirectory = "assets",
                NativesDirectory = "natives",
                LibrariesDirectory = "libraries",
                Classpath = new[] { "a.jar" },
                ExtraJvmArgs = userArgs,
                LoggingConfigPath = loggingConfig
            },
            new RuleContext { OsName = "windows", Arch = "x86_64" });
}
