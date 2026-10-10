using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using STlauncher.Core.Diagnostics;

namespace STlauncher.App.Services;

/// <summary>
/// Notices when the interface stops answering. A background thread posts a tiny ping to
/// the interface thread every two seconds; fifteen seconds without an answer is a freeze,
/// and a report is written next to the crash log, then rewritten with the full duration
/// when the interface comes back.
/// </summary>
/// <remarks>
/// The cost is one sleeping thread and one empty dispatcher callback per tick: no timer on
/// the interface thread, nothing measured unless a freeze is actually being written.
/// The decisions (thresholds, what counts as sleep) are in <see cref="FreezeDetector"/>.
/// </remarks>
public sealed class FreezeWatchdog : IDisposable
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(15);

    private readonly FreezeReportStore _store;
    private readonly ActivitySnapshot _activity;
    private readonly FreezeDetector _detector = new(Tick, Threshold);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Action _pong;

    private Thread? _thread;
    private long _sent;
    private long _answered;
    private long _answeredAtTicks;
    private int _uiThreadId;

    private List<string>? _details;
    private TimeSpan _uiCpuAtFreeze;

    public FreezeWatchdog(FreezeReportStore store, ActivitySnapshot activity)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));

        // One delegate for the life of the watchdog: a ping allocates nothing of ours.
        _pong = () =>
        {
            Volatile.Write(ref _answeredAtTicks, _clock.Elapsed.Ticks);
            Volatile.Write(ref _answered, Volatile.Read(ref _sent));
        };
    }

    /// <summary>Call on the interface thread, once.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _uiThreadId = CurrentNativeThreadId();

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "STlauncher freeze watchdog"
        };

        _thread.Start();
    }

    public void Dispose() => _stop.Set();

    private void Run()
    {
        try
        {
            while (!_stop.Wait(Tick))
            {
                var answered = Volatile.Read(ref _answered) == Volatile.Read(ref _sent);
                var answeredAt = TimeSpan.FromTicks(Volatile.Read(ref _answeredAtTicks));
                var observation = _detector.Observe(_clock.Elapsed, DateTimeOffset.Now, answered, answeredAt);

                switch (observation.Signal)
                {
                    case FreezeSignal.Started:
                        _details = DescribeFreeze();
                        Write(observation, recovered: false, afterword: null);
                        break;

                    case FreezeSignal.Ongoing:
                        Write(observation, recovered: false, afterword: null);
                        break;

                    case FreezeSignal.Recovered:
                        Write(observation, recovered: true, DescribeRecovery());
                        _details = null;
                        break;
                }

                if (observation.SendPing)
                {
                    Interlocked.Increment(ref _sent);

                    // Input priority: an interface that draws but takes no clicks is frozen
                    // as far as the player is concerned, and one that is merely busy
                    // drawing must not be reported.
                    Dispatcher.UIThread.Post(_pong, DispatcherPriority.Input);
                }
            }
        }
        catch (Exception ex)
        {
            // The watchdog is a courtesy; if it breaks, it goes quietly and says why.
            CrashLog.Write(ex, "freeze watchdog stopped, the launcher kept running");
        }
    }

    private void Write(FreezeObservation observation, bool recovered, IReadOnlyList<string>? afterword)
    {
        try
        {
            _store.Write(new FreezeReport(
                observation.Since,
                observation.Silence,
                recovered,
                CrashLog.Version,
                CrashLog.OperatingSystem,
                _details ?? (IReadOnlyList<string>)Array.Empty<string>(),
                afterword));
        }
        catch (Exception)
        {
            // A full disk is not the watchdog's to solve.
        }
    }

    /// <summary>Everything that can be learned about a silent interface thread without touching it.</summary>
    private List<string> DescribeFreeze()
    {
        var lines = new List<string>
        {
            // Said plainly so nobody looks for a stack that was never going to be here.
            "ui thread stack = not captured. .NET 8 can only walk another thread's stack from a debugger " +
            "or out of a full memory dump; the launcher attaches neither to itself. What follows is what " +
            "can be known from outside the thread."
        };

        lines.AddRange(_activity.Describe(DateTimeOffset.Now));

        var ui = FindUiThread();

        if (ui is not null)
        {
            try
            {
                _uiCpuAtFreeze = ui.TotalProcessorTime;

                // "Wait" means blocked on something (a lock, a file, a synchronous network
                // call); "Running" with processor time growing means a loop.
                lines.Add(ui.ThreadState == System.Diagnostics.ThreadState.Wait
                    ? FormattableString.Invariant($"ui thread = waiting ({ui.WaitReason}), processor time so far {_uiCpuAtFreeze.TotalSeconds:F1} s")
                    : FormattableString.Invariant($"ui thread = {ui.ThreadState}, processor time so far {_uiCpuAtFreeze.TotalSeconds:F1} s"));
            }
            catch (Exception)
            {
                lines.Add("ui thread = state not readable");
            }
        }
        else
        {
            lines.Add("ui thread = state not available on this system");
        }

        try
        {
            ThreadPool.GetAvailableThreads(out var workers, out _);
            ThreadPool.GetMaxThreads(out var maxWorkers, out _);

            lines.Add($"thread pool = {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} queued, {maxWorkers - workers} busy");
            lines.Add($"memory = working set {Environment.WorkingSet / 1024 / 1024} MB, managed heap {GC.GetTotalMemory(false) / 1024 / 1024} MB");
            lines.Add(FormattableString.Invariant($"gc = gen2 collections {GC.CollectionCount(2)}, total pause {GC.GetTotalPauseDuration().TotalSeconds:F1} s"));
            lines.Add(FormattableString.Invariant($"launcher uptime = {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMinutes:F0} min"));
        }
        catch (Exception)
        {
            // Whatever was gathered before the failure still goes into the report.
        }

        return lines;
    }

    private List<string> DescribeRecovery()
    {
        var lines = new List<string>();

        try
        {
            if (FindUiThread() is { } ui)
            {
                var used = ui.TotalProcessorTime - _uiCpuAtFreeze;
                lines.Add(FormattableString.Invariant($"ui thread processor time since the freeze was noticed = {used.TotalSeconds:F1} s ") +
                          "(near zero: it was blocked waiting; near the duration: it was computing)");
            }
        }
        catch (Exception)
        {
        }

        lines.AddRange(_activity.Describe(DateTimeOffset.Now));
        return lines;
    }

    private ProcessThread? FindUiThread()
    {
        if (_uiThreadId == 0)
        {
            return null;
        }

        try
        {
            foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
            {
                if (thread.Id == _uiThreadId)
                {
                    return thread;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static int CurrentNativeThreadId()
    {
        try
        {
            return OperatingSystem.IsWindows() ? (int)GetCurrentThreadId() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
