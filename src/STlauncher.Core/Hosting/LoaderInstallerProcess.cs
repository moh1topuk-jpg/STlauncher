using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Hosting;

/// <summary>What to run to put a loader's server together: Java, the installer jar and the folder to do it in.</summary>
public sealed record LoaderInstallerCommand(string JavaPath, IReadOnlyList<string> Arguments, string WorkingDirectory);

public enum LoaderInstallerEnd
{
    /// <summary>The installer ran to its end; <see cref="LoaderInstallerExit.ExitCode"/> says how.</summary>
    Exited,

    /// <summary>It took longer than an install can take and was ended.</summary>
    TimedOut,

    /// <summary>It printed nothing for so long that it must be stuck, and was ended.</summary>
    WentSilent,

    /// <summary>Java could not be started at all; <see cref="LoaderInstallerExit.Detail"/> has the system's words.</summary>
    FailedToStart
}

public sealed record LoaderInstallerExit(LoaderInstallerEnd End, int ExitCode = -1, string? Detail = null)
{
    public bool Succeeded => End == LoaderInstallerEnd.Exited && ExitCode == 0;
}

/// <summary>
/// Runs a loader's installer as a child process: its output goes to the caller line by
/// line, it cannot run for ever, and it cannot outlive the launcher.
/// </summary>
public static class LoaderInstallerProcess
{
    /// <summary>
    /// A Forge install is a few hundred megabytes of downloads and a few minutes of
    /// patching the game; on a slow line and a slow disk that is a quarter of an hour.
    /// Twice that with nothing finished is a hang, not patience.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The installer names every file it fetches and every step it runs. The longest
    /// quiet stretch is the step that patches the server jar - a minute or two.
    /// </summary>
    public static readonly TimeSpan DefaultSilenceTimeout = TimeSpan.FromMinutes(10);

    public static async Task<LoaderInstallerExit> RunAsync(
        LoaderInstallerCommand command,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        TimeSpan? silenceTimeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.JavaPath,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Redirected and closed at once: an installer that stops to ask something
            // gets an end of input instead of waiting for an answer nobody can give.
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var lastOutput = Stopwatch.StartNew();

        void Line(string? data)
        {
            if (data is null)
            {
                return;
            }

            lock (lastOutput)
            {
                lastOutput.Restart();
            }

            try
            {
                onLine?.Invoke(data);
            }
            catch (Exception)
            {
                // A listener's failure is the listener's; the install goes on.
            }
        }

        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new LoaderInstallerExit(LoaderInstallerEnd.FailedToStart, Detail: ex.Message);
        }

        // Before anything else, as with the server itself: from here on the installer
        // must not be able to outlive the launcher.
        ChildProcessGuard.Attach(process);
        ChildProcessGuard.TrackHelper(process);

        try
        {
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var total = timeout ?? DefaultTimeout;
            var silence = silenceTimeout ?? DefaultSilenceTimeout;
            var running = Stopwatch.StartNew();

            // Also waits for both output streams to end, so no line arrives after the verdict.
            var exited = process.WaitForExitAsync(CancellationToken.None);

            while (await Task.WhenAny(exited, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false) != exited)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await KillAsync(process, exited).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (running.Elapsed > total)
                {
                    await KillAsync(process, exited).ConfigureAwait(false);
                    return new LoaderInstallerExit(LoaderInstallerEnd.TimedOut);
                }

                TimeSpan quiet;

                lock (lastOutput)
                {
                    quiet = lastOutput.Elapsed;
                }

                if (quiet > silence)
                {
                    await KillAsync(process, exited).ConfigureAwait(false);
                    return new LoaderInstallerExit(LoaderInstallerEnd.WentSilent);
                }
            }

            await exited.ConfigureAwait(false);
            return new LoaderInstallerExit(LoaderInstallerEnd.Exited, process.ExitCode);
        }
        finally
        {
            ChildProcessGuard.UntrackHelper(process);
        }
    }

    /// <summary>The whole tree: the installer starts further Javas for its patching steps.</summary>
    private static async Task KillAsync(Process process, Task exited)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, which is what was wanted.
        }

        // So that the files it held are free to be cleaned up when this returns.
        await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
    }
}
