using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Hosting;

public enum ServerState
{
    /// <summary>Java is up and the world is loading; nobody can join yet.</summary>
    Starting,

    /// <summary>The server said it is done loading.</summary>
    Running,

    /// <summary>A stop was asked for, or the server announced one itself; the world is being saved.</summary>
    Stopping,

    /// <summary>The process is gone. <see cref="ServerProcess.Completion"/> says how it ended.</summary>
    Exited
}

public enum ServerExitReason
{
    /// <summary>Asked to stop - by the launcher, or with the stop command in the console or the game - and did.</summary>
    Stopped,

    /// <summary>Did not stop within the time given and was ended by force.</summary>
    Killed,

    /// <summary>Exited before it was ever ready; <see cref="ServerExit.Problem"/> says why when the console did.</summary>
    FailedToStart,

    /// <summary>Was running and went down without being asked to.</summary>
    Crashed
}

/// <summary>What the console named as the cause, when it named one.</summary>
public enum ServerProblem
{
    None,
    PortInUse,
    EulaNotAccepted,
    WrongJava,
    NotEnoughMemory,
    WorldInUse,
    Crash
}

/// <param name="LastLines">The end of the console, for showing next to the verdict.</param>
public sealed record ServerExit(
    ServerExitReason Reason,
    ServerProblem Problem,
    int ExitCode,
    IReadOnlyList<string> LastLines);

/// <summary>
/// One running server: its console as a stream of recognised lines, its state, who is
/// online, a way to type commands and a way to stop it.
/// </summary>
/// <remarks>
/// Events are raised on thread-pool threads, in console order within each stream. A
/// subscriber that touches the screen has to move to the UI thread itself.
/// </remarks>
public sealed class ServerProcess
{
    /// <summary>How much of the console is kept for a page opened after the lines went by.</summary>
    public const int RecentLineCapacity = 500;

    /// <summary>Saving a large world on a slow disk takes a while; shorter than this and worlds get cut off mid-save.</summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(30);

    private const int ExitLineCount = 40;

    private readonly object _gate = new();
    private readonly object _io = new();
    private readonly Process _process;
    private readonly Queue<ServerLine> _recent = new();
    private readonly List<string> _players = new();
    private readonly TaskCompletionSource<ServerExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ServerState _state = ServerState.Starting;
    private ServerProblem _problem;
    private bool _wasReady;
    private bool _stopRequested;
    private bool _stopAnnounced;
    private bool _killed;
    private bool _disposed;

    private ServerProcess(HostedServer server, ServerCommand command, Process process)
    {
        Server = server;
        Command = command;
        _process = process;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public HostedServer Server { get; }

    public ServerCommand Command { get; }

    public string ServerDirectory => Command.WorkingDirectory;

    public int ProcessId { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public ServerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool HasExited => State == ServerState.Exited;

    /// <summary>Who is on the server right now, in the order they joined.</summary>
    public IReadOnlyList<string> Players
    {
        get
        {
            lock (_gate)
            {
                return _players.ToArray();
            }
        }
    }

    /// <summary>The last <see cref="RecentLineCapacity"/> lines of the console, oldest first.</summary>
    public IReadOnlyList<ServerLine> RecentLines
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    /// <summary>Completes when the process is gone, with how it ended. Never faults.</summary>
    public Task<ServerExit> Completion => _exit.Task;

    /// <summary>True once the server is ready for players; false if it exited without getting there.</summary>
    public Task<bool> Ready => _ready.Task;

    /// <summary>Every line of the console, already recognised.</summary>
    public event Action<ServerLine>? LineReceived;

    public event Action<ServerState>? StateChanged;

    public event Action<string>? PlayerJoined;

    public event Action<string>? PlayerLeft;

    public event Action<ServerExit>? Exited;

    /// <summary>
    /// Starts the server and returns at once; the world is still loading. Throws when the
    /// Java executable cannot be started at all.
    /// </summary>
    public static ServerProcess Start(HostedServer server, string serverDirectory, string javaPath)
        => Start(server, ServerCommandLine.Build(server, serverDirectory, javaPath));

    public static ServerProcess Start(HostedServer server, ServerCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // Without a byte order mark: the server would read it as part of the first command.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        var result = new ServerProcess(server, command, process);

        process.OutputDataReceived += (_, e) => result.OnLine(e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => result.OnLine(e.Data, isError: true);

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        // Before anything else: from here on the server must not be able to outlive the launcher.
        ChildProcessGuard.Attach(process);
        ChildProcessGuard.Track(result);

        result.ProcessId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _ = result.WatchAsync();
        return result;
    }

    /// <summary>Waits for the "Done" line. False when the server exits first or the time runs out.</summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var finished = await Task.WhenAny(_ready.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return finished == _ready.Task && _ready.Task.Result;
    }

    /// <summary>
    /// Types a command into the server's console. A leading slash is dropped: the console
    /// takes commands without one, and people type it out of habit. False when the server
    /// is no longer there to hear it.
    /// </summary>
    public bool SendCommand(string command)
    {
        // One line is one command; a line break would smuggle in a second.
        var text = (command ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim().TrimStart('/').TrimStart();

        if (text.Length == 0)
        {
            return false;
        }

        if (!Write(text))
        {
            return false;
        }

        if (string.Equals(text, "stop", StringComparison.OrdinalIgnoreCase))
        {
            MarkStopRequested();
        }

        return true;
    }

    /// <summary>
    /// Stops the server the way it wants to be stopped: the stop command, which saves the
    /// world, then a wait. Only when that time runs out is the process ended by force.
    /// </summary>
    public async Task<ServerExit> StopAsync(TimeSpan? timeout = null)
    {
        if (_exit.Task.IsCompleted)
        {
            return await _exit.Task.ConfigureAwait(false);
        }

        MarkStopRequested();

        if (Write("stop"))
        {
            var wait = timeout ?? DefaultStopTimeout;
            var finished = await Task.WhenAny(_exit.Task, Task.Delay(wait)).ConfigureAwait(false);

            if (finished == _exit.Task)
            {
                return await _exit.Task.ConfigureAwait(false);
            }
        }

        Kill();

        // Ending a process is quick, but not something to wait on forever.
        var ended = await Task.WhenAny(_exit.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);

        return ended == _exit.Task
            ? await _exit.Task.ConfigureAwait(false)
            : new ServerExit(ServerExitReason.Killed, ServerProblem.None, -1, TailLines());
    }

    /// <summary>Ends the process at once, without saving. For when <see cref="StopAsync"/> is not an option.</summary>
    public void Kill()
    {
        lock (_gate)
        {
            if (_state == ServerState.Exited)
            {
                return;
            }

            _killed = true;
        }

        lock (_io)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone, or not ours to end; either way there is nothing more to do.
            }
        }
    }

    private bool Write(string text)
    {
        lock (_io)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                if (_process.HasExited)
                {
                    return false;
                }

                _process.StandardInput.WriteLine(text);
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    private void MarkStopRequested()
    {
        var changed = false;

        lock (_gate)
        {
            _stopRequested = true;

            if (_state is ServerState.Starting or ServerState.Running)
            {
                _state = ServerState.Stopping;
                changed = true;
            }
        }

        if (changed)
        {
            Raise(StateChanged, ServerState.Stopping);
        }
    }

    private void OnLine(string? data, bool isError)
    {
        if (data is null)
        {
            return;
        }

        var line = ServerOutput.Parse(data, isError);
        ServerState? changed = null;

        lock (_gate)
        {
            _recent.Enqueue(line);

            if (_recent.Count > RecentLineCapacity)
            {
                _recent.Dequeue();
            }

            switch (line.Kind)
            {
                case ServerLineKind.Plain:
                    break;

                case ServerLineKind.Ready:
                    _wasReady = true;

                    // Whatever was complained about on the way up did not stop the start;
                    // it must not turn a later ordinary stop into a "crash".
                    _problem = ServerProblem.None;

                    if (_state == ServerState.Starting)
                    {
                        _state = ServerState.Running;
                        changed = ServerState.Running;
                    }

                    break;

                case ServerLineKind.PlayerJoined:
                    if (!_players.Contains(line.Player!))
                    {
                        _players.Add(line.Player!);
                    }

                    break;

                case ServerLineKind.PlayerLeft:
                    _players.Remove(line.Player!);
                    break;

                case ServerLineKind.Stopping:
                    _stopAnnounced = true;

                    if (_state is ServerState.Starting or ServerState.Running)
                    {
                        _state = ServerState.Stopping;
                        changed = ServerState.Stopping;
                    }

                    break;

                default:
                    NoteProblem(ProblemOf(line.Kind));
                    break;
            }
        }

        Raise(LineReceived, line);

        if (changed is { } state)
        {
            Raise(StateChanged, state);
        }

        switch (line.Kind)
        {
            case ServerLineKind.Ready:
                _ready.TrySetResult(true);
                break;

            case ServerLineKind.PlayerJoined:
                Raise(PlayerJoined, line.Player!);
                break;

            case ServerLineKind.PlayerLeft:
                Raise(PlayerLeft, line.Player!);
                break;
        }
    }

    /// <summary>
    /// The first cause named wins - what follows is usually its consequences - except
    /// that a bare "crash" gives way to anything more specific.
    /// </summary>
    private void NoteProblem(ServerProblem problem)
    {
        if (problem == ServerProblem.None)
        {
            return;
        }

        if (_problem == ServerProblem.None || (_problem == ServerProblem.Crash && problem != ServerProblem.Crash))
        {
            _problem = problem;
        }
    }

    private static ServerProblem ProblemOf(ServerLineKind kind) => kind switch
    {
        ServerLineKind.PortInUse => ServerProblem.PortInUse,
        ServerLineKind.EulaRequired => ServerProblem.EulaNotAccepted,
        ServerLineKind.WrongJava => ServerProblem.WrongJava,
        ServerLineKind.NotEnoughMemory => ServerProblem.NotEnoughMemory,
        ServerLineKind.WorldInUse => ServerProblem.WorldInUse,
        ServerLineKind.Crash => ServerProblem.Crash,
        _ => ServerProblem.None
    };

    private async Task WatchAsync()
    {
        var exitCode = -1;

        try
        {
            // Also waits for both output streams to end, so no line arrives after the verdict.
            await _process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = _process.ExitCode;
        }
        catch (Exception)
        {
            // The process object is unusable; what matters is that the server is reported as gone.
        }

        ServerExit exit;

        lock (_gate)
        {
            exit = new ServerExit(ReasonLocked(), _problem, exitCode, TailLinesLocked());
            _state = ServerState.Exited;
            _players.Clear();
        }

        lock (_io)
        {
            _disposed = true;
            _process.Dispose();
        }

        ChildProcessGuard.Untrack(this);

        _ready.TrySetResult(false);
        Raise(StateChanged, ServerState.Exited);
        Raise(Exited, exit);
        _exit.TrySetResult(exit);
    }

    private ServerExitReason ReasonLocked()
    {
        if (_killed)
        {
            return ServerExitReason.Killed;
        }

        if (_stopRequested)
        {
            return ServerExitReason.Stopped;
        }

        if (!_wasReady)
        {
            return ServerExitReason.FailedToStart;
        }

        // A crashing server also announces "Stopping server" on its way down, so the
        // announcement counts as a clean stop only when no failure was named before it.
        return _stopAnnounced && _problem == ServerProblem.None
            ? ServerExitReason.Stopped
            : ServerExitReason.Crashed;
    }

    private IReadOnlyList<string> TailLines()
    {
        lock (_gate)
        {
            return TailLinesLocked();
        }
    }

    private IReadOnlyList<string> TailLinesLocked()
        => _recent.Skip(Math.Max(0, _recent.Count - ExitLineCount)).Select(l => l.Text).ToArray();

    /// <summary>A subscriber that throws must not take the console pump down with it.</summary>
    private static void Raise<T>(Action<T>? handler, T argument)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action<T>)subscriber)(argument);
            }
            catch (Exception)
            {
            }
        }
    }
}
