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
    private bool _launcherLogFailed;

    /// <summary>Game output arrives on background threads; the UI drains it in batches.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingConsoleLines = new();

    private const int MaxConsoleLines = 2000;

    public ObservableCollection<string> Console { get; } = new();


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
    private void AppendConsole(string line) => _pendingConsoleLines.Enqueue(line);

    private void StartConsoleFlusher()
    {
        _consoleTimer?.Stop();

        _consoleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _consoleTimer.Tick += (_, _) => FlushConsole();
        _consoleTimer.Start();
    }

    private void WriteLauncherLog(System.Collections.Generic.List<string> lines)
    {
        if (_launcherLogFailed)
        {
            return;
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

            var stamp = DateTime.Now.ToString("HH:mm:ss");
            System.IO.File.AppendAllLines(LauncherLogPath, lines.Select(l => $"{stamp} {l}"));
        }
        catch (Exception)
        {
            // A log that cannot be written is not worth failing every flush over.
            _launcherLogFailed = true;
        }
    }

    [RelayCommand]
    private void OpenLauncherLog()
    {
        try
        {
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
        var appended = false;
        var batch = new System.Collections.Generic.List<string>();

        while (_pendingConsoleLines.TryDequeue(out var line))
        {
            Console.Add(line);
            batch.Add(line);
            appended = true;
        }

        if (!appended)
        {
            return;
        }

        WriteLauncherLog(batch);

        while (Console.Count > MaxConsoleLines)
        {
            Console.RemoveAt(0);
        }
    }
}
