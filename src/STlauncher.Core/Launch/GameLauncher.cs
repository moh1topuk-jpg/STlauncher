using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Launch;

public sealed class GameLauncher
{
    /// <summary>
    /// Log lines that indicate the game window is up. Versions and loaders log
    /// different things, so any match counts.
    /// </summary>
    private static readonly string[] StartedMarkers =
    {
        "Setting user:",
        "Setting user ",
        "LWJGL Version:",
        "Backend library:",
        "Sound engine started",
        "Reloading ResourceManager",
        "OpenAL initialized"
    };

    private readonly object _logLock = new();
    private DateTime _lastLogFlushUtc = DateTime.UtcNow;

    public event Action<string>? OutputReceived;

    public event Action<string>? ErrorReceived;

    /// <summary>Raised once, when the game is considered to be running.</summary>
    public event Action? GameStarted;

    /// <summary>Full path of the log file for the most recent run.</summary>
    public string? LogFilePath { get; private set; }

    public async Task<int> RunAsync(
        LaunchCommand command,
        string workingDirectory,
        string? logDirectory = null,
        TimeSpan? startTimeout = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workingDirectory);

        // The log writer is per run, not a field: this class is a DI singleton, so a shared
        // writer would make two concurrent runs interleave into a single file.
        var logWriter = OpenLogFile(logDirectory);

        try
        {
            var startSignal = new StartSignal(() => GameStarted?.Invoke());

            var startInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) => OnLine(e.Data, logWriter, startSignal, isError: false);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data, logWriter, startSignal, isError: true);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Fallback: if no known marker appears but the process is still alive, consider
            // the game started so the launcher is not stuck waiting.
            _ = FallbackStartAsync(process, startSignal, startTimeout ?? TimeSpan.FromSeconds(90), cancellationToken);

            // The game must outlive the launcher, so the process is intentionally not
            // killed when the launcher shuts down or the token is cancelled.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            // Let the redirected-output handlers drain before closing the log.
            await FlushLogAsync(logWriter).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            if (logWriter is not null)
            {
                await logWriter.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnLine(string? line, StreamWriter? logWriter, StartSignal startSignal, bool isError)
    {
        if (line is null)
        {
            return;
        }

        WriteLog(logWriter, line);

        if (isError)
        {
            ErrorReceived?.Invoke(line);
        }
        else
        {
            OutputReceived?.Invoke(line);
        }

        startSignal.Check(line);
    }

    private static async Task FallbackStartAsync(
        Process process,
        StartSignal startSignal,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);

            if (!process.HasExited)
            {
                startSignal.Fallback();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Opens the run's log file once. Writing each line with File.AppendAllText meant an
    /// open/write/close per line - thousands of syscalls during startup, serialized on the
    /// same lock the output pumps use.
    /// </summary>
    private StreamWriter? OpenLogFile(string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            LogFilePath = null;
            return null;
        }

        try
        {
            Directory.CreateDirectory(logDirectory);

            // A short unique suffix keeps two concurrent runs off the same file even when
            // they start within the same second.
            var name = $"game-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..34] + ".log";
            var path = Path.Combine(logDirectory, name);

            LogFilePath = path;

            return new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: false))
            {
                AutoFlush = false
            };
        }
        catch (Exception)
        {
            LogFilePath = null;
            return null;
        }
    }

    private void WriteLog(StreamWriter? logWriter, string line)
    {
        if (logWriter is null)
        {
            return;
        }

        try
        {
            lock (_logLock)
            {
                logWriter.WriteLine(line);

                // Flushed about once a second rather than per line: the buffer absorbs the
                // startup burst while still keeping the file current.
                if (DateTime.UtcNow - _lastLogFlushUtc > TimeSpan.FromSeconds(1))
                {
                    logWriter.Flush();
                    _lastLogFlushUtc = DateTime.UtcNow;
                }
            }
        }
        catch (Exception)
        {
            // Logging must never break the game process.
        }
    }

    private async Task FlushLogAsync(StreamWriter? logWriter)
    {
        if (logWriter is null)
        {
            return;
        }

        try
        {
            await logWriter.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Same reasoning as above.
        }
    }

    /// <summary>Fires exactly once, on the first recognised marker or on the fallback.</summary>
    private sealed class StartSignal
    {
        private readonly Action _onStarted;
        private int _fired;

        public StartSignal(Action onStarted)
        {
            _onStarted = onStarted;
        }

        public void Check(string line)
        {
            if (Volatile.Read(ref _fired) != 0)
            {
                return;
            }

            foreach (var marker in StartedMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    Fire();
                    return;
                }
            }
        }

        public void Fallback() => Fire();

        private void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            try
            {
                _onStarted();
            }
            catch (Exception)
            {
                // A subscriber must not break the log pump.
            }
        }
    }
}