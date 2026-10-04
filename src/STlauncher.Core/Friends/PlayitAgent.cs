using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace STlauncher.Core.Friends;

public enum PlayitState
{
    /// <summary>The agent has not been downloaded; <see cref="PlayitAgent.Download"/> says what that would take.</summary>
    NotInstalled,

    /// <summary>Downloaded, not running.</summary>
    Stopped,

    /// <summary>Running, not connected to playit yet.</summary>
    Starting,

    /// <summary>The player has to open <see cref="PlayitStatus.ClaimUrl"/> in a browser and approve the agent there.</summary>
    WaitingForClaim,

    /// <summary>
    /// Connected, but the account has no tunnel that leads to the server's port. One is
    /// added at <see cref="PlayitStatus.ManageUrl"/>.
    /// </summary>
    WaitingForTunnel,

    /// <summary><see cref="PlayitStatus.PublicAddress"/> is live.</summary>
    Running,

    /// <summary>See <see cref="PlayitStatus.Failure"/>.</summary>
    Failed
}

public enum PlayitFailure
{
    None,

    /// <summary>playit publishes no agent for this system.</summary>
    UnsupportedPlatform,

    /// <summary>The download did not complete: no network, or GitHub is not reachable from here.</summary>
    DownloadFailed,

    /// <summary>The file is not the one this launcher version expects. It is not run.</summary>
    HashMismatch,

    /// <summary>The system would not start the agent.</summary>
    StartFailed,

    /// <summary>The player declined the agent in the browser.</summary>
    ClaimRejected,

    /// <summary>The agent stopped by itself; <see cref="PlayitAgent.RecentOutput"/> has its last words.</summary>
    Exited
}

/// <summary>What installing the agent downloads: shown to the player before anything is fetched.</summary>
public sealed record PlayitDownload(string Version, string FileName, string Url, long Size, string Sha256);

/// <param name="ClaimUrl">The link to open while <see cref="PlayitState.WaitingForClaim"/>.</param>
/// <param name="ManageUrl">The agent's page on playit.gg, where tunnels are added and changed. Known once connected.</param>
/// <param name="PublicAddress">The address of the tunnel that leads to the server, while <see cref="PlayitState.Running"/>.</param>
/// <param name="Tunnels">Every tunnel of the agent, including ones that lead to another port or are switched off.</param>
public sealed record PlayitStatus(
    PlayitState State,
    PlayitFailure Failure,
    string? ClaimUrl,
    string? ManageUrl,
    string? PublicAddress,
    IReadOnlyList<PlayitTunnel> Tunnels);

/// <summary>
/// The playit.gg agent as a way to give the server a public address that works without
/// the launcher on the friend's side. Nothing of it ships with the launcher: the agent
/// is downloaded when the player asks, from playit's own GitHub release, as one pinned
/// file whose SHA-256 is checked before it is ever run. It lives in tools/playit under
/// the data root together with its secret file.
///
/// The pinned version is 0.17.1, the last one that is a single program run in the
/// foreground. The 1.0 line is a Windows service and needs administrator rights, which
/// is exactly what this feature promises not to ask for.
/// </summary>
public sealed class PlayitAgent : IAsyncDisposable
{
    public const string Version = "0.17.1";

    private const string ReleaseBase = "https://github.com/playit-cloud/playit-agent/releases/download/v" + Version + "/";
    private const string ApiBase = "https://api.playit.gg";
    private const string ManageBase = "https://playit.gg/account/agents/";
    private const int OutputLines = 40;

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollWhileWaiting = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PollWhileRunning = TimeSpan.FromSeconds(20);

    /// <summary>The files this launcher version accepts, by system. Sizes and hashes are those of the v0.17.1 release.</summary>
    public static IReadOnlyList<PlayitDownload> KnownDownloads { get; } = new[]
    {
        new PlayitDownload(Version, "playit-windows-x86_64-signed.exe", ReleaseBase + "playit-windows-x86_64-signed.exe", 5094152,
            "9b00d6ff7d37d1052e5ae097e1348e11deae8617cd7a8ba39d1777f2006316a3"),
        new PlayitDownload(Version, "playit-windows-x86-signed.exe", ReleaseBase + "playit-windows-x86-signed.exe", 3859208,
            "5302a28438f4612d1fd26148420a60cf9235ce4271d5d6549bec474b709cfa88"),
        new PlayitDownload(Version, "playit-linux-amd64", ReleaseBase + "playit-linux-amd64", 6170528,
            "e78d463d93aa1e3ec36a06ded5a1f4fe879905fdceb865df8f4cef6124f8a555"),
        new PlayitDownload(Version, "playit-linux-aarch64", ReleaseBase + "playit-linux-aarch64", 6478384,
            "cd3fa1cedac40a71d80a120e6353e08836308840340b58e659e8f25d00601f66")
    };

    private readonly HttpClient _http;
    private readonly ILogger<PlayitAgent>? _logger;
    private readonly object _gate = new();
    private readonly Queue<string> _output = new();

    private Process? _process;
    private CancellationTokenSource? _run;
    private Task? _poll;
    private int _serverPort;
    private bool _connected;
    private PlayitStatus _status;

    public PlayitAgent(LauncherPaths paths, HttpClient http, ILogger<PlayitAgent>? logger = null)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;

        Folder = Path.Combine(paths.Root, "tools", "playit");
        Download = DownloadFor(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            RuntimeInformation.OSArchitecture);

        // The version is in the file name, so a launcher that pins a newer agent later
        // does not mistake the old file for it.
        var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;
        ExecutablePath = Path.Combine(Folder, "playit-" + Version + extension);
        SecretPath = Path.Combine(Folder, "playit.toml");

        _status = Idle(PlayitFailure.None);
    }

    /// <summary>tools/playit under the data root. Created on install.</summary>
    public string Folder { get; }

    public string ExecutablePath { get; }

    /// <summary>The agent's secret: what ties it to the player's playit account. It never leaves this folder.</summary>
    public string SecretPath { get; }

    /// <summary>What <see cref="InstallAsync"/> would fetch on this system, or null when playit has no agent for it.</summary>
    public PlayitDownload? Download { get; }

    /// <summary>True when the file is there and has the expected size. The hash is checked on install and on every start.</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                return Download is not null && new FileInfo(ExecutablePath) is { Exists: true } file && file.Length == Download.Size;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>True when the agent has been approved on a playit account before, so starting it needs no browser.</summary>
    public bool IsClaimed => ReadSecret() is not null;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is not null;
            }
        }
    }

    public PlayitStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The agent's last lines of output, oldest first: what to show or attach when it fails.</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (_gate)
            {
                return _output.ToArray();
            }
        }
    }

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event Action<PlayitStatus>? StatusChanged;

    /// <summary>The release file for a system, or null when there is none.</summary>
    public static PlayitDownload? DownloadFor(bool windows, bool linux, Architecture architecture)
    {
        string? name = null;

        if (windows)
        {
            // ARM64 Windows runs the x64 agent under emulation; playit builds no native one.
            name = architecture == Architecture.X86 ? "playit-windows-x86-signed.exe" : "playit-windows-x86_64-signed.exe";
        }
        else if (linux)
        {
            name = architecture switch
            {
                Architecture.X64 => "playit-linux-amd64",
                Architecture.Arm64 => "playit-linux-aarch64",
                _ => null
            };
        }

        return name is null ? null : KnownDownloads.FirstOrDefault(d => d.FileName == name);
    }

    /// <summary>
    /// Downloads the agent and checks it. Call only when the player asked for it, after
    /// showing them <see cref="Download"/>. Progress is in bytes of <see cref="PlayitDownload.Size"/>.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Download is null)
        {
            Set(Idle(PlayitFailure.UnsupportedPlatform));
            return false;
        }

        if (IsRunning)
        {
            return true;
        }

        if (HasExpectedHash())
        {
            Set(Idle(PlayitFailure.None));
            return true;
        }

        Directory.CreateDirectory(Folder);

        var temp = ExecutablePath + "." + Guid.NewGuid().ToString("N") + ".part";

        try
        {
            string hash;
            long total = 0;

            using (var response = await _http.GetAsync(Download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                var buffer = new byte[81920];

                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    total += read;

                    // The size is pinned along with the hash: a longer answer is already
                    // the wrong file, and there is no reason to store the rest of it.
                    if (total > Download.Size)
                    {
                        break;
                    }

                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total);
                }

                hash = Convert.ToHexString(sha.GetHashAndReset());
            }

            if (total != Download.Size || !string.Equals(hash, Download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("The playit agent from {Url} does not match the pinned hash.", Download.Url);
                TryDelete(temp);
                Set(Idle(PlayitFailure.HashMismatch));
                return false;
            }

            File.Move(temp, ExecutablePath, overwrite: true);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(ExecutablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            Set(Idle(PlayitFailure.None));
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to download the playit agent from {Url}", Download.Url);
            TryDelete(temp);
            Set(Idle(PlayitFailure.DownloadFailed));
            return false;
        }
    }

    /// <summary>
    /// Starts the agent for a server on <paramref name="serverPort"/>. Returns false, with
    /// the reason in <see cref="Status"/>, when it is not installed or will not start.
    /// From here on <see cref="StatusChanged"/> tells the story: a claim link the first
    /// time, then the public address.
    /// </summary>
    public bool Start(int serverPort)
    {
        if (serverPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(serverPort));
        }

        PlayitStatus status;

        lock (_gate)
        {
            // Already running: only the port it is matched against changes.
            if (_process is not null)
            {
                _serverPort = serverPort;
                return true;
            }
        }

        if (Download is null)
        {
            Set(Idle(PlayitFailure.UnsupportedPlatform));
            return false;
        }

        if (!File.Exists(ExecutablePath))
        {
            Set(Idle(PlayitFailure.None));
            return false;
        }

        // Checked on every start, not only after the download: what runs is the file
        // this launcher version was built to run, or nothing.
        if (!HasExpectedHash())
        {
            Set(new PlayitStatus(PlayitState.Failed, PlayitFailure.HashMismatch, null, null, null, Array.Empty<PlayitTunnel>()));
            return false;
        }

        StopLeftover();

        var info = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = Folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // --stdout turns the agent's full-screen interface into plain log lines.
        info.ArgumentList.Add("--secret_path");
        info.ArgumentList.Add(SecretPath);
        info.ArgumentList.Add("--stdout");
        info.ArgumentList.Add("start");

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(process, e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(process, e.Data);
        process.Exited += (_, _) => OnExited(process);

        var started = false;

        lock (_gate)
        {
            if (_process is not null)
            {
                process.Dispose();
                return true;
            }

            try
            {
                process.Start();
                started = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to start the playit agent at {Path}", ExecutablePath);
                process.Dispose();
            }

            if (started)
            {
                var run = new CancellationTokenSource();

                _process = process;
                _serverPort = serverPort;
                _connected = false;
                _output.Clear();
                _run = run;
                _poll = Task.Run(() => PollAsync(process, run.Token));
            }

            status = _status = new PlayitStatus(
                started ? PlayitState.Starting : PlayitState.Failed,
                started ? PlayitFailure.None : PlayitFailure.StartFailed,
                null, null, null, Array.Empty<PlayitTunnel>());
        }

        if (started)
        {
            WritePid(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        StatusChanged?.Invoke(status);
        return started;
    }

    /// <summary>Stops the agent. The public address stops working; the claim is kept for next time.</summary>
    public async Task StopAsync()
    {
        Process? process;
        CancellationTokenSource? run;
        Task? poll;

        lock (_gate)
        {
            process = _process;
            run = _run;
            poll = _poll;
            _process = null;
            _run = null;
            _poll = null;
        }

        if (process is null)
        {
            return;
        }

        run?.Cancel();

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already gone, or refusing to die within the wait: nothing more to do here.
        }

        if (poll is not null)
        {
            await poll.ConfigureAwait(false);
        }

        process.Dispose();
        run?.Dispose();
        TryDelete(PidPath);
        Set(Idle(PlayitFailure.None));
    }

    /// <summary>
    /// Makes the agent forget its playit account, so the next start asks for a new
    /// claim. The secret file is set aside under a dated name, not deleted. Does nothing
    /// while the agent runs.
    /// </summary>
    public bool Unlink()
    {
        if (IsRunning || !File.Exists(SecretPath))
        {
            return false;
        }

        try
        {
            var aside = SecretPath + ".unlinked-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(SecretPath, aside);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private string PidPath => Path.Combine(Folder, "agent.pid");

    private void OnLine(Process process, string? line)
    {
        if (line is null)
        {
            return;
        }

        var parsed = PlayitOutput.Parse(line);
        PlayitStatus? changed = null;

        lock (_gate)
        {
            if (!ReferenceEquals(process, _process))
            {
                return;
            }

            _output.Enqueue(line);

            while (_output.Count > OutputLines)
            {
                _output.Dequeue();
            }

            if (parsed is null)
            {
                return;
            }

            switch (parsed.Kind)
            {
                case PlayitEventKind.ClaimLink:
                    changed = _status with { State = PlayitState.WaitingForClaim, Failure = PlayitFailure.None, ClaimUrl = parsed.Url };
                    break;

                case PlayitEventKind.ClaimApproved:
                    changed = _status with { State = PlayitState.Starting, ClaimUrl = null };
                    break;

                case PlayitEventKind.ClaimRejected:
                    changed = _status with { Failure = PlayitFailure.ClaimRejected, ClaimUrl = null };
                    break;

                case PlayitEventKind.SecretInvalid:
                    _connected = false;
                    break;

                case PlayitEventKind.Connected:
                    _connected = true;

                    // The agent repeats its lines at most once in ten seconds, so the
                    // "approved" one can be skipped; being connected says the same.
                    if (_status.State == PlayitState.WaitingForClaim)
                    {
                        changed = _status with { State = PlayitState.Starting, ClaimUrl = null };
                    }

                    break;
            }

            if (changed is null || changed == _status)
            {
                return;
            }

            _status = changed;
        }

        StatusChanged?.Invoke(changed);
    }

    private void OnExited(Process process)
    {
        PlayitStatus status;
        CancellationTokenSource? run;

        lock (_gate)
        {
            // Stopped on request: StopAsync has already taken the process away.
            if (!ReferenceEquals(process, _process))
            {
                return;
            }

            run = _run;
            _process = null;
            _run = null;
            _poll = null;

            var failure = _status.Failure == PlayitFailure.ClaimRejected ? PlayitFailure.ClaimRejected : PlayitFailure.Exited;
            status = _status = new PlayitStatus(PlayitState.Failed, failure, null, null, null, Array.Empty<PlayitTunnel>());
        }

        run?.Cancel();
        TryDelete(PidPath);
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// Asks playit which tunnels the agent has. The agent only logs that it is running,
    /// not where, so the address comes from the API it talks to itself, with the secret
    /// it saved. The secret goes to api.playit.gg over https and nowhere else.
    /// </summary>
    private async Task PollAsync(Process process, CancellationToken token)
    {
        var interval = PollWhileWaiting;

        try
        {
            while (true)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);

                bool connected;
                int serverPort;

                lock (_gate)
                {
                    if (!ReferenceEquals(process, _process))
                    {
                        return;
                    }

                    connected = _connected;
                    serverPort = _serverPort;
                }

                var secret = connected ? ReadSecret() : null;

                if (secret is null)
                {
                    continue;
                }

                PlayitRunData? data;

                try
                {
                    data = await FetchRunDataAsync(secret, token).ConfigureAwait(false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    continue;
                }

                // No answer, or a refused key: the agent notices the latter itself and
                // starts a new claim, which arrives through its output.
                if (data is null || data.InvalidKey)
                {
                    continue;
                }

                var match = data.Tunnels.FirstOrDefault(t => t.Enabled && t.CarriesTcp && t.LocalPort == serverPort);

                var status = new PlayitStatus(
                    match is null ? PlayitState.WaitingForTunnel : PlayitState.Running,
                    PlayitFailure.None,
                    null,
                    data.AgentId is null ? null : ManageBase + data.AgentId,
                    match?.PublicAddress,
                    data.Tunnels);

                var changed = false;

                lock (_gate)
                {
                    // A claim that started while the answer was on its way wins: the
                    // tunnels of a key that is being replaced are yesterday's news.
                    if (!ReferenceEquals(process, _process) || _status.State == PlayitState.WaitingForClaim)
                    {
                        continue;
                    }

                    if (!Same(status, _status))
                    {
                        _status = status;
                        changed = true;
                    }
                }

                if (changed)
                {
                    StatusChanged?.Invoke(status);
                }

                interval = match is null ? PollWhileWaiting : PollWhileRunning;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<PlayitRunData?> FetchRunDataAsync(string secret, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/v1/agents/rundata");
        request.Headers.TryAddWithoutValidation("Authorization", "Agent-Key " + secret);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(ApiTimeout);

        using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);

        return PlayitOutput.ParseRunData(body);
    }

    private string? ReadSecret()
    {
        try
        {
            return File.Exists(SecretPath) ? PlayitOutput.ReadSecret(File.ReadAllText(SecretPath)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool HasExpectedHash()
    {
        if (!IsInstalled)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(ExecutablePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(hash, Download!.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// An agent left running by a launcher that was killed would keep the tunnel open
    /// with nobody watching it. The one this launcher started is found by its saved
    /// process id, and only stopped if that process really is this folder's agent.
    /// </summary>
    private void StopLeftover()
    {
        try
        {
            if (!File.Exists(PidPath) || !int.TryParse(File.ReadAllText(PidPath).Trim(), out var id))
            {
                return;
            }

            using var leftover = Process.GetProcessById(id);

            if (string.Equals(leftover.MainModule?.FileName, ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                leftover.Kill(entireProcessTree: true);
                leftover.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // No such process any more, or not ours to look at: either way not a leftover.
        }

        TryDelete(PidPath);
    }

    private void WritePid(Process process)
    {
        try
        {
            File.WriteAllText(PidPath, process.Id.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
        }
    }

    private PlayitStatus Idle(PlayitFailure failure)
    {
        var state = failure switch
        {
            PlayitFailure.None => IsInstalled ? PlayitState.Stopped : PlayitState.NotInstalled,
            _ => PlayitState.Failed
        };

        return new PlayitStatus(state, failure, null, null, null, Array.Empty<PlayitTunnel>());
    }

    private void Set(PlayitStatus status)
    {
        lock (_gate)
        {
            if (Same(status, _status))
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    /// <summary>Record equality stops at the list reference; two polls with the same tunnels are the same status.</summary>
    private static bool Same(PlayitStatus a, PlayitStatus b)
        => a.State == b.State && a.Failure == b.Failure && a.ClaimUrl == b.ClaimUrl &&
           a.ManageUrl == b.ManageUrl && a.PublicAddress == b.PublicAddress &&
           a.Tunnels.SequenceEqual(b.Tunnels);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
