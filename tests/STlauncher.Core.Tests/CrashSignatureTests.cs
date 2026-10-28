using System;
using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Launch;
using Xunit;

namespace STlauncher.Core.Tests;

public class CrashSignatureTests
{
    private static IReadOnlyList<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static readonly IReadOnlyList<CrashModInfo> Mods = new[]
    {
        new CrashModInfo("sodium-fabric-0.6.13+mc1.21.4.jar", new[] { "sodium" }),
        new CrashModInfo("create-1.20.1-0.5.1.f.jar", new[] { "create", "flywheel" }),
        new CrashModInfo("fabric-api-0.119.2+1.21.4.jar", new[] { "fabric-api", "fabric-rendering-v1" }),
        new CrashModInfo("iris-fabric-1.8.8+mc1.21.4.jar", new[] { "iris" }),
    };

    // A vanilla crash report: the exception is right under "Description:".
    private const string VanillaReport = """
        ---- Minecraft Crash Report ----
        // Shall we play a game?

        Time: 2026-10-20 21:14:03
        Description: Rendering screen

        java.lang.NullPointerException: Cannot invoke "net.minecraft.class_1937.method_8510()" because "C:\Users\Vasya\AppData\Roaming\STlauncher\x" is null
        	at net.minecraft.class_757.method_3192(class_757.java:1105)
        	at net.caffeinemc.mods.sodium.client.render.SodiumWorldRenderer.setupTerrain(SodiumWorldRenderer.java:211)
        	at net.minecraft.class_310.method_1523(class_310.java:1342)
        	at net.minecraft.client.main.Main.main(Main.java:265)

        A detailed walkthrough of the error, its code path and all known details is as follows:
        ---------------------------------------------------------------------------------------
        -- Head --
        Thread: Render thread
        """;

    [Fact]
    public void VanillaCrashReport_ClassAndModFromPackage()
    {
        var signature = CrashSignature.Extract(Lines(VanillaReport), null, null, Mods);

        Assert.Equal("java.lang.NullPointerException @sodium", signature);
    }

    [Fact]
    public void VanillaCrashReport_WithoutMetadata_ClassOnly()
    {
        // Without the build's ids, a package word blames nobody.
        Assert.Equal("java.lang.NullPointerException", CrashSignature.Extract(Lines(VanillaReport), null));
    }

    [Fact]
    public void CrashReportPrintedToLog_IsRead()
    {
        var log = Lines("[21:14:02] [Render thread/INFO]: Stopping!\n" + VanillaReport +
                        "\n#@!@# Game crashed! Crash report saved to: #@!@# C:\\Users\\Vasya\\crash-reports\\crash-2026.txt");

        Assert.Equal("java.lang.NullPointerException @sodium", CrashSignature.Extract(null, log, null, Mods));
    }

    [Fact]
    public void Wrapper_IsLookedThrough_ToTheCause()
    {
        var report = Lines("""
            Description: Unexpected error

            java.util.concurrent.CompletionException: java.lang.IllegalStateException: Duplicate registration
            	at java.base/java.util.concurrent.CompletableFuture.encodeThrowable(CompletableFuture.java:315)
            Caused by: java.lang.IllegalStateException: Duplicate registration
            	at com.simibubi.create.foundation.data.CreateRegistrate.register(CreateRegistrate.java:88)
            	... 12 more
            """);

        Assert.Equal("java.lang.IllegalStateException @create", CrashSignature.Extract(report, null, null, Mods));
    }

    // Fabric: a mixin that failed to apply, as the log prints it.
    [Fact]
    public void FabricMixinError_OwnerFromTheMixinLine()
    {
        var log = Lines("""
            [21:02:11] [Render thread/INFO]: Loaded 48 mods
            [21:02:12] [main/ERROR]: Mixin apply for mod iris failed iris.mixins.json:MixinGameRenderer from mod iris -> net.minecraft.class_757: org.spongepowered.asm.mixin.injection.throwables.InvalidInjectionException Critical injection failure
            [21:02:12] [main/FATAL]: Unreported exception thrown!
            org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: An unexpected critical error was encountered
            	at org.spongepowered.asm.mixin.transformer.MixinProcessor.applyMixins(MixinProcessor.java:392)
            	at org.spongepowered.asm.mixin.transformer.MixinTransformer.transformClass(MixinTransformer.java:234)
            Caused by: org.spongepowered.asm.mixin.throwables.MixinApplyError: Mixin [iris.mixins.json:MixinGameRenderer from mod iris] from phase [DEFAULT] in config [iris.mixins.json] FAILED during APPLY
            	at org.spongepowered.asm.mixin.transformer.MixinProcessor.handleMixinError(MixinProcessor.java:638)
            """);

        Assert.Equal("org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError @iris", CrashSignature.Extract(null, log, null, Mods));
    }

    [Fact]
    public void FabricMixinHandlerFrame_NamesTheMod()
    {
        var log = Lines("""
            [12:00:00] [Render thread/FATAL]: Unreported exception thrown!
            java.lang.ClassCastException: class net.minecraft.class_1297 cannot be cast to class net.minecraft.class_1657
            	at net.minecraft.class_757.handler$zza000$sodium$onRenderWorld(class_757.java:5012)
            	at net.minecraft.class_757.method_3188(class_757.java:1270)
            """);

        Assert.Equal("java.lang.ClassCastException @sodium", CrashSignature.Extract(null, log, null, Mods));
    }

    [Fact]
    public void ForgeFrame_JarMapsToModId_NotFileName()
    {
        var report = Lines("""
            Description: Ticking entity

            java.lang.ArrayIndexOutOfBoundsException: Index 5 out of bounds for length 5
            	at net.minecraft.world.entity.Entity.tick(Entity.java:412) ~[client-1.20.1-20230612.114412-srg.jar%23411!/:?]
            	at com.example.obfuscated.a.b(SourceFile:12) ~[create-1.20.1-0.5.1.f.jar%23218!/:0.5.1.f] {re:classloading}
            """);

        var signature = CrashSignature.Extract(report, null, null, Mods);

        Assert.Equal("java.lang.ArrayIndexOutOfBoundsException @create", signature);
        Assert.DoesNotContain(".jar", signature);
    }

    [Fact]
    public void ForgeSuspectedMods_WhenFramesSayNothing()
    {
        var report = Lines("""
            Description: Exception in server tick loop

            java.lang.StackOverflowError: null
            	at java.base/java.util.HashMap.hash(HashMap.java:338)

            -- System Details --
            	Suspected Mods: Flywheel (flywheel), Minecraft (minecraft)
            """);

        Assert.Equal("java.lang.StackOverflowError @flywheel", CrashSignature.Extract(report, null, null, Mods));
    }

    [Fact]
    public void OutOfMemory_IsJustTheClass()
    {
        var log = Lines("""
            [19:40:01] [Render thread/FATAL]: Unreported exception thrown!
            java.lang.OutOfMemoryError: Java heap space
            	at java.base/java.util.Arrays.copyOf(Arrays.java:3541)
            """);

        Assert.Equal("java.lang.OutOfMemoryError", CrashSignature.Extract(null, log, null, Mods));
    }

    // The JVM died natively: the banner on the console, and the same head in hs_err_pid*.log.
    private const string HsErr = """
        #
        # A fatal error has been detected by the Java Runtime Environment:
        #
        #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffb1c2b5e10, pid=11804, tid=9412
        #
        # JRE version: OpenJDK Runtime Environment Temurin-21.0.4+7 (21.0.4+7) (build 21.0.4+7-LTS)
        # Java VM: OpenJDK 64-Bit Server VM Temurin-21.0.4+7 (21.0.4+7-LTS, mixed mode, sharing, tiered, compressed oops, compressed class ptrs, g1 gc, windows-amd64)
        # Problematic frame:
        # C  [atio6axx.dll+0x1b5e10]
        #
        # No core dump will be written. Minidumps are not enabled by default on client versions of Windows
        #
        # An error report file with more information is saved as:
        # C:\Users\Вася Пупкин\AppData\Roaming\STlauncher\instances\main\hs_err_pid11804.log
        #
        """;

    [Fact]
    public void HsErr_DriverDll_IsLabelledByVendor()
    {
        Assert.Equal("EXCEPTION_ACCESS_VIOLATION in amd-driver", CrashSignature.Extract(null, null, Lines(HsErr), Mods));
    }

    [Theory]
    [InlineData("# C  [nvoglv64.dll+0x8d4a1e]", "nvidia-driver")]
    [InlineData("# C  [ig9icd64.dll+0x1a2b3c]", "intel-driver")]
    [InlineData("# C  [RTSSHooks64.dll+0x3f00]", "rtss")]
    [InlineData("# C  [jvm.dll+0x3a1b2c]", "jvm")]
    [InlineData("# V  [jvm.dll+0x3a1b2c]", "jvm")]
    [InlineData("# J 4512 c2 net.minecraft.class_2338.method_10069(III)Lnet/minecraft/class_2338; (35 bytes) @ 0x0000021c", "java")]
    [InlineData("# C  [lwjgl_opengl.dll+0x1c2b]", "lwjgl")]
    [InlineData("# C  [ntdll.dll+0x1c2b]", "ntdll")]
    [InlineData("# C  [my_secret_tool_Vasya.dll+0x1c2b]", "native")]
    public void ProblematicFrame_Labels(string frame, string label)
    {
        Assert.Equal(label, CrashSignature.FrameLabel(frame));
    }

    [Fact]
    public void HsErrBanner_InTheLog_WinsOverJavaNoise()
    {
        var log = Lines("[12:00:00] [Worker-Main-3/ERROR]: Caught exception\njava.io.IOException: nope\n\tat a.b.C.d(C.java:1)\n" + HsErr);

        Assert.Equal("EXCEPTION_ACCESS_VIOLATION in amd-driver", CrashSignature.Extract(null, log));
    }

    [Fact]
    public void CyrillicMessage_DoesNotSurvive()
    {
        var log = Lines("""
            [20:11:45] [Render thread/FATAL]: Unreported exception thrown!
            java.lang.IllegalStateException: Не удалось загрузить мир «Мой мир» игрока Вася
            	at net.minecraft.class_310.method_1550(class_310.java:2011)
            """);

        var signature = CrashSignature.Extract(null, log, null, Mods);

        Assert.Equal("java.lang.IllegalStateException", signature);
        Assert.True(signature!.All(c => c < 128));
    }

    [Fact]
    public void NothingRecognisable_IsNull()
    {
        var log = Lines("[12:00:00] [main/INFO]: Setting user: Steve\n[12:00:05] [Render thread/INFO]: Stopping!");

        Assert.Null(CrashSignature.Extract(null, log, null, Mods));
    }

    [Fact]
    public void LongSignature_IsClipped()
    {
        var cls = "com." + string.Join(".", Enumerable.Repeat("verylongpackagename", 8)) + ".SomethingException";
        var log = Lines($"[12:00:00] [main/FATAL]: boom\n{cls}: x\n\tat a.b.C.d(C.java:1)");

        Assert.Equal(CrashSignature.MaxLength, CrashSignature.Extract(null, log)!.Length);
    }

    /// <summary>
    /// The privacy promise, tested on a crash that has everything personal in it: the
    /// signature is made only of a class name, a mod id and fixed labels.
    /// </summary>
    [Fact]
    public void PathsNicknamesAndAddresses_NeverSurvive()
    {
        var personal = new[] { "Vasya", "Вася", "Users", "AppData", "Steve_2011", "mc.showtime.su", "192.168.1.20", "25565", @":\", "/home/" };

        var cases = new List<IReadOnlyList<string>>
        {
            Lines("""
                [12:00:00] [main/INFO]: Setting user: Steve_2011
                [12:00:10] [Render thread/INFO]: Connecting to mc.showtime.su, 25565
                [12:00:11] [Render thread/FATAL]: Unreported exception thrown!
                java.io.FileNotFoundException: C:\Users\Vasya\AppData\Roaming\STlauncher\instances\main\options.txt (Отказано в доступе)
                	at java.base/java.io.FileInputStream.open0(Native Method)
                	at C:\Users\Vasya\evil.Frame(x.java:1)
                	at /home/vasya/mods/some.Thing(Thing.java:1) ~[/home/vasya/.minecraft/mods/secret-vasya-mod.jar:?]
                """),
            Lines("""
                [12:00:11] [Server thread/FATAL]: Exception in thread "Steve_2011's thread"
                java.net.ConnectException: Connection refused: mc.showtime.su/192.168.1.20:25565
                	at io.netty.channel.unix.Errors.newConnectException0(Errors.java:166)
                """),
            Lines(HsErr),
        };

        foreach (var lines in cases)
        {
            foreach (var asReport in new[] { false, true })
            {
                var signature = asReport
                    ? CrashSignature.Extract(lines, null, null, Mods)
                    : CrashSignature.Extract(null, lines, lines, Mods);

                if (signature is null)
                {
                    continue;
                }

                foreach (var word in personal)
                {
                    Assert.DoesNotContain(word, signature, StringComparison.OrdinalIgnoreCase);
                }

                Assert.Matches(@"^[A-Za-z0-9_$.]+(?: @[a-z0-9_\-]+| in [a-z0-9\-]+)?$", signature);
            }
        }
    }

    [Fact]
    public void UnknownJar_IsNeverSentAsAFileName()
    {
        var log = Lines("""
            [12:00:11] [Render thread/FATAL]: Unreported exception thrown!
            java.lang.NullPointerException: null
            	at com.vasya.privatemod.Thing.run(Thing.java:1) ~[vasya-private-mod-1.0.jar:?]
            """);

        Assert.Equal("java.lang.NullPointerException", CrashSignature.Extract(null, log, null, Mods));
    }

    [Fact]
    public void JvmErrorReportPath_IsFoundInTheBanner()
    {
        Assert.Equal(
            @"C:\Users\Вася Пупкин\AppData\Roaming\STlauncher\instances\main\hs_err_pid11804.log",
            CrashAnalyzer.FindJvmErrorReportPath(Lines(HsErr)));
    }

    [Fact]
    public void HsErr_Analyzer_GraphicsDriverCrash()
    {
        var result = CrashAnalyzer.Analyze(Lines(HsErr));

        Assert.Equal(CrashCause.GraphicsDriverCrash, result.Cause);
        Assert.Equal("AMD", result.Subject);
    }

    [Fact]
    public void HsErr_Analyzer_OverlayCrash()
    {
        var result = CrashAnalyzer.Analyze(new[] { "# Problematic frame:", "# C  [DiscordHook64.dll+0x2f1a0]" });

        Assert.Equal(CrashCause.OverlayCrash, result.Cause);
        Assert.Equal("Discord", result.Subject);
    }

    [Fact]
    public void Lwjgl2_PixelFormatNotAccelerated_IsNoOpenGl()
    {
        var log = new[] { "org.lwjgl.LWJGLException: Pixel format not accelerated", "\tat org.lwjgl.opengl.WindowsPeerInfo.nChoosePixelFormat(Native Method)" };

        Assert.Equal(CrashCause.NoOpenGl, CrashAnalyzer.Analyze(log).Cause);
    }

    [Fact]
    public void OutOfMemoryError_StaysOutOfMemory()
    {
        Assert.Equal(CrashCause.OutOfMemory, CrashAnalyzer.Analyze(new[] { "java.lang.OutOfMemoryError: Java heap space" }).Cause);
    }

    [Fact]
    public void NativeFrame_InUnknownLibrary_IsNotADriverCrash()
    {
        Assert.Equal(CrashCause.Unknown, CrashAnalyzer.Analyze(new[] { "# C  [ntdll.dll+0x1c2b]" }).Cause);
    }
}
