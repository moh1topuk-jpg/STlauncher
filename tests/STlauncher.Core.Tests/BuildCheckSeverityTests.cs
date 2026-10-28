using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// "The game will not start" only where the loader itself refuses; everything the loader
/// tolerates is at most a soft warning. Shapes taken from real fabric.mod.json files.
/// </summary>
public class BuildCheckSeverityTests
{
    private const string Game = "1.21.11";

    private static string TempGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        return root;
    }

    private static string Jar(string game, string file, string json, params (string Path, byte[] Bytes)[] nested)
    {
        var path = Path.Combine(game, "mods", file);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "fabric.mod.json", Encoding.UTF8.GetBytes(json));

        foreach (var (inner, bytes) in nested)
        {
            Write(archive, inner, bytes);
        }

        return path;
    }

    private static byte[] NestedJar(string json)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "fabric.mod.json", Encoding.UTF8.GetBytes(json));
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    private static IReadOnlyList<BuildIssue> Check(string game, string loaderVersion = "0.19.5")
        => BuildChecker.Check(game, LoaderKind.Fabric, Game, loaderVersion);

    private const string FabricApi = """{ "id": "fabric-api", "name": "Fabric API", "version": "0.141.6+1.21.11", "depends": { "fabricloader": ">=0.17.2", "minecraft": "~1.21.11" } }""";

    [Fact]
    public void RecommendsSuggestsConflicts_OnSomethingMissing_AreNotWontStart()
    {
        var game = TempGame();
        Jar(game, "fabric-api.jar", FabricApi);
        Jar(game, "zoomify.jar", """
            { "schemaVersion": 1, "id": "zoomify", "name": "Zoomify", "version": "2.15.2+1.21.11",
              "depends": { "fabricloader": ">=0.16", "minecraft": "1.21.11", "fabric-api": "*" },
              "recommends": { "modmenu": "*", "yet_another_config_lib_v3": ">=3.6" },
              "suggests": { "controlify": "*" },
              "conflicts": { "okzoomer": "*" } }
            """);

        Assert.Empty(Check(game));
    }

    [Fact]
    public void Conflicts_WithAModThatIsThere_IsASoftWarning()
    {
        var game = TempGame();
        Jar(game, "a.jar", """{ "id": "a", "name": "A", "version": "1.0", "conflicts": { "b": "<2" } }""");
        Jar(game, "b.jar", """{ "id": "b", "name": "B", "version": "1.5" }""");

        var issue = Assert.Single(Check(game));
        Assert.Equal(BuildIssueKind.Discouraged, issue.Kind);
        Assert.False(issue.IsBlocking);
    }

    [Fact]
    public void DependsMissing_WontStart()
    {
        var game = TempGame();
        Jar(game, "rrls.jar", """{ "id": "rrls", "name": "Remove Reloading Screen", "version": "5.1.15", "depends": { "minecraft": ">=1.21.11", "forgeconfigapiport": ">=21" } }""");

        var issue = Assert.Single(Check(game));
        Assert.Equal(BuildIssueKind.MissingDependency, issue.Kind);
        Assert.Equal("forgeconfigapiport", issue.Detail);
        Assert.True(issue.IsBlocking);
    }

    [Theory]
    [InlineData("~1.21", false)]
    [InlineData(">=1.21.2", false)]
    [InlineData("1.21.x", false)]
    [InlineData("*", false)]
    [InlineData("1.21.11", false)]
    [InlineData(">=1.21.11- <1.22", false)]   // a pre-release lower bound
    [InlineData(">1.21.11-rc.1", false)]      // the release is above its own release candidate
    [InlineData("1.21.10", true)]
    [InlineData("1.21", true)]                // Fabric reads a bare version as exactly it: 1.21.0
    [InlineData(">=26.3- <=26.3", true)]
    [InlineData("~26.4-", true)]
    [InlineData("1.21.12-alpha.1", true)]
    public void MinecraftRange_AgainstTheBuild(string range, bool wontStart)
    {
        var game = TempGame();
        Jar(game, "m.jar", $$"""{ "id": "m", "name": "M", "version": "1.0", "depends": { "minecraft": "{{range}}" } }""");

        var issues = Check(game);

        if (wontStart)
        {
            var issue = Assert.Single(issues);
            Assert.Equal(BuildIssueKind.WrongGameVersion, issue.Kind);
            Assert.True(issue.IsBlocking);
        }
        else
        {
            Assert.Empty(issues);
        }
    }

    [Fact]
    public void MinecraftRange_InAnArray_IsAnyOf()
    {
        var game = TempGame();
        Jar(game, "m.jar", """{ "id": "m", "name": "M", "version": "1.0", "depends": { "minecraft": ["1.21.10", "1.21.11"] } }""");
        Jar(game, "n.jar", """{ "id": "n", "name": "N", "version": "1.0", "depends": { "minecraft": [">=26.3- <=26.3", "~26.3"] } }""");

        var issue = Assert.Single(Check(game));
        Assert.Equal("n.jar", issue.FileName);
    }

    [Fact]
    public void UnreadableRange_IsNoClaim()
    {
        var game = TempGame();
        Jar(game, "m.jar", """{ "id": "m", "name": "M", "version": "1.0", "depends": { "minecraft": "some text" } }""");

        Assert.Empty(Check(game));
    }

    [Fact]
    public void RecommendsMinecraft_OutsideRange_IsSoft()
    {
        var game = TempGame();
        Jar(game, "m.jar", """{ "id": "m", "name": "M", "version": "1.0", "recommends": { "minecraft": "1.20.x" } }""");

        var issue = Assert.Single(Check(game));
        Assert.Equal(BuildIssueKind.WrongGameVersion, issue.Kind);
        Assert.False(issue.IsBlocking);
    }

    [Fact]
    public void Breaks_OnAModThatIsThere_WontStart()
    {
        var game = TempGame();
        Jar(game, "fabric-api.jar", FabricApi);
        Jar(game, "sodium.jar", """{ "id": "sodium", "name": "Sodium", "version": "0.8.12+mc1.21.11", "breaks": { "iris": "<=1.10.6", "optifabric": "*" } }""");
        Jar(game, "iris.jar", """{ "id": "iris", "name": "Iris", "version": "1.10.5+mc1.21.11", "depends": { "sodium": "0.8.x" } }""");

        var issue = Assert.Single(Check(game));
        Assert.Equal(BuildIssueKind.Incompatible, issue.Kind);
        Assert.True(issue.IsBlocking);
    }

    [Fact]
    public void Breaks_OnAVersionNotInTheBuild_IsNothing()
    {
        var game = TempGame();
        Jar(game, "sodium.jar", """{ "id": "sodium", "name": "Sodium", "version": "0.8.12+mc1.21.11", "breaks": { "iris": "<=1.10.6" } }""");
        Jar(game, "iris.jar", """{ "id": "iris", "name": "Iris", "version": "1.10.7+mc1.21.11" }""");

        Assert.Empty(Check(game));
    }

    [Fact]
    public void Depends_PresentInAVersionOutsideItsRange_WontStart()
    {
        var game = TempGame();
        Jar(game, "fabric-api.jar", FabricApi);
        Jar(game, "architectury.jar", """{ "id": "architectury", "name": "Architectury", "version": "22.0.5", "depends": { "fabric-api": ">=0.160.5" } }""");

        var issue = Assert.Single(Check(game));
        Assert.Equal(BuildIssueKind.WrongDependencyVersion, issue.Kind);
        Assert.Equal(">=0.160.5", issue.Range);
        Assert.True(issue.IsBlocking);
    }

    [Fact]
    public void FabricLoader_TooOld_WontStart_UnknownLoaderVersion_IsNothing()
    {
        var game = TempGame();
        Jar(game, "m.jar", """{ "id": "m", "name": "M", "version": "1.0", "depends": { "fabricloader": ">=0.18.5" } }""");

        Assert.True(Assert.Single(Check(game, "0.16.10")).IsBlocking);
        Assert.Empty(Check(game, "0.19.5"));
        Assert.Empty(BuildChecker.Check(game, LoaderKind.Fabric, Game, loaderVersion: null));
    }

    [Fact]
    public void NestedJar_AndProvidesAlias_SatisfyADependency()
    {
        var game = TempGame();

        // Forge Config API Port shipped inside another mod, and an alias from "provides".
        Jar(game, "host.jar",
            """{ "id": "host", "name": "Host", "version": "1.0", "provides": [ "cloth-config2" ], "jars": [ { "file": "META-INF/jars/fcap.jar" } ] }""",
            ("META-INF/jars/fcap.jar", NestedJar("""{ "id": "forgeconfigapiport", "version": "21.11.1" }""")));
        Jar(game, "rrls.jar", """{ "id": "rrls", "name": "RRLS", "version": "5.1.15", "depends": { "forgeconfigapiport": ">=21", "cloth-config2": "*" } }""");

        Assert.Empty(Check(game));
    }

    [Fact]
    public void ALibraryNestedInsideTheModThatNeedsIt_CountsToo()
    {
        var game = TempGame();
        Jar(game, "zoomify.jar",
            """{ "id": "zoomify", "name": "Zoomify", "version": "2.15", "depends": { "yet_another_config_lib_v3": ">=3.6" }, "jars": [ { "file": "META-INF/jars/yacl.jar" } ] }""",
            ("META-INF/jars/yacl.jar", NestedJar("""{ "id": "yet_another_config_lib_v3", "version": "3.8.2+1.21.11-fabric" }""")));

        Assert.Empty(Check(game));
    }

    [Fact]
    public void ARootModBesideANestedNewerCopy_IsNotJudgedByTheCopy()
    {
        // Voxy carries a Sodium of its own; Iris asks for the 0.8 line the build has.
        var game = TempGame();
        Jar(game, "sodium.jar", """{ "id": "sodium", "name": "Sodium", "version": "0.8.12+mc1.21.11" }""");
        Jar(game, "voxy.jar",
            """{ "id": "voxy", "name": "Voxy", "version": "0.2.16", "jars": [ { "file": "META-INF/jars/dummy.jar" } ] }""",
            ("META-INF/jars/dummy.jar", NestedJar("""{ "id": "sodium", "version": "0.9.3-beta.1" }""")));
        Jar(game, "iris.jar", """{ "id": "iris", "name": "Iris", "version": "1.10.7", "depends": { "sodium": "0.8.x" } }""");

        Assert.DoesNotContain(Check(game), i => i.IsBlocking);
    }

    [Fact]
    public void ServerOnlyMod_IsSkippedLikeTheLoaderSkipsIt()
    {
        var game = TempGame();
        Jar(game, "server.jar", """{ "id": "srv", "name": "Server thing", "version": "1.0", "environment": "server", "depends": { "luckperms": "*", "minecraft": "1.20.1" } }""");

        Assert.Empty(Check(game));
    }

    [Fact]
    public void TwoRenderers_IsNeverMoreThanAWarning()
    {
        var game = TempGame();
        Jar(game, "sodium.jar", """{ "id": "sodium", "name": "Sodium", "version": "0.8.12" }""");
        Jar(game, "embeddium.jar", """{ "id": "embeddium", "name": "Embeddium", "version": "1.0" }""");

        var issues = Check(game);
        Assert.Contains(issues, i => i.Kind == BuildIssueKind.TwoRenderers);
        Assert.DoesNotContain(issues, i => i.IsBlocking);
    }

    [Fact]
    public void ForgeBareVersion_IsARecommendation_AndOptionalPresentOutOfRange_WontStart()
    {
        var game = TempGame();
        var path = Path.Combine(game, "mods", "a.jar");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(archive, "META-INF/mods.toml", Encoding.UTF8.GetBytes(
                "modLoader=\"javafml\"\n[[mods]]\nmodId=\"a\"\nversion=\"1.0\"\n" +
                "[[dependencies.a]]\nmodId=\"minecraft\"\nmandatory=true\nversionRange=\"1.20.1\"\n" +
                "[[dependencies.a]]\nmodId=\"b\"\nmandatory=false\nversionRange=\"[2.0,)\"\n"));
        }

        using (var archive = ZipFile.Open(Path.Combine(game, "mods", "b.jar"), ZipArchiveMode.Create))
        {
            Write(archive, "META-INF/mods.toml", Encoding.UTF8.GetBytes("modLoader=\"javafml\"\n[[mods]]\nmodId=\"b\"\nversion=\"1.5\"\n"));
        }

        var issue = Assert.Single(BuildChecker.Check(game, LoaderKind.Forge, "1.21.1"));
        Assert.Equal(BuildIssueKind.WrongDependencyVersion, issue.Kind);
        Assert.True(issue.IsBlocking);
    }

    [Fact]
    public void Dismissal_HoldsUntilTheFileChanges_AndNeverHidesAHardOne()
    {
        var game = TempGame();
        var path = Jar(game, "m.jar", """{ "id": "m", "name": "M", "version": "1.0", "recommends": { "minecraft": "1.20.x" } }""");
        var soft = Assert.Single(Check(game));
        var dismissed = new List<DismissedBuildWarning>();
        ModFileStamp? Stamp(string name) => ModFileStamp.Of(Path.Combine(game, "mods", name));

        BuildWarningDismissals.Dismiss(dismissed, "m.jar", Stamp("m.jar")!.Value);
        Assert.True(BuildWarningDismissals.IsDismissed(dismissed, soft, Stamp));

        // Switched off and on: the same file under another name.
        File.Move(path, path + ".disabled");
        Assert.True(BuildWarningDismissals.IsDismissed(dismissed, soft with { FileName = "m.jar.disabled" }, Stamp));
        File.Move(path + ".disabled", path);

        Assert.False(BuildWarningDismissals.IsDismissed(dismissed, soft with { Severity = BuildIssueSeverity.WontStart }, Stamp));

        // Updated: written again, so the warning is looked at again.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        Assert.False(BuildWarningDismissals.IsDismissed(dismissed, soft, Stamp));
        Assert.Equal(1, BuildWarningDismissals.Prune(dismissed, Stamp));
    }
}

public class ModUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PinnedByCatalog_IsNeverOffered()
    {
        Assert.False(ModUpdatePolicy.IsOffered(pinnedByCatalog: true, "v2", null));
        Assert.True(ModUpdatePolicy.IsOffered(pinnedByCatalog: false, "v2", null));
    }

    [Fact]
    public void SkippedVersion_StaysSkipped_UntilANewerOneAppears()
    {
        Assert.False(ModUpdatePolicy.IsOffered(false, "v2", "v2"));
        Assert.True(ModUpdatePolicy.IsOffered(false, "v3", "v2"));
        Assert.Equal("AANNREEL", ModUpdatePolicy.SkipKey("AANNREEL", "sodium.jar"));
        Assert.Equal("x.jar", ModUpdatePolicy.SkipKey(null, "x.jar.disabled"));
    }

    [Theory]
    [InlineData(ModUpdateFrequency.Daily, 2, false)]
    [InlineData(ModUpdateFrequency.Daily, 25, true)]
    [InlineData(ModUpdateFrequency.Always, 2, false)]   // the check itself stays at once a day
    [InlineData(ModUpdateFrequency.Weekly, 25, false)]
    [InlineData(ModUpdateFrequency.Weekly, 24 * 8, true)]
    [InlineData(ModUpdateFrequency.Never, 24 * 30, false)]
    public void Check_AtMostOnceADayPerBuild(ModUpdateFrequency frequency, int hoursSince, bool expected)
    {
        Assert.Equal(expected, ModUpdatePolicy.ShouldCheck(frequency, Now.AddHours(-hoursSince), Now));
    }

    [Fact]
    public void Check_NeverDoneBefore_RunsUnlessNever()
    {
        Assert.True(ModUpdatePolicy.ShouldCheck(ModUpdateFrequency.Daily, null, Now));
        Assert.False(ModUpdatePolicy.ShouldCheck(ModUpdateFrequency.Never, null, Now));
    }

    [Theory]
    [InlineData(ModUpdateFrequency.Always, 1, true)]
    [InlineData(ModUpdateFrequency.Daily, 1, false)]
    [InlineData(ModUpdateFrequency.Daily, 24, true)]
    [InlineData(ModUpdateFrequency.Weekly, 48, false)]
    [InlineData(ModUpdateFrequency.Never, 1000, false)]
    public void HomeLine_AsOftenAsTheSettingSays(ModUpdateFrequency frequency, int hoursSince, bool expected)
    {
        Assert.Equal(expected, ModUpdatePolicy.ShouldShowNotice(frequency, Now.AddHours(-hoursSince), Now));
    }

    [Fact]
    public void CatalogOffer_NotNow_HoldsForTheSameRevisionOnly()
    {
        var first = ModUpdatePolicy.CatalogOfferKey("showtime", new[] { "get:sodium@0.8.12", "get:iris@1.10.7" });
        var sameInOtherOrder = ModUpdatePolicy.CatalogOfferKey("showtime", new[] { "get:iris@1.10.7", "get:sodium@0.8.12" });
        var next = ModUpdatePolicy.CatalogOfferKey("showtime", new[] { "get:sodium@0.8.13", "get:iris@1.10.7" });

        Assert.Equal(first, sameInOtherOrder);
        Assert.False(ModUpdatePolicy.ShouldOfferCatalog(sameInOtherOrder, declinedKey: first));
        Assert.True(ModUpdatePolicy.ShouldOfferCatalog(next, declinedKey: first));
        Assert.True(ModUpdatePolicy.ShouldOfferCatalog(first, declinedKey: null));
    }
}
