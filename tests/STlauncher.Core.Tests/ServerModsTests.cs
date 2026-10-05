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
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

internal static class ServerModsFixture
{
    public static string FabricJson(string id, string name, string version = "1.0.0", string? description = null)
        => "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"version\":\"" + version + "\"" +
           (description is null ? string.Empty : ",\"description\":\"" + description + "\"") + "}";

    /// <summary>A server folder with a mods folder holding the given Fabric mods.</summary>
    public static string Server(params (string File, string Id, string Name)[] mods)
    {
        var server = HostingTemp.Directory();
        var folder = Path.Combine(server, "mods");
        Directory.CreateDirectory(folder);

        foreach (var (file, id, name) in mods)
        {
            HostingTemp.Jar(folder, file, ("fabric.mod.json", FabricJson(id, name)));
        }

        return server;
    }

    public static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

public class ServerModsListTests
{
    [Fact]
    public void List_ShowsWhatTheJarSaysAndWhereItCameFrom()
    {
        var server = HostingTemp.Directory();
        var mods = Path.Combine(server, "mods");
        var build = HostingTemp.Directory();

        HostingTemp.Jar(mods, "lithium-0.14.jar", ("fabric.mod.json", ServerModsFixture.FabricJson("lithium", "Lithium", "0.14.3", "Makes the server faster\\nSecond line")));
        HostingTemp.Jar(mods, "chunky-1.4.jar", ("fabric.mod.json", ServerModsFixture.FabricJson("chunky", "Chunky")));
        HostingTemp.Jar(mods, "handmade.jar.disabled", ("fabric.mod.json", ServerModsFixture.FabricJson("handmade", "Handmade")));
        HostingTemp.Jar(mods, "plain-library.jar", ("readme.txt", "nothing about itself"));
        File.WriteAllText(Path.Combine(mods, "notes.txt"), "not a mod");

        // The build has the same lithium file; chunky is on record as added from Modrinth.
        HostingTemp.Jar(Path.Combine(build, "mods"), "lithium-0.14.jar", ("fabric.mod.json", ServerModsFixture.FabricJson("lithium", "Lithium")));
        ServerMods.Record(server, new ServerModRecord { FileName = "chunky-1.4.jar", Origin = ServerModOrigin.Modrinth, ProjectId = "PRJ1", Title = "Chunky" });

        var list = ServerMods.List(server, build);

        Assert.Equal(4, list.Count);

        var lithium = list.Single(m => m.BaseName == "lithium-0.14.jar");
        Assert.Equal("Lithium", lithium.Title);
        Assert.Equal("0.14.3", lithium.Version);
        Assert.Equal("Makes the server faster", lithium.Description);
        Assert.Equal(ServerModOrigin.Build, lithium.Origin);
        Assert.True(lithium.Enabled);
        Assert.Contains("lithium", lithium.ModIds);

        var chunky = list.Single(m => m.BaseName == "chunky-1.4.jar");
        Assert.Equal(ServerModOrigin.Modrinth, chunky.Origin);
        Assert.Equal("PRJ1", chunky.ProjectId);

        var handmade = list.Single(m => m.BaseName == "handmade.jar");
        Assert.False(handmade.Enabled);
        Assert.Equal("handmade.jar.disabled", handmade.FileName);
        Assert.Equal(ServerModOrigin.Unknown, handmade.Origin);

        // A jar that says nothing is still listed, under its file name.
        Assert.Equal("plain-library", list.Single(m => m.BaseName == "plain-library.jar").Title);

        // Switched-off mods come last.
        Assert.Same(handmade, list[^1]);
    }

    [Fact]
    public void List_OfAServerWithoutModsFolder_IsEmpty()
        => Assert.Empty(ServerMods.List(HostingTemp.Directory()));

    [Fact]
    public void SetEnabled_RenamesTheFileAndBack_NeverDeleting()
    {
        var server = ServerModsFixture.Server(("lithium.jar", "lithium", "Lithium"));
        var jar = Path.Combine(server, "mods", "lithium.jar");
        var bytes = File.ReadAllBytes(jar);

        Assert.Equal("lithium.jar.disabled", ServerMods.SetEnabled(server, "lithium.jar", enabled: false));
        Assert.False(File.Exists(jar));
        Assert.Equal(bytes, File.ReadAllBytes(jar + ".disabled"));
        Assert.False(ServerMods.List(server).Single().Enabled);

        // Asked for the state it is already in: nothing to do, and the name is reported.
        Assert.Equal("lithium.jar.disabled", ServerMods.SetEnabled(server, "lithium.jar", enabled: false));

        // Either name addresses the mod.
        Assert.Equal("lithium.jar", ServerMods.SetEnabled(server, "lithium.jar.disabled", enabled: true));
        Assert.Equal(bytes, File.ReadAllBytes(jar));
        Assert.True(ServerMods.List(server).Single().Enabled);
    }

    [Fact]
    public void SetEnabled_LeavesBothFiles_WhenTheOtherNameIsTaken()
    {
        var server = ServerModsFixture.Server(("lithium.jar", "lithium", "Lithium"));
        File.WriteAllText(Path.Combine(server, "mods", "lithium.jar.disabled"), "an older copy");

        Assert.Null(ServerMods.SetEnabled(server, "lithium.jar", enabled: false));
        Assert.True(File.Exists(Path.Combine(server, "mods", "lithium.jar")));
        Assert.Equal("an older copy", File.ReadAllText(Path.Combine(server, "mods", "lithium.jar.disabled")));
    }

    [Theory]
    [InlineData("../server.json")]
    [InlineData("..\\outside.jar")]
    [InlineData("sub/inner.jar")]
    [InlineData("C:\\somewhere\\mod.jar")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public void SetEnabledAndRemove_TakeOnlyAJarNameInTheModsFolder(string name)
    {
        var server = ServerModsFixture.Server(("lithium.jar", "lithium", "Lithium"));
        File.WriteAllText(Path.Combine(server, "outside.jar"), "x");
        File.WriteAllText(Path.Combine(server, "mods", "notes.txt"), "x");

        Assert.Null(ServerMods.SetEnabled(server, name, enabled: false));
        Assert.Null(ServerMods.Remove(server, name));
        Assert.True(File.Exists(Path.Combine(server, "outside.jar")));
        Assert.True(File.Exists(Path.Combine(server, "mods", "notes.txt")));
    }

    [Fact]
    public void Remove_MovesTheJarIntoRemoved_AndDropsItsRecord()
    {
        var server = ServerModsFixture.Server(("chunky.jar", "chunky", "Chunky"), ("lithium.jar", "lithium", "Lithium"));
        var bytes = File.ReadAllBytes(Path.Combine(server, "mods", "chunky.jar"));
        ServerMods.Record(server, new ServerModRecord { FileName = "chunky.jar", Origin = ServerModOrigin.Modrinth, ProjectId = "PRJ1" });
        ServerMods.Record(server, new ServerModRecord { FileName = "lithium.jar", Origin = ServerModOrigin.Build });

        var moved = ServerMods.Remove(server, "chunky.jar", new DateTime(2026, 10, 5, 10, 15, 0));

        Assert.Equal(Path.Combine(server, ".removed", "20261005-101500-chunky.jar"), moved);
        Assert.Equal(bytes, File.ReadAllBytes(moved!));
        Assert.False(File.Exists(Path.Combine(server, "mods", "chunky.jar")));
        Assert.Equal("lithium.jar", Assert.Single(ServerMods.ReadRecords(server)).FileName);
        Assert.Equal("lithium.jar", Assert.Single(ServerMods.List(server)).BaseName);
    }

    [Fact]
    public void Remove_TwiceInOneSecond_KeepsBothFiles()
    {
        var server = ServerModsFixture.Server(("chunky.jar", "chunky", "Chunky"));
        var when = new DateTime(2026, 10, 5, 10, 15, 0);

        var first = ServerMods.Remove(server, "chunky.jar", when);
        HostingTemp.Jar(Path.Combine(server, "mods"), "chunky.jar", ("fabric.mod.json", ServerModsFixture.FabricJson("chunky", "Chunky")));
        ServerMods.SetEnabled(server, "chunky.jar", enabled: false);
        var second = ServerMods.Remove(server, "chunky.jar.disabled", when);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void RecordBuildCopies_AddsOnlyWhatIsNotOnRecord()
    {
        var server = ServerModsFixture.Server(("chunky.jar", "chunky", "Chunky"), ("lithium.jar", "lithium", "Lithium"));
        ServerMods.Record(server, new ServerModRecord { FileName = "chunky.jar", Origin = ServerModOrigin.Modrinth, ProjectId = "PRJ1" });

        ServerMods.RecordBuildCopies(server, new[] { "chunky.jar", "lithium.jar", "../evil.jar" });

        var records = ServerMods.ReadRecords(server).ToDictionary(r => r.FileName);
        Assert.Equal(2, records.Count);
        Assert.Equal(ServerModOrigin.Modrinth, records["chunky.jar"].Origin);
        Assert.Equal(ServerModOrigin.Build, records["lithium.jar"].Origin);

        // On record, so "from the build" even when the build is not given.
        Assert.Equal(ServerModOrigin.Build, ServerMods.List(server).Single(m => m.BaseName == "lithium.jar").Origin);
    }

    [Fact]
    public void ReadRecords_OfABrokenFile_IsEmptyAndTheListStillWorks()
    {
        var server = ServerModsFixture.Server(("lithium.jar", "lithium", "Lithium"));
        File.WriteAllText(ServerMods.RecordsPath(server), "{ not json");

        Assert.Empty(ServerMods.ReadRecords(server));
        Assert.Single(ServerMods.List(server));
    }
}

public class ServerModCatalogTests
{
    private const string Api = "https://api.modrinth.com/v2";

    /// <summary>Answers by path; everything asked is kept, so a test can say what was not asked.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = new();

        public void Json(string path, string body) => _responses[path] = Encoding.UTF8.GetBytes(body);

        public void Bytes(string path, byte[] body) => _responses[path] = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.AbsoluteUri);
            }

            var key = request.RequestUri!.GetLeftPart(UriPartial.Path);

            return Task.FromResult(_responses.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (ServerModCatalog Catalog, Stub Stub) NewCatalog()
    {
        var stub = new Stub();
        var http = new HttpClient(stub);
        return (new ServerModCatalog(new ModrinthClient(http), new ModManager(new DownloadClient(http, maxAttempts: 1))), stub);
    }

    private static byte[] JarBytes(string id, string name, string version)
    {
        var directory = HostingTemp.Directory();
        var path = HostingTemp.Jar(directory, "made.jar", ("fabric.mod.json", ServerModsFixture.FabricJson(id, name, version)));
        return File.ReadAllBytes(path);
    }

    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static string Version(string id, string project, string fileName, string url, string? sha1, long size, params (string Project, string Type)[] dependencies)
        => "[{\"id\":\"" + id + "\",\"project_id\":\"" + project + "\",\"name\":\"" + id + "\",\"version_number\":\"1.0\"," +
           "\"game_versions\":[\"1.21.1\"],\"loaders\":[\"fabric\"],\"version_type\":\"release\"," +
           "\"files\":[{\"url\":\"" + url + "\",\"filename\":\"" + fileName.Replace("\\", "\\\\") + "\",\"size\":" + size + ",\"primary\":true," +
           "\"hashes\":{" + (sha1 is null ? string.Empty : "\"sha1\":\"" + sha1 + "\"") + "}}]," +
           "\"dependencies\":[" + string.Join(",", dependencies.Select(d => "{\"project_id\":\"" + d.Project + "\",\"dependency_type\":\"" + d.Type + "\"}")) + "]}]";

    private static string Project(string id, string slug, string title)
        => "{\"id\":\"" + id + "\",\"slug\":\"" + slug + "\",\"title\":\"" + title + "\",\"description\":\"\",\"project_type\":\"mod\"}";

    [Fact]
    public void BuildFacets_AsksForTheServersVersionLoaderAndServerSide()
    {
        Assert.Equal(
            "[[\"project_type:mod\"],[\"categories:fabric\"],[\"versions:1.21.1\"],[\"server_side:required\",\"server_side:optional\"]]",
            ServerModCatalog.BuildFacets("1.21.1", LoaderKind.Fabric));

        Assert.Contains("[\"categories:neoforge\"]", ServerModCatalog.BuildFacets("1.21.1", LoaderKind.NeoForge));
        Assert.DoesNotContain("unsupported", ServerModCatalog.BuildFacets("1.21.1", LoaderKind.Forge));
    }

    [Fact]
    public void SupportsMods_IsFalseForAVanillaServer()
    {
        Assert.False(ServerModCatalog.SupportsMods(LoaderKind.Vanilla));
        Assert.True(ServerModCatalog.SupportsMods(LoaderKind.Fabric));
        Assert.True(ServerModCatalog.SupportsMods(LoaderKind.Forge));
        Assert.True(ServerModCatalog.SupportsMods(LoaderKind.NeoForge));
    }

    [Fact]
    public async Task Search_SendsTheServerFacets_AndReadsTheHits()
    {
        var (catalog, stub) = NewCatalog();
        stub.Json(Api + "/search", "{\"hits\":[{\"project_id\":\"MAP\",\"slug\":\"bluemap\",\"title\":\"BlueMap\",\"description\":\"A map\",\"downloads\":9200000,\"categories\":[\"fabric\",\"utility\"]}],\"total_hits\":1}");

        var page = await catalog.SearchAsync(" map ", "1.21.1", LoaderKind.Fabric);

        Assert.Equal("BlueMap", Assert.Single(page.Items).Title);

        var request = Assert.Single(stub.Requests);
        Assert.Contains("query=map&", request);
        Assert.Contains("index=relevance", request);
        Assert.Contains("facets=" + Uri.EscapeDataString(ServerModCatalog.BuildFacets("1.21.1", LoaderKind.Fabric)), request);

        // Nothing typed: the most downloaded server mods, not an empty page.
        await catalog.SearchAsync(null, "1.21.1", LoaderKind.Fabric);
        Assert.Contains("index=downloads", stub.Requests[^1]);
    }

    [Fact]
    public async Task Plan_ListsWhatTheModNeedsBeforeIt_LeavesOutWhatTheServerHas_AndFetchesNoFile()
    {
        var (catalog, stub) = NewCatalog();
        var server = ServerModsFixture.Server(("have-lib.jar", "havelib", "Have Lib"), ("cloth-config-15.jar", "cloth-config", "Cloth Config API"));
        ServerMods.Record(server, new ServerModRecord { FileName = "have-lib.jar", Origin = ServerModOrigin.Modrinth, ProjectId = "HAVE" });

        // MAP needs LIB (not there), HAVE (on record), CLOTH (there under its id) and, optionally, OPT.
        stub.Json(Api + "/project/MAP/version", Version("map-v", "MAP", "bluemap-5.jar", "https://cdn.test/bluemap-5.jar", "aa11", 5_000_000,
            ("LIB", "required"), ("HAVE", "required"), ("CLOTH", "required"), ("OPT", "optional")));

        // LIB needs DEEP, and MAP again: a loop must not run for ever.
        stub.Json(Api + "/project/LIB/version", Version("lib-v", "LIB", "lib-2.jar", "https://cdn.test/lib-2.jar", "bb22", 300_000, ("DEEP", "required"), ("MAP", "required")));
        stub.Json(Api + "/project/DEEP/version", Version("deep-v", "DEEP", "deep-1.jar", "https://cdn.test/deep-1.jar", "cc33", 40_000));
        stub.Json(Api + "/project/LIB", Project("LIB", "the-lib", "The Lib"));
        stub.Json(Api + "/project/DEEP", Project("DEEP", "deep", "Deep Core"));
        stub.Json(Api + "/project/CLOTH", Project("CLOTH", "cloth-config", "Cloth Config API"));

        var presence = await catalog.PresenceAsync(server);
        var plan = await catalog.PlanAsync("MAP", "BlueMap", "1.21.1", LoaderKind.Fabric, presence);

        Assert.True(plan.CanInstall);
        Assert.Equal(new[] { "deep-1.jar", "lib-2.jar", "bluemap-5.jar" }, plan.Downloads.Select(d => d.FileName));
        Assert.Equal(new[] { "Deep Core", "The Lib", "BlueMap" }, plan.Downloads.Select(d => d.Title));
        Assert.Equal(new[] { true, true, false }, plan.Downloads.Select(d => d.IsDependency));
        Assert.Equal(5_340_000, plan.TotalBytes);
        Assert.All(plan.Downloads, d => Assert.Equal("cdn.test", d.Host));
        Assert.Equal(new[] { "Cloth Config API", "Have Lib" }, plan.AlreadyThere.OrderBy(t => t));

        // A plan is only a list: no file was asked for and the folder is as it was.
        Assert.DoesNotContain(stub.Requests, r => r.Contains("cdn.test", StringComparison.Ordinal));
        Assert.DoesNotContain(stub.Requests, r => r.Contains("/project/OPT", StringComparison.Ordinal));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(server, "mods")).Length);
    }

    [Fact]
    public async Task Plan_SaysSo_WhenAModOrWhatItNeedsHasNoVersionForTheServer()
    {
        var (catalog, stub) = NewCatalog();
        var server = ServerModsFixture.Server();

        stub.Json(Api + "/project/MAP/version", Version("map-v", "MAP", "bluemap-5.jar", "https://cdn.test/bluemap-5.jar", "aa11", 10, ("LIB", "required")));
        stub.Json(Api + "/project/LIB/version", "[]");
        stub.Json(Api + "/project/LIB", Project("LIB", "the-lib", "The Lib"));

        var plan = await catalog.PlanAsync("MAP", "BlueMap", "1.21.1", LoaderKind.Fabric, await catalog.PresenceAsync(server));

        Assert.False(plan.CanInstall);
        Assert.Equal(new ServerModProblem(ServerModProblemKind.NoVersion, "The Lib"), Assert.Single(plan.Problems));
    }

    [Theory]
    [InlineData("bluemap-5.jar", null, "https://cdn.test/bluemap-5.jar")]          // no hash to check against
    [InlineData("..\\..\\start.jar", "aa11", "https://cdn.test/bluemap-5.jar")]    // a path, not a name
    [InlineData("bluemap-5.exe", "aa11", "https://cdn.test/bluemap-5.jar")]        // not a mod
    [InlineData("bluemap-5.jar", "aa11", "http://cdn.test/bluemap-5.jar")]         // not over https
    public async Task Plan_RefusesAFileThatCannotBeTrusted(string fileName, string? sha1, string url)
    {
        var (catalog, stub) = NewCatalog();
        stub.Json(Api + "/project/MAP/version", Version("map-v", "MAP", fileName, url, sha1, 10));

        var plan = await catalog.PlanAsync("MAP", "BlueMap", "1.21.1", LoaderKind.Fabric, await catalog.PresenceAsync(ServerModsFixture.Server()));

        Assert.False(plan.CanInstall);
        Assert.Empty(plan.Downloads);
        Assert.Equal(ServerModProblemKind.NoFile, Assert.Single(plan.Problems).Kind);
    }

    [Fact]
    public async Task Install_FetchesChecksAndRecords_AndSwitchesOffTheOlderFileOfTheSameMod()
    {
        var (catalog, stub) = NewCatalog();
        var server = ServerModsFixture.Server(("bluemap-4.jar", "bluemap", "BlueMap"));

        var lib = JarBytes("thelib", "The Lib", "2.0");
        var map = JarBytes("bluemap", "BlueMap", "5.0");

        stub.Json(Api + "/project/MAP/version", Version("map-v", "MAP", "bluemap-5.jar", "https://cdn.test/bluemap-5.jar", Sha1(map), map.Length, ("LIB", "required")));
        stub.Json(Api + "/project/LIB/version", Version("lib-v", "LIB", "lib-2.jar", "https://cdn.test/lib-2.jar", Sha1(lib), lib.Length));
        stub.Json(Api + "/project/LIB", Project("LIB", "the-lib", "The Lib"));
        stub.Bytes("https://cdn.test/bluemap-5.jar", map);
        stub.Bytes("https://cdn.test/lib-2.jar", lib);

        // The older bluemap is on the server under a hash Modrinth does not know, and its
        // id is not the project's slug: the plan cannot see it, the install still must.
        var plan = await catalog.PlanAsync("MAP", "Blue Map 3D", "1.21.1", LoaderKind.Fabric, new ServerModCatalog(new ModrinthClient(new HttpClient(new Stub())), new ModManager(new DownloadClient(new HttpClient()))).PresenceAsync(server, Array.Empty<ServerModEntry>()).Result);
        var started = new List<string>();
        var result = await catalog.InstallAsync(plan, server, new SyncProgress(d => started.Add(d.FileName)));

        Assert.Equal(new[] { "lib-2.jar", "bluemap-5.jar" }, result.Installed);
        Assert.Equal(new[] { "lib-2.jar", "bluemap-5.jar" }, started);
        Assert.Equal(new[] { "bluemap-4.jar" }, result.SwitchedOff);

        var mods = Path.Combine(server, "mods");
        Assert.Equal(map, File.ReadAllBytes(Path.Combine(mods, "bluemap-5.jar")));
        Assert.Equal(lib, File.ReadAllBytes(Path.Combine(mods, "lib-2.jar")));
        Assert.True(File.Exists(Path.Combine(mods, "bluemap-4.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(mods, "bluemap-4.jar")));

        var list = ServerMods.List(server);
        var added = list.Single(m => m.BaseName == "bluemap-5.jar");
        Assert.Equal(ServerModOrigin.Modrinth, added.Origin);
        Assert.Equal("MAP", added.ProjectId);
        Assert.Equal(ServerModOrigin.Modrinth, list.Single(m => m.BaseName == "lib-2.jar").Origin);

        // And now the server is known to have both.
        var presence = await catalog.PresenceAsync(server);
        Assert.True(presence.HasProject("MAP"));
        Assert.True(presence.Has("LIB", null, null));
        Assert.True(presence.Has(null, "thelib", null));
        Assert.False(presence.Has("OTHER", "other-mod", "Other Mod"));
    }

    [Fact]
    public async Task Install_OfAFileThatIsNotWhatModrinthDescribed_LeavesNothingBehind()
    {
        var (catalog, stub) = NewCatalog();
        var server = ServerModsFixture.Server();
        var map = JarBytes("bluemap", "BlueMap", "5.0");

        stub.Json(Api + "/project/MAP/version", Version("map-v", "MAP", "bluemap-5.jar", "https://cdn.test/bluemap-5.jar", Sha1(map), map.Length));
        stub.Bytes("https://cdn.test/bluemap-5.jar", Encoding.ASCII.GetBytes("something else entirely"));

        var plan = await catalog.PlanAsync("MAP", "BlueMap", "1.21.1", LoaderKind.Fabric, await catalog.PresenceAsync(server));

        await Assert.ThrowsAnyAsync<Exception>(() => catalog.InstallAsync(plan, server));

        Assert.Empty(Directory.GetFiles(Path.Combine(server, "mods")));
        Assert.Empty(ServerMods.ReadRecords(server));
    }

    [Fact]
    public async Task Install_RefusesAPlanWithProblems()
    {
        var (catalog, _) = NewCatalog();
        var plan = new ServerModPlan("MAP", "BlueMap", Array.Empty<ServerModDownload>(), Array.Empty<string>(),
            new[] { new ServerModProblem(ServerModProblemKind.NoVersion, "BlueMap") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.InstallAsync(plan, ServerModsFixture.Server()));
    }

    /// <summary>Progress that reports on the calling thread, so the order is the order of the install.</summary>
    private sealed class SyncProgress : IProgress<ServerModDownload>
    {
        private readonly Action<ServerModDownload> _report;

        public SyncProgress(Action<ServerModDownload> report) => _report = report;

        public void Report(ServerModDownload value) => _report(value);
    }
}

public class ServerConfigFilesTests
{
    private static ServerModEntry Mod(string baseName, string title, params string[] ids)
        => new(baseName, baseName, baseName, true, 0, title, null, null, null, ServerModOrigin.Unknown, null, ids);

    [Fact]
    public void Discover_ListsTheModSettings_AndLeavesTheServersOwnFilesAndBackupsOut()
    {
        var server = HostingTemp.Directory();
        ServerModsFixture.WriteFile(server, "server.properties", "motd=hi\nlevel-name=world\n");
        ServerModsFixture.WriteFile(server, "whitelist.json", "[]");
        ServerModsFixture.WriteFile(server, "config/lithium.properties", "a=b");
        ServerModsFixture.WriteFile(server, "config/voicechat/voicechat-server.properties", "port=24454");
        ServerModsFixture.WriteFile(server, "config/jei-server.toml", "a = 1");
        ServerModsFixture.WriteFile(server, "config/c.json5", "{}");
        ServerModsFixture.WriteFile(server, "config/d.yml", "a: 1");
        ServerModsFixture.WriteFile(server, "config/e.cfg", "a=1");
        ServerModsFixture.WriteFile(server, "config/f.conf", "a=1");
        ServerModsFixture.WriteFile(server, "config/lithium.properties.bak-20261005-101500", "old");
        ServerModsFixture.WriteFile(server, "config/readme.txt", "not settings");
        ServerModsFixture.WriteFile(server, "config/.hidden/secret.toml", "a = 1");
        ServerModsFixture.WriteFile(server, "world/serverconfig/forge-server.toml", "a = 1");

        var files = ServerConfigFiles.Discover(server);

        Assert.Equal(
            new[]
            {
                "config/c.json5", "config/d.yml", "config/e.cfg", "config/f.conf", "config/jei-server.toml",
                "config/lithium.properties", "config/voicechat/voicechat-server.properties", "world/serverconfig/forge-server.toml"
            },
            files.Select(f => f.RelativePath));

        Assert.Equal(ConfigFormat.Yaml, files.Single(f => f.Name == "d.yml").Format);
        Assert.Equal(ConfigFormat.Json5, files.Single(f => f.Name == "c.json5").Format);
    }

    [Fact]
    public void Match_GivesAFileToTheModItsNameIsFor_AndLeavesTheRestUnderOther()
    {
        var files = new[]
        {
            new ServerConfigFile("config/voicechat/voicechat-server.properties", 10, ConfigFormat.Properties),
            new ServerConfigFile("config/lithium.properties", 10, ConfigFormat.Properties),
            new ServerConfigFile("config/jei-server.toml", 10, ConfigFormat.Toml),
            new ServerConfigFile("config/simple-voice-chat-extra.json", 10, ConfigFormat.Json),
            new ServerConfigFile("config/fabric/indigo-renderer.properties", 10, ConfigFormat.Properties),
            new ServerConfigFile("config/s.toml", 10, ConfigFormat.Toml)
        };

        var mods = new[]
        {
            Mod("voicechat-fabric.jar", "Simple Voice Chat", "voicechat"),
            Mod("lithium.jar", "Lithium", "lithium"),
            Mod("jei.jar", "Just Enough Items", "jei"),

            // One letter is no name to match by.
            Mod("s.jar", "S", "s")
        };

        var index = ServerConfigFiles.Match(files, mods);

        Assert.Equal(
            new[] { "config/simple-voice-chat-extra.json", "config/voicechat/voicechat-server.properties" },
            index.ByMod["voicechat-fabric.jar"].Select(f => f.RelativePath).OrderBy(p => p));
        Assert.Equal("config/lithium.properties", Assert.Single(index.ByMod["lithium.jar"]).RelativePath);
        Assert.Equal("config/jei-server.toml", Assert.Single(index.ByMod["jei.jar"]).RelativePath);
        Assert.False(index.ByMod.ContainsKey("s.jar"));
        Assert.Equal(new[] { "config/fabric/indigo-renderer.properties", "config/s.toml" }, index.Other.Select(f => f.RelativePath));
    }

    [Theory]
    [InlineData("../outside.toml")]
    [InlineData("config/../../outside.toml")]
    [InlineData("config/..\\..\\outside.toml")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/etc/passwd.conf")]
    [InlineData("server.properties")]
    [InlineData("server-mods.json")]
    [InlineData("config/run.bat")]
    [InlineData("mods/lithium.jar")]
    [InlineData("")]
    public void Resolve_RefusesAnythingButASettingsFileInsideTheServer(string relative)
    {
        var server = HostingTemp.Directory();
        Assert.Null(ServerConfigFiles.Resolve(server, relative));
        Assert.Equal(ConfigFileStatus.NotAllowed, ServerConfigFiles.Read(server, relative).Status);
        Assert.Equal(ConfigFileStatus.NotAllowed, ServerConfigFiles.Write(server, relative, "x").Status);
    }

    [Fact]
    public void Resolve_TakesASettingsFileUnderTheServer()
    {
        var server = HostingTemp.Directory();
        Assert.Equal(
            Path.Combine(Path.GetFullPath(server), "config", "voicechat", "voicechat-server.properties"),
            ServerConfigFiles.Resolve(server, "config/voicechat/voicechat-server.properties"));
    }

    [Fact]
    public void Write_KeepsThePreviousContentBesideTheFile_EveryTime()
    {
        var server = HostingTemp.Directory();
        ServerModsFixture.WriteFile(server, "config/lithium.properties", "first");
        var when = new DateTime(2026, 10, 5, 10, 15, 0);

        var one = ServerConfigFiles.Write(server, "config/lithium.properties", "second", now: when);
        var two = ServerConfigFiles.Write(server, "config/lithium.properties", "third", now: when);

        Assert.True(one.IsOk);
        Assert.Equal(Path.Combine(Path.GetFullPath(server), "config", "lithium.properties.bak-20261005-101500"), one.BackupPath);
        Assert.Equal("first", File.ReadAllText(one.BackupPath!));
        Assert.NotEqual(one.BackupPath, two.BackupPath);
        Assert.Equal("second", File.ReadAllText(two.BackupPath!));
        Assert.Equal("third", ServerConfigFiles.Read(server, "config/lithium.properties").Text);

        // No temporary file is left, and the backups are not offered as settings.
        Assert.Equal(3, Directory.GetFiles(Path.Combine(server, "config")).Length);
        Assert.Single(ServerConfigFiles.Discover(server));
    }

    [Fact]
    public void ReadAndWrite_RefuseALargeOrBinaryFile_AndLeaveItAsItIs()
    {
        var server = HostingTemp.Directory();
        var big = new string('a', (int)ServerConfigFiles.MaxBytes + 1);
        ServerModsFixture.WriteFile(server, "config/big.json", big);
        File.WriteAllBytes(Path.Combine(server, "config", "binary.toml"), new byte[] { 0x61, 0x00, 0x62 });

        Assert.Equal(ConfigFileStatus.TooLarge, ServerConfigFiles.Read(server, "config/big.json").Status);
        Assert.True(ServerConfigFiles.Discover(server).Single(f => f.Name == "big.json").TooLarge);
        Assert.Equal(ConfigFileStatus.NotText, ServerConfigFiles.Read(server, "config/binary.toml").Status);
        Assert.Equal(ConfigFileStatus.Missing, ServerConfigFiles.Read(server, "config/none.toml").Status);

        ServerModsFixture.WriteFile(server, "config/small.json", "{}");
        Assert.Equal(ConfigFileStatus.TooLarge, ServerConfigFiles.Write(server, "config/small.json", big).Status);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(server, "config", "small.json")));
    }

    [Fact]
    public void ReadAndWrite_KeepTheFilesEncoding()
    {
        var server = HostingTemp.Directory();
        var bom = Path.Combine(server, "config", "bom.toml");
        var latin = Path.Combine(server, "config", "latin.properties");
        Directory.CreateDirectory(Path.GetDirectoryName(bom)!);
        File.WriteAllBytes(bom, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("name = \"Мир\"")).ToArray());
        File.WriteAllBytes(latin, Encoding.Latin1.GetBytes("motd=Caf\u00e9"));

        var readBom = ServerConfigFiles.Read(server, "config/bom.toml");
        Assert.Equal(ConfigEncoding.Utf8WithBom, readBom.Encoding);
        Assert.Equal("name = \"Мир\"", readBom.Text);
        ServerConfigFiles.Write(server, "config/bom.toml", "name = \"Мир 2\"", readBom.Encoding);
        Assert.Equal(Encoding.UTF8.GetPreamble(), File.ReadAllBytes(bom).Take(3).ToArray());

        var readLatin = ServerConfigFiles.Read(server, "config/latin.properties");
        Assert.Equal(ConfigEncoding.Latin1, readLatin.Encoding);
        Assert.Equal("motd=Caf\u00e9", readLatin.Text);
        ServerConfigFiles.Write(server, "config/latin.properties", "motd=Caf\u00e9!", readLatin.Encoding);
        Assert.Equal(Encoding.Latin1.GetBytes("motd=Caf\u00e9!"), File.ReadAllBytes(latin));
    }
}

public class ConfigFieldDocumentTests
{
    [Fact]
    public void Properties_AreFieldsWithTheirComments_AndAChangeTouchesOnlyItsLine()
    {
        const string text =
            "# Simple Voice Chat\r\n" +
            "\r\n" +
            "# The port of the voice chat server\r\n" +
            "# Set to -1 to use the game port\r\n" +
            "port=24454\r\n" +
            "max_voice_distance = 48.0\r\n" +
            "allow_recording:true\r\n" +
            "bind_address=\r\n";

        var document = ConfigFieldDocument.TryParse(ConfigFormat.Properties, text)!;

        Assert.Equal(new[] { "port", "max_voice_distance", "allow_recording", "bind_address" }, document.Fields.Select(f => f.Key));
        Assert.Equal("The port of the voice chat server\nSet to -1 to use the game port", document.Fields[0].Comment);
        Assert.Equal("24454", document.Fields[0].Value);
        Assert.Equal("48.0", document.Fields[1].Value);
        Assert.Equal(ConfigValueKind.Bool, document.Fields[2].Kind);
        Assert.Equal(string.Empty, document.Fields[3].Value);
        Assert.Equal(ConfigValueKind.Text, document.Fields[3].Kind);

        // Written as a number, kept a number: a port cannot become a word.
        Assert.Equal(ConfigValueKind.Number, document.Fields[0].Kind);
        Assert.Equal(ConfigValueKind.Number, document.Fields[1].Kind);
        Assert.False(document.Accepts(document.Fields[0], "far"));
        Assert.True(document.Accepts(document.Fields[0], "-1"));

        // Nothing changed: the same text, line endings included.
        Assert.Equal(text, document.Apply(new Dictionary<int, string>()));

        var changed = document.Apply(new Dictionary<int, string>
        {
            [document.Fields[0].Line] = "25000",
            [document.Fields[2].Line] = "false",
            [document.Fields[3].Line] = "0.0.0.0"
        });

        Assert.Equal(text.Replace("port=24454", "port=25000").Replace("allow_recording:true", "allow_recording:false").Replace("bind_address=", "bind_address=0.0.0.0"), changed);
    }

    [Fact]
    public void Toml_KeepsSectionsCommentsAndTypes_AndQuotesTextAsItWas()
    {
        const string text =
            "#General settings\n" +
            "[general]\n" +
            "\t#Radius in chunks\n" +
            "\t#Range: 1 ~ 64\n" +
            "\tradius = 16 # kept\n" +
            "\tenabled = true\n" +
            "\tname = \"World map\"\n" +
            "\tpath = 'C:\\maps'\n" +
            "\tworlds = [\"overworld\", \"nether\"]\n" +
            "\n" +
            "[web.server]\n" +
            "\tport = 8100\n";

        var document = ConfigFieldDocument.TryParse(ConfigFormat.Toml, text)!;
        var byKey = document.Fields.ToDictionary(f => f.Key);

        Assert.Equal(new[] { "radius", "enabled", "name", "path", "worlds", "port" }, document.Fields.Select(f => f.Key));
        Assert.Equal("general", byKey["radius"].Section);
        Assert.Equal("web.server", byKey["port"].Section);
        Assert.Equal("Radius in chunks\nRange: 1 ~ 64", byKey["radius"].Comment);
        Assert.Equal(ConfigValueKind.Number, byKey["radius"].Kind);
        Assert.Equal("16", byKey["radius"].Value);
        Assert.Equal(ConfigValueKind.Bool, byKey["enabled"].Kind);
        Assert.Equal("World map", byKey["name"].Value);
        Assert.Equal("C:\\maps", byKey["path"].Value);
        Assert.Equal(ConfigValueKind.Raw, byKey["worlds"].Kind);

        Assert.False(document.Accepts(byKey["radius"], "sixteen"));
        Assert.False(document.Accepts(byKey["enabled"], "yes"));
        Assert.False(document.Accepts(byKey["name"], "two\nlines"));

        var changed = document.Apply(new Dictionary<int, string>
        {
            [byKey["radius"].Line] = "32",
            [byKey["name"].Line] = "Say \"hi\"",
            [byKey["path"].Line] = "it's",
            [byKey["port"].Line] = "8200"
        });

        Assert.Equal(
            text.Replace("radius = 16 # kept", "radius = 32 # kept")
                .Replace("name = \"World map\"", "name = \"Say \\\"hi\\\"\"")
                .Replace("path = 'C:\\maps'", "path = \"it's\"")
                .Replace("port = 8100", "port = 8200"),
            changed);

        // What was written reads back as the same values.
        var again = ConfigFieldDocument.TryParse(ConfigFormat.Toml, changed)!;
        Assert.Equal("32", again.Fields.Single(f => f.Key == "radius").Value);
        Assert.Equal("it's", again.Fields.Single(f => f.Key == "path").Value);
    }

    [Fact]
    public void Json_NestedObjectsGiveSections_AndStringsAreEscapedOnTheWayOut()
    {
        const string text =
            "{\n" +
            "  // Shown to players\n" +
            "  \"motd\": \"Hello\",\n" +
            "  \"limits\": {\n" +
            "    \"players\": 10,\n" +
            "    \"pvp\": false\n" +
            "  },\n" +
            "  \"tags\": [\"a\", \"b\"],\n" +
            "  \"owner\": null\n" +
            "}\n";

        var document = ConfigFieldDocument.TryParse(ConfigFormat.Json5, text)!;
        var byKey = document.Fields.ToDictionary(f => f.Key);

        Assert.Equal(new[] { "motd", "players", "pvp", "tags", "owner" }, document.Fields.Select(f => f.Key));
        Assert.Equal("Shown to players", byKey["motd"].Comment);
        Assert.Equal("limits", byKey["players"].Section);
        Assert.Equal(string.Empty, byKey["tags"].Section);
        Assert.Equal(ConfigValueKind.Number, byKey["players"].Kind);
        Assert.Equal(ConfigValueKind.Raw, byKey["owner"].Kind);

        var changed = document.Apply(new Dictionary<int, string>
        {
            [byKey["motd"].Line] = "Say \"hi\"",
            [byKey["players"].Line] = "20"
        });

        Assert.Equal(text.Replace("\"Hello\"", "\"Say \\\"hi\\\"\"").Replace("\"players\": 10", "\"players\": 20"), changed);
        Assert.Throws<FormatException>(() => document.Apply(new Dictionary<int, string> { [byKey["players"].Line] = "many" }));
    }

    [Theory]
    [InlineData(ConfigFormat.Toml, "[[servers]]\nname = \"a\"\n")]
    [InlineData(ConfigFormat.Toml, "list = [\n  \"a\",\n]\n")]
    [InlineData(ConfigFormat.Toml, "text = \"\"\"\nlong\n\"\"\"\n")]
    [InlineData(ConfigFormat.Json, "{\"a\": 1, \"b\": 2}")]
    [InlineData(ConfigFormat.Json, "{\n  \"list\": [\n    1\n  ]\n}\n")]
    [InlineData(ConfigFormat.Json5, "{\n  bare: 1\n}\n")]
    [InlineData(ConfigFormat.Properties, "key=continues \\\n  on the next line\n")]
    [InlineData(ConfigFormat.Yaml, "a: 1\n")]
    [InlineData(ConfigFormat.Cfg, "a=1\n")]
    [InlineData(ConfigFormat.Properties, "# only a comment\n")]
    public void WhatTheLineReaderIsNotSureOf_OpensAsText(ConfigFormat format, string text)
        => Assert.Null(ConfigFieldDocument.TryParse(format, text));
}
