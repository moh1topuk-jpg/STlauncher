using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using STlauncher.Core.Launch;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// A game that dies at once, and a log that reads right: what the JVM prints when it
/// cannot start, Windows status codes, output in two encodings at once, the home folder
/// kept out of what is shown, and one game per build.
/// </summary>
public class LaunchFailureTests
{
    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static LaunchResult Instant(int exitCode = 1)
        => new(exitCode, Started: false, TimeSpan.FromSeconds(1), Array.Empty<string>());

    // ---------------------------------------------------------------- what the JVM says

    // As java.exe prints them, consequence lines included.

    private static readonly string[] MissingJavaDll =
    {
        "Error: could not find java.dll",
        "Error: Could not find Java SE Runtime Environment."
    };

    private static readonly string[] MissingServerJvm =
    {
        @"Error: missing `server' JVM at `C:\Users\Anna\AppData\Roaming\STlauncher\runtime\java-17\bin\server\jvm.dll'.",
        "Please install or use the JRE or JDK that contains these missing components."
    };

    private static readonly string[] BootClassPath =
    {
        "Error occurred during initialization of VM",
        "Failed setting boot class path."
    };

    private static readonly string[] UnrecognizedVmOption =
    {
        "Unrecognized VM option 'UseConcMarkSweepGC'",
        "Error: Could not create the Java Virtual Machine.",
        "Error: A fatal exception has occurred. Program will exit."
    };

    private static readonly string[] UnrecognizedOption =
    {
        "Unrecognized option: --add-opens",
        "Error: Could not create the Java Virtual Machine.",
        "Error: A fatal exception has occurred. Program will exit."
    };

    private static readonly string[] InvalidHeap =
    {
        "Invalid maximum heap size: -Xmx8192M",
        "The specified size exceeds the maximum representable size.",
        "Error: Could not create the Java Virtual Machine.",
        "Error: A fatal exception has occurred. Program will exit."
    };

    private static readonly string[] HeapLargerThanMaximum =
    {
        "Error occurred during initialization of VM",
        "Initial heap size set to a larger value than the maximum heap size"
    };

    private static readonly string[] NoRoomForHeap =
    {
        "Error occurred during initialization of VM",
        "Could not reserve enough space for 8388608KB object heap"
    };

    private static readonly string[] ConflictingCollectors =
    {
        "Conflicting collector combinations in option list; please refer to the release notes for the combinations allowed",
        "Error: Could not create the Java Virtual Machine.",
        "Error: A fatal exception has occurred. Program will exit."
    };

    [Fact]
    public void AJavaWithFilesMissingIsCalledDamaged_AndTheLineSaysWhich()
    {
        var dll = LaunchFailureAnalyzer.Analyze(Instant(), MissingJavaDll);
        Assert.Equal(CrashCause.JavaBroken, dll.Cause);
        Assert.Equal("could not find java.dll", dll.Subject);

        var server = LaunchFailureAnalyzer.Analyze(Instant(), MissingServerJvm);
        Assert.Equal(CrashCause.JavaBroken, server.Cause);
        Assert.StartsWith("missing `server' JVM", server.Subject);

        var boot = LaunchFailureAnalyzer.Analyze(Instant(), BootClassPath);
        Assert.Equal(CrashCause.JavaBroken, boot.Cause);
        Assert.Equal("Failed setting boot class path.", boot.Subject);
    }

    [Fact]
    public void AnUnknownOptionIsNamed_NotTheLineThatFollowsEveryFailure()
    {
        var vm = LaunchFailureAnalyzer.Analyze(Instant(), UnrecognizedVmOption);
        Assert.Equal(CrashCause.JavaOption, vm.Cause);
        Assert.Equal("UseConcMarkSweepGC", vm.Subject);
        Assert.Equal("Unrecognized VM option 'UseConcMarkSweepGC'", vm.Evidence);

        var plain = LaunchFailureAnalyzer.Analyze(Instant(), UnrecognizedOption);
        Assert.Equal(CrashCause.JavaOption, plain.Cause);
        Assert.Equal("--add-opens", plain.Subject);
    }

    [Fact]
    public void ARefusedHeapSizeIsItsOwnCause()
    {
        var invalid = LaunchFailureAnalyzer.Analyze(Instant(), InvalidHeap);
        Assert.Equal(CrashCause.JavaMemory, invalid.Cause);
        Assert.Equal("-Xmx8192M", invalid.Subject);

        var larger = LaunchFailureAnalyzer.Analyze(Instant(), HeapLargerThanMaximum);
        Assert.Equal(CrashCause.JavaMemory, larger.Cause);
        Assert.Null(larger.Subject);
    }

    [Fact]
    public void NoRoomForTheHeapStaysWithTheCauseThatHasAFix()
        => Assert.Equal(CrashCause.SystemMemory, LaunchFailureAnalyzer.Analyze(Instant(), NoRoomForHeap).Cause);

    [Fact]
    public void AnUnnamedFailureShowsTheCauseLine_NotTheConsequence()
    {
        var diagnosis = LaunchFailureAnalyzer.Analyze(Instant(), ConflictingCollectors);

        Assert.Equal(CrashCause.JavaNotStarted, diagnosis.Cause);
        Assert.StartsWith("Conflicting collector combinations", diagnosis.Subject);
        Assert.DoesNotContain("Could not create", diagnosis.Subject);
    }

    [Fact]
    public void AGameLogLineThatQuotesTheJvmIsNotTheJvmSpeaking()
    {
        var lines = new[]
        {
            "[12:00:01] [main/INFO]: Unrecognized option: --fancy passed to SomeMod",
            "[12:00:02] [main/ERROR]: Mod 'A' (a) 1.0 requires any version of b, which is missing!"
        };

        Assert.Equal(CrashCause.Unknown, LaunchFailureAnalyzer.Analyze(Instant(), lines).Cause);
    }

    [Fact]
    public void AJavaThatWasNeverRunIsNamedByWhatStoppedIt()
    {
        var missing = LaunchResult.NotStarted(JavaStartError.NotFound, "The system cannot find the file specified.");
        var blocked = LaunchResult.NotStarted(JavaStartError.AccessDenied, "Access is denied.");

        Assert.True(missing.IsFailedLaunch);
        Assert.Equal(CrashCause.JavaMissing, LaunchFailureAnalyzer.Analyze(missing, Array.Empty<string>()).Cause);
        Assert.Equal(CrashCause.JavaBlocked, LaunchFailureAnalyzer.Analyze(blocked, Array.Empty<string>()).Cause);
    }

    [Fact]
    public void OnlyAnEarlyExitBeforeTheGameStartedIsAFailedLaunch()
    {
        Assert.True(Instant().IsFailedLaunch);

        // The player closed the game normally a few seconds in.
        Assert.False(new LaunchResult(0, false, TimeSpan.FromSeconds(3), Array.Empty<string>()).IsFailedLaunch);

        // It started, then crashed: a session, and a crash in the game.
        var crashed = new LaunchResult(1, true, TimeSpan.FromSeconds(8), UnrecognizedVmOption);
        Assert.False(crashed.IsFailedLaunch);
        Assert.Equal(CrashCause.Unknown, LaunchFailureAnalyzer.Analyze(crashed, UnrecognizedVmOption).Cause);

        // No marker, but it lived for minutes: whatever it was, it ran.
        Assert.False(new LaunchResult(1, false, TimeSpan.FromMinutes(5), Array.Empty<string>()).IsFailedLaunch);
    }

    // ---------------------------------------------------------------- Windows status codes

    [Theory]
    [InlineData(-1073741819,"0xC0000005", NtStatusKind.AccessViolation)]
    [InlineData(unchecked((int)0xC0000135), "0xC0000135", NtStatusKind.MissingDll)]
    [InlineData(unchecked((int)0xC000007B), "0xC000007B", NtStatusKind.MissingDll)]
    [InlineData(unchecked((int)0xC0000142), "0xC0000142", NtStatusKind.InitFailed)]
    [InlineData(unchecked((int)0xC00000FD), "0xC00000FD", NtStatusKind.StackOverflow)]
    [InlineData(unchecked((int)0xC0000409), "0xC0000409", NtStatusKind.StackBufferOverrun)]
    [InlineData(unchecked((int)0xC0000017), "0xC0000017", NtStatusKind.OutOfMemory)]
    [InlineData(unchecked((int)0xC000012D), "0xC000012D", NtStatusKind.OutOfMemory)]
    [InlineData(unchecked((int)0xC0000374), "0xC0000374", NtStatusKind.Unknown)]
    public void AStatusCodeIsShownAsHex_AndTheCommonOnesAreNamed(int exitCode, string shown, NtStatusKind kind)
    {
        Assert.True(NtStatus.IsNtStatus(exitCode));
        Assert.Equal(shown, NtStatus.Format(exitCode));
        Assert.Equal(kind, NtStatus.Classify(exitCode));
        Assert.False(string.IsNullOrWhiteSpace(NtStatus.Describe(exitCode)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(255)]
    [InlineData(130)]
    public void TheGamesOwnExitCodesStayNumbers(int exitCode)
    {
        Assert.False(NtStatus.IsNtStatus(exitCode));
        Assert.Equal(exitCode.ToString(), NtStatus.Format(exitCode));
        Assert.Equal(NtStatusKind.Unknown, NtStatus.Classify(exitCode));
    }

    // ---------------------------------------------------------------- decoding

    private static readonly Encoding Cyrillic = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;

    [Fact]
    public void ALineIsUtf8WhenItIsValidUtf8_AndTheSystemCodePageWhenItIsNot()
    {
        const string text = @"C:\Users\Дмитрий\AppData\Roaming\.minecraft";

        Assert.Equal(text, GameOutputDecoder.Decode(Encoding.UTF8.GetBytes(text), Cyrillic));
        Assert.Equal(text, GameOutputDecoder.Decode(Cyrillic.GetBytes(text), Cyrillic));
        Assert.Equal("plain ascii", GameOutputDecoder.Decode(Encoding.ASCII.GetBytes("plain ascii"), Cyrillic));
        Assert.Equal(string.Empty, GameOutputDecoder.Decode(ReadOnlySpan<byte>.Empty, Cyrillic));
    }

    [Fact]
    public async Task OneStreamInTwoEncodingsReadsRight_AndABadByteDoesNotEndIt()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Cyrillic.GetBytes("Ошибка: не найден файл\r\n"));
        bytes.AddRange(Encoding.UTF8.GetBytes("[main/INFO]: Загрузка мира\n"));
        bytes.AddRange(new byte[] { (byte)'a', 0xFF, 0xFE, (byte)'b', (byte)'\n' });
        bytes.AddRange(Encoding.UTF8.GetBytes("\n"));
        bytes.AddRange(Encoding.UTF8.GetBytes("last line, no line break"));

        var lines = new List<string>();
        await GameOutputDecoder.PumpAsync(new Trickle(bytes.ToArray()), lines.Add, Cyrillic);

        Assert.Equal(5, lines.Count);
        Assert.Equal("Ошибка: не найден файл", lines[0]);
        Assert.Equal("[main/INFO]: Загрузка мира", lines[1]);
        Assert.StartsWith("a", lines[2]);
        Assert.EndsWith("b", lines[2]);
        Assert.Equal(4, lines[2].Length);
        Assert.Equal(string.Empty, lines[3]);
        Assert.Equal("last line, no line break", lines[4]);
    }

    [Fact]
    public async Task ALineWithNoEndIsCut_NotGrownWithoutLimit()
    {
        var bytes = Enumerable.Repeat((byte)'x', GameOutputDecoder.MaxLineBytes * 2 + 10).ToArray();
        var lines = new List<string>();

        await GameOutputDecoder.PumpAsync(new MemoryStream(bytes), lines.Add, Cyrillic);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.True(line.Length <= GameOutputDecoder.MaxLineBytes));
        Assert.Equal(bytes.Length, lines.Sum(l => l.Length));
    }

    [Fact]
    public async Task ASubscriberThatThrowsDoesNotEndTheStream()
    {
        var seen = 0;

        await GameOutputDecoder.PumpAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo\nthree\n")),
            _ =>
            {
                seen++;
                throw new InvalidOperationException("boom");
            },
            Cyrillic);

        Assert.Equal(3, seen);
    }

    /// <summary>Hands the bytes over a few at a time, so a character and a line break both get split across reads.</summary>
    private sealed class Trickle : MemoryStream
    {
        public Trickle(byte[] bytes) : base(bytes)
        {
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }

    [Fact]
    public void TheGameIsToldToPrintUtf8_UnlessItsOwnArgumentsSayOtherwise()
    {
        var all = LaunchCommandBuilder.OutputEncodingArguments(new[] { "-Xmx2G", "-XX:+UseG1GC" });

        Assert.Equal(
            new[]
            {
                "-Dfile.encoding=UTF-8",
                "-Dsun.stdout.encoding=UTF-8",
                "-Dsun.stderr.encoding=UTF-8",
                "-Dstdout.encoding=UTF-8",
                "-Dstderr.encoding=UTF-8"
            },
            all);

        var some = LaunchCommandBuilder.OutputEncodingArguments(new[] { "-Dfile.encoding=windows-1251", "-Dstdout.encoding=IBM866" });

        Assert.DoesNotContain(some, a => a.StartsWith("-Dfile.encoding=", StringComparison.Ordinal));
        Assert.DoesNotContain(some, a => a.StartsWith("-Dstdout.encoding=", StringComparison.Ordinal));
        Assert.Contains("-Dsun.stdout.encoding=UTF-8", some);
        Assert.Contains("-Dstderr.encoding=UTF-8", some);
    }

    [Fact]
    public void TheEncodingArgumentsComeBeforeTheMainClass()
    {
        var command = LaunchCommandBuilder.Build(
            new LaunchOptions
            {
                Version = new Metadata.ResolvedVersion { Id = "1.12.2", MainClass = "net.minecraft.client.main.Main" },
                Account = Auth.OfflineAuth.Login("Steve"),
                JavaPath = "java",
                GameDirectory = "game",
                AssetsDirectory = "assets",
                NativesDirectory = "natives",
                LibrariesDirectory = "libraries",
                Classpath = new[] { "a.jar" }
            },
            new Metadata.RuleContext { OsName = "windows", Arch = "x86_64" });

        var arguments = command.Arguments.ToList();

        Assert.True(arguments.IndexOf("-Dfile.encoding=UTF-8") >= 0);
        Assert.True(arguments.IndexOf("-Dfile.encoding=UTF-8") < arguments.IndexOf("net.minecraft.client.main.Main"));
        Assert.Single(arguments, a => a == "-Dsun.stderr.encoding=UTF-8");
    }

    // ---------------------------------------------------------------- the process itself

    [Fact]
    public async Task AJavaThatIsNotThereIsAResult_NotAnException()
    {
        var launcher = new GameLauncher();
        var result = await launcher.RunDetailedAsync(
            new LaunchCommand(Path.Combine(Temp(), "bin", "java.exe"), new[] { "-version" }),
            Temp());

        Assert.Equal(JavaStartError.NotFound, result.StartError);
        Assert.True(result.IsFailedLaunch);
        Assert.False(string.IsNullOrWhiteSpace(result.StartErrorMessage));
    }

    [Fact]
    public async Task ARunThatDiesAtOnceKeepsWhatItPrinted_AndIsNotASession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // cmd stands in for a Java that cannot start.
        var launcher = new GameLauncher();
        var started = false;
        launcher.GameStarted += () => started = true;

        var result = await launcher.RunDetailedAsync(
            new LaunchCommand("cmd.exe", new[] { "/c", "echo Error: could not find java.dll 1>&2& exit 1" }),
            Temp());

        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Started);
        Assert.False(started);
        Assert.True(result.IsFailedLaunch);
        Assert.Equal(CrashCause.JavaBroken, LaunchFailureAnalyzer.Analyze(result, result.EarlyOutput).Cause);
    }

    [Fact]
    public async Task OutputInTheSystemCodePageAndAStartMarkerBothArrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Temp();
        var file = Path.Combine(directory, "out.txt");
        var ansi = GameOutputDecoder.SystemAnsi;

        // Only what the system code page can hold survives the trip through it.
        var native = ansi.GetString(ansi.GetBytes("Файл не найден"));

        var bytes = new List<byte>();
        bytes.AddRange(ansi.GetBytes(native + "\r\n"));
        bytes.AddRange(Encoding.UTF8.GetBytes("[main/INFO]: Мир загружен\r\n"));
        bytes.AddRange(Encoding.UTF8.GetBytes("[main/INFO]: Setting user: Steve\r\n"));
        File.WriteAllBytes(file, bytes.ToArray());

        var launcher = new GameLauncher();
        var lines = new List<string>();
        var started = false;
        launcher.OutputReceived += line => { lock (lines) { lines.Add(line); } };
        launcher.GameStarted += () => started = true;

        // "type" copies the bytes as they are.
        var logs = Path.Combine(directory, "logs");
        var result = await launcher.RunDetailedAsync(new LaunchCommand("cmd.exe", new[] { "/c", "type", file }), directory, logs);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Started);
        Assert.True(started);
        Assert.False(result.IsFailedLaunch);
        Assert.Contains("[main/INFO]: Мир загружен", lines);
        Assert.Contains("[main/INFO]: Setting user: Steve", lines);
        Assert.Contains(native, lines);

        // The log on disk is the decoded text, in UTF-8.
        Assert.Contains("[main/INFO]: Мир загружен", File.ReadAllLines(launcher.LogFilePath!));
    }

    // ---------------------------------------------------------------- masking

    [Fact]
    public void TheHomeFolderIsTakenOutOfWhatIsShown()
    {
        const string home = @"C:\Users\Anna Petrova";

        Assert.Equal(
            @"Error: missing `server' JVM at `%USERPROFILE%\AppData\Roaming\STlauncher\runtime\java-17\bin\server\jvm.dll'.",
            PathMask.Mask(@"Error: missing `server' JVM at `C:\Users\Anna Petrova\AppData\Roaming\STlauncher\runtime\java-17\bin\server\jvm.dll'.", home));

        // As Java prints a path, as JSON keeps it, in another case, and at the end of a line.
        Assert.Equal("at %USERPROFILE%/mods/a.jar", PathMask.Mask("at C:/Users/Anna Petrova/mods/a.jar", home));
        Assert.Equal(@"""%USERPROFILE%\\x""", PathMask.Mask(@"""C:\\Users\\Anna Petrova\\x""", home));
        Assert.Equal(@"%USERPROFILE%\x", PathMask.Mask(@"c:\users\anna petrova\x", home));
        Assert.Equal("--gameDir %USERPROFILE%", PathMask.Mask(@"--gameDir C:\Users\Anna Petrova", home));
        Assert.Equal(@"%USERPROFILE%\a;%USERPROFILE%\b", PathMask.Mask(@"C:\Users\Anna Petrova\a;C:\Users\Anna Petrova\b", home));
    }

    [Fact]
    public void AnotherUsersFolderThatBeginsTheSameIsNotMasked_AndAShortHomeMasksNothing()
    {
        Assert.Equal(@"C:\Users\Annabel\x", PathMask.Mask(@"C:\Users\Annabel\x", @"C:\Users\Anna"));
        Assert.Equal(@"C:\Users\Anna\x", PathMask.Mask(@"C:\Users\Anna\x", @"C:\"));
        Assert.Equal(@"C:\Users\Anna\x", PathMask.Mask(@"C:\Users\Anna\x", null));
        Assert.Equal(string.Empty, PathMask.Mask(null, @"C:\Users\Anna"));
    }

    [Fact]
    public void TheCopiedReportCarriesNoHomeFolder_AndAStatusCodeAsHex()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(home) || home.Length < 4)
        {
            return;
        }

        var java = Path.Combine(home, "jdk", "bin", "java.exe");
        var context = new CrashReportContext("1.0", "Build", "1.20.1", "Fabric", "0.16", java, 4096, unchecked((int)0xC0000005), Array.Empty<string>());
        var report = CrashReport.Build(context, CrashDiagnosis.None, new[] { $"Loading {Path.Combine(home, "mods", "a.jar")}" });

        Assert.DoesNotContain(home, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PathMask.Placeholder, report);
        Assert.Contains("exit code 0xC0000005", report);
    }

    // ---------------------------------------------------------------- one game per build

    [Theory]
    [InlineData(@"""C:\java\bin\javaw.exe"" -Xmx4G -cp a.jar;b.jar net.minecraft.client.main.Main --username Steve --gameDir C:\Games\build --assetsDir C:\a", @"C:\Games\build")]
    [InlineData(@"java.exe -cp x Main --gameDir ""C:\Users\Anna Petrova\AppData\Roaming\STlauncher\instances\my build"" --width 854", @"C:\Users\Anna Petrova\AppData\Roaming\STlauncher\instances\my build")]
    [InlineData(@"java.exe Main --gameDir ""C:\Games\build\\"" --demo", @"C:\Games\build\\")]
    [InlineData(@"java.exe Main --gameDir", null)]
    [InlineData(@"java.exe -jar server.jar nogui", null)]
    [InlineData(@"java.exe Main --notgameDir C:\x", null)]
    public void TheGameFolderIsReadOffACommandLine(string commandLine, string? expected)
        => Assert.Equal(expected, RunningGames.GameDirectoryOf(commandLine));

    [Fact]
    public void TheGameFolderIsReadOffSeparateArguments()
    {
        Assert.Equal("/home/a/build", RunningGames.GameDirectoryOf(new[] { "java", "Main", "--gameDir", "/home/a/build", "--demo" }));
        Assert.Null(RunningGames.GameDirectoryOf(new[] { "java", "Main", "--gameDir" }));
    }

    [Fact]
    public void AFolderIsTheSameHoweverItIsWritten()
    {
        var directory = Temp();

        Assert.True(RunningGames.SameDirectory(directory, directory + Path.DirectorySeparatorChar));
        Assert.True(RunningGames.SameDirectory(directory, Path.Combine(directory, "mods", "..")));
        Assert.False(RunningGames.SameDirectory(directory, directory + "2"));
    }

    [Fact]
    public void AGameTheLauncherStartedIsRunningUntilItEnds()
    {
        var directory = Temp();
        var other = Temp();

        Assert.False(RunningGames.IsRunning(directory));

        // A process id nothing has: the registry is what is under test.
        using (RunningGames.Track(directory + Path.DirectorySeparatorChar, int.MaxValue - 7))
        {
            Assert.True(RunningGames.IsTracked(directory));
            Assert.True(RunningGames.IsRunning(directory));
            Assert.False(RunningGames.IsRunning(other));
        }

        Assert.False(RunningGames.IsRunning(directory));
        Assert.False(RunningGames.IsRunning(null));
    }

    [Fact]
    public void AnotherProcessesCommandLineCanBeRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The test host stands in for a game left running from before a restart.
        var own = RunningGames.CommandLineOf(Environment.ProcessId);

        Assert.False(string.IsNullOrWhiteSpace(own));
        Assert.Null(RunningGames.CommandLineOf(int.MaxValue - 7));
    }
}
