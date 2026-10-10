using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using STlauncher.Core.Skins;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// "Show my skin in the game": the mod's config, the other skin mods the launcher steps
/// back for, which jar a build gets, and the promise not to bring back what the player
/// took out. Nothing here touches the network; Modrinth is a stub.
/// </summary>
public class InGameSkinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly string _library;

    public InGameSkinTests()
    {
        _game = Path.Combine(_root, "game");
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(Path.Combine(_game, "mods"));
        Directory.CreateDirectory(_library);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ===================== The config =====================

    /// <summary>The mod's own default list, as it writes it: a Mojang lookup by nickname comes first.</summary>
    private const string DefaultConfig = """
        {
          "version": "15.0.1",
          "buildNumber": 12,
          "loadlist": [
            { "name": "Mojang", "type": "MojangAPI" },
            { "name": "ElyBy", "type": "ElyByAPI" },
            { "name": "LocalSkin", "type": "Legacy", "checkPNG": false, "skin": "LocalSkin/skins/{USERNAME}.png", "model": "auto", "cape": "LocalSkin/capes/{USERNAME}.png" },
            { "name": "Сервер друга", "type": "CustomSkinAPI", "root": "https://skins.example/csl/" }
          ],
          "enableTransparentSkin": true,
          "cacheExpiry": 30
        }
        """;

    private static List<string> Names(string config)
        => JsonNode.Parse(config)!["loadlist"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).ToList();

    [Fact]
    public void Merge_PutsTheLocalFileAheadOfTheMojangLookup_AndKeepsEveryOtherEntry()
    {
        var merged = InGameSkinConfig.Merge(DefaultConfig, slim: false);

        Assert.NotNull(merged);
        Assert.Equal(new[] { "STlauncher", "Mojang", "ElyBy", "LocalSkin", "Сервер друга" }, Names(merged!));

        var root = JsonNode.Parse(merged!)!;
        var ours = root["loadlist"]![0]!;
        Assert.Equal("Legacy", ours["type"]!.GetValue<string>());
        Assert.Equal("STlauncher/skins/{USERNAME}.png", ours["skin"]!.GetValue<string>());
        Assert.Equal("default", ours["model"]!.GetValue<string>());

        // No lookup by nickname rides in with the entry: a file, and nothing else.
        Assert.Null(ours["cape"]);
        Assert.Null(ours["root"]);
        Assert.Null(ours["apiRoot"]);

        // The player's entry is carried over whole, and so is everything outside the list.
        Assert.Equal("https://skins.example/csl/", root["loadlist"]![4]!["root"]!.GetValue<string>());
        Assert.Equal("LocalSkin/capes/{USERNAME}.png", root["loadlist"]![3]!["cape"]!.GetValue<string>());
        Assert.Equal("15.0.1", root["version"]!.GetValue<string>());
        Assert.Equal(12, root["buildNumber"]!.GetValue<int>());
        Assert.Equal(30, root["cacheExpiry"]!.GetValue<int>());
        Assert.Contains("Сервер друга", merged);
    }

    [Fact]
    public void Merge_IsIdempotent_AndSaysWhenThereIsNothingToWrite()
    {
        var once = InGameSkinConfig.Merge(DefaultConfig, slim: true)!;

        Assert.Null(InGameSkinConfig.Merge(once, slim: true));
        Assert.Equal("slim", JsonNode.Parse(once)!["loadlist"]![0]!["model"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_UpdatesTheModel_WithoutAddingASecondEntry()
    {
        var slim = InGameSkinConfig.Merge(DefaultConfig, slim: true)!;
        var classic = InGameSkinConfig.Merge(slim, slim: false)!;

        Assert.Equal(new[] { "STlauncher", "Mojang", "ElyBy", "LocalSkin", "Сервер друга" }, Names(classic));
        Assert.Equal("default", JsonNode.Parse(classic)!["loadlist"]![0]!["model"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_MovesAnEntryThePlayerDraggedDown_BackToTheFront()
    {
        var merged = InGameSkinConfig.Merge(DefaultConfig, slim: false)!;
        var root = JsonNode.Parse(merged)!;
        var list = root["loadlist"]!.AsArray();
        var ours = list[0]!;
        list.RemoveAt(0);
        list.Add(ours);

        var again = InGameSkinConfig.Merge(root.ToJsonString(), slim: false);

        Assert.NotNull(again);
        Assert.Equal(new[] { "STlauncher", "Mojang", "ElyBy", "LocalSkin", "Сервер друга" }, Names(again!));
    }

    [Fact]
    public void Merge_GivesAConfigWithoutAListOne_AndRefusesWhatIsNotAConfig()
    {
        Assert.Equal(new[] { "STlauncher" }, Names(InGameSkinConfig.Merge("""{ "version": "14.28" }""", slim: false)!));

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => InGameSkinConfig.Merge("not json at all", slim: false));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => InGameSkinConfig.Merge("[1, 2]", slim: false));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => InGameSkinConfig.Merge("""{ "loadlist": "oops" }""", slim: false));
    }

    // ===================== Other skin mods =====================

    private string Jar(string fileName, string? entryName = null, string? entryText = null)
    {
        var path = Path.Combine(_game, "mods", fileName);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        if (entryName is not null)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(entryText);
        }

        return path;
    }

    private string FabricJar(string fileName, string id)
        => Jar(fileName, "fabric.mod.json", $$"""{ "schemaVersion": 1, "id": "{{id}}", "version": "1.0.0", "name": "{{id}}" }""");

    private string ForgeJar(string fileName, string id)
        => Jar(fileName, "META-INF/mods.toml", $"modLoader=\"javafml\"\n[[mods]]\nmodId=\"{id}\"\nversion=\"1.0\"\ndisplayName=\"{id}\"\n");

    [Theory]
    [InlineData("skinrestorer", "SkinRestorer")]
    [InlineData("fabrictailor", "Fabric Tailor")]
    [InlineData("offlineskins", "OfflineSkins")]
    [InlineData("hdskins", "HD Skins")]
    [InlineData("tlskincape", "TLSkinCape")]
    public void Detector_FindsAnotherSkinMod_ByTheIdItDeclares_WhateverTheFileIsCalled(string id, string name)
    {
        FabricJar("sodium.jar", "sodium");
        FabricJar("renamed-by-the-player.jar", id);

        var found = SkinModDetector.Find(_game);

        Assert.NotNull(found);
        Assert.Equal(name, found!.Name);
        Assert.Equal("renamed-by-the-player.jar", found.FileName);
        Assert.False(found.IsCustomSkinLoader);
    }

    [Fact]
    public void Detector_KnowsCustomSkinLoader_InBothItsGenerations_AndOnForge()
    {
        FabricJar("a.jar", "customskinloader-bootstrap");
        Assert.True(SkinModDetector.Find(_game)!.IsCustomSkinLoader);
        File.Delete(Path.Combine(_game, "mods", "a.jar"));

        FabricJar("b.jar", "customskinloader");
        Assert.True(SkinModDetector.Find(_game)!.IsCustomSkinLoader);
        File.Delete(Path.Combine(_game, "mods", "b.jar"));

        ForgeJar("c.jar", "customskinloader");
        Assert.True(SkinModDetector.Find(_game)!.IsCustomSkinLoader);
    }

    [Fact]
    public void Detector_FallsBackToTheFileName_OnlyForAJarThatDeclaresNothing()
    {
        // The old Forge jar is a coremod with no mods.toml.
        Jar("CustomSkinLoader_ForgeV1-14.28.jar");
        Assert.Equal("CustomSkinLoader", SkinModDetector.Find(_game)!.Name);
        File.Delete(Path.Combine(_game, "mods", "CustomSkinLoader_ForgeV1-14.28.jar"));

        // A mod with its own id is itself, whatever its file name hints at.
        FabricJar("skinrestorer-compat-for-something.jar", "somethingelse");
        Assert.Null(SkinModDetector.Find(_game));
    }

    [Fact]
    public void Detector_IgnoresSwitchedOffJars_TheLaunchersOwnJar_AndOrdinaryMods()
    {
        FabricJar("sodium.jar", "sodium");
        FabricJar("fabrictailor.jar.disabled", "fabrictailor");
        FabricJar("CustomSkinLoader_Universal-15.0.1.jar", "customskinloader-bootstrap");

        Assert.Null(SkinModDetector.Find(_game, exceptFileName: "CustomSkinLoader_Universal-15.0.1.jar"));
        Assert.Null(SkinModDetector.Find(Path.Combine(_root, "no-such-build")));
    }

    [Fact]
    public void Detector_ReportsTheModTheLauncherCannotWorkWith_BeforeACustomSkinLoader()
    {
        FabricJar("a-customskinloader.jar", "customskinloader");
        FabricJar("z-fabrictailor.jar", "fabrictailor");

        Assert.Equal("Fabric Tailor", SkinModDetector.Find(_game)!.Name);
    }

    // ===================== Which jar =====================

    private static ModVersion Version(string number, string[] games, string[] loaders, string type = "release", string? sha512 = "ab", string host = "https://cdn.modrinth.com/data/idMHQ4n2/versions/x/", string? file = null)
        => new(
            "id-" + number,
            number,
            number,
            games,
            loaders,
            new[] { new ModFile(host + (file ?? $"CustomSkinLoader-{number}.jar"), file ?? $"CustomSkinLoader-{number}.jar", "aa", sha512, 1000, true) },
            type);

    [Fact]
    public void Pick_TakesTheNewestReleaseForTheBuildsLoaderAndGameVersion()
    {
        var versions = new[]
        {
            Version("16.0-beta", new[] { "1.20.1" }, new[] { "fabric" }, type: "beta"),
            Version("15.0.1-Universal", new[] { "1.20.1", "1.21.1" }, new[] { "fabric", "forge", "neoforge", "quilt" }),
            Version("14.28-Fabric", new[] { "1.20.1" }, new[] { "fabric", "quilt" }),
            Version("14.28-ForgeV1", new[] { "1.12.2" }, new[] { "forge" })
        };

        Assert.Equal("15.0.1", InGameSkinMod.Pick(versions, "1.20.1", LoaderKind.Fabric)!.Version);
        Assert.Equal("CustomSkinLoader-15.0.1-Universal.jar", InGameSkinMod.Pick(versions, "1.21.1", LoaderKind.NeoForge)!.FileName);
        Assert.Equal("14.28", InGameSkinMod.Pick(versions, "1.12.2", LoaderKind.Forge)!.Version);

        // Nothing for this game version on this loader, and nothing at all without a loader.
        Assert.Null(InGameSkinMod.Pick(versions, "1.12.2", LoaderKind.Fabric));
        Assert.Null(InGameSkinMod.Pick(versions, "1.7.10", LoaderKind.Forge));
        Assert.Null(InGameSkinMod.Pick(versions, "1.20.1", LoaderKind.Vanilla));
    }

    [Fact]
    public void Pick_RefusesAFileWithoutAHash_FromAnotherHost_OrWithAPathForAName()
    {
        var games = new[] { "1.20.1" };
        var loaders = new[] { "fabric" };

        Assert.Null(InGameSkinMod.Pick(new[] { Version("1", games, loaders, sha512: null) }, "1.20.1", LoaderKind.Fabric));
        Assert.Null(InGameSkinMod.Pick(new[] { Version("1", games, loaders, host: "https://example.org/") }, "1.20.1", LoaderKind.Fabric));
        Assert.Null(InGameSkinMod.Pick(new[] { Version("1", games, loaders, file: "../../evil.jar") }, "1.20.1", LoaderKind.Fabric));
        Assert.Null(InGameSkinMod.Pick(new[] { Version("1", games, loaders, file: "readme.txt") }, "1.20.1", LoaderKind.Fabric));
    }

    [Fact]
    public void Pinned_CoversTheFourLoaders_AndOnlyTheGameVersionsItWasPublishedFor()
    {
        foreach (var loader in new[] { LoaderKind.Fabric, LoaderKind.Quilt, LoaderKind.Forge, LoaderKind.NeoForge })
        {
            Assert.Same(InGameSkinMod.Pinned, InGameSkinMod.PinnedFor("1.20.1", loader));
            Assert.Same(InGameSkinMod.Pinned, InGameSkinMod.PinnedFor("1.12.2", loader));
        }

        Assert.Null(InGameSkinMod.PinnedFor("1.20.1", LoaderKind.Vanilla));
        Assert.Null(InGameSkinMod.PinnedFor("1.7.10", LoaderKind.Forge));
        Assert.Null(InGameSkinMod.PinnedFor("24w14a", LoaderKind.Fabric));
        Assert.Null(InGameSkinMod.PinnedFor(null, LoaderKind.Fabric));

        Assert.Equal(128, InGameSkinMod.Pinned.Sha512.Length);
        Assert.StartsWith("https://cdn.modrinth.com/data/" + InGameSkinMod.ProjectId + "/", InGameSkinMod.Pinned.Url);
    }

    // ===================== The switch, with Modrinth as a stub =====================

    /// <summary>Answers the version list and serves the jar; counts what was asked for.</summary>
    private sealed class FakeModrinth : HttpMessageHandler
    {
        public byte[] JarBytes { get; } = MakeJar();

        public bool ApiDown { get; set; }

        public bool NothingForThisBuild { get; set; }

        public string? DeclaredSha512 { get; set; }

        public List<string> Requests { get; } = new();

        public int Downloads => Requests.Count(r => r.EndsWith(".jar", StringComparison.Ordinal));

        private static byte[] MakeJar()
        {
            using var buffer = new MemoryStream();

            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
                writer.Write("""{ "schemaVersion": 1, "id": "customskinloader-bootstrap", "version": "15.0.1" }""");
            }

            return buffer.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            if (url.EndsWith(".jar", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(JarBytes) });
            }

            if (ApiDown)
            {
                throw new HttpRequestException("no route to host");
            }

            var sha = DeclaredSha512 ?? Convert.ToHexString(SHA512.HashData(JarBytes)).ToLowerInvariant();

            var body = NothingForThisBuild
                ? "[]"
                : $$"""
                    [{
                      "id": "OLaesh5y", "project_id": "idMHQ4n2", "name": "15.0.1", "version_number": "15.0.1-Universal",
                      "version_type": "release", "game_versions": ["1.20.1"], "loaders": ["fabric", "forge", "neoforge", "quilt"],
                      "files": [{ "url": "https://cdn.modrinth.com/data/idMHQ4n2/versions/OLaesh5y/CustomSkinLoader_Universal-15.0.1.jar",
                                  "filename": "CustomSkinLoader_Universal-15.0.1.jar", "primary": true, "size": {{JarBytes.Length}},
                                  "hashes": { "sha1": "00", "sha512": "{{sha}}" } }]
                    }]
                    """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private const string JarName = "CustomSkinLoader_Universal-15.0.1.jar";

    private (InGameSkin Service, FakeModrinth Modrinth) Service()
    {
        var handler = new FakeModrinth();
        var http = new HttpClient(handler);
        return (new InGameSkin(new DownloadClient(http, maxAttempts: 1), new ModrinthClient(http)), handler);
    }

    private static Instance Build(LoaderKind loader = LoaderKind.Fabric, string version = "1.20.1")
        => new() { Id = "test", Name = "Test", Loader = loader, VersionId = version };

    private string ModPath(string fileName) => Path.Combine(_game, "mods", fileName);

    private string Skin(byte fill = 7)
    {
        var path = Path.Combine(_library, "skin.png");
        File.WriteAllBytes(path, Enumerable.Repeat(fill, 64).ToArray());
        return path;
    }

    private string DataPath(params string[] parts) => Path.Combine(new[] { _game, "CustomSkinLoader" }.Concat(parts).ToArray());

    [Fact]
    public void ANewBuild_IsOff_AndNothingIsDownloadedOrWrittenForIt()
    {
        var (_, modrinth) = Service();
        var instance = Build();

        Assert.Equal(InGameSkinState.Off, InGameSkin.Inspect(instance, _game).State);

        var refresh = InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false);

        Assert.Equal(InGameSkinState.Off, refresh.State);
        Assert.False(Directory.Exists(DataPath()));
        Assert.Empty(Directory.GetFiles(Path.Combine(_game, "mods")));
        Assert.Empty(modrinth.Requests);
    }

    [Fact]
    public async Task Enable_DownloadsTheJar_ChecksItsHash_AndRemembersItWasTheLauncher()
    {
        var (service, modrinth) = Service();
        var instance = Build();

        Assert.Equal(InGameSkinEnableResult.Installed, await service.EnableAsync(instance, _game));

        Assert.Equal(modrinth.JarBytes, File.ReadAllBytes(ModPath(JarName)));
        Assert.True(instance.SkinInGame!.Enabled);
        Assert.Equal(JarName, instance.SkinInGame.Jar);
        Assert.Equal("15.0.1", instance.SkinInGame.ModVersion);
        Assert.Equal(InGameSkinState.On, InGameSkin.Inspect(instance, _game).State);
        Assert.Contains(modrinth.Requests, r => r.Contains("/project/idMHQ4n2/version", StringComparison.Ordinal) && r.Contains("fabric", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enable_LeavesNothingBehind_WhenTheFileIsNotTheOneModrinthDescribed()
    {
        var (service, modrinth) = Service();
        modrinth.DeclaredSha512 = new string('0', 128);
        var instance = Build();

        await Assert.ThrowsAnyAsync<IOException>(() => service.EnableAsync(instance, _game));

        Assert.Null(instance.SkinInGame);
        Assert.Empty(Directory.GetFiles(Path.Combine(_game, "mods")));
    }

    [Fact]
    public async Task Enable_FallsBackToThePinnedRelease_OnlyWhenModrinthCannotBeAsked()
    {
        var (service, modrinth) = Service();
        modrinth.ApiDown = true;
        var instance = Build();

        // The pinned file's real hash is not the stub's, so the download is refused: what
        // matters here is that the pinned address was the one asked for.
        await Assert.ThrowsAnyAsync<IOException>(() => service.EnableAsync(instance, _game));
        Assert.Contains(InGameSkinMod.Pinned.Url, modrinth.Requests);

        modrinth.Requests.Clear();
        modrinth.ApiDown = false;
        modrinth.NothingForThisBuild = true;

        Assert.Equal(InGameSkinEnableResult.NoVersion, await service.EnableAsync(instance, _game));
        Assert.Equal(0, modrinth.Downloads);
        Assert.Null(instance.SkinInGame);
    }

    [Fact]
    public async Task Enable_DoesNothingForAVanillaBuild_OrBesideAnotherSkinMod()
    {
        var (service, modrinth) = Service();

        var vanilla = Build(LoaderKind.Vanilla);
        Assert.Equal(InGameSkinState.NoLoader, InGameSkin.Inspect(vanilla, _game).State);
        Assert.Equal(InGameSkinEnableResult.NoLoader, await service.EnableAsync(vanilla, _game));

        FabricJar("fabrictailor-2.5.jar", "fabrictailor");
        var modded = Build();
        var status = InGameSkin.Inspect(modded, _game);

        Assert.Equal(InGameSkinState.OtherSkinMod, status.State);
        Assert.Equal("Fabric Tailor", status.Mod!.Name);
        Assert.Equal("fabrictailor-2.5.jar", status.Mod.FileName);
        Assert.Equal(InGameSkinEnableResult.OtherSkinMod, await service.EnableAsync(modded, _game));

        Assert.Empty(modrinth.Requests);
        Assert.Null(modded.SkinInGame);
        Assert.Single(Directory.GetFiles(Path.Combine(_game, "mods")));
    }

    [Fact]
    public async Task Enable_UsesTheCustomSkinLoaderTheBuildAlreadyHas_AndNeverTouchesThatFile()
    {
        var (service, modrinth) = Service();
        var own = FabricJar("CustomSkinLoader_Fabric-14.28.jar", "customskinloader");
        var instance = Build();

        Assert.Equal(InGameSkinEnableResult.UsingOwnMod, await service.EnableAsync(instance, _game));
        Assert.Empty(modrinth.Requests);
        Assert.Null(instance.SkinInGame!.Jar);
        Assert.Equal(InGameSkinState.OnWithOwnMod, InGameSkin.Inspect(instance, _game).State);

        // The skin still goes in: the mod is there, whoever installed it.
        Assert.Equal("Player.png", InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false).SkinFile);

        // Off again: the player's own jar stays switched on.
        Assert.Null(InGameSkin.Disable(instance, _game));
        Assert.False(instance.SkinInGame.Enabled);
        Assert.True(File.Exists(own));
    }

    [Fact]
    public async Task Disable_RenamesOnlyTheLaunchersJar_AndLeavesTheConfigAndTheSkin()
    {
        var (service, _) = Service();
        var instance = Build();
        await service.EnableAsync(instance, _game);
        InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false);
        FabricJar("sodium.jar", "sodium");

        Assert.Equal(JarName, InGameSkin.Disable(instance, _game));

        Assert.False(File.Exists(ModPath(JarName)));
        Assert.True(File.Exists(ModPath(JarName + ".disabled")));
        Assert.True(File.Exists(ModPath("sodium.jar")));
        Assert.True(File.Exists(DataPath("ExtraList", "STlauncher.json")));
        Assert.True(File.Exists(DataPath("STlauncher", "skins", "Player.png")));
        Assert.Equal(InGameSkinState.Off, InGameSkin.Inspect(instance, _game).State);
    }

    [Fact]
    public async Task TurningItOnAgain_BringsTheSameFileBack_WithoutADownload()
    {
        var (service, modrinth) = Service();
        var instance = Build();
        await service.EnableAsync(instance, _game);
        InGameSkin.Disable(instance, _game);
        var downloads = modrinth.Downloads;

        Assert.Equal(InGameSkinEnableResult.SwitchedBackOn, await service.EnableAsync(instance, _game));

        Assert.Equal(downloads, modrinth.Downloads);
        Assert.True(File.Exists(ModPath(JarName)));
        Assert.False(File.Exists(ModPath(JarName + ".disabled")));
        Assert.Equal(InGameSkinState.On, InGameSkin.Inspect(instance, _game).State);
    }

    // ===================== "Do not bring it back" =====================

    [Fact]
    public async Task AJarThePlayerDeleted_StaysDeleted_ThroughAnyNumberOfLaunches()
    {
        var (service, modrinth) = Service();
        var instance = Build();
        await service.EnableAsync(instance, _game);
        var downloads = modrinth.Downloads;

        File.Delete(ModPath(JarName));

        for (var launch = 0; launch < 3; launch++)
        {
            var refresh = InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false);
            Assert.Equal(InGameSkinState.ModRemoved, refresh.State);
            Assert.False(refresh.RecordChanged);
        }

        Assert.Equal(downloads, modrinth.Downloads);
        Assert.Empty(Directory.GetFiles(Path.Combine(_game, "mods")));
        Assert.False(Directory.Exists(DataPath()));
        Assert.Equal(InGameSkinState.ModRemoved, InGameSkin.Inspect(instance, _game).State);

        // The switch, flipped again, is the player asking again.
        Assert.Equal(InGameSkinEnableResult.Installed, await service.EnableAsync(instance, _game));
        Assert.True(File.Exists(ModPath(JarName)));
    }

    [Fact]
    public async Task AJarThePlayerSwitchedOff_IsNotSwitchedBackOn_ByALaunch()
    {
        var (service, _) = Service();
        var instance = Build();
        await service.EnableAsync(instance, _game);

        // What the mod list's own switch does.
        File.Move(ModPath(JarName), ModPath(JarName + ".disabled"));

        Assert.Equal(InGameSkinState.ModRemoved, InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false).State);
        Assert.False(File.Exists(ModPath(JarName)));
        Assert.True(File.Exists(ModPath(JarName + ".disabled")));
    }

    // ===================== Before a launch =====================

    [Fact]
    public async Task Refresh_BeforeTheModHasEverRun_LeavesTheEntryForTheModToAddItself()
    {
        var (service, _) = Service();
        var instance = Build();
        await service.EnableAsync(instance, _game);

        var refresh = InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: true);

        Assert.Equal("Player.png", refresh.SkinFile);
        Assert.True(refresh.RecordChanged);
        Assert.Equal(File.ReadAllBytes(Skin()), File.ReadAllBytes(DataPath("STlauncher", "skins", "Player.png")));

        // No config written on the mod's behalf: it would cost the player the mod's own default list.
        Assert.False(File.Exists(DataPath("CustomSkinLoader.json")));

        var extra = JsonNode.Parse(File.ReadAllText(DataPath("ExtraList", "STlauncher.json")))!;
        Assert.Equal("Legacy", extra["type"]!.GetValue<string>());
        Assert.Equal("STlauncher/skins/{USERNAME}.png", extra["skin"]!.GetValue<string>());
        Assert.Equal("slim", extra["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Refresh_WithTheModsConfigInPlace_PutsTheEntryFirst_AndFollowsTheModel()
    {
        var (service, _) = Service();
        var instance = Build();
        await service.EnableAsync(instance, _game);
        Directory.CreateDirectory(DataPath());
        File.WriteAllText(DataPath("CustomSkinLoader.json"), DefaultConfig);

        InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false);

        var text = File.ReadAllText(DataPath("CustomSkinLoader.json"));
        Assert.Equal(new[] { "STlauncher", "Mojang", "ElyBy", "LocalSkin", "Сервер друга" }, Names(text));
        Assert.False(File.Exists(DataPath("ExtraList", "STlauncher.json")));

        // A second launch with nothing changed writes nothing.
        var configStamp = File.GetLastWriteTimeUtc(DataPath("CustomSkinLoader.json"));
        var skinStamp = File.GetLastWriteTimeUtc(DataPath("STlauncher", "skins", "Player.png"));
        Thread.Sleep(30);
        Assert.False(InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false).RecordChanged);
        Assert.Equal(configStamp, File.GetLastWriteTimeUtc(DataPath("CustomSkinLoader.json")));
        Assert.Equal(skinStamp, File.GetLastWriteTimeUtc(DataPath("STlauncher", "skins", "Player.png")));

        // The skin was redrawn for thin arms.
        InGameSkin.Refresh(instance, _game, "Player", Skin(fill: 9), slim: true);
        Assert.Equal("slim", JsonNode.Parse(File.ReadAllText(DataPath("CustomSkinLoader.json")))!["loadlist"]![0]!["model"]!.GetValue<string>());
        Assert.Equal(9, File.ReadAllBytes(DataPath("STlauncher", "skins", "Player.png"))[0]);
    }

    [Fact]
    public async Task Refresh_WithADamagedConfig_DoesNotTouchIt()
    {
        var (service, _) = Service();
        var instance = Build();
        await service.EnableAsync(instance, _game);
        Directory.CreateDirectory(DataPath());
        File.WriteAllText(DataPath("CustomSkinLoader.json"), "{ half a file");

        InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false);

        Assert.Equal("{ half a file", File.ReadAllText(DataPath("CustomSkinLoader.json")));
        Assert.True(File.Exists(DataPath("ExtraList", "STlauncher.json")));
    }

    [Fact]
    public async Task Refresh_TakesTheCopyAway_WhenNothingIsWorn_OrTheNicknameChanged()
    {
        var (service, _) = Service();
        var instance = Build();
        await service.EnableAsync(instance, _game);
        InGameSkin.Refresh(instance, _game, "Player", Skin(), slim: false);

        // A file the player keeps beside it is theirs.
        File.WriteAllText(DataPath("STlauncher", "skins", "Friend.png"), "friend");

        var renamed = InGameSkin.Refresh(instance, _game, "Other_Nick", Skin(), slim: false);
        Assert.Equal("Other_Nick.png", renamed.SkinFile);
        Assert.True(renamed.RecordChanged);
        Assert.False(File.Exists(DataPath("STlauncher", "skins", "Player.png")));
        Assert.True(File.Exists(DataPath("STlauncher", "skins", "Other_Nick.png")));

        var bare = InGameSkin.Refresh(instance, _game, "Other_Nick", skinPath: null, slim: false);
        Assert.Null(bare.SkinFile);
        Assert.True(bare.RecordChanged);
        Assert.False(File.Exists(DataPath("STlauncher", "skins", "Other_Nick.png")));
        Assert.True(File.Exists(DataPath("STlauncher", "skins", "Friend.png")));
        Assert.Null(instance.SkinInGame!.SkinFile);
    }

    [Fact]
    public void TheBuildsRecord_SurvivesItsFile_AndAnOldBuildReadsAsOff()
    {
        var instance = Build();
        instance.SkinInGame = new InGameSkinSettings { Enabled = true, Jar = JarName, ModVersion = "15.0.1", SkinFile = "Player.png" };

        var json = System.Text.Json.JsonSerializer.Serialize(instance);
        var back = System.Text.Json.JsonSerializer.Deserialize<Instance>(json)!;

        Assert.True(back.SkinInGame!.Enabled);
        Assert.Equal(JarName, back.SkinInGame.Jar);
        Assert.Equal("Player.png", back.SkinInGame.SkinFile);

        Assert.DoesNotContain("skinInGame", System.Text.Json.JsonSerializer.Serialize(Build()));
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<Instance>("""{ "id": "old", "name": "Old" }""")!.SkinInGame);
    }
}
