using System.Linq;
using STlauncher.Core.Launch;
using Xunit;

namespace STlauncher.Core.Tests;

public class CrashAnalyzerTests
{
    [Fact]
    public void Fabric_MissingDependency_NamesBothMods()
    {
        var log = new[]
        {
            "[main/INFO]: Loading 12 mods:",
            "[main/ERROR]: Incompatible mods found!",
            " - Mod 'Sodium Extra' (sodium-extra) 0.9.1+fabric requires version 0.8.12 or later of 'sodium' (sodium), which is missing!",
            "Exception in thread \"main\" net.fabricmc.loader.impl.FormattedException: Mod resolution failed"
        };

        var result = CrashAnalyzer.Analyze(log);

        Assert.Equal(CrashCause.MissingDependency, result.Cause);
        Assert.Equal("Sodium Extra", result.Subject);
        Assert.Equal("sodium", result.Detail);
    }

    [Fact]
    public void Fabric_AnyVersionOfFabricApi_IsMissingDependency()
    {
        var log = new[]
        {
            " - Mod 'Xaero's Minimap' (xaerominimap) 26.5.0 requires any version of fabric-api, which is missing!"
        };

        var result = CrashAnalyzer.Analyze(log);

        Assert.Equal(CrashCause.MissingDependency, result.Cause);
        Assert.Equal("fabric-api", result.Detail);
    }

    [Fact]
    public void Fabric_RequiresOtherMinecraft_IsVersionMismatch()
    {
        var log = new[]
        {
            " - Mod 'Lithium' (lithium) mc1.20.4-0.12.1 requires version 1.20.4 of minecraft, which is missing!"
        };

        var result = CrashAnalyzer.Analyze(log);

        Assert.Equal(CrashCause.ModForOtherVersion, result.Cause);
        Assert.Equal("Lithium", result.Subject);
    }

    [Fact]
    public void Forge_MissingMandatoryDependency()
    {
        var log = new[]
        {
            "Missing or unsupported mandatory dependencies:",
            "\tMod ID: 'cloth_config', Requested by: 'xaerominimap', Expected range: '[11.0.0,)', Actual version: '[MISSING]'"
        };

        var result = CrashAnalyzer.Analyze(log);

        Assert.Equal(CrashCause.MissingDependency, result.Cause);
        Assert.Equal("xaerominimap", result.Subject);
        Assert.Equal("cloth_config", result.Detail);
    }

    [Fact]
    public void Forge_WrongMinecraftRange_IsVersionMismatch()
    {
        var log = new[]
        {
            "\tMod ID: 'minecraft', Requested by: 'jei', Expected range: '[1.20.1]', Actual version: '1.20.4'"
        };

        Assert.Equal(CrashCause.ModForOtherVersion, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void OutOfMemory_IsRecognised()
    {
        var log = new[] { "[Render thread/FATAL]: Error executing task", "java.lang.OutOfMemoryError: Java heap space" };

        Assert.Equal(CrashCause.OutOfMemory, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void JavaTooOld_ReportsRequiredVersion()
    {
        var log = new[]
        {
            "Error: LinkageError occurred while loading main class net.minecraft.client.main.Main",
            "\tjava.lang.UnsupportedClassVersionError: net/minecraft/client/main/Main has been compiled by a more recent version of the Java Runtime (class file version 65.0), this version of the Java Runtime only recognizes class file versions up to 61.0"
        };

        var result = CrashAnalyzer.Analyze(log);

        Assert.Equal(CrashCause.JavaTooOld, result.Cause);
        Assert.Equal("21", result.Detail);
    }

    [Fact]
    public void GraphicsDriver_IsRecognised()
    {
        var log = new[] { "[main/ERROR]: GLFW error 65542: WGL: The driver does not appear to support OpenGL" };

        Assert.Equal(CrashCause.Graphics, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void GraphicsDriver_NativeCrashFrame()
    {
        var log = new[] { "# Problematic frame:", "# C  [ig9icd64.dll+0x1a2b3c]" };

        Assert.Equal(CrashCause.Graphics, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void DuplicateMod_IsRecognised()
    {
        var log = new[] { "[main/ERROR]: Duplicate mod id \"sodium\" in mods/sodium-0.5.jar and mods/sodium-0.6.jar" };

        var result = CrashAnalyzer.Analyze(log);

        Assert.Equal(CrashCause.DuplicateMod, result.Cause);
        Assert.Equal("sodium", result.Subject);
    }

    [Fact]
    public void MixinFailure_NamesTheMod()
    {
        var log = new[]
        {
            "org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: An unexpected critical error was encountered",
            "Caused by: org.spongepowered.asm.mixin.throwables.MixinApplyError: Mixin [chatanimation.mixins.json:ChatHudMixin from mod chatanimation] from phase [DEFAULT] in config [chatanimation.mixins.json] FAILED during APPLY"
        };

        var result = CrashAnalyzer.Analyze(log);

        Assert.Equal(CrashCause.MixinFailure, result.Cause);
        Assert.Equal("chatanimation", result.Subject);
    }

    [Fact]
    public void MissingDependency_OutranksTheMixinErrorItCauses()
    {
        var log = new[]
        {
            "Caused by: org.spongepowered.asm.mixin.throwables.MixinApplyError: Mixin [x from mod y] FAILED during APPLY",
            " - Mod 'Y' (y) 1.0 requires any version of fabric-api, which is missing!"
        };

        Assert.Equal(CrashCause.MissingDependency, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void BrokenInstallation_IsRecognised()
    {
        var log = new[] { "Error: Could not find or load main class net.fabricmc.loader.impl.launch.knot.KnotClient" };

        Assert.Equal(CrashCause.BrokenInstallation, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void DiskFull_IsRecognised()
    {
        var log = new[] { "java.io.IOException: There is not enough space on the disk" };

        Assert.Equal(CrashCause.DiskFull, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void NothingRecognised_IsUnknown()
    {
        var log = new[] { "[main/INFO]: Loading Minecraft 1.21.4", "[main/INFO]: Backend library: LWJGL version 3.3.3" };

        Assert.Equal(CrashCause.Unknown, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void CrashReportPath_IsExtracted()
    {
        var log = new[]
        {
            "[Render thread/INFO]: #@!@# Game crashed! Crash report saved to: #@!@# C:\\games\\crash-reports\\crash-2026-09-22_10.11.12-client.txt"
        };

        Assert.Equal("C:\\games\\crash-reports\\crash-2026-09-22_10.11.12-client.txt", CrashAnalyzer.FindCrashReportPath(log));
    }

    [Fact]
    public void Report_ContainsBuildDiagnosisAndTail()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"line {i}").ToList();
        lines[10] = "java.lang.NullPointerException: early";

        var context = new CrashReportContext("0.3.2", "Showtime", "1.21.11", "Fabric", "0.16.9", null, 4096, 1, new[] { "sodium.jar" });
        var diagnosis = new CrashDiagnosis(CrashCause.OutOfMemory, Evidence: "java.lang.OutOfMemoryError");

        var report = CrashReport.Build(context, diagnosis, lines);

        Assert.Contains("STlauncher 0.3.2", report);
        Assert.Contains("Showtime · Minecraft 1.21.11 · Fabric 0.16.9", report);
        Assert.Contains("out of memory", report);
        Assert.Contains("sodium.jar", report);
        Assert.Contains("NullPointerException: early", report);
        Assert.Contains("line 199", report);
        Assert.DoesNotContain("line 50\n", report);
    }
}
