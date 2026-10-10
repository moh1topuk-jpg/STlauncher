using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Boost;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// "Ускорение" is only worth having if "off" means "as it was". These tests hold it to
/// that: options.txt comes back byte for byte, a key the player changed stays changed,
/// a jar the player replaced is not touched, and no slot is filled twice.
/// </summary>
public class BoostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-boost-" + Guid.NewGuid().ToString("N"));

    public BoostTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception)
        {
        }
    }

    // ===================== options.txt =====================

    /// <summary>
    /// A file as a 1.7.10-era game left it on a Russian Windows: code page 1251 (not
    /// valid UTF-8), CRLF, a line with no colon and a key nobody here knows.
    /// </summary>
    private static byte[] OldFile()
    {
        var bytes = new List<byte>();

        void Line(string ascii) => bytes.AddRange(Encoding.ASCII.GetBytes(ascii + "\r\n"));

        Line("renderDistance:12");
        Line("particles:0");
        bytes.AddRange(Encoding.ASCII.GetBytes("resourcePacks:[\""));
        bytes.AddRange(new byte[] { 0xCF, 0xE0, 0xEA }); // "Пак" in Windows-1251
        bytes.AddRange(Encoding.ASCII.GetBytes(".zip\"]\r\n"));
        Line("a line without a colon");
        Line("ao:2");
        Line("clouds:true");
        Line("enableVsync:true");
        Line("maxFps:260");
        Line("someModKey:whatever:with:colons");
        Line("mipmapLevels:4");

        return bytes.ToArray();
    }

    [Fact]
    public void Options_OnThenOff_IsTheSameBytes_ForANonUtf8File()
    {
        var original = OldFile();
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(original));

        var file = OptionsFile.Parse(original)!;
        var records = BoostOptions.Apply(file, BoostOptions.Plan(file, "1.7.10"));

        Assert.NotEmpty(records);
        Assert.NotEqual(original, file.ToBytes());

        var reread = OptionsFile.Parse(file.ToBytes())!;
        var revert = BoostOptions.Revert(reread, records);

        Assert.Empty(revert.LeftToPlayer);
        Assert.Equal(original, reread.ToBytes());
    }

    [Fact]
    public void Options_OnlyTheChangedLinesChange()
    {
        var original = OldFile();
        var file = OptionsFile.Parse(original)!;
        var changes = BoostOptions.Plan(file, "1.7.10");
        BoostOptions.Apply(file, changes);

        var before = Split(original);
        var after = Split(file.ToBytes());

        // Nothing was absent in this file, so no line was added and the order is kept.
        Assert.Equal(before.Count, after.Count);

        var keys = changes.Select(c => c.Key).ToHashSet();

        for (var i = 0; i < before.Count; i++)
        {
            var key = Encoding.Latin1.GetString(before[i]).Split(':')[0];

            if (!keys.Contains(key))
            {
                Assert.Equal(before[i], after[i]);
            }
        }

        Assert.Equal(new[] { "renderDistance", "particles", "ao", "mipmapLevels", "enableVsync", "maxFps" }.OrderBy(k => k), keys.OrderBy(k => k));
    }

    [Fact]
    public void Options_OldGame_GetsNoKeyItDoesNotKnow_AndItsCloudsAreLeft()
    {
        var file = OptionsFile.Parse(OldFile())!;
        var changes = BoostOptions.Plan(file, "1.7.10");

        Assert.DoesNotContain(changes, c => c.Key is "simulationDistance" or "entityDistanceScaling" or "biomeBlendRadius" or "entityShadows" or "clouds" or "cloudStatus");
        Assert.Equal("1", changes.Single(c => c.Key == "ao").Value);
    }

    [Fact]
    public void Options_AbsentKeys_AreWrittenForAGameThatReadsThem_AndRemovedAgain()
    {
        var original = Encoding.UTF8.GetBytes("version:3465\nrenderDistance:16\nlang:ru_ru");
        var file = OptionsFile.Parse(original)!;
        var changes = BoostOptions.Plan(file, "1.20.1");

        Assert.Null(changes.Single(c => c.Key == "simulationDistance").Previous);
        Assert.Null(changes.Single(c => c.Key == "enableVsync").Previous);
        Assert.Equal("16", changes.Single(c => c.Key == "renderDistance").Previous);

        var records = BoostOptions.Apply(file, changes);
        var text = Encoding.UTF8.GetString(file.ToBytes());

        // The last line had no ending; it gets one before anything is added after it.
        Assert.Contains("lang:ru_ru\nsimulationDistance:6\n", text);
        Assert.StartsWith("version:3465\nrenderDistance:8\n", text);

        BoostOptions.Revert(file, records);
        var restored = Encoding.UTF8.GetString(file.ToBytes());

        Assert.DoesNotContain("simulationDistance", restored);
        Assert.DoesNotContain("enableVsync", restored);

        // Only the line ending the file never had differs from the original.
        Assert.Equal("version:3465\nrenderDistance:16\nlang:ru_ru\n", restored);
    }

    [Fact]
    public void Options_KeyChangedByHand_StaysThePlayers()
    {
        var file = OptionsFile.Parse(Encoding.UTF8.GetBytes("renderDistance:16\nparticles:0\nentityShadows:true\n"))!;
        var records = BoostOptions.Apply(file, BoostOptions.Plan(file, "1.20.1"));

        // The player raised the distance in the game's menu and switched shadows back on;
        // the game then wrote its own file, dropping a key it had no use for.
        file.Set("renderDistance", "10");
        file.Set("entityShadows", "true");
        file.Remove("biomeBlendRadius");

        var revert = BoostOptions.Revert(file, records);

        Assert.Equal("10", file.Get("renderDistance"));
        Assert.Equal("true", file.Get("entityShadows"));
        Assert.Null(file.Get("biomeBlendRadius"));
        Assert.Equal("0", file.Get("particles"));
        Assert.Contains("renderDistance", revert.LeftToPlayer);
        Assert.Contains("entityShadows", revert.LeftToPlayer);
        Assert.Contains("biomeBlendRadius", revert.LeftToPlayer);
        Assert.Contains("particles", revert.Restored);

        // A key that had not existed and still says what was written is taken out again.
        Assert.Null(file.Get("simulationDistance"));
    }

    [Fact]
    public void Options_NeverRaiseAnything()
    {
        var file = OptionsFile.Parse(Encoding.UTF8.GetBytes(
            "renderDistance:4\nsimulationDistance:5\nentityDistanceScaling:0.5\nparticles:2\ncloudStatus:\"off\"\n" +
            "entityShadows:false\nao:false\nmipmapLevels:0\nbiomeBlendRadius:0\nenableVsync:false\nmaxFps:60\n"))!;

        Assert.Empty(BoostOptions.Plan(file, "1.21.1", monitorHz: 60));
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData(60, 120)]
    [InlineData(120, 120)]
    [InlineData(144, 150)]
    [InlineData(165, 170)]
    [InlineData(360, 250)]
    public void Options_FrameCap_FollowsTheMonitor_AndIsNeverUnlimited(int? monitorHz, int cap)
    {
        Assert.Equal(cap, BoostOptions.FpsCap(monitorHz));

        var file = OptionsFile.Parse(Encoding.UTF8.GetBytes("enableVsync:true\nmaxFps:260\n"))!;
        var changes = BoostOptions.Plan(file, "1.20.1", monitorHz);

        Assert.Equal("false", changes.Single(c => c.Key == "enableVsync").Value);
        Assert.Equal(cap.ToString(), changes.Single(c => c.Key == "maxFps").Value);
    }

    [Fact]
    public void Options_Clouds_KeepTheQuotingOfTheFile()
    {
        var quoted = OptionsFile.Parse(Encoding.UTF8.GetBytes("cloudStatus:\"fancy\"\n"))!;
        var bare = OptionsFile.Parse(Encoding.UTF8.GetBytes("renderClouds:true\n"))!;
        var fast = OptionsFile.Parse(Encoding.UTF8.GetBytes("renderClouds:fast\n"))!;

        Assert.Equal("\"fast\"", BoostOptions.Plan(quoted, null).Single().Value);
        Assert.Equal("fast", BoostOptions.Plan(bare, null).Single().Value);
        Assert.Empty(BoostOptions.Plan(fast, null));
    }

    [Fact]
    public void Options_UnknownGameVersion_OnlyTouchesWhatTheFileHas()
    {
        var file = OptionsFile.Parse(Encoding.UTF8.GetBytes("renderDistance:12\n"))!;

        Assert.Equal(new[] { "renderDistance" }, BoostOptions.Plan(file, "24w14a").Select(c => c.Key));
        Assert.Equal(new[] { "renderDistance" }, BoostOptions.Plan(file, null).Select(c => c.Key));
    }

    [Fact]
    public void Options_FileThatIsNotText_IsNotReadAtAll()
    {
        Assert.Null(OptionsFile.Parse(Encoding.Unicode.GetBytes("renderDistance:12\r\n")));
        Assert.Null(OptionsFile.Parse(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 }));

        var path = Path.Combine(_root, "options.txt");
        var binary = Encoding.Unicode.GetBytes("renderDistance:12\r\n");
        File.WriteAllBytes(path, binary);

        Assert.Null(OptionsFile.TryLoad(path));
        Assert.Equal(binary, File.ReadAllBytes(path));
    }

    [Fact]
    public void Options_BomAndMissingFile()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("renderDistance:12\n")).ToArray();
        var file = OptionsFile.Parse(withBom)!;

        Assert.Equal("12", file.Get("renderDistance"));
        Assert.Equal(withBom, file.ToBytes());

        var missing = OptionsFile.TryLoad(Path.Combine(_root, "nowhere", "options.txt"))!;
        Assert.Null(missing.Get("renderDistance"));
        Assert.Empty(missing.ToBytes());
    }

    [Fact]
    public void Options_SaveAndLoad_OnDisk()
    {
        var path = Path.Combine(_root, "options.txt");
        File.WriteAllBytes(path, OldFile());

        var file = OptionsFile.TryLoad(path)!;
        var records = BoostOptions.Apply(file, BoostOptions.Plan(file, "1.7.10"));
        file.Save(path);

        var again = OptionsFile.TryLoad(path)!;
        Assert.Equal("8", again.Get("renderDistance"));

        BoostOptions.Revert(again, records);
        again.Save(path);

        Assert.Equal(OldFile(), File.ReadAllBytes(path));
    }

    private static List<byte[]> Split(byte[] bytes)
    {
        var lines = new List<byte[]>();
        var start = 0;

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lines.Add(bytes[start..(i + 1)]);
                start = i + 1;
            }
        }

        return lines;
    }

    // ===================== Slots =====================

    private const string Game = "1.20.1";

    private sealed class Modrinth : IModSource
    {
        private readonly Dictionary<string, ModProject> _projects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ModVersion>> _versions = new(StringComparer.OrdinalIgnoreCase);

        public ModSource Source => ModSource.Modrinth;

        public List<string> Asked { get; } = new();

        public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string slug, string loader = "fabric", string channel = "release", string number = "1.0", params string[] requires)
        {
            var id = "P-" + slug;

            if (!_projects.ContainsKey(id))
            {
                var project = new ModProject(id, slug, slug, string.Empty, null, null, 0, Array.Empty<string>());
                _projects[id] = project;
                _projects[slug] = project;
                _versions[id] = new List<ModVersion>();
            }

            _versions[id].Add(new ModVersion(
                $"V-{slug}-{number}",
                slug,
                number,
                new[] { Game },
                new[] { loader },
                new[] { new ModFile($"https://cdn.test/{slug}-{number}.jar", $"{slug}-{loader}-{number}.jar", null, null, 1000, true) },
                channel)
            {
                ProjectId = id,
                Dependencies = requires.Select(r => new ModDependency("P-" + r, null, "required")).ToList()
            });
        }

        public void AddSet(string loader, params string[] slugs)
        {
            foreach (var slug in slugs)
            {
                Add(slug, loader);
            }
        }

        public Task<ModProject?> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
            => Task.FromResult(_projects.TryGetValue(idOrSlug, out var project) ? project : null);

        public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectIdOrSlug, string? gameVersion, LoaderKind loader, CancellationToken cancellationToken = default)
        {
            Asked.Add(projectIdOrSlug);

            if (Failing.Contains(projectIdOrSlug))
            {
                throw new InvalidOperationException("the site did not answer");
            }

            var name = ModrinthClient.ToModrinthLoader(loader);
            var id = _projects.TryGetValue(projectIdOrSlug, out var project) ? project.Id : projectIdOrSlug;

            return Task.FromResult<IReadOnlyList<ModVersion>>(
                _versions.TryGetValue(id, out var versions)
                    ? versions.Where(v => name is null || v.Loaders.Contains(name)).ToList()
                    : new List<ModVersion>());
        }

        public Task<ModSearchPage> SearchAsync(string query, string? gameVersion, LoaderKind loader, string? category = null, string sort = "relevance", int limit = 20, int offset = 0, CancellationToken cancellationToken = default, string projectType = ProjectTypes.Mod)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(string projectType = ProjectTypes.Mod, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModCategory>>(Array.Empty<ModCategory>());
    }

    private static readonly string[] FabricSet = { "sodium", "lithium", "ferrite-core", "entityculling", "immediatelyfast", "modernfix", "dynamic-fps", "moreculling" };

    private static Task<BoostModPlan> Plan(
        Modrinth source,
        LoaderKind loader,
        IEnumerable<BuildJar>? jars = null,
        IEnumerable<InstalledModRecord>? records = null,
        IEnumerable<BoostJarRecord>? parked = null)
        => BoostMods.PlanAsync(
            source,
            new ModInstallResolver(new IModSource[] { source }),
            (records ?? Array.Empty<InstalledModRecord>()).ToList(),
            (jars ?? Array.Empty<BuildJar>()).ToList(),
            (parked ?? Array.Empty<BoostJarRecord>()).ToList(),
            loader,
            Game);

    private static BuildJar Jar(string fileName, string? id = null, LoaderKind loader = LoaderKind.Fabric, bool enabled = true, params string[] requires)
        => new(fileName, enabled, id is null
            ? Array.Empty<ModMetadata>()
            : new[]
            {
                new ModMetadata(fileName, id, id, "1.0", loader, requires.Select(r => new ModDependency2(r, null, true)).ToList(), null, Array.Empty<string>())
            });

    private static BoostModState StateOf(BoostModPlan plan, string title) => plan.Items.Single(i => i.Slot.Title == title).State;

    [Fact]
    public void Slots_FollowTheLoader()
    {
        string[] Of(LoaderKind loader, string version) => BoostMods.SlotsFor(loader, version).Select(s => s.Slugs[0]).ToArray();

        Assert.Equal(FabricSet, Of(LoaderKind.Fabric, Game));
        Assert.Equal(FabricSet, Of(LoaderKind.Quilt, Game));
        Assert.Equal(new[] { "embeddium", "ferrite-core", "entityculling", "immediatelyfast", "modernfix", "dynamic-fps" }, Of(LoaderKind.Forge, "1.16.5"));
        Assert.Equal(new[] { "sodium", "ferrite-core", "entityculling", "immediatelyfast", "modernfix", "dynamic-fps" }, Of(LoaderKind.NeoForge, "1.21.1"));
        Assert.Equal(new[] { "sodium", "embeddium" }, BoostMods.SlotsFor(LoaderKind.NeoForge, "1.21.1")[0].Slugs);

        Assert.Empty(Of(LoaderKind.Forge, "1.12.2"));
        Assert.Empty(Of(LoaderKind.Forge, "1.7.10"));
        Assert.Empty(Of(LoaderKind.Vanilla, Game));
    }

    [Theory]
    [InlineData("sodium-fabric-0.5.8+mc1.20.4.jar", "sodium", true)]
    [InlineData("sodium-fabric-0.5.8+mc1.20.4.jar.disabled", "sodium", true)]
    [InlineData("sodium.jar", "sodium", true)]
    [InlineData("sodium-extra-0.5.4+mc1.20.4.jar", "sodium", false)]
    [InlineData("reeses-sodium-options-1.7.jar", "sodium", false)]
    [InlineData("ferritecore-6.0.1-fabric.jar", "ferrite-core", true)]
    [InlineData("dynamic-fps-3.4.2+minecraft-1.20.jar", "dynamic-fps", true)]
    [InlineData("ImmediatelyFast-Fabric-1.2.8+1.20.4.jar", "immediatelyfast", true)]
    [InlineData("embeddium-0.3.31+mc1.20.1.jar", "embeddium", true)]
    [InlineData("embeddiumplus-1.2.jar", "embeddium", false)]
    [InlineData("rubidium-mc1.19.2-0.6.2c.jar", "rubidium", true)]
    [InlineData("OptiFine_1.20.1_HD_U_I6.jar", "optifine", true)]
    [InlineData("preview_OptiFine_1.21_HD_U_J1_pre9.jar", "preview-optifine", true)]
    [InlineData("lithium-fabric-mc1.20.1-0.11.2.jar", "lithium", true)]
    public void Slots_FileNameSaysWhichModItIs(string fileName, string slug, bool expected)
        => Assert.Equal(expected, BoostMods.IsNamedAfter(fileName, slug));

    [Fact]
    public async Task Slots_EmptyBuild_TakesTheWholeSet_AndADependencyOnce()
    {
        var source = new Modrinth();
        source.AddSet("fabric", "lithium", "ferrite-core", "entityculling", "immediatelyfast", "modernfix", "fabric-api");
        source.Add("sodium", requires: "fabric-api");
        source.Add("dynamic-fps", requires: "fabric-api");
        source.Add("moreculling", requires: "fabric-api");

        var plan = await Plan(source, LoaderKind.Fabric);

        Assert.All(plan.Items, i => Assert.Equal(BoostModState.Install, i.State));
        Assert.Equal(9, plan.Downloads.Count);
        Assert.Single(plan.Downloads, d => d.Slug == "fabric-api");
        Assert.Equal(9000, plan.TotalBytes);
        Assert.True(plan.ChangesAnything);

        // What a mod requires stands before it.
        var names = plan.Downloads.Select(d => d.Slug).ToList();
        Assert.True(names.IndexOf("fabric-api") < names.IndexOf("sodium"));
    }

    [Theory]
    [InlineData("embeddium-0.3.31+mc1.20.1.jar", null)]
    [InlineData("whatever.jar", "rubidium")]
    [InlineData("sodium-fabric-0.5.jar.disabled", null)]
    public async Task Slots_RendererIsFilledByAnyOfTheThree(string fileName, string? id)
    {
        var source = new Modrinth();
        source.AddSet("fabric", FabricSet);

        var plan = await Plan(source, LoaderKind.Fabric, new[] { Jar(fileName, id, enabled: !fileName.EndsWith(".disabled")) });

        var renderer = plan.Items.Single(i => i.Slot.Renderer);
        Assert.Equal(BoostModState.Present, renderer.State);
        Assert.Equal(fileName, renderer.FileName);
        Assert.DoesNotContain(plan.Downloads, d => d.Slug is "sodium" or "embeddium");
        Assert.Equal(7, plan.Downloads.Count);
    }

    [Fact]
    public async Task Slots_FilledByRecord_ByJarId_ByFileName()
    {
        var source = new Modrinth();
        source.AddSet("fabric", FabricSet);

        var plan = await Plan(
            source,
            LoaderKind.Fabric,
            jars: new[] { Jar("fc.jar", "ferritecore"), Jar("entityculling-fabric-1.6.jar") },
            records: new[]
            {
                new InstalledModRecord { FileName = "l.jar", Source = ModSource.Modrinth, Id = "lithium" },
                new InstalledModRecord { FileName = "mf.jar", Source = ModSource.CurseForge, Id = "some-other-name", ProjectId = "1" },
                new InstalledModRecord { FileName = "dfps.jar", Source = ModSource.Modrinth, Id = "P-dynamic-fps" }
            });

        Assert.Equal(BoostModState.Present, StateOf(plan, "Lithium"));
        Assert.Equal(BoostModState.Present, StateOf(plan, "FerriteCore"));
        Assert.Equal(BoostModState.Present, StateOf(plan, "Entity Culling"));
        Assert.Equal(BoostModState.Present, StateOf(plan, "Dynamic FPS"));
        Assert.Equal(BoostModState.Install, StateOf(plan, "ModernFix"));
        Assert.Equal(BoostModState.Install, StateOf(plan, "Sodium"));
    }

    [Theory]
    [InlineData("OptiFine_1.20.1_HD_U_I6.jar", null)]
    [InlineData("of.jar", "optifabric")]
    public async Task Slots_WithOptiFine_SkipTheRendererAndWhatBreaksWithIt(string fileName, string? id)
    {
        var source = new Modrinth();
        source.AddSet("fabric", FabricSet);

        var plan = await Plan(source, LoaderKind.Fabric, new[] { Jar(fileName, id) });

        Assert.True(plan.HasOptiFine);
        Assert.Equal(BoostModState.SkippedForOptiFine, StateOf(plan, "Sodium"));
        Assert.Equal(BoostModState.SkippedForOptiFine, StateOf(plan, "ImmediatelyFast"));
        Assert.Equal(BoostModState.SkippedForOptiFine, StateOf(plan, "More Culling"));
        Assert.Equal(new[] { "lithium", "ferrite-core", "entityculling", "modernfix", "dynamic-fps" }, plan.Downloads.Select(d => d.Slug));
        Assert.DoesNotContain("P-sodium", source.Asked);
    }

    [Fact]
    public async Task Slots_ReleaseThenBeta_NeverAlpha()
    {
        var source = new Modrinth();
        source.Add("sodium", channel: "alpha", number: "3.0");
        source.Add("sodium", channel: "beta", number: "2.0");
        source.Add("sodium", channel: "release", number: "1.0");
        source.Add("lithium", channel: "alpha", number: "3.0");
        source.Add("lithium", channel: "beta", number: "2.0");
        source.Add("modernfix", channel: "alpha", number: "3.0");

        var plan = await Plan(source, LoaderKind.Fabric);

        Assert.Equal("1.0", plan.Items.Single(i => i.Slot.Title == "Sodium").Plan!.Root.Version!.VersionNumber);
        Assert.Equal("2.0", plan.Items.Single(i => i.Slot.Title == "Lithium").Plan!.Root.Version!.VersionNumber);
        Assert.Equal(BoostModState.Unavailable, StateOf(plan, "ModernFix"));

        // Not on Modrinth at all, for this build: not offered either.
        Assert.Equal(BoostModState.Unavailable, StateOf(plan, "FerriteCore"));
    }

    [Fact]
    public async Task Slots_NeoForge_TakesEmbeddiumWhenSodiumHasNoFile()
    {
        var source = new Modrinth();
        source.Add("sodium", loader: "fabric");
        source.Add("embeddium", loader: "neoforge");

        var plan = await Plan(source, LoaderKind.NeoForge);

        Assert.Equal("embeddium", plan.Items.Single(i => i.Slot.Renderer).Plan!.Root.Slug);

        source.Add("sodium", loader: "neoforge");
        plan = await Plan(source, LoaderKind.NeoForge);

        Assert.Equal("sodium", plan.Items.Single(i => i.Slot.Renderer).Plan!.Root.Slug);
        Assert.DoesNotContain(plan.Downloads, d => d.Slug == "embeddium");
    }

    [Fact]
    public async Task Slots_Quilt_TakesTheFabricFile()
    {
        var source = new Modrinth();
        source.Add("sodium", loader: "fabric");
        source.Add("lithium", loader: "quilt");

        var plan = await Plan(source, LoaderKind.Quilt);

        Assert.Equal(BoostModState.Install, StateOf(plan, "Sodium"));
        Assert.Equal("lithium-quilt-1.0.jar", plan.Items.Single(i => i.Slot.Title == "Lithium").Plan!.Root.File!.FileName);
    }

    [Fact]
    public async Task Slots_ModWhoseDependencyCannotBeHad_IsNotInstalledHalf()
    {
        var source = new Modrinth();
        source.Add("sodium", requires: "fabric-api");
        source.Add("fabric-api", loader: "forge");
        source.Add("lithium");
        source.Failing.Add("P-lithium");

        var plan = await Plan(source, LoaderKind.Fabric);

        Assert.Equal(BoostModState.Unavailable, StateOf(plan, "Sodium"));
        Assert.Equal(BoostModState.Unavailable, StateOf(plan, "Lithium"));
        Assert.Empty(plan.Downloads);
        Assert.False(plan.ChangesAnything);
    }

    [Fact]
    public async Task Slots_ParkedJars_ComeBackOn_InsteadOfADownload()
    {
        var source = new Modrinth();
        source.AddSet("fabric", FabricSet);

        var parked = new[]
        {
            new BoostJarRecord { FileName = "sodium-fabric-1.0.jar", Slug = "sodium", Title = "Sodium", Sha1 = "a" },
            new BoostJarRecord { FileName = "fabric-api-1.0.jar", Slug = "fabric-api", Title = "Fabric API", Sha1 = "b", Dependency = true },
            new BoostJarRecord { FileName = "cloth-1.0.jar", Slug = "cloth-config", Title = "Cloth", Sha1 = "c", Dependency = true }
        };

        var jars = new[]
        {
            Jar("sodium-fabric-1.0.jar.disabled", "sodium", enabled: false),
            Jar("fabric-api-1.0.jar.disabled", "fabric-api", enabled: false),
            Jar("cloth-1.0.jar.disabled", "cloth-config", enabled: false),

            // The player has added a newer Cloth Config since: the parked one stays off.
            Jar("cloth-config-2.0.jar", "cloth-config")
        };

        var records = new[] { new InstalledModRecord { FileName = "sodium-fabric-1.0.jar.disabled", Source = ModSource.Modrinth, Id = "sodium" } };

        var plan = await Plan(source, LoaderKind.Fabric, jars, records, parked);

        Assert.Equal(BoostModState.Reenable, StateOf(plan, "Sodium"));
        Assert.Equal(new[] { "sodium-fabric-1.0.jar", "fabric-api-1.0.jar" }, plan.Reenable.Select(r => r.FileName));
        Assert.DoesNotContain(plan.Downloads, d => d.Slug == "sodium");
        Assert.Equal(7, plan.Downloads.Count);
    }

    [Fact]
    public async Task Slots_ParkedSodium_StaysOff_WhenThePlayerHasPutEmbeddiumIn()
    {
        var source = new Modrinth();
        source.AddSet("fabric", FabricSet);

        var plan = await Plan(
            source,
            LoaderKind.Fabric,
            jars: new[] { Jar("sodium-fabric-1.0.jar.disabled", "sodium", enabled: false), Jar("embeddium-1.jar", "embeddium") },
            parked: new[] { new BoostJarRecord { FileName = "sodium-fabric-1.0.jar", Slug = "sodium", Sha1 = "a" } });

        Assert.Equal(BoostModState.Present, plan.Items.Single(i => i.Slot.Renderer).State);
        Assert.Empty(plan.Reenable);
    }

    // ===================== The record of jars and the way back =====================

    private string WriteJar(string fileName, string id, string version = "1.0", params string[] requires)
    {
        var path = Path.Combine(_root, "mods", fileName);
        File.Delete(path);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("fabric.mod.json");
            using var writer = new StreamWriter(entry.Open());

            writer.Write(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                ["id"] = id,
                ["name"] = id,
                ["version"] = version,
                ["depends"] = requires.ToDictionary(r => r, _ => "*")
            }));
        }

        return path;
    }

    private bool Exists(string fileName) => File.Exists(Path.Combine(_root, "mods", fileName));

    [Fact]
    public void Jars_Off_SwitchesOffOnlyWhatIsStillTheInstalledFile()
    {
        WriteJar("sodium.jar", "sodium");
        WriteJar("lithium.jar", "lithium");
        WriteJar("modernfix.jar", "modernfix");
        WriteJar("fabric-api.jar", "fabric-api");
        WriteJar("players-own.jar", "something");

        var installed = new[] { "sodium.jar", "lithium.jar", "modernfix.jar", "fabric-api.jar", "ferritecore.jar" }
            .Select(name => BoostJars.Describe(_root, name, Path.GetFileNameWithoutExtension(name), name, dependency: name == "fabric-api.jar"))
            .ToList();

        Assert.All(installed.Take(4), jar => Assert.False(string.IsNullOrEmpty(jar.Sha1)));
        Assert.Null(installed[4].Sha1);

        // Since then: the player put a newer Lithium under the same name, and updated
        // ModernFix through the mod list, which switched the old file off by itself.
        WriteJar("lithium.jar", "lithium", version: "2.0");
        File.Move(Path.Combine(_root, "mods", "modernfix.jar"), Path.Combine(_root, "mods", "modernfix.jar.disabled"));
        WriteJar("modernfix-2.jar", "modernfix", version: "2.0");

        var revert = BoostJars.SwitchOff(_root, installed, LoaderKind.Fabric, Game);

        Assert.Equal(new[] { "sodium.jar", "fabric-api.jar" }, revert.SwitchedOff.Select(j => j.FileName));
        Assert.Equal(new[] { "lithium.jar", "modernfix.jar", "ferritecore.jar" }, revert.LeftToPlayer.Select(j => j.FileName));
        Assert.Empty(revert.LeftNeeded);

        // Switched off, never deleted.
        Assert.True(Exists("sodium.jar.disabled"));
        Assert.False(Exists("sodium.jar"));
        Assert.True(Exists("fabric-api.jar.disabled"));
        Assert.True(Exists("lithium.jar"));
        Assert.True(Exists("modernfix-2.jar"));
        Assert.True(Exists("modernfix.jar.disabled"));
        Assert.True(Exists("players-own.jar"));
    }

    [Fact]
    public void Jars_Off_LeavesWhatAnotherModNowNeeds()
    {
        WriteJar("sodium.jar", "sodium", requires: "fabric-api");
        WriteJar("fabric-api.jar", "fabric-api");
        WriteJar("lithium.jar", "lithium");

        var installed = new[] { "sodium.jar", "fabric-api.jar", "lithium.jar" }
            .Select(name => BoostJars.Describe(_root, name, null, name, dependency: false))
            .ToList();

        // The player adds a shader mod that requires Sodium, which requires Fabric API.
        WriteJar("iris.jar", "iris", requires: "sodium");

        var revert = BoostJars.SwitchOff(_root, installed, LoaderKind.Fabric, Game);

        Assert.Equal(new[] { "lithium.jar" }, revert.SwitchedOff.Select(j => j.FileName));
        Assert.Equal(new[] { "sodium.jar", "fabric-api.jar" }, revert.LeftNeeded.Select(n => n.Jar.FileName));
        Assert.Equal("iris", revert.LeftNeeded[0].NeededBy);
        Assert.True(Exists("sodium.jar"));
        Assert.True(Exists("fabric-api.jar"));
    }

    [Fact]
    public void Jars_Off_DoesNotOverwriteASwitchedOffFileOfTheSameName()
    {
        WriteJar("sodium.jar", "sodium");
        var installed = new[] { BoostJars.Describe(_root, "sodium.jar", "sodium", "Sodium", false) };
        File.WriteAllText(Path.Combine(_root, "mods", "sodium.jar.disabled"), "the player's own");

        var revert = BoostJars.SwitchOff(_root, installed, LoaderKind.Fabric, Game);

        Assert.Empty(revert.SwitchedOff);
        Assert.Single(revert.LeftToPlayer);
        Assert.Equal("the player's own", File.ReadAllText(Path.Combine(_root, "mods", "sodium.jar.disabled")));
        Assert.True(Exists("sodium.jar"));
    }

    [Fact]
    public void Jars_OffThenOn_BringsBackTheSameFiles_ButNotAChangedOne()
    {
        WriteJar("sodium.jar", "sodium");
        WriteJar("lithium.jar", "lithium");

        var installed = new[] { "sodium.jar", "lithium.jar" }
            .Select(name => BoostJars.Describe(_root, name, null, name, false))
            .ToList();

        var parked = BoostJars.SwitchOff(_root, installed, LoaderKind.Fabric, Game).SwitchedOff;
        Assert.Equal(2, parked.Count);
        Assert.Equal(2, BoostJars.StillParked(_root, parked).Count);

        // The player swaps the switched-off Lithium for something else under its name.
        File.WriteAllText(Path.Combine(_root, "mods", "lithium.jar.disabled"), "not that file");

        Assert.Equal(new[] { "sodium.jar" }, BoostJars.StillParked(_root, parked).Select(j => j.FileName));

        var back = BoostJars.SwitchOn(_root, parked);

        Assert.Equal(new[] { "sodium.jar" }, back.Select(j => j.FileName));
        Assert.True(Exists("sodium.jar"));
        Assert.False(Exists("sodium.jar.disabled"));
        Assert.True(Exists("lithium.jar.disabled"));
        Assert.False(Exists("lithium.jar"));
    }

    [Fact]
    public void Record_LivesInTheBuildsOwnFile_AndKeepsWasAbsent()
    {
        var instance = new Instance
        {
            Id = "b",
            Name = "b",
            Boost = new BoostRecord
            {
                Active = true,
                Jars = { new BoostJarRecord { FileName = "sodium.jar", Slug = "sodium", Sha1 = "abc", Dependency = false } },
                Options =
                {
                    new BoostOptionRecord { Key = "renderDistance", Previous = "12", Written = "8" },
                    new BoostOptionRecord { Key = "simulationDistance", Previous = null, Written = "6" }
                }
            }
        };

        var json = JsonSerializer.Serialize(instance);
        var read = JsonSerializer.Deserialize<Instance>(json)!;

        Assert.True(read.Boost!.Active);
        Assert.Equal("abc", read.Boost.Jars.Single().Sha1);
        Assert.Equal("12", read.Boost.Options[0].Previous);
        Assert.Null(read.Boost.Options[1].Previous);
        Assert.Equal("6", read.Boost.Options[1].Written);

        // A build that never used the switch carries nothing about it.
        Assert.DoesNotContain("boost", JsonSerializer.Serialize(new Instance { Id = "c" }));
    }
}
