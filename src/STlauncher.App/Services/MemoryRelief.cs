using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.App.Services;

/// <summary>
/// Hands memory back to the system once the launcher has hidden for the game. A hidden
/// window draws nothing and is asked for nothing, but what it gathered while it was used
/// - the heap grown at start, pictures already let go of and not yet collected, pages that
/// will not be touched for an hour - stays counted against the machine until something
/// says otherwise. On a computer with eight gigabytes the game wants that room.
/// </summary>
internal static class MemoryRelief
{
    private static int _pending;

    /// <summary>
    /// A moment after hiding: one full, compacting collection, and on Windows the working
    /// set is trimmed. Both are done once, off the interface thread; nothing is lost by
    /// either - what is needed again comes back as it is touched.
    /// </summary>
    public static void AfterHiding(Action<string>? log = null)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Let the hide itself, and whatever the caller has just let go of, settle.
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                var before = Environment.WorkingSet;

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

                // Pictures free their pixels from finalizers; the second pass takes what those released.
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                if (OperatingSystem.IsWindows())
                {
                    using var process = System.Diagnostics.Process.GetCurrentProcess();
                    SetProcessWorkingSetSize(process.Handle, -1, -1);
                }

                log?.Invoke($"[mem] hidden for the game: working set {before / 1024 / 1024} MB -> {Environment.WorkingSet / 1024 / 1024} MB");
            }
            catch (Exception)
            {
                // Relief, not a duty: a launcher that could not give memory back still works.
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
