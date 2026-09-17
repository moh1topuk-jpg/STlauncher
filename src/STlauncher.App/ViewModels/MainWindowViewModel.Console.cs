using System;
using System.Collections.ObjectModel;
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

    private void FlushConsole()
    {
        var appended = false;

        while (_pendingConsoleLines.TryDequeue(out var line))
        {
            Console.Add(line);
            appended = true;
        }

        if (!appended)
        {
            return;
        }

        while (Console.Count > MaxConsoleLines)
        {
            Console.RemoveAt(0);
        }
    }
}
