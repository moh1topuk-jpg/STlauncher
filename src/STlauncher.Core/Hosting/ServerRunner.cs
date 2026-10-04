using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace STlauncher.Core.Hosting;

public enum ServerStartStatus
{
    Started,

    /// <summary>The server's jar is not there; it has to be installed first.</summary>
    NotInstalled,

    /// <summary>The player has not agreed to Mojang's EULA for this server.</summary>
    EulaNotAccepted,

    /// <summary>This server is already running in this launcher.</summary>
    AlreadyRunning,

    /// <summary>Something on this machine already listens on the server's port.</summary>
    PortInUse,

    /// <summary>The Java executable given does not exist.</summary>
    JavaNotFound,

    /// <summary>The process could not be started; see <see cref="ServerStartResult.Detail"/>.</summary>
    FailedToLaunch
}

/// <param name="Process">The running server when <see cref="Status"/> is <see cref="ServerStartStatus.Started"/>.</param>
/// <param name="Detail">The system's own words for a <see cref="ServerStartStatus.FailedToLaunch"/>, for the log.</param>
public sealed record ServerStartResult(ServerStartStatus Status, ServerProcess? Process = null, string? Detail = null)
{
    public bool Started => Status == ServerStartStatus.Started;
}

/// <summary>
/// Starts servers and keeps track of the ones that are running, so that a page opened
/// later finds the server it left, a server is never started twice, and all of them can
/// be stopped together when the launcher closes.
/// </summary>
public sealed class ServerRunner
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ServerProcess> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HostedServerStore _store;
    private readonly ILogger<ServerRunner>? _logger;

    public ServerRunner(HostedServerStore store, ILogger<ServerRunner>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>Raised when a server has been started. Thread-pool thread or the caller's.</summary>
    public event Action<ServerProcess>? Started;

    /// <summary>Raised when a server's process is gone, however it ended. Thread-pool thread.</summary>
    public event Action<ServerProcess, ServerExit>? Exited;

    public IReadOnlyList<ServerProcess> Running
    {
        get
        {
            lock (_gate)
            {
                return _running.Values.ToArray();
            }
        }
    }

    /// <summary>The running process of a server, or null when it is not running.</summary>
    public ServerProcess? Find(string serverId)
    {
        lock (_gate)
        {
            return _running.TryGetValue(serverId, out var process) ? process : null;
        }
    }

    public bool IsRunning(string serverId) => Find(serverId) is not null;

    /// <summary>
    /// Whether the server could be started right now, without starting it:
    /// <see cref="ServerStartStatus.Started"/> means nothing stands in the way, anything
    /// else names what does. Java is not part of this check; see <see cref="ServerJava"/>.
    /// </summary>
    public ServerStartStatus Check(HostedServer server)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        if (IsRunning(server.Id))
        {
            return ServerStartStatus.AlreadyRunning;
        }

        var directory = _store.ServerDirectory(server);

        if (string.IsNullOrWhiteSpace(server.LaunchJar) || !File.Exists(Path.Combine(directory, server.LaunchJar!)))
        {
            return ServerStartStatus.NotInstalled;
        }

        // Both the launcher's record and the game's own file: the record says the player
        // agreed here, the file is what the server will actually look at.
        if (!server.EulaAccepted || !ServerEula.IsAccepted(directory))
        {
            return ServerStartStatus.EulaNotAccepted;
        }

        return IsPortFree(server.Port) ? ServerStartStatus.Started : ServerStartStatus.PortInUse;
    }

    /// <summary>
    /// Starts the server with the given Java and returns as soon as the process is up.
    /// Await <see cref="ServerProcess.Ready"/> to know when players can join.
    /// </summary>
    public ServerStartResult Start(HostedServer server, string javaPath)
    {
        var status = Check(server);

        if (status != ServerStartStatus.Started)
        {
            return new ServerStartResult(status);
        }

        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
        {
            return new ServerStartResult(ServerStartStatus.JavaNotFound);
        }

        var directory = _store.ServerDirectory(server);
        SyncProperties(server, directory);

        ServerProcess process;

        lock (_gate)
        {
            if (_running.ContainsKey(server.Id))
            {
                return new ServerStartResult(ServerStartStatus.AlreadyRunning);
            }

            try
            {
                process = ServerProcess.Start(server, directory, javaPath);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                _logger?.LogWarning(ex, "Could not start server {Id}.", server.Id);
                return new ServerStartResult(ServerStartStatus.FailedToLaunch, Detail: ex.Message);
            }

            _running[server.Id] = process;
        }

        _ = ForgetWhenExitedAsync(server.Id, process);

        try
        {
            server.LastStartedAt = DateTimeOffset.UtcNow;
            _store.Save(server);
        }
        catch (IOException ex)
        {
            // "Last started" is for display; the server is already running.
            _logger?.LogWarning(ex, "Could not record the start of server {Id}.", server.Id);
        }

        _logger?.LogInformation("Server {Id} started, pid {Pid}.", server.Id, process.ProcessId);
        Raise(() => Started?.Invoke(process));
        return new ServerStartResult(ServerStartStatus.Started, process);
    }

    /// <summary>
    /// Stops every running server gracefully, all at once. For the launcher's shutdown:
    /// await it before the window closes, and the worlds are saved.
    /// </summary>
    public Task StopAllAsync(TimeSpan? timeout = null)
        => Task.WhenAll(Running.Select(p => p.StopAsync(timeout)));

    /// <summary>True when nothing on this machine listens on the port yet.</summary>
    public static bool IsPortFree(int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        try
        {
            // Looked up, not tried. A listening socket of the launcher's own, even one
            // closed a moment later, makes Windows ask the player whether the launcher
            // may accept connections - a question about the wrong program, asked by a
            // page that was only opened.
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().All(e => e.Port != port);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or NotImplementedException)
        {
            return CanBind(port);
        }
    }

    /// <summary>For a system that will not list its listeners: the port is bound but never listened on.</summary>
    private static bool CanBind(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true
            };

            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// The port the launcher shows, forwards and invites people to must be the port the
    /// game listens on, so server.properties follows <c>server.json</c> at every start.
    /// A server that somehow has no server.properties yet gets the friends' defaults:
    /// left to the game it would come up in online mode, where the launcher's own
    /// accounts cannot join.
    /// </summary>
    private static void SyncProperties(HostedServer server, string directory)
    {
        var path = ServerProperties.PathIn(directory);

        if (!File.Exists(path))
        {
            var fresh = new ServerProperties();
            fresh.ApplyFriendsDefaults(server.Name, server.Port);
            fresh.Save(path);
            return;
        }

        var properties = ServerProperties.Load(path);

        if (properties.GetInt(ServerProperties.PortKey, -1) != server.Port)
        {
            properties.Set(ServerProperties.PortKey, server.Port);
            properties.Save(path);
        }
    }

    private async Task ForgetWhenExitedAsync(string serverId, ServerProcess process)
    {
        var exit = await process.Completion.ConfigureAwait(false);

        lock (_gate)
        {
            if (_running.TryGetValue(serverId, out var current) && ReferenceEquals(current, process))
            {
                _running.Remove(serverId);
            }
        }

        _logger?.LogInformation("Server {Id} exited: {Reason} ({Problem}), code {Code}.", serverId, exit.Reason, exit.Problem, exit.ExitCode);
        Raise(() => Exited?.Invoke(process, exit));
    }

    private void Raise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // A subscriber's failure is the subscriber's; the server keeps running.
            _logger?.LogWarning(ex, "A server event subscriber failed.");
        }
    }
}
