using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using STlauncher.Core.Boost;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Two renderers must never be switched on together. When a server's build brings its
/// own, the one "Ускорение" installed steps aside - switched off, never deleted, and
/// written down so that off and on again still add up - and the build check names two
/// renderers as that.
/// </summary>
public class BoostRendererTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "stl-renderer-" + Guid.NewGuid().ToString("N"));
    private readonly string _mods;

    public BoostRendererTests()
    {
        _mods = Path.Combine(_game, "mods");
        Directory.CreateDirectory(_mods);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_game, true);
        }
        catch (Exception)
        {
        }
    }

    private void FabricJar(string file, string id, string extra = "")
    {
        using var archive = ZipFile.Open(Path.Combine(_mods, file), ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), Encoding.UTF8);
        writer.Write($$"""{ "id": "{{id}}", "name": "{{id}}", "version": "1.0"{{extra}} }""");
    }

    /// <summary>A build where the switch is on and has brought Sodium and Lithium.</summary>
    private BoostRecord BoostedBuild()
    {
        FabricJar("sodium-fabric-0.6.13.jar", "sodium");
        FabricJar("lithium-0.15.jar", "lithium");

        return new BoostRecord
        {
            Active = true,
            Jars =
            {
                BoostJars.Describe(_game, "sodium-fabric-0.6.13.jar", "sodium", "Sodium", dependency: false),
                BoostJars.Describe(_game, "lithium-0.15.jar", "lithium", "Lithium", dependency: false)
            }
        };
    }

    private bool Has(string file) => File.Exists(Path.Combine(_mods, file));

    [Fact]
    public void CatalogRenderer_SwitchesTheSwitchsOwnOff_AndParksIt()
    {
        var record = BoostedBuild();
        FabricJar("embeddium-1.0.11.jar", "embeddium");

        var parked = BoostRenderer.StepAside(_game, record, "embeddium-1.0.11.jar", "embeddium", LoaderKind.Fabric, "1.21.1");

        Assert.Equal("sodium-fabric-0.6.13.jar", Assert.Single(parked).FileName);

        // Renamed, not deleted; the catalog's file and the other boost mod are as they were.
        Assert.False(Has("sodium-fabric-0.6.13.jar"));
        Assert.True(Has("sodium-fabric-0.6.13.jar.disabled"));
        Assert.True(Has("embeddium-1.0.11.jar"));
        Assert.True(Has("lithium-0.15.jar"));

        // The record: no longer "installed and on", but parked. The switch itself stays on.
        Assert.True(record.Active);
        Assert.Equal("lithium-0.15.jar", Assert.Single(record.Jars).FileName);
        Assert.Equal("sodium-fabric-0.6.13.jar", Assert.Single(record.Parked).FileName);

        // One renderer left: the check has nothing to say.
        Assert.DoesNotContain(BuildChecker.Check(_game, LoaderKind.Fabric, null), i => i.Kind == BuildIssueKind.TwoRenderers);
    }

    [Fact]
    public void AfterSteppingAside_OffAndOnAgainStayConsistent()
    {
        var record = BoostedBuild();
        FabricJar("embeddium-1.0.11.jar", "embeddium");
        BoostRenderer.StepAside(_game, record, "embeddium-1.0.11.jar", null, LoaderKind.Fabric, "1.21.1");

        // Off: only Lithium is the switch's to take back; the parked Sodium is not touched twice.
        var off = BoostJars.SwitchOff(_game, record.Jars, LoaderKind.Fabric, "1.21.1");
        Assert.Equal("lithium-0.15.jar", Assert.Single(off.SwitchedOff).FileName);
        Assert.Empty(off.LeftToPlayer);

        // On again: Sodium is still known as parked, unchanged - so the plan can see that
        // the renderer's place is taken by the catalog's and leave this one off (the plan
        // side is covered by Slots_ParkedSodium_StaysOff_WhenThePlayerHasPutEmbeddiumIn).
        var still = BoostJars.StillParked(_game, record.Parked.Concat(off.SwitchedOff));
        Assert.Contains(still, j => j.FileName == "sodium-fabric-0.6.13.jar");
        Assert.True(Has("embeddium-1.0.11.jar"));
    }

    [Fact]
    public void RendererIsRecognised_ByProject_ByJarId_ByFileName()
    {
        // By project alone: the file says nothing and is named otherwise.
        var record = BoostedBuild();
        File.WriteAllBytes(Path.Combine(_mods, "renderer-from-server.jar"), new byte[] { 1, 2, 3 });
        Assert.Single(BoostRenderer.StepAside(_game, record, "renderer-from-server.jar", "rubidium", LoaderKind.Fabric, null));

        // By the id the jar declares.
        Assert.True(BoostRenderer.IsRenderer(new BuildJar("x.jar", true, ModMetadataReader.Read(Path.Combine(_mods, "sodium-fabric-0.6.13.jar.disabled"))), LoaderKind.Fabric, null));

        // By file name, for a jar without metadata - and not for a neighbour of the name.
        Assert.True(BoostRenderer.IsRenderer(new BuildJar("rubidium-mc1.20.1-0.7.1.jar", true, Array.Empty<ModMetadata>()), LoaderKind.Forge, null));
        Assert.False(BoostRenderer.IsRenderer(new BuildJar("sodium-extra-0.6.0.jar", true, Array.Empty<ModMetadata>()), LoaderKind.Fabric, null));
    }

    [Fact]
    public void NotARenderer_ChangesNothing()
    {
        var record = BoostedBuild();
        FabricJar("jei-19.jar", "jei");

        Assert.Empty(BoostRenderer.StepAside(_game, record, "jei-19.jar", "jei", LoaderKind.Fabric, null));
        Assert.True(Has("sodium-fabric-0.6.13.jar"));
        Assert.Equal(2, record.Jars.Count);
        Assert.Empty(record.Parked);
    }

    [Fact]
    public void JarThePlayerHasReplaced_IsNotTouched_AndTheCheckNamesBoth()
    {
        var record = BoostedBuild();

        // The same name, other contents: the player's own build of Sodium.
        File.Delete(Path.Combine(_mods, "sodium-fabric-0.6.13.jar"));
        FabricJar("sodium-fabric-0.6.13.jar", "sodium", ", \"description\": \"mine\"");
        FabricJar("embeddium-1.0.11.jar", "embeddium");

        Assert.Empty(BoostRenderer.StepAside(_game, record, "embeddium-1.0.11.jar", "embeddium", LoaderKind.Fabric, null));
        Assert.True(Has("sodium-fabric-0.6.13.jar"));
        Assert.Equal(2, record.Jars.Count);

        var issue = Assert.Single(BuildChecker.Check(_game, LoaderKind.Fabric, null), i => i.Kind == BuildIssueKind.TwoRenderers);
        Assert.True(issue.IsBlocking);
        Assert.False(issue.FixableBySwitch);
        Assert.Equal(new[] { "embeddium-1.0.11.jar", "sodium-fabric-0.6.13.jar" }, new[] { issue.FileName, issue.OtherFileName }.OrderBy(f => f));
    }

    [Fact]
    public void SameFileTakenOverByTheCatalog_IsLeftOn()
    {
        var record = BoostedBuild();

        Assert.Empty(BoostRenderer.StepAside(_game, record, "sodium-fabric-0.6.13.jar", "sodium", LoaderKind.Fabric, null));
        Assert.True(Has("sodium-fabric-0.6.13.jar"));
    }

    [Fact]
    public void SwitchedOffFileOfTheSameName_IsNotOverwritten()
    {
        var record = BoostedBuild();
        File.WriteAllText(Path.Combine(_mods, "sodium-fabric-0.6.13.jar.disabled"), "the player's");
        FabricJar("embeddium-1.0.11.jar", "embeddium");

        Assert.Empty(BoostRenderer.StepAside(_game, record, "embeddium-1.0.11.jar", "embeddium", LoaderKind.Fabric, null));
        Assert.Equal("the player's", File.ReadAllText(Path.Combine(_mods, "sodium-fabric-0.6.13.jar.disabled")));
        Assert.True(Has("sodium-fabric-0.6.13.jar"));
    }

    [Fact]
    public void Check_TwoRenderers_SaidOnce_InsteadOfTheirOwnConflictLine()
    {
        FabricJar("sodium-0.6.jar", "sodium", ", \"breaks\": { \"embeddium\": \"*\" }");
        FabricJar("embeddium-1.0.jar", "embeddium");
        FabricJar("lithium-0.15.jar", "lithium");

        var issues = BuildChecker.Check(_game, LoaderKind.Fabric, null);

        Assert.Single(issues, i => i.Kind == BuildIssueKind.TwoRenderers);
        Assert.DoesNotContain(issues, i => i.Kind is BuildIssueKind.Incompatible or BuildIssueKind.Discouraged);
    }

    [Fact]
    public void Check_OneRenderer_OrASwitchedOffSecond_OrTwoFilesOfOneMod_IsNotTwoRenderers()
    {
        FabricJar("sodium-0.6.jar", "sodium");
        FabricJar("embeddium-1.0.jar.disabled", "embeddium");
        FabricJar("sodium-extra-0.6.jar", "sodium-extra");

        Assert.DoesNotContain(BuildChecker.Check(_game, LoaderKind.Fabric, null), i => i.Kind == BuildIssueKind.TwoRenderers);

        // Two files of Sodium are a doubled mod, and the duplicate line already says so.
        FabricJar("sodium-0.5.jar", "sodium");
        var issues = BuildChecker.Check(_game, LoaderKind.Fabric, null);

        Assert.Contains(issues, i => i.Kind == BuildIssueKind.DuplicateMod);
        Assert.DoesNotContain(issues, i => i.Kind == BuildIssueKind.TwoRenderers);
    }
}
