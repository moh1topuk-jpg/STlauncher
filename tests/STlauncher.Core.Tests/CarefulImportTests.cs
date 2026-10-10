using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core;
using STlauncher.Core.Import;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The careful half of importing: what settings come along and what never does, links
/// that are not walked through, what is copied from which kind of source, the sweep of
/// other drives, and a game version that is read rather than guessed.
/// </summary>
public class CarefulImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    private readonly LauncherPaths _paths;
    private readonly InstanceManager _instances;
    private readonly InstanceImporter _importer;

    public CarefulImportTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "launcher"));
        _paths.EnsureCreated();
        _instances = new InstanceManager(_paths);
        _importer = new InstanceImporter(_paths, _instances);
    }

    private static void Write(string directory, string file, string content)
    {
        var path = Path.Combine(directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A Prism instance with a world, described by mmc-pack.json.</summary>
    private string PrismInstance(string name, string instanceCfg = "[General]\nname=Pack\n", string launcher = "prism")
    {
        var instance = Path.Combine(_root, launcher, name);
        Write(instance, ".minecraft/saves/world/level.dat", "world");
        Write(instance, "mmc-pack.json", """
            { "components": [
                { "uid": "net.minecraft", "version": "1.20.1" },
                { "uid": "net.fabricmc.fabric-loader", "version": "0.15.11" } ] }
            """);
        Write(instance, "instance.cfg", instanceCfg);
        return instance;
    }

    private ExternalInstance ScanPrism(string launcher = "prism")
        => ExternalInstanceScanner.Scan(Path.Combine(_root, launcher), ExternalLauncherKind.Prism).Single();

    /// <summary>
    /// A symbolic link, or on Windows without the privilege for one, a junction. False
    /// when this machine lets the test make neither - the link tests then have nothing
    /// to check and pass empty.
    /// </summary>
    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                process!.WaitForExit(10000);
            }
            catch (Exception)
            {
                return false;
            }
        }

        var made = Directory.Exists(link) && LinkGuard.IsLink(link);

        // For checking that the link tests really ran on a machine that should manage it.
        if (!made && Environment.GetEnvironmentVariable("STLAUNCHER_TESTS_REQUIRE_LINKS") == "1")
        {
            Assert.Fail($"Could not create a link at {link}.");
        }

        return made;
    }

    // ===================== 1. Settings that come along =====================

    [Theory]
    [InlineData("-Xmx4G")]
    [InlineData("-Xms512m")]
    [InlineData("-Xss2M")]
    [InlineData("-Xmn256m")]
    [InlineData("-XX:+UseG1GC")]
    [InlineData("-XX:-OmitStackTraceInFastThrow")]
    [InlineData("-XX:MaxGCPauseMillis=50")]
    [InlineData("-XX:G1HeapRegionSize=32M")]
    [InlineData("-XX:MaxDirectMemorySize=2G")]
    [InlineData("-XX:InitiatingHeapOccupancyPercent=15")]
    [InlineData("-XX:ShenandoahGCMode=iu")]
    [InlineData("-XX:+UnlockExperimentalVMOptions")]
    [InlineData("-Dfile.encoding=UTF-8")]
    [InlineData("-Djava.net.preferIPv4Stack=true")]
    [InlineData("-Dfml.readTimeout=240")]
    [InlineData("--add-opens=java.base/java.lang=ALL-UNNAMED")]
    [InlineData("--add-exports=java.base/sun.security.util=ALL-UNNAMED")]
    public void Allowlist_LetsThroughWhatPeopleTune(string argument)
        => Assert.True(JvmArgumentAllowlist.IsAllowed(argument), argument);

    [Theory]
    [InlineData("-javaagent:C:/evil.jar")]
    [InlineData("-javaagent:evil.jar=opts")]
    [InlineData("-agentlib:jdwp=transport=dt_socket,server=y,address=5005")]
    [InlineData("-agentpath:/tmp/lib.so")]
    [InlineData("-Xbootclasspath/a:evil.jar")]
    [InlineData("-Xbootclasspath:evil.jar")]
    [InlineData("-cp")]
    [InlineData("-classpath")]
    [InlineData("--class-path")]
    [InlineData("--module-path")]
    [InlineData("-p")]
    [InlineData("--upgrade-module-path=x")]
    [InlineData("-Djava.library.path=C:/natives")]
    [InlineData("-Dlog4j.configurationFile=http://evil/x.xml")]
    [InlineData("-Dlog4j2.formatMsgNoLookups=false")]
    [InlineData("-Dlog4j2.configurationFile=evil.xml")]
    [InlineData("-Djava.security.manager=allow")]
    [InlineData("-Dfile.encoding=../../x")]
    [InlineData("-XX:OnError=calc.exe")]
    [InlineData("-XX:OnOutOfMemoryError=cmd")]
    [InlineData("-XX:+CrashOnOutOfMemoryError")]
    [InlineData("-XX:HeapDumpPath=C:/dump")]
    [InlineData("-XX:ErrorFile=C:/x.log")]
    [InlineData("-XX:Flags=.hotspotrc")]
    [InlineData("-XX:VMOptionsFile=opts")]
    [InlineData("-XX:CompileCommandFile=cmds")]
    [InlineData("-XX:+HeapDumpOnOutOfMemoryError")]
    [InlineData("-XX:StartFlightRecording=filename=x.jfr")]
    [InlineData("-XX:G1HeapRegionSize=C:/x")]
    [InlineData("-Xlog:gc:file=gc.log")]
    [InlineData("-Xloggc:gc.log")]
    [InlineData("-Xshare:dump")]
    [InlineData("-Xrunjdwp:transport=dt_socket")]
    [InlineData("@argfile")]
    [InlineData("-jar")]
    [InlineData("evil.jar")]
    [InlineData("--add-opens=java.base/java.lang=ALL-UNNAMED;calc")]
    [InlineData("--add-modules=ALL-SYSTEM")]
    [InlineData("--patch-module=java.base=evil.jar")]
    [InlineData("-XX:+UseG1GC -javaagent:x.jar")]
    [InlineData("")]
    public void Allowlist_DropsEverythingThatCanLoadCodeOrNameAFile(string argument)
        => Assert.False(JvmArgumentAllowlist.IsAllowed(argument), argument);

    [Fact]
    public void Allowlist_SortsARawLineIntoKeptAndDropped()
    {
        var result = JvmArgumentAllowlist.Filter(
            "-XX:+UseG1GC  \"-javaagent:C:\\Program Files\\x.jar\" -Dfile.encoding=UTF-8 --add-opens java.base/java.util=ALL-UNNAMED -cp evil.jar -XX:+UseG1GC");

        Assert.Equal(
            new[] { "-XX:+UseG1GC", "-Dfile.encoding=UTF-8", "--add-opens=java.base/java.util=ALL-UNNAMED" },
            result.Kept);

        // The quoted agent stays one argument - and is dropped as one, path and all.
        Assert.Contains(@"-javaagent:C:\Program Files\x.jar", result.Dropped);
        Assert.Contains("-cp", result.Dropped);
        Assert.Contains("evil.jar", result.Dropped);
    }

    [Fact]
    public void Allowlist_AnAddOpensWithNothingSafeAfterItIsDropped()
    {
        var result = JvmArgumentAllowlist.Filter("--add-opens C:/x.jar");

        Assert.Empty(result.Kept);
        Assert.Equal(2, result.Dropped.Count);
    }

    [Fact]
    public void SourceSettings_AreReadOnlyWhereTheBuildOverridesThem()
    {
        PrismInstance("Pack", """
            [General]
            name=Pack
            OverrideMemory=true
            MaxMemAlloc=6144
            MinMemAlloc=1024
            OverrideWindow=true
            MinecraftWinWidth=1600
            MinecraftWinHeight=900
            OverrideJavaArgs=true
            JvmArgs="-XX:+UseG1GC -Xmx12G -javaagent:\"C:/tools/agent.jar\" -Dfile.encoding=UTF-8"
            """);

        var settings = ScanPrism().Settings!;

        Assert.Equal(6144, settings.MaxMemoryMb);
        Assert.Equal(1024, settings.MinMemoryMb);
        Assert.Equal(1600, settings.Width);
        Assert.Equal(900, settings.Height);

        // The heap is the memory setting's business; a second -Xmx is not carried.
        Assert.Equal(new[] { "-XX:+UseG1GC", "-Dfile.encoding=UTF-8" }, settings.JvmArguments);
        Assert.Contains(settings.DroppedJvmArguments, a => a.StartsWith("-javaagent", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceSettings_ValuesUnderASwitchedOffOverrideAreLeftovers()
    {
        PrismInstance("Pack", """
            [General]
            OverrideMemory=false
            MaxMemAlloc=6144
            OverrideWindow=false
            MinecraftWinWidth=1600
            MinecraftWinHeight=900
            OverrideJavaArgs=false
            JvmArgs=-XX:+UseG1GC
            """);

        Assert.Null(ScanPrism().Settings);
    }

    [Theory]
    [InlineData("256")]
    [InlineData("511")]
    [InlineData("65537")]
    [InlineData("999999999")]
    [InlineData("-4096")]
    [InlineData("4096; rm")]
    [InlineData("")]
    public void SourceSettings_MemoryOutsideTheRangeIsNotCarried(string value)
    {
        var settings = SourceInstanceSettings.FromValues(new Dictionary<string, string>
        {
            ["OverrideMemory"] = "true",
            ["MaxMemAlloc"] = value
        });

        Assert.Null(settings);
    }

    [Theory]
    [InlineData("512")]
    [InlineData("65536")]
    public void SourceSettings_TheEdgesOfTheMemoryRangeAreIn(string value)
    {
        var settings = SourceInstanceSettings.FromValues(new Dictionary<string, string>
        {
            ["OverrideMemory"] = "true",
            ["MaxMemAlloc"] = value,
            ["MinMemAlloc"] = "70000"
        });

        Assert.Equal(int.Parse(value), settings!.MaxMemoryMb);

        // A lower bound above the upper one is nonsense and is not taken.
        Assert.Null(settings.MinMemoryMb);
    }

    [Fact]
    public async Task Import_AppliesCarriedSettingsAndThePinnedLoader()
    {
        PrismInstance("Pack", """
            [General]
            name=Pack
            OverrideMemory=true
            MaxMemAlloc=8192
            OverrideWindow=true
            MinecraftWinWidth=1280
            MinecraftWinHeight=720
            OverrideJavaArgs=true
            JvmArgs=-XX:+UseZGC -Xbootclasspath/p:evil.jar
            """);

        var result = await _importer.ImportAsync(ScanPrism(), ImportMode.Link);

        Assert.Equal(8192, result.Instance.MaxMemoryMb);
        Assert.Equal(1280, result.Instance.Width);
        Assert.Equal(720, result.Instance.Height);
        Assert.Equal("-XX:+UseZGC", result.Instance.ExtraJvmArgs);
        Assert.Equal("1.20.1", result.Instance.VersionId);

        // The exact loader build from mmc-pack.json, not "whatever is newest today".
        Assert.Equal(LoaderKind.Fabric, result.Instance.Loader);
        Assert.Equal("0.15.11", result.Instance.LoaderVersion);

        Assert.Contains("-Xbootclasspath/p:evil.jar", result.CarriedSettings!.DroppedJvmArguments);

        // And it is what was saved, not only what was returned.
        var saved = _instances.Get(result.Instance.Id)!;
        Assert.Equal(8192, saved.MaxMemoryMb);
        Assert.Equal("-XX:+UseZGC", saved.ExtraJvmArgs);
    }

    [Fact]
    public async Task Import_ChecksCarriedSettingsAgain_WhoeverBuiltTheRecord()
    {
        PrismInstance("Pack");

        // A record put together by something other than the reader: nothing in it is trusted.
        var forged = ScanPrism() with
        {
            Settings = new CarriedSettings(
                9_999_999, 1, 99999, 5,
                new[] { "-javaagent:x.jar", "-XX:+UseG1GC" },
                Array.Empty<string>())
        };

        var result = await _importer.ImportAsync(forged, ImportMode.Link);

        Assert.Equal(2048, result.Instance.MaxMemoryMb);
        Assert.Null(result.Instance.Width);
        Assert.Equal("-XX:+UseG1GC", result.Instance.ExtraJvmArgs);
    }

    [Fact]
    public async Task Import_ABuildThatOverridesNothingKeepsTheLaunchersDefaults()
    {
        PrismInstance("Pack");

        var result = await _importer.ImportAsync(ScanPrism(), ImportMode.Link);

        Assert.Equal(2048, result.Instance.MaxMemoryMb);
        Assert.Null(result.Instance.Width);
        Assert.Null(result.Instance.ExtraJvmArgs);
        Assert.Null(result.CarriedSettings);
    }

    // ===================== 2. Links =====================

    [Fact]
    public void Scan_ARootThatIsALinkIsRefusedWithWhereItLeads()
    {
        var real = Path.Combine(_root, "D-drive", ".minecraft");
        Write(real, "versions/1.21.1/1.21.1.json", """{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }""");

        var link = Path.Combine(_root, "appdata-minecraft");

        if (!TryLinkDirectory(link, real))
        {
            return;
        }

        foreach (var found in new[]
                 {
                     ExternalInstanceScanner.Scan(link, ExternalLauncherKind.DotMinecraft),
                     ExternalInstanceScanner.ScanUnknownFolder(link),
                     ExternalInstanceScanner.ScanAll(new[] { (link, ExternalLauncherKind.DotMinecraft) })
                 })
        {
            var refused = Assert.Single(found);
            Assert.False(refused.IsUsable);
            Assert.Equal(ExternalInstanceProblem.SourceIsLink, refused.Problem);
            Assert.Equal(real, refused.LinkTarget, ignoreCase: true);
        }

        // The real folder, pointed at by hand, is still found.
        Assert.Equal("1.21.1", ExternalInstanceScanner.ScanUnknownFolder(real).Single().VersionId);
    }

    [Fact]
    public void Scan_AnInstanceBehindALinkIsListedButNotOpened()
    {
        PrismInstance("Real pack");

        var elsewhere = Path.Combine(_root, "elsewhere", "Linked pack");
        Write(elsewhere, ".minecraft/saves/w/level.dat", "x");
        Write(elsewhere, "mmc-pack.json", """{ "components": [ { "uid": "net.minecraft", "version": "1.19.2" } ] }""");

        if (!TryLinkDirectory(Path.Combine(_root, "prism", "Linked pack"), elsewhere))
        {
            return;
        }

        var found = ExternalInstanceScanner.Scan(Path.Combine(_root, "prism"), ExternalLauncherKind.Prism);

        Assert.Equal(2, found.Count);
        Assert.True(found.Single(i => i.Name == "Pack").IsUsable);

        var linked = found.Single(i => i.Name == "Linked pack");
        Assert.Equal(ExternalInstanceProblem.SourceIsLink, linked.Problem);

        // Nothing behind the link was read: not even its version.
        Assert.Equal(string.Empty, linked.VersionId);
    }

    [Fact]
    public void Scan_AGameFolderThatIsALinkIsRefused()
    {
        var instance = Path.Combine(_root, "prism", "Pack");
        Write(instance, "mmc-pack.json", """{ "components": [ { "uid": "net.minecraft", "version": "1.20.1" } ] }""");

        var real = Path.Combine(_root, "elsewhere", "game");
        Write(real, "saves/w/level.dat", "x");

        if (!TryLinkDirectory(Path.Combine(instance, ".minecraft"), real))
        {
            return;
        }

        var found = ScanPrism();

        Assert.Equal(ExternalInstanceProblem.SourceIsLink, found.Problem);
        Assert.Equal(real, found.LinkTarget, ignoreCase: true);
    }

    [Fact]
    public async Task Import_ThroughALinkIsRefusedInBothModes_AndCreatesNothing()
    {
        PrismInstance("Pack");
        var scanned = ScanPrism();

        var real = Path.Combine(_root, "elsewhere", "game");
        Write(real, "saves/w/level.dat", "x");
        var link = Path.Combine(_root, "link-to-game");

        if (!TryLinkDirectory(link, real))
        {
            return;
        }

        // Found as a real folder, a link by the time the button is pressed.
        var swapped = scanned with { GameDirectory = link };

        foreach (var mode in new[] { ImportMode.Link, ImportMode.Copy })
        {
            var error = await Assert.ThrowsAsync<LinkedSourceException>(() => _importer.ImportAsync(swapped, mode));
            Assert.Equal(real, error.Target, ignoreCase: true);
        }

        Assert.Empty(_instances.List());
    }

    [Fact]
    public async Task Copy_DoesNotFollowLinksInsideTheBuild_AndSaysWhichItLeft()
    {
        var instance = PrismInstance("Pack");
        var game = Path.Combine(instance, ".minecraft");
        Write(game, "mods/sodium.jar", "mod");

        var outside = Path.Combine(_root, "outside");
        Write(outside, "secret.txt", "not part of the build");
        Write(outside, "deep/more.txt", "neither is this");

        if (!TryLinkDirectory(Path.Combine(game, "screenshots"), outside) ||
            !TryLinkDirectory(Path.Combine(game, "saves", "shared-world"), outside))
        {
            return;
        }

        var source = ScanPrism();
        var estimate = InstanceImporter.EstimateCopySize(source);
        var result = await _importer.ImportAsync(source, ImportMode.Copy);
        var directory = _instances.GameDirectory(result.Instance);

        Assert.True(File.Exists(Path.Combine(directory, "mods", "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(directory, "saves", "world", "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(directory, "screenshots")));
        Assert.False(Directory.Exists(Path.Combine(directory, "saves", "shared-world")));
        Assert.Empty(Directory.EnumerateFiles(directory, "secret.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(directory, "more.txt", SearchOption.AllDirectories));

        Assert.Contains("screenshots", result.SkippedLinks);
        Assert.Contains(Path.Combine("saves", "shared-world"), result.SkippedLinks);

        // The estimate counts the same files the copy takes.
        Assert.Equal(estimate, result.CopiedBytes);
    }

    [Fact]
    public void Sweep_DoesNotEnterLinks()
    {
        var real = Path.Combine(_root, "real", "mc");
        Write(real, "versions/1.21.1/1.21.1.json", "{}");
        Directory.CreateDirectory(Path.Combine(real, "saves"));

        var drive = Path.Combine(_root, "drive");
        Directory.CreateDirectory(drive);

        if (!TryLinkDirectory(Path.Combine(drive, "Games"), Path.Combine(_root, "real")))
        {
            return;
        }

        Assert.Empty(DriveSweep.Sweep(new[] { drive }).Hits);
    }

    // ===================== 3. What is copied =====================

    /// <summary>A shared .minecraft with everything a real one accumulates.</summary>
    private ExternalInstance SharedRoot()
    {
        var mc = Path.Combine(_root, "shared", ".minecraft");
        const string id = "fabric-1.21.1";

        Write(mc, $"versions/{id}/{id}.json", """{ "id": "fabric-1.21.1", "inheritsFrom": "1.21.1", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient" }""");

        foreach (var folder in ImportCopyRules.SharedRootFolders)
        {
            Write(mc, $"{folder}/item.txt", folder);
        }

        foreach (var file in ImportCopyRules.SharedRootFiles)
        {
            Write(mc, file, file);
        }

        Write(mc, "launcher_accounts.json", "{ \"accessToken\": \"secret\" }");
        Write(mc, "launcher_accounts_microsoft_store.json", "secret");
        Write(mc, "launcher_msa_credentials.bin", "secret");
        Write(mc, "launcher_profiles.json", "{ \"profiles\": {} }");
        Write(mc, "TlauncherProfiles.json", "{}");
        Write(mc, "some_future_token_store.json", "secret nobody has heard of yet");
        Write(mc, "essential/microsoft_accounts.json", "secret");
        Write(mc, "screenshots/2026.png", "png");
        Write(mc, "logs/latest.log", "log");
        Write(mc, "assets/objects/aa/huge", new string('x', 4000));
        Write(mc, "config/somemod/accounts.json", "secret inside a copied folder");
        Write(mc, "config/somemod/settings.toml", "fine");

        return ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single();
    }

    [Fact]
    public async Task Copy_FromASharedRoot_TakesOnlyTheNamedSet()
    {
        var source = SharedRoot();
        Assert.True(source.SharesGameDirectory);

        var result = await _importer.ImportAsync(source, ImportMode.Copy);
        var directory = _instances.GameDirectory(result.Instance);

        var top = Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .Where(n => !string.Equals(n, InstanceManager.DefinitionFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ImportCopyRules.SharedRootFolders.Concat(ImportCopyRules.SharedRootFiles).OrderBy(n => n, StringComparer.Ordinal),
            top);

        // No sign-in anywhere in the copy, by any name - including inside a folder that is taken.
        Assert.Empty(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("secret", StringComparison.Ordinal)));
        Assert.True(File.Exists(Path.Combine(directory, "config", "somemod", "settings.toml")));
    }

    [Fact]
    public void Estimate_OfASharedRoot_CountsOnlyTheNamedSet()
    {
        var size = InstanceImporter.EstimateCopySize(SharedRoot());

        // The 4000-byte file is in assets/, and assets is not on the list.
        Assert.InRange(size, 1, 3999);
    }

    [Fact]
    public async Task Copy_FromABuildsOwnFolder_TakesEverythingButCachesLogsAndLauncherFiles()
    {
        var instance = Path.Combine(_root, "curseforge", "Pack");
        Write(instance, "minecraftinstance.json", """{ "name": "Pack", "gameVersion": "1.20.1", "baseModLoader": null }""");
        Write(instance, ".curseclient", "x");
        Write(instance, "mods/jei.jar", "mod");
        Write(instance, "saves/w/level.dat", "world");
        Write(instance, "screenshots/1.png", "png");
        Write(instance, "journeymap/data/map.dat", "map");
        Write(instance, "schematics/house.litematic", "s");
        Write(instance, "options.txt", "o");
        Write(instance, "servers.dat_old", "s");

        foreach (var cache in new[] { "logs", "crash-reports", ".cache", ".fabric", ".mixin.out", "webcache", "webcache2", "WebCache3" })
        {
            Write(instance, $"{cache}/file", "cache");
        }

        Write(instance, "launcher_accounts.json", "secret");
        Write(instance, "essential/microsoft_accounts.json", "secret");

        var source = ExternalInstanceScanner.Scan(Path.Combine(_root, "curseforge"), ExternalLauncherKind.CurseForge).Single();
        Assert.False(source.SharesGameDirectory);

        var result = await _importer.ImportAsync(source, ImportMode.Copy);
        var directory = _instances.GameDirectory(result.Instance);

        foreach (var kept in new[]
                 {
                     "mods/jei.jar", "saves/w/level.dat", "screenshots/1.png", "journeymap/data/map.dat",
                     "schematics/house.litematic", "options.txt", "servers.dat_old"
                 })
        {
            Assert.True(File.Exists(Path.Combine(directory, kept)), kept);
        }

        foreach (var left in new[]
                 {
                     "logs", "crash-reports", ".cache", ".fabric", ".mixin.out", "webcache", "webcache2", "WebCache3"
                 })
        {
            Assert.False(Directory.Exists(Path.Combine(directory, left)), left);
        }

        Assert.False(File.Exists(Path.Combine(directory, "minecraftinstance.json")));
        Assert.False(File.Exists(Path.Combine(directory, ".curseclient")));
        Assert.False(File.Exists(Path.Combine(directory, "launcher_accounts.json")));
        Assert.False(File.Exists(Path.Combine(directory, "essential", "microsoft_accounts.json")));

        // The launcher's own definition is its own, not the source's file of that name.
        var definition = File.ReadAllText(Path.Combine(directory, InstanceManager.DefinitionFileName));
        Assert.Contains(result.Instance.Id, definition, StringComparison.Ordinal);
    }

    // ===================== 4. Other drives =====================

    [Fact]
    public void Sweep_FindsGameFoldersAndLaunchersWithinThreeLevels()
    {
        var drive = Path.Combine(_root, "drive");

        // A moved game folder under a name of the player's own.
        Write(drive, "Games/MC/versions/1.21.1/1.21.1.json", "{}");
        Directory.CreateDirectory(Path.Combine(drive, "Games", "MC", "saves"));

        // A portable launcher two folders down.
        Write(drive, "Soft/Minecraft/MyLauncher/instances/Pack/mmc-pack.json", "{}");
        Write(drive, "Soft/Minecraft/MyLauncher/multimc.cfg", "x");

        // Too deep, in a system tree, and not a game folder at all.
        Write(drive, "a/b/c/d/.minecraft/versions/x/x.json", "{}");
        Directory.CreateDirectory(Path.Combine(drive, "a", "b", "c", "d", ".minecraft", "saves"));
        Write(drive, "Windows/.minecraft/versions/x/x.json", "{}");
        Directory.CreateDirectory(Path.Combine(drive, "Windows", ".minecraft", "saves"));
        Write(drive, "$Recycle.Bin/.minecraft/versions/x/x.json", "{}");
        Directory.CreateDirectory(Path.Combine(drive, "$Recycle.Bin", ".minecraft", "saves"));
        Write(drive, "Projects/app/versions/readme.txt", "a folder called versions and nothing of the game");

        var result = DriveSweep.Sweep(new[] { drive });

        Assert.False(result.TimedOut);
        Assert.Equal(2, result.Hits.Count);
        Assert.Contains(result.Hits, h => h.Path == Path.Combine(drive, "Games", "MC") && h.Kind == ExternalLauncherKind.DotMinecraft);
        Assert.Contains(result.Hits, h => h.Path == Path.Combine(drive, "Soft", "Minecraft", "MyLauncher") && h.Kind == ExternalLauncherKind.MultiMc);

        // And what it found is something the scanner can then open.
        Write(drive, "Games/MC/versions/1.21.1/1.21.1.json", """{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }""");
        Assert.Equal("1.21.1", ExternalInstanceScanner.ScanUnknownFolder(result.Hits.First(h => h.Kind == ExternalLauncherKind.DotMinecraft).Path).Single().VersionId);
    }

    [Fact]
    public void Sweep_StopsAtTheTimeLimitTheHitCapAndOnCancel()
    {
        var drive = Path.Combine(_root, "drive");

        for (var i = 0; i < 6; i++)
        {
            Write(drive, $"mc{i}/versions/x/x.json", "{}");
            Directory.CreateDirectory(Path.Combine(drive, $"mc{i}", "saves"));
        }

        var outOfTime = DriveSweep.Sweep(new[] { drive }, budget: TimeSpan.Zero);
        Assert.True(outOfTime.TimedOut);
        Assert.Empty(outOfTime.Hits);

        var capped = DriveSweep.Sweep(new[] { drive }, maxHits: 3);
        Assert.True(capped.HitLimitReached);
        Assert.Equal(3, capped.Hits.Count);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DriveSweep.Sweep(new[] { drive }, cancellationToken: cancelled.Token));

        Assert.Equal(6, DriveSweep.Sweep(new[] { drive }).Hits.Count);
    }

    [Fact]
    public void Sweep_NeverNamesTheSystemDrive()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory);

        Assert.DoesNotContain(DriveSweep.OtherDriveRoots(), r => string.Equals(r, system, StringComparison.OrdinalIgnoreCase));
    }

    // ===================== 5. The game version =====================

    [Theory]
    [InlineData("1.21.1", "1.21.1")]
    [InlineData("Fabric 1.21.11", "1.21.11")]
    [InlineData("Forge-1.12", "1.12")]
    [InlineData("Optifine 1.16.5 HD", "1.16.5")]
    [InlineData("OptiFine_1.16.5_HD_U_G8", "1.16.5")]
    [InlineData("fabric-loader-0.16.0-1.21.1", "1.21.1")]
    [InlineData("1.20.1-forge-47.2.0", "1.20.1")]
    [InlineData("Forge 1.12.2 14.23.5.2860", "1.12.2")]
    [InlineData("Fabric 26.2", "26.2")]
    [InlineData("NightfallCraft 2.2.9.7", null)]
    [InlineData("NightfallCraft 1.20.1", null)]
    [InlineData("SkyBlock 1.4", null)]
    [InlineData("MyPack v1.5", null)]
    [InlineData("Pack 1.7.10 edition 2", null)]
    [InlineData("NeoForge 21.1.77", null)]
    [InlineData("1.20.1 and 1.19.2", null)]
    [InlineData("1.20.1 1.19.2", null)]
    [InlineData("my build", null)]
    [InlineData("1.99", null)]
    [InlineData("", null)]
    public void NameIsReadAsAVersionOnlyWhenItIsNothingElse(string name, string? expected)
        => Assert.Equal(expected, GameVersionDetector.FromName(name));

    private ExternalInstance ScanVersion(string id, string json, Action<string>? arrange = null)
    {
        var mc = Path.Combine(_root, "mc-" + Guid.NewGuid().ToString("N")[..6]);
        Write(mc, $"versions/{id}/{id}.json", json);
        arrange?.Invoke(mc);

        return ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft).Single(i => i.VersionId == id);
    }

    [Fact]
    public void APackNamedLikeAVersion_IsNotGivenThatVersion()
    {
        // A complete profile of a pack, with nothing in it that names the game.
        var found = ScanVersion("NightfallCraft 1.4.7", """
            { "id": "NightfallCraft 1.4.7", "mainClass": "net.minecraft.launchwrapper.Launch", "libraries": [] }
            """);

        Assert.True(found.IsUsable);
        Assert.Null(found.GameVersion);
    }

    [Fact]
    public void TheVersionComesFromWhatTheProfileIsBuiltOn_NotFromItsName()
    {
        // The name says 2.2.9.7 and even carries a plausible "1.4"; the jar says 1.20.1.
        Assert.Equal("1.20.1", ScanVersion("NightfallCraft 1.4", """
            { "id": "NightfallCraft 1.4", "jar": "1.20.1", "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher" }
            """).GameVersion);

        Assert.Equal("1.21.1", ScanVersion("Pack 1.5", """
            { "id": "Pack 1.5", "mainClass": "x", "libraries": [ { "name": "net.neoforged:neoforge:21.1.77" } ] }
            """).GameVersion);

        Assert.Equal("1.20.1", ScanVersion("Pack 1.6", """
            { "id": "Pack 1.6", "mainClass": "x", "libraries": [ { "name": "net.minecraftforge:fmlloader:1.20.1-47.2.0" } ] }
            """).GameVersion);

        Assert.Equal("1.16.5", ScanVersion("Pack 1.7", """
            { "id": "Pack 1.7", "mainClass": "x", "libraries": [ { "name": "optifine:OptiFine:1.16.5_HD_U_G8" } ] }
            """).GameVersion);

        Assert.Equal("1.20.1", ScanVersion("Pack 1.8", """
            { "id": "Pack 1.8", "mainClass": "x", "arguments": { "game": [ "--fml.mcVersion", "1.20.1" ] } }
            """).GameVersion);
    }

    [Fact]
    public void AProfileBuiltOnAnotherCustomProfile_IsFollowedToTheGame()
    {
        var found = ScanVersion("ForgeOptiFine 1.3", """
            { "id": "ForgeOptiFine 1.3", "inheritsFrom": "My Forge", "mainClass": "x" }
            """,
            mc => Write(mc, "versions/My Forge/My Forge.json", """
                { "id": "My Forge", "inheritsFrom": "1.19.2", "mainClass": "x" }
                """));

        Assert.Equal("1.19.2", found.GameVersion);
    }

    [Fact]
    public void AParentThatIsNowhereAndNamesNoGame_IsNotTakenAsTheVersion()
    {
        var profile = JsonSerializer.Deserialize<VersionJson>(
            """{ "id": "x", "inheritsFrom": "SomePack 3.1", "mainClass": "x" }""", MetadataJson.Options)!;

        Assert.Null(ExternalInstanceScanner.DetectGameVersion(profile, "x"));
    }

    [Fact]
    public void TheLogIsAsked_InAFolderOnlyThisBuildPlaysIn()
    {
        // TLauncher's separate-folder mode: the build lives in its version folder.
        var found = ScanVersion("NightfallCraft 1.4", """
            { "id": "NightfallCraft 1.4", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient" }
            """,
            mc =>
            {
                Directory.CreateDirectory(Path.Combine(mc, "versions", "NightfallCraft 1.4", "mods"));
                Write(mc, "versions/NightfallCraft 1.4/logs/latest.log",
                    "[12:00:00] [main/INFO]: Loading Minecraft 1.20.4 with Fabric Loader 0.15.11\n");
            });

        Assert.True(found.HasOwnFolder);
        Assert.Equal("1.20.4", found.GameVersion);
    }

    [Fact]
    public void TheLogOfASharedFolder_BelongsToWhicheverVersionRanLast_AndIsNotAsked()
    {
        var found = ScanVersion("NightfallCraft 1.4", """
            { "id": "NightfallCraft 1.4", "mainClass": "x" }
            """,
            mc => Write(mc, "logs/latest.log", "[main/INFO]: Loading Minecraft 1.20.4 with Fabric Loader 0.15.11\n"));

        Assert.True(found.SharesGameDirectory);
        Assert.Null(found.GameVersion);
    }

    [Fact]
    public void AnUndescribedFolder_GetsItsVersionFromTheLog_NeverFromItsName()
    {
        var packs = Path.Combine(_root, "hand-made");
        Write(packs, "SkyBlock 1.4/saves/w/level.dat", "x");
        Write(packs, "SkyBlock 1.4/logs/latest.log",
            "[00:00:01] [main/INFO]: ModLauncher running: args [--username, P, --version, forge-47.2.0, --launchTarget, forgeclient, --fml.forgeVersion, 47.2.0, --fml.mcVersion, 1.20.1, --fml.forgeGroup, net.minecraftforge]\n");
        Write(packs, "Arcadia 1.7.10/saves/w/level.dat", "x");

        var found = ExternalInstanceScanner.Scan(packs, ExternalLauncherKind.Unknown);

        var withLog = found.Single(i => i.Name == "SkyBlock 1.4");
        Assert.Equal("1.20.1", withLog.VersionId);
        Assert.Equal(LoaderKind.Forge, withLog.Loader);
        Assert.True(withLog.VersionInferred);

        // No log, no mods: the name alone is not evidence, and the player picks.
        var withoutLog = found.Single(i => i.Name == "Arcadia 1.7.10");
        Assert.False(withoutLog.HasKnownVersion);
    }

    [Fact]
    public void ALegacyHomeFolderNamedAfterAPack_IsNotGivenAVersionByItsName()
    {
        var mc = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(Path.Combine(mc, "versions"));
        Directory.CreateDirectory(Path.Combine(mc, "home", "NightfallCraft 1.5", "saves"));
        Directory.CreateDirectory(Path.Combine(mc, "home", "1.8.9", "saves"));

        var found = ExternalInstanceScanner.Scan(mc, ExternalLauncherKind.DotMinecraft);

        Assert.Null(found.Single(i => i.Name == "NightfallCraft 1.5").GameVersion);
        Assert.Equal("1.8.9", found.Single(i => i.Name == "1.8.9").GameVersion);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // A recursive delete removes a link as a link; it does not reach through it.
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
