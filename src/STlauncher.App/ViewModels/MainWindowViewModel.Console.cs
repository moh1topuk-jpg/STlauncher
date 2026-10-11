using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace STlauncher.App.ViewModels;

/// <summary>
/// The developer console. Game output arrives on background threads in bursts, so it is
/// queued and drained on a timer rather than posted line by line.
/// </summary>
public partial class MainWindowViewModel
{
    private DispatcherTimer? _consoleTimer;

    /// <summary>
    /// The same lines, on disk. "Send me the log" is the first thing support asks, and
    /// until now the console was gone the moment the launcher closed.
    /// </summary>
    public string LauncherLogPath => System.IO.Path.Combine(_paths.Logs, "launcher.log");

    private const long MaxLauncherLogBytes = 2 * 1024 * 1024;
    private volatile bool _launcherLogFailed;

    /// <summary>Game output arrives on background threads; the UI drains it in batches.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingConsoleLines = new();

    private const int MaxConsoleLines = 2000;

    private readonly LineLog _consoleLines = new();

    public ObservableCollection<string> Console => _consoleLines;


    [RelayCommand]
    private void ClearConsole()
    {
        Console.Clear();

        // Drop anything still queued, otherwise a cleared console refills a moment later.
        while (_pendingConsoleLines.TryDequeue(out _))
        {
        }
    }


    /// <summary>
    /// Queues a console line. The actual list update is batched on a timer: Minecraft
    /// emits thousands of lines during startup, and posting one dispatcher work item per
    /// line makes the window unresponsive exactly when the user is watching it.
    /// </summary>
    private void AppendConsole(string line)
    {
        _pendingConsoleLines.Enqueue(line);
        ArmConsoleFlusher();
    }

    /// <summary>1 while the flusher is running or has been asked to; 0 while it rests with nothing to do.</summary>
    private int _consoleFlusherArmed;

    /// <summary>
    /// The flusher only ticks while there are lines to move. It used to tick for the life
    /// of the launcher, seven times a second, to find the queue empty nearly every time.
    /// </summary>
    private void ArmConsoleFlusher()
    {
        if (_consoleTimer is not null && System.Threading.Interlocked.CompareExchange(ref _consoleFlusherArmed, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(() => _consoleTimer?.Start(), DispatcherPriority.Background);
        }
    }

    private void StartConsoleFlusher()
    {
        _consoleTimer?.Stop();

        _consoleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _consoleTimer.Tick += (_, _) =>
        {
            FlushConsole();

            // Rest until the next line; one that slipped in just now starts it again.
            _consoleTimer?.Stop();
            System.Threading.Volatile.Write(ref _consoleFlusherArmed, 0);

            if (!_pendingConsoleLines.IsEmpty)
            {
                ArmConsoleFlusher();
            }
        };

        System.Threading.Volatile.Write(ref _consoleFlusherArmed, 1);
        _consoleTimer.Start();
    }

    /// <summary>Lines on their way to launcher.log, stamped, in the order they were printed.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _launcherLogLines = new();

    /// <summary>Held by whoever is writing the file, so the lines of two writers never interleave.</summary>
    private readonly object _launcherLogGate = new();

    /// <summary>1 while a background writer is at work or about to be.</summary>
    private int _launcherLogWriting;

    /// <summary>
    /// Sends the lines to the file from a pool thread. The file used to be appended to
    /// from the thread that draws the window, on every batch: a slow or sleeping disk
    /// held the interface each time the game printed a line.
    /// </summary>
    private void WriteLauncherLog(System.Collections.Generic.List<string> lines)
    {
        if (_launcherLogFailed)
        {
            return;
        }

        var stamp = DateTime.Now.ToString("HH:mm:ss");

        foreach (var line in lines)
        {
            _launcherLogLines.Enqueue($"{stamp} {line}");
        }

        if (System.Threading.Interlocked.CompareExchange(ref _launcherLogWriting, 1, 0) == 0)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    DrainLauncherLog();
                }
                finally
                {
                    System.Threading.Volatile.Write(ref _launcherLogWriting, 0);
                }

                // A line that arrived after the last look and before the flag went down.
                if (!_launcherLogLines.IsEmpty && System.Threading.Interlocked.CompareExchange(ref _launcherLogWriting, 1, 0) == 0)
                {
                    try
                    {
                        DrainLauncherLog();
                    }
                    finally
                    {
                        System.Threading.Volatile.Write(ref _launcherLogWriting, 0);
                    }
                }
            });
        }
    }

    /// <summary>
    /// Everything printed so far is in the file when this returns. For the places that
    /// read the file or may be the last to run: sending a report, opening the log, a
    /// failed start, the exit.
    /// </summary>
    public void FlushLauncherLog()
    {
        FlushConsole();
        DrainLauncherLog();
    }

    /// <summary>The same for a caller off the interface thread: what has reached the queue is written; the console list is not touched.</summary>
    private void FlushLauncherLogFromAnyThread() => DrainLauncherLog();

    private void DrainLauncherLog()
    {
        lock (_launcherLogGate)
        {
            if (_launcherLogFailed || _launcherLogLines.IsEmpty)
            {
                return;
            }

            var lines = new System.Collections.Generic.List<string>();

            while (_launcherLogLines.TryDequeue(out var line))
            {
                lines.Add(line);
            }

            try
            {
                System.IO.Directory.CreateDirectory(_paths.Logs);

                var info = new System.IO.FileInfo(LauncherLogPath);

                // Rolled, not truncated: the previous file still holds yesterday's failure.
                if (info.Exists && info.Length > MaxLauncherLogBytes)
                {
                    System.IO.File.Move(LauncherLogPath, LauncherLogPath + ".old", overwrite: true);
                }

                System.IO.File.AppendAllLines(LauncherLogPath, lines);
            }
            catch (Exception)
            {
                // A log that cannot be written is not worth failing every flush over.
                _launcherLogFailed = true;
            }
        }
    }

    [RelayCommand]
    private void OpenLauncherLog()
    {
        try
        {
            FlushLauncherLog();

            if (!System.IO.File.Exists(LauncherLogPath))
            {
                Status = Localize("Console_NoLogYet", "The log file has not been written yet");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LauncherLogPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }

    private void FlushConsole()
    {
        // Almost every tick finds nothing: no list is made for it.
        if (_pendingConsoleLines.IsEmpty)
        {
            return;
        }

        var batch = new System.Collections.Generic.List<string>();

        while (_pendingConsoleLines.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        if (batch.Count == 0)
        {
            return;
        }

        // One change for the lines that came and one for those that fell off the top.
        _consoleLines.Append(batch, MaxConsoleLines);
        WriteLauncherLog(batch);
    }
}
