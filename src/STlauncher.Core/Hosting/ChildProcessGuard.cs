using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace STlauncher.Core.Hosting;

/// <summary>
/// Makes sure a server does not outlive the launcher. A server left running with no
/// launcher has no console and no stop button: it holds the port and the world open,
/// and the next start fails on both without saying why.
/// </summary>
/// <remarks>
/// Two layers. On a normal exit every running server is told to stop and given a few
/// seconds to save. Underneath, on Windows, the servers sit in a job object that the
/// system closes together with the launcher - which also covers a crash or the launcher
/// being ended from the task manager, where no code of ours gets to run.
/// </remarks>
internal static class ChildProcessGuard
{
    /// <summary>How long an exiting launcher waits for servers to save before ending them.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static readonly List<ServerProcess> Live = new();
    private static readonly List<Process> Helpers = new();
    private static bool _hooked;
    private static IntPtr _job;
    private static bool _jobUnavailable;

    public static void Track(ServerProcess server)
    {
        lock (Gate)
        {
            Live.Add(server);

            if (!_hooked)
            {
                _hooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAllOnExit();
            }
        }
    }

    public static void Untrack(ServerProcess server)
    {
        lock (Gate)
        {
            Live.Remove(server);
        }
    }

    /// <summary>
    /// For a child that has nothing to save - a loader's installer: when the launcher
    /// exits it is simply ended, so it does not go on downloading with nobody watching.
    /// </summary>
    public static void TrackHelper(Process process)
    {
        lock (Gate)
        {
            Helpers.Add(process);

            if (!_hooked)
            {
                _hooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAllOnExit();
            }
        }
    }

    public static void UntrackHelper(Process process)
    {
        lock (Gate)
        {
            Helpers.Remove(process);
        }
    }

    /// <summary>Ties the process to the launcher's lifetime. Best effort: a failure here must not fail the start.</summary>
    public static void Attach(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var job = EnsureJob();

            if (job != IntPtr.Zero)
            {
                AssignProcessToJobObject(job, process.Handle);
            }
        }
        catch (Exception)
        {
            // Without the job the exit handler below still covers a normal exit.
        }
    }

    private static void StopAllOnExit()
    {
        ServerProcess[] servers;
        Process[] helpers;

        lock (Gate)
        {
            servers = Live.ToArray();
            helpers = Helpers.ToArray();
        }

        foreach (var helper in helpers)
        {
            try
            {
                helper.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone or already disposed: either way it is not running.
            }
        }

        if (servers.Length == 0)
        {
            return;
        }

        try
        {
            // All at once and with one shared deadline: two servers must not take twice as long.
            Task.WaitAll(servers.Select(s => s.StopAsync(ExitGrace)).ToArray<Task>(), ExitGrace + TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // The process is on its way out; whatever is still alive is ended below.
        }

        foreach (var server in servers)
        {
            server.Kill();
        }
    }

    private static IntPtr EnsureJob()
    {
        lock (Gate)
        {
            if (_job != IntPtr.Zero || _jobUnavailable)
            {
                return _job;
            }

            var job = CreateJobObjectW(IntPtr.Zero, null);

            if (job == IntPtr.Zero)
            {
                _jobUnavailable = true;
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = KillOnJobClose }
            };

            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                _jobUnavailable = true;
                return IntPtr.Zero;
            }

            // Never closed on purpose: the handle going away with the process is the
            // whole mechanism.
            _job = job;
            return _job;
        }
    }

    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
        int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
