using System;
using System.Linq;
using System.Threading.Tasks;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

/// <summary>
/// A game that dies at once. Java that would not run, a JVM that refused its options and
/// a process Windows stopped are not crashes in the game, and the crash card says which
/// of them it was, in the JVM's own cause line rather than its "could not create the Java
/// Virtual Machine". Also the guard against a second game in one build's folder.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The Java the failed run used, and whose it was: the advice depends on both.</summary>
    private string? _crashJavaPath;
    private bool _crashJavaPicked;
    private bool _crashJavaReinstalled;

    /// <summary>The build that was started. The fix is for it, whichever build is open by then.</summary>
    private Instance? _crashInstance;

    private void RememberLaunchFailure(LaunchResult result, string javaPath, bool javaPicked)
    {
        _crashJavaPath = javaPath;
        _crashJavaPicked = javaPicked;
        _crashJavaReinstalled = result.JavaReinstalled;
        _crashInstance = SelectedInstance;
    }

    /// <summary>
    /// One verdict out of three readers. What the JVM said about itself comes first: it
    /// is the reason nothing else ran. Then what the game's log names. A Windows status
    /// code is the last resort - it says how the process died, not why.
    /// </summary>
    private CrashDiagnosis DiagnoseRun(LaunchResult result, CrashDiagnosis fromLog)
    {
        var launch = LaunchFailureAnalyzer.Analyze(result, _crashLogLines.Count > 0 ? _crashLogLines : result.EarlyOutput);

        if (launch.Cause != CrashCause.Unknown)
        {
            return launch;
        }

        if (fromLog.Cause != CrashCause.Unknown)
        {
            return fromLog;
        }

        return NtStatus.IsNtStatus(result.ExitCode)
            ? new CrashDiagnosis(CrashCause.WindowsError, NtStatus.Format(result.ExitCode), NtStatus.Describe(result.ExitCode))
            : CrashDiagnosis.None;
    }

    /// <summary>A line the JVM printed, fit to show: no home folder in it and no wall of text.</summary>
    private static string Shown(string? line)
    {
        var text = PathMask.Mask(line).Trim();

        return text.Length <= 220 ? text : text[..220] + "…";
    }

    private bool CrashBuildHasJvmArgs => !string.IsNullOrWhiteSpace(_crashInstance?.ExtraJvmArgs);

    private bool CrashJavaIsLaunchers => !_crashJavaPicked && _java.ManagedRuntimeMajor(_crashJavaPath) is not null;

    private string LaunchFailureTitle(CrashDiagnosis d) => d.Cause switch
    {
        CrashCause.JavaMissing => Localize("Crash_JavaMissing", "Java could not be started: its file is not there"),
        CrashCause.JavaBlocked => Localize("Crash_JavaBlocked", "Windows did not let Java start"),
        CrashCause.JavaBroken => Localize("Crash_JavaBroken", "This Java is damaged: {0}", Shown(d.Subject)),
        CrashCause.JavaOption => Localize("Crash_JavaOption", "Java does not know the option {0}", Shown(d.Subject)),
        CrashCause.JavaMemory => d.Subject is null
            ? Localize("Crash_JavaMemoryPlain", "Java refused the memory it was told to take")
            : Localize("Crash_JavaMemory", "Java refused the memory setting {0}", Shown(d.Subject)),
        CrashCause.JavaNotStarted => string.IsNullOrWhiteSpace(d.Subject)
            ? Localize("Crash_JavaNotStartedPlain", "Java could not start")
            : Localize("Crash_JavaNotStarted", "Java could not start: {0}", Shown(d.Subject)),
        CrashCause.WindowsError => NtStatus.Classify(_crashExitCode) switch
        {
            NtStatusKind.AccessViolation => Localize("Nt_AccessViolation", "The game crashed with an access violation ({0})", d.Subject),
            NtStatusKind.MissingDll => Localize("Nt_MissingDll", "A system library the game needs is missing or is for another architecture ({0})", d.Subject),
            NtStatusKind.InitFailed => Localize("Nt_InitFailed", "A library the game needs failed to initialise ({0})", d.Subject),
            NtStatusKind.StackOverflow => Localize("Nt_StackOverflow", "The game ran out of stack ({0})", d.Subject),
            NtStatusKind.StackBufferOverrun => Localize("Nt_StackBufferOverrun", "Windows stopped the game on a protection check ({0})", d.Subject),
            NtStatusKind.OutOfMemory => Localize("Nt_OutOfMemory", "Windows had no memory or page file left for the game ({0})", d.Subject),
            _ => Localize("Nt_Unknown", "Windows stopped the game with code {0}", d.Subject)
        },
        _ => string.Empty
    };

    /// <summary>Null for a cause that is not a launch failure: the caller has its own words for those.</summary>
    private string? LaunchFailureAdvice(CrashDiagnosis d) => d.Cause switch
    {
        CrashCause.JavaBlocked => Localize("Crash_JavaBlockedAdvice", "Most often an antivirus is checking java.exe. Wait a minute and press Play again; if it repeats, add the launcher's folder to the antivirus exclusions."),
        CrashCause.JavaMissing or CrashCause.JavaBroken => JavaRepairAdvice(),
        CrashCause.JavaOption => CrashBuildHasJvmArgs
            ? Localize("Crash_JavaOptionAdvice", "It most likely comes from the build's extra Java arguments. Clear them, or remove this one in the build's settings.")
            : Localize("Crash_JavaOptionForeignAdvice", "The build has no extra Java arguments, so this Java is too old or too new for the game. Let the launcher pick Java itself."),
        CrashCause.JavaMemory => Localize("Crash_JavaMemoryAdvice", "A 32-bit Java cannot take this much, and a wrong size in the build's extra Java arguments gives the same. Let the launcher pick Java itself, or give the game less memory."),
        CrashCause.WindowsError => NtStatus.Classify(_crashExitCode) switch
        {
            NtStatusKind.AccessViolation => Localize("Nt_AccessViolationAdvice", "Usually a graphics driver or an overlay (Discord, MSI Afterburner, OBS). Update the driver and switch overlays off."),
            NtStatusKind.MissingDll => Localize("Nt_MissingDllAdvice", "Install the Microsoft Visual C++ runtime, and let the launcher pick Java itself."),
            NtStatusKind.InitFailed => Localize("Nt_InitFailedAdvice", "Restart the computer. If it repeats, an antivirus or an overlay is getting into the game."),
            NtStatusKind.StackOverflow => Localize("Nt_StackOverflowAdvice", "Usually a mod caught in a loop. Switch off the mods that were added last."),
            NtStatusKind.StackBufferOverrun => Localize("Nt_StackBufferOverrunAdvice", "Usually a graphics driver or a program that gets into the game. Update the driver and switch overlays off."),
            NtStatusKind.OutOfMemory => Localize("Nt_OutOfMemoryAdvice", "Close heavy programs, give the game less memory, or enlarge the page file in Windows."),
            _ => null
        },
        _ => null
    };

    private string JavaRepairAdvice()
    {
        if (_crashJavaPicked)
        {
            return Localize("Crash_JavaPickedAdvice", "This Java was chosen in the settings, so the launcher does not touch it. Pick another one, or let the launcher choose.");
        }

        if (!CrashJavaIsLaunchers)
        {
            return Localize("Crash_JavaSystemAdvice", "This Java is installed on the computer and is not the launcher's own. Reinstall it, or remove it: the launcher then downloads its own.");
        }

        return _crashJavaReinstalled
            ? Localize("Crash_JavaReinstalledAdvice", "The launcher installed its Java again and it still does not start. An antivirus may be removing its files: add the launcher's folder to the exclusions.")
            : Localize("Crash_JavaDownloadAdvice", "The launcher could not download its Java again. Check the connection and press Play.");
    }

    private string LaunchFailureFixLabel(CrashDiagnosis d) => d.Cause switch
    {
        CrashCause.JavaOption or CrashCause.JavaMemory when CrashBuildHasJvmArgs
            => Localize("Crash_FixClearJvmArgs", "Clear the build's Java arguments"),
        CrashCause.JavaOption or CrashCause.JavaMemory or CrashCause.JavaMissing or CrashCause.JavaBroken
            when SelectedJavaChoice?.Path is { Length: > 0 } => Localize("Crash_FixJava", "Java: automatic"),
        CrashCause.WindowsError when NtStatus.Classify(_crashExitCode) == NtStatusKind.OutOfMemory && LowerMemoryAfterCrash() is { } less
            => Localize("Crash_FixMemory", "Give the game {0} MB", less),
        _ => string.Empty
    };

    private void ApplyLaunchFailureFix()
    {
        switch (_crash.Cause)
        {
            case CrashCause.JavaOption or CrashCause.JavaMemory when CrashBuildHasJvmArgs:
                ClearCrashBuildJvmArgs();
                break;

            case CrashCause.JavaOption or CrashCause.JavaMemory or CrashCause.JavaMissing or CrashCause.JavaBroken
                when SelectedJavaChoice?.Path is { Length: > 0 }:
                SelectedJavaChoice = JavaChoices.FirstOrDefault();
                Status = Localize("Crash_JavaApplied", "Java is picked automatically now - try again");
                break;

            case CrashCause.WindowsError
                when NtStatus.Classify(_crashExitCode) == NtStatusKind.OutOfMemory && LowerMemoryAfterCrash() is { } less:
                MaxMemoryMb = less;
                Status = Localize("Crash_MemoryApplied", "Memory set to {0} MB - try again", less);
                break;
        }
    }

    /// <summary>
    /// Clears the extra Java arguments of the build that failed - the one that was
    /// started, even when another build is open by now.
    /// </summary>
    private void ClearCrashBuildJvmArgs()
    {
        if (_crashInstance is not { } instance)
        {
            return;
        }

        // The same object when the build is still the open one; otherwise the open build
        // with the same id, so what is on screen and what is saved do not part ways.
        var target = SelectedInstance is { } open && open.Id == instance.Id ? open : instance;

        try
        {
            target.ExtraJvmArgs = null;
            instance.ExtraJvmArgs = null;
            _instances.Save(target);

            Status = Localize("Crash_JvmArgsCleared", "The extra Java arguments of \"{0}\" are cleared - try again", target.Name);
            AppendConsole($"[crash] cleared the extra Java arguments of {target.Name}");
        }
        catch (Exception ex)
        {
            Status = Localize("Error_SaveBuild", "Failed to save the build: {0}", ex.Message);
        }

        OnPropertyChanged(nameof(BuildJvmArgs));
    }

    /// <summary>
    /// True, with the reason on screen, when a game is already running in the selected
    /// build's folder: one this launcher started, or one left from before it was restarted.
    /// Two games in one folder write the same world.
    /// </summary>
    private async Task<bool> IsBuildGameRunningAsync()
    {
        var directory = InstanceDirectory;

        // Held while the processes are read: a second press in that moment must not get
        // past the check that the first one is still making.
        IsBusy = true;

        bool running;

        try
        {
            running = await Task.Run(() => RunningGames.IsRunning(directory));
        }
        catch (Exception)
        {
            // Not knowing is not a reason to keep the player out of their game.
            running = false;
        }

        if (!running)
        {
            return false;
        }

        IsBusy = false;
        Status = Localize("Game_AlreadyRunning", "This build's game is already running. Close it first: two games in one folder damage the worlds.");
        AppendConsole($"[launcher] not started: a game is already running in {PathMask.Mask(directory)}");

        return true;
    }

    /// <summary>
    /// Undoes the "last played: just now" written before the start, for a launch that
    /// never became a game.
    /// </summary>
    private void ForgetFailedLaunch(Instance? instance, DateTimeOffset? playedBefore)
    {
        if (instance is null)
        {
            return;
        }

        try
        {
            instance.LastPlayedAt = playedBefore;
            _instances.Save(instance);
        }
        catch (Exception ex)
        {
            AppendConsole($"[play] could not save the build: {ex.Message}");
        }

        RefreshBuildListItem(instance);
        OnPropertyChanged(nameof(LastPlayedLabel));
    }
}
