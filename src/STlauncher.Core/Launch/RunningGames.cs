using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Launch;

/// <summary>
/// Answers "is this build's game running right now". Two games in one folder write the
/// same world and the same options, and replacing a build's files under a running game
/// breaks it, so everything that starts or changes a build asks here first.
/// </summary>
/// <remarks>
/// Two sources. What this launcher started is tracked by process. A game left running
/// from before the launcher was restarted, or started by another launcher on a linked
/// build, is found by its command line: every version since 1.6 is given its folder as
/// "--gameDir".
/// </remarks>
public static partial class RunningGames
{
    private static readonly ConcurrentDictionary<int, string> Tracked = new();

    /// <summary>
    /// Records a game this launcher has just started. Dispose the result when the process
    /// has ended.
    /// </summary>
    public static IDisposable Track(string gameDirectory, int processId)
    {
        Tracked[processId] = Normalize(gameDirectory);
        return new Tracking(processId);
    }

    /// <summary>True when a game is running in this folder, whoever started it.</summary>
    public static bool IsRunning(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return false;
        }

        return IsTracked(gameDirectory) || FindProcesses(gameDirectory).Count > 0;
    }

    /// <summary>True when this launcher started a game in this folder and it has not ended.</summary>
    public static bool IsTracked(string gameDirectory)
    {
        var wanted = Normalize(gameDirectory);

        return Tracked.Values.Any(directory => Same(directory, wanted));
    }

    /// <summary>
    /// Java processes whose command line names this folder as the game directory. Empty
    /// when none does, and when the system will not show command lines: not knowing must
    /// not stop a player from starting their game.
    /// </summary>
    public static IReadOnlyList<int> FindProcesses(string gameDirectory)
    {
        var wanted = Normalize(gameDirectory);
        var found = new List<int>();

        foreach (var name in new[] { "java", "javaw" })
        {
            Process[] processes;

            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited &&
                        ReadGameDirectory(process.Id) is { } directory &&
                        Same(Normalize(directory), wanted))
                    {
                        found.Add(process.Id);
                    }
                }
                catch (Exception)
                {
                    // Gone between the listing and the look, or not ours to look at.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return found;
    }

    [GeneratedRegex(@"(?:^|\s)""?--gameDir""?\s+(?:""(?<quoted>[^""]*)""|(?<plain>\S+))", RegexOptions.CultureInvariant)]
    private static partial Regex GameDirArgument();

    /// <summary>The value of "--gameDir" in a command line as Windows keeps it: one string, quoted where needed.</summary>
    public static string? GameDirectoryOf(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return null;
        }

        var match = GameDirArgument().Match(commandLine);

        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;

        return value.Length > 0 ? value : null;
    }

    /// <summary>The same, from arguments that are already separate.</summary>
    public static string? GameDirectoryOf(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == "--gameDir" && arguments[i + 1].Length > 0)
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    /// <summary>True when two paths name one folder, however each was written.</summary>
    public static bool SameDirectory(string first, string second)
        => Same(Normalize(first), Normalize(second));

    private static bool Same(string first, string second)
        => string.Equals(
            first,
            second,
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try
        {
            // A quoted argument that ends in a backslash arrives with it doubled.
            return Path.GetFullPath(path.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path.Trim().TrimEnd('\\', '/');
        }
    }

    /// <summary>
    /// The command line of a running process on Windows, or null: when the process is
    /// gone, belongs to another user, or the system is not Windows.
    /// </summary>
    public static string? CommandLineOf(int processId)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsCommandLine(processId) : null;

    private static string? ReadGameDirectory(int processId)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GameDirectoryOf(WindowsCommandLine(processId));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var raw = File.ReadAllText($"/proc/{processId}/cmdline");
            return GameDirectoryOf(raw.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }

        return null;
    }

    private sealed class Tracking : IDisposable
    {
        private readonly int _processId;

        public Tracking(int processId) => _processId = processId;

        public void Dispose() => Tracked.TryRemove(_processId, out _);
    }

    // ---------------------------------------------------------------- Windows

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>ProcessCommandLineInformation: the command line as a UNICODE_STRING, since Windows 8.1.</summary>
    private const int ProcessCommandLineInformation = 60;

    /// <summary>
    /// Another process's command line, or null. .NET has no call for it; the documented
    /// alternatives are WMI, which is a package the launcher does not carry, and starting
    /// PowerShell, which takes a second. This asks the kernel directly and needs no more
    /// rights than the launcher has over a game the same player started.
    /// </summary>
    private static string? WindowsCommandLine(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var buffer = IntPtr.Zero;

        try
        {
            // The first call only says how much room the answer needs.
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);

            if (needed <= 0 || needed > 1024 * 1024)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(needed);

            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
            {
                return null;
            }

            // UNICODE_STRING: length in bytes, capacity, then - after padding to pointer
            // size - the pointer to the characters, which sit in this same buffer.
            var bytes = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);

            return text == IntPtr.Zero || bytes == 0 ? null : Marshal.PtrToStringUni(text, bytes / 2);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr buffer, int length, out int returnLength);
}
