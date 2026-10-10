using System;
using System.Collections.Generic;
using System.ComponentModel;
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

    /// <summary>How many of the first lines a run keeps for <see cref="LaunchResult.EarlyOutput"/>.</summary>
    private const int EarlyLines = 400;

    /// <summary>
    /// How long the output is given to finish after the process has. A child the game
    /// started can hold the pipe open for as long as it lives; the launcher does not wait
    /// for somebody else's process.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs the game and returns its exit code. A Java that cannot be started at all is an
    /// exception here; <see cref="RunDetailedAsync"/> reports it as a result instead.
    /// </summary>
    public async Task<int> RunAsync(
        LaunchCommand command,
        string workingDirectory,
        string? logDirectory = null,
        TimeSpan? startTimeout = null,
        CancellationToken cancellationToken = default)
        => (await RunCoreAsync(command, workingDirectory, logDirectory, startTimeout, captureStartError: false, cancellationToken)
            .ConfigureAwait(false)).ExitCode;

    /// <summary>
    /// Runs the game and says how the run went: whether it got as far as starting, how
    /// long it lived and what it printed first. Never throws for a Java that would not start.
    /// </summary>
    public Task<LaunchResult> RunDetailedAsync(
        LaunchCommand command,
        string workingDirectory,
        string? logDirectory = null,
        TimeSpan? startTimeout = null,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(command, workingDirectory, logDirectory, startTimeout, captureStartError: true, cancellationToken);

    private async Task<LaunchResult> RunCoreAsync(
        LaunchCommand command,
        string workingDirectory,
        string? logDirectory,
        TimeSpan? startTimeout,
        bool captureStartError,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);

        // The log writer is per run, not a field: this class is a DI singleton, so a shared
        // writer would make two concurrent runs interleave into a single file.
        var logWriter = OpenLogFile(logDirectory);

        try
        {
            var startSignal = new StartSignal(() => GameStarted?.Invoke());
            var early = new List<string>();

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

            if (command.Environment is not null)
            {
                foreach (var (name, value) in command.Environment)
                {
                    startInfo.Environment[name] = value;
                }
            }

            // Each stream has its own pump, and an XML event spans lines.
            var outputFilter = new Log4jXmlFilter();
            var errorFilter = new Log4jXmlFilter();

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            try
            {
                process.Start();
            }
            catch (Exception ex) when (captureStartError && ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                WriteLog(logWriter, $"[launcher] Java could not be started: {ex.Message}");
                await FlushLogAsync(logWriter).ConfigureAwait(false);
                return LaunchResult.NotStarted(ClassifyStartError(ex), ex.Message);
            }

            var clock = Stopwatch.StartNew();
            using var tracking = RunningGames.Track(workingDirectory, process.Id);

            // The streams are read as bytes and decoded a line at a time. The text readers
            // Process offers decode the whole stream in one encoding, which is wrong for
            // half of what a game prints, whichever encoding is picked.
            var pumps = Task.WhenAll(
                GameOutputDecoder.PumpAsync(
                    process.StandardOutput.BaseStream,
                    line => OnRawLine(line, outputFilter, logWriter, startSignal, early, isError: false)),
                GameOutputDecoder.PumpAsync(
                    process.StandardError.BaseStream,
                    line => OnRawLine(line, errorFilter, logWriter, startSignal, early, isError: true)));

            // Fallback: if no known marker appears but the process is still alive, consider
            // the game started so the launcher is not stuck waiting.
            _ = FallbackStartAsync(process, startSignal, startTimeout ?? TimeSpan.FromSeconds(90), cancellationToken);

            // The game must outlive the launcher, so the process is intentionally not
            // killed when the launcher shuts down or the token is cancelled.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Stop();

            // Let the pumps read what is still in the pipes before closing the log.
            await Task.WhenAny(pumps, Task.Delay(DrainTimeout, CancellationToken.None)).ConfigureAwait(false);
            await FlushLogAsync(logWriter).ConfigureAwait(false);

            string[] firstLines;

            lock (early)
            {
                firstLines = early.ToArray();
            }

            return new LaunchResult(process.ExitCode, startSignal.Fired, clock.Elapsed, firstLines);
        }
        finally
        {
            if (logWriter is not null)
            {
                // A pump that outlived the drain timeout may still be writing a line.
                lock (_logLock)
                {
                    logWriter.Dispose();
                }
            }
        }
    }

    /// <summary>Windows error codes for "Windows would not run this file".</summary>
    private static JavaStartError ClassifyStartError(Exception exception) => exception switch
    {
        // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND.
        Win32Exception { NativeErrorCode: 2 or 3 } => JavaStartError.NotFound,
        FileNotFoundException or DirectoryNotFoundException => JavaStartError.NotFound,

        // ERROR_ACCESS_DENIED and ERROR_SHARING_VIOLATION: something holds java.exe, which
        // is what an antivirus scanning a freshly unpacked runtime looks like.
        // ERROR_VIRUS_INFECTED and ERROR_ACCESS_DISABLED_BY_POLICY are the same party saying no for good.
        Win32Exception { NativeErrorCode: 5 or 32 or 225 or 1260 } => JavaStartError.AccessDenied,
        UnauthorizedAccessException => JavaStartError.AccessDenied,

        _ => JavaStartError.Other
    };

    private void OnRawLine(string? line, Log4jXmlFilter filter, StreamWriter? logWriter, StartSignal startSignal, List<string> early, bool isError)
    {
        if (line is null)
        {
            return;
        }

        foreach (var plain in filter.Feed(line))
        {
            lock (early)
            {
                if (early.Count < EarlyLines)
                {
                    early.Add(plain);
                }
            }

            OnLine(plain, logWriter, startSignal, isError);
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

        public bool Fired => Volatile.Read(ref _fired) != 0;

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