using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core.Auth;
using STlauncher.Core.Friends;
using STlauncher.Core.Hosting;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;

namespace STlauncher.App.ViewModels;

/// <summary>What the state pill of the server card says.</summary>
public enum HostServerState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping
}

/// <summary>
/// One line of "what will happen": on the creation card, in a consent panel, on an invite.
/// </summary>
/// <param name="IsDownload">Something fetched from the network; drawn with a marked bullet.</param>
public sealed record HostPlanLine(string Title, string Detail, bool IsDownload = false)
{
    public bool IsNote => !IsDownload;
}

/// <param name="FolderName">The save to copy, or null for a new world.</param>
public sealed record HostWorldChoice(string Display, string? FolderName, long SizeBytes = 0);

public enum HostWayKind
{
    Relay,
    Direct
}

/// <summary>One way friends can reach the server: its switch, what it does and where it stands.</summary>
public partial class HostWayItem : ObservableObject
{
    public HostWayItem(HostWayKind kind)
    {
        Kind = kind;
    }

    public HostWayKind Kind { get; }

    public bool IsRelay => Kind == HostWayKind.Relay;

    public bool IsDirect => Kind == HostWayKind.Direct;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>What the player must have for the way to work at all; shown apart from the description so it is not read past.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warning = string.Empty;

    public bool HasWarning => Warning.Length > 0;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _canSwitch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOff))]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private FriendsWayState _state = FriendsWayState.Off;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>The address friends use, while the way is ready and has one to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddress))]
    private string _address = string.Empty;

    public bool IsOff => State == FriendsWayState.Off;

    public bool IsWorking => State == FriendsWayState.Working;

    public bool IsReady => State == FriendsWayState.Ready;

    public bool IsFailed => State == FriendsWayState.Failed;

    public bool HasAddress => Address.Length > 0;
}

/// <summary>
/// "My server": a Minecraft server the player runs on this machine for friends. The page
/// creates it from a build, starts and stops it, shows its console and whitelist, and
/// opens the ways friends connect by. Nothing is downloaded, started or opened to the
/// network until the player has seen what it is and pressed the button for it; and
/// nothing here outlives the launcher window without the player being asked.
/// </summary>
public partial class MainWindowViewModel
{
    private const int MaxHostConsoleLines = 500;

    private HostingServices _hosting = null!;
    private bool _hostLoaded;
    private bool _applyingHostServer;

    /// <summary>The one running server. The core can run several; the page keeps to one so there is one console to watch.</summary>
    private ServerProcess? _hostProcess;

    /// <summary>The ways opened for the running server. Null while no server is ready.</summary>
    private FriendsHostSession? _hostSession;

    private CancellationTokenSource? _hostDirectCts;

    private readonly ConcurrentQueue<string> _hostPendingLines = new();
    private DispatcherTimer? _hostConsoleTimer;

    public bool IsHostSection => Section == ShellSection.Host;

    /// <summary>Called once from the constructor: takes the services and listens for the page being opened.</summary>
    private void AttachHosting(HostingServices hosting)
    {
        _hosting = hosting;

        HostWays.Add(new HostWayItem(HostWayKind.Relay));
        HostWays.Add(new HostWayItem(HostWayKind.Direct));
        AttachHostTabs();

        // Subscribed before the window's own handler, so by the time the window is asked
        // to close this already knows the launcher itself asked for it (the game ended).
        RequestCloseLauncher += () => _closeAskedByLauncher = true;

        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Section):
                    OnPropertyChanged(nameof(IsHostSection));

                    if (Section == ShellSection.Host)
                    {
                        OpenHostSection();
                    }

                    break;

                case nameof(Language):
                    RaiseHostCardLabels();
                    RefreshHostWays();
                    RefreshFriendServers();
                    break;
            }
        };
    }

    /// <summary>The lists are read the first time the page is opened, not at startup: most players never host.</summary>
    private void OpenHostSection()
    {
        if (!_hostLoaded)
        {
            _hostLoaded = true;
            LoadHostServers();
            LoadFriendServers();
        }

        // The catalog, which names the relay, may have arrived since the last look.
        RefreshHostWays();
        RefreshFriendServers();
    }

    // ===================== The list of servers =====================

    public ObservableCollection<HostedServer> HostServers { get; } = new();

    [ObservableProperty]
    private HostedServer? _selectedHostServer;

    public bool HasHostServers => HostServers.Count > 0;

    public bool HasManyHostServers => HostServers.Count > 1;

    /// <summary>The friendly explanation: nothing made yet and the creation card is closed.</summary>
    public bool ShowHostEmpty => !HasHostServers && !IsHostCreateOpen;

    public bool ShowHostServer => SelectedHostServer is not null && !IsHostCreateOpen;

    private void LoadHostServers()
    {
        try
        {
            HostServers.Clear();

            foreach (var server in _hosting.Store.List())
            {
                HostServers.Add(server);
            }

            if (_hosting.Store.UnreadableDefinitions.Count > 0)
            {
                Status = Localize(
                    "Host_Unreadable",
                    "A server could not be read and was left untouched: {0}",
                    string.Join(", ", _hosting.Store.UnreadableDefinitions.Select(Path.GetFileName)));
            }
        }
        catch (Exception ex)
        {
            Status = Localize("Host_LoadFailed", "Could not read the list of servers: {0}", ex.Message);
            AppendConsole($"[host] {ex}");
        }

        SelectedHostServer = HostServers
            .OrderByDescending(s => s.LastStartedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        RaiseHostListChanged();
    }

    private void RaiseHostListChanged()
    {
        OnPropertyChanged(nameof(HasHostServers));
        OnPropertyChanged(nameof(HasManyHostServers));
        OnPropertyChanged(nameof(ShowHostEmpty));
        OnPropertyChanged(nameof(ShowHostServer));
    }

    partial void OnSelectedHostServerChanged(HostedServer? value)
    {
        OnPropertyChanged(nameof(ShowHostServer));
        RefreshHostCard();
    }

    private void SaveHostServer(HostedServer server)
    {
        try
        {
            _hosting.Store.Save(server);
        }
        catch (Exception ex)
        {
            Status = Localize("Host_SaveFailed", "Could not save the server's settings: {0}", ex.Message);
        }
    }

    // ===================== The server card =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostStateLabel))]
    [NotifyPropertyChangedFor(nameof(IsHostOnline))]
    [NotifyPropertyChangedFor(nameof(IsHostWorking))]
    [NotifyPropertyChangedFor(nameof(IsHostOff))]
    [NotifyPropertyChangedFor(nameof(CanStartHost))]
    [NotifyPropertyChangedFor(nameof(CanStopHost))]
    [NotifyPropertyChangedFor(nameof(CanEditHost))]
    [NotifyPropertyChangedFor(nameof(CanPlayOnHost))]
    [NotifyPropertyChangedFor(nameof(HostPlayersLabel))]
    private HostServerState _hostState = HostServerState.Stopped;

    public string HostStateLabel => HostState switch
    {
        HostServerState.NotInstalled => Localize("Host_StateNotInstalled", "not installed"),
        HostServerState.Starting => Localize("Host_StateStarting", "starting"),
        HostServerState.Running => Localize("Host_StateRunning", "running"),
        HostServerState.Stopping => Localize("Host_StateStopping", "stopping"),
        _ => Localize("Host_StateStopped", "stopped")
    };

    public bool IsHostOnline => HostState == HostServerState.Running;

    public bool IsHostWorking => HostState is HostServerState.Starting or HostServerState.Stopping;

    public bool IsHostOff => HostState is HostServerState.Stopped or HostServerState.NotInstalled;

    public bool CanStartHost => IsHostOff && !IsHostBusy;

    public bool CanStopHost => HostState is HostServerState.Starting or HostServerState.Running;

    /// <summary>Memory, the port and removal wait for the server to be stopped.</summary>
    public bool CanEditHost => IsHostOff && !IsHostBusy;

    /// <summary>Something is being downloaded, copied or looked up for the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartHost))]
    [NotifyPropertyChangedFor(nameof(CanEditHost))]
    [NotifyPropertyChangedFor(nameof(CanCreateHostServer))]
    private bool _isHostBusy;

    [ObservableProperty]
    private string _hostBusyText = string.Empty;

    [ObservableProperty]
    private double _hostProgress;

    /// <summary>The size of what is being fetched is not known, so the bar only shows that work goes on.</summary>
    [ObservableProperty]
    private bool _isHostProgressUnknown;

    /// <summary>Why the server is not running although it was asked to; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostNotice))]
    private string _hostNotice = string.Empty;

    public bool HasHostNotice => HostNotice.Length > 0;

    /// <summary>The notice is about a taken port, and another one can be picked with one click.</summary>
    [ObservableProperty]
    private bool _hostNoticeOffersPort;

    public string HostServerName => SelectedHostServer?.Name ?? string.Empty;

    public string HostVersionLabel => SelectedHostServer?.GameVersion ?? string.Empty;

    public string HostLoaderLabel => SelectedHostServer?.Loader.ToString() ?? string.Empty;

    public string HostPortLabel => Localize("Host_PortChip", "port {0}", SelectedHostServer?.Port ?? HostedServer.DefaultPort);

    public string HostMemoryLabel => Localize("Host_MemoryChip", "{0} of memory", FormatHostMemory(SelectedHostServer?.MemoryMb ?? HostedServer.DefaultMemoryMb));

    /// <summary>The build the server was made from, if it is still there.</summary>
    private Instance? HostSourceInstance => SelectedHostServer?.SourceInstanceId is { Length: > 0 } id
        ? _allInstances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase))
        : null;

    public string HostBuildLabel => SelectedHostServer is null
        ? string.Empty
        : HostSourceInstance is { } instance
            ? Localize("Host_BasedOn", "made from the build “{0}”", instance.Name)
            : Localize("Host_BasedOnGone", "the build it was made from is gone");

    /// <summary>"Join it myself": the server is up and the build it was made from can be started.</summary>
    public bool CanPlayOnHost => IsHostOnline && HostSourceInstance is not null;

    public ObservableCollection<string> HostPlayers { get; } = new();

    public bool HasHostPlayers => HostPlayers.Count > 0;

    public string HostPlayersLabel => !IsHostOnline
        ? Localize("Host_PlayersOffline", "Players show up here once the server is running")
        : HostPlayers.Count == 0
            ? Localize("Host_PlayersNone", "Nobody is on the server")
            : Localize("Host_PlayersCount", "On the server: {0}", HostPlayers.Count);

    private static string FormatHostMemory(double megabytes)
        => Localize("Host_MemoryValue", "{0:0.#} GB", megabytes / 1024d);

    /// <summary>The running process of the selected server, or null.</summary>
    private ServerProcess? SelectedHostProcess
        => SelectedHostServer is { } server &&
           _hostProcess is { HasExited: false } process &&
           string.Equals(process.Server.Id, server.Id, StringComparison.OrdinalIgnoreCase)
            ? process
            : null;

    private void RaiseHostCardLabels()
    {
        OnPropertyChanged(nameof(HostServerName));
        OnPropertyChanged(nameof(HostVersionLabel));
        OnPropertyChanged(nameof(HostLoaderLabel));
        OnPropertyChanged(nameof(HostPortLabel));
        OnPropertyChanged(nameof(HostMemoryLabel));
        OnPropertyChanged(nameof(HostBuildLabel));
        OnPropertyChanged(nameof(HostStateLabel));
        OnPropertyChanged(nameof(HostPlayersLabel));
        OnPropertyChanged(nameof(CanPlayOnHost));
    }

    /// <summary>Puts the selected server on the card: its state, console, players, whitelist and switches.</summary>
    private void RefreshHostCard()
    {
        var server = SelectedHostServer;

        CloseHostConsent();
        HostNotice = string.Empty;
        HostNoticeOffersPort = false;

        HostConsole.Clear();
        HostPlayers.Clear();
        HostWhitelist.Clear();

        // Whatever is queued belongs to the running server; its recent lines are read below.
        while (_hostPendingLines.TryDequeue(out _))
        {
        }

        if (server is not null)
        {
            _applyingHostServer = true;

            try
            {
                HostMemoryMb = server.MemoryMb;
                HostPortValue = server.Port;
            }
            finally
            {
                _applyingHostServer = false;
            }

            try
            {
                foreach (var player in ServerAccessLists.ReadWhitelist(_hosting.Store.ServerDirectory(server)))
                {
                    HostWhitelist.Add(player.Name);
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"[host] whitelist: {ex.Message}");
            }

            if (SelectedHostProcess is { } process)
            {
                foreach (var line in process.RecentLines)
                {
                    HostConsole.Add(line.Text);
                }

                foreach (var player in process.Players)
                {
                    HostPlayers.Add(player);
                }

                HostState = MapHostState(process.State);
            }
            else
            {
                HostState = _hosting.Installer.IsInstalled(server) ? HostServerState.Stopped : HostServerState.NotInstalled;
            }
        }

        OnPropertyChanged(nameof(HasHostPlayers));
        OnPropertyChanged(nameof(HasHostConsole));
        RaiseHostCardLabels();
        RefreshHostWays();
    }

    private static HostServerState MapHostState(ServerState state) => state switch
    {
        ServerState.Starting => HostServerState.Starting,
        ServerState.Running => HostServerState.Running,
        ServerState.Stopping => HostServerState.Stopping,
        _ => HostServerState.Stopped
    };

    // ===================== Start and stop =====================

    [RelayCommand]
    private async Task StartHostServerAsync()
    {
        if (SelectedHostServer is not { } server || IsHostBusy || IsHostCreateOpen)
        {
            return;
        }

        if (_hostProcess is { HasExited: false } other &&
            !string.Equals(other.Server.Id, server.Id, StringComparison.OrdinalIgnoreCase))
        {
            Status = Localize("Host_OtherRunning", "Stop the server “{0}” first: one runs at a time.", other.Server.Name);
            return;
        }

        CloseHostConsent();
        HostNotice = string.Empty;
        HostNoticeOffersPort = false;

        try
        {
            switch (_hosting.Runner.Check(server))
            {
                case ServerStartStatus.AlreadyRunning:
                    return;

                case ServerStartStatus.NotInstalled:
                    await OfferHostInstallAsync(server);
                    return;

                case ServerStartStatus.EulaNotAccepted:
                    OfferHostEula(server);
                    return;

                case ServerStartStatus.PortInUse:
                    HostNotice = Localize("Host_NoticePort", "Port {0} is taken by another program - perhaps another Minecraft server is already running.", server.Port);
                    HostNoticeOffersPort = true;
                    return;
            }

            IsHostBusy = true;
            IsHostProgressUnknown = true;
            HostBusyText = Localize("Host_BusyFindJava", "Looking for Java…");

            var major = server.JavaMajor > 0 ? server.JavaMajor : 21;
            var java = await Task.Run(() => _hosting.Java.Find(major));

            if (java is null)
            {
                // Never fetched on the quiet: the panel says what and how big, and waits.
                await OfferHostJavaAsync(server, major);
                return;
            }

            var result = _hosting.Runner.Start(server, java);

            switch (result.Status)
            {
                case ServerStartStatus.Started when result.Process is not null:
                    AttachHostProcess(result.Process);
                    Status = Localize("Host_Starting", "The server is starting: the world is loading…");
                    break;

                case ServerStartStatus.PortInUse:
                    HostNotice = Localize("Host_NoticePort", "Port {0} is taken by another program - perhaps another Minecraft server is already running.", server.Port);
                    HostNoticeOffersPort = true;
                    break;

                case ServerStartStatus.JavaNotFound:
                    HostNotice = Localize("Host_NoticeJavaPath", "Java was not found where the search said it was. Try again.");
                    break;

                case ServerStartStatus.AlreadyRunning:
                    break;

                default:
                    HostNotice = Localize("Host_NoticeLaunch", "The server could not be started: {0}", result.Detail ?? result.Status.ToString());
                    break;
            }
        }
        catch (Exception ex)
        {
            HostNotice = Localize("Host_NoticeLaunch", "The server could not be started: {0}", ex.Message);
            AppendConsole($"[host] {ex}");
        }
        finally
        {
            IsHostBusy = false;
            IsHostProgressUnknown = false;
            HostBusyText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task StopHostServerAsync()
    {
        if (SelectedHostProcess is not { } process)
        {
            return;
        }

        HostState = HostServerState.Stopping;
        Status = Localize("Host_Stopping", "Stopping the server, the world is being saved…");

        try
        {
            // The exit handler puts the card back; this only asks.
            await process.StopAsync();
        }
        catch (Exception ex)
        {
            AppendConsole($"[host] stop: {ex}");
        }
    }

    /// <summary>Follows a server that has just been started: console, state, players, exit.</summary>
    private void AttachHostProcess(ServerProcess process)
    {
        _hostProcess = process;

        // A new server is a new reason to ask before the window closes.
        _hostCloseConfirmed = false;

        HostConsole.Clear();
        HostPlayers.Clear();
        OnPropertyChanged(nameof(HasHostPlayers));
        OnPropertyChanged(nameof(HasHostConsole));

        // All of these arrive on thread-pool threads. Lines come in bursts while the world
        // loads, so they are queued and drained on a timer; the rest is rare and is posted.
        process.LineReceived += line => _hostPendingLines.Enqueue(line.Text);
        process.StateChanged += state => Dispatcher.UIThread.Post(() => OnHostStateChanged(process, state));
        process.PlayerJoined += _ => Dispatcher.UIThread.Post(() => RefreshHostPlayers(process));
        process.PlayerLeft += _ => Dispatcher.UIThread.Post(() => RefreshHostPlayers(process));
        process.Exited += exit => Dispatcher.UIThread.Post(() => OnHostExited(process, exit));

        StartHostConsolePump();

        // Anything that happened between the start and the subscriptions above.
        OnHostStateChanged(process, process.State);

        if (process.Completion.IsCompleted)
        {
            OnHostExited(process, process.Completion.Result);
        }
    }

    private void OnHostStateChanged(ServerProcess process, ServerState state)
    {
        if (!ReferenceEquals(process, _hostProcess) || state == ServerState.Exited)
        {
            return;
        }

        if (state == ServerState.Running && _hostSession is null)
        {
            StartHostWays(process.Server);
            Status = Localize("Host_Ready", "The server “{0}” is running. Friends can be invited now.", process.Server.Name);
        }

        if (ReferenceEquals(SelectedHostProcess, process))
        {
            HostState = MapHostState(state);
        }

        RefreshHostWays();
    }

    private void RefreshHostPlayers(ServerProcess process)
    {
        if (!ReferenceEquals(SelectedHostProcess, process))
        {
            return;
        }

        HostPlayers.Clear();

        foreach (var player in process.Players)
        {
            HostPlayers.Add(player);
        }

        OnPropertyChanged(nameof(HasHostPlayers));
        OnPropertyChanged(nameof(HostPlayersLabel));
    }

    private void OnHostExited(ServerProcess process, ServerExit exit)
    {
        if (!ReferenceEquals(process, _hostProcess))
        {
            return;
        }

        var selected = SelectedHostServer is { } server &&
                       string.Equals(server.Id, process.Server.Id, StringComparison.OrdinalIgnoreCase);

        FlushHostConsole();
        _hostConsoleTimer?.Stop();
        _hostProcess = null;

        // The server is gone, so is every way to it: the room, the port on the router, the tunnel.
        _ = StopHostWaysAsync();

        var notice = DescribeHostExit(process.Server, exit);

        if (selected)
        {
            HostState = HostServerState.Stopped;
            HostPlayers.Clear();
            OnPropertyChanged(nameof(HasHostPlayers));
            OnPropertyChanged(nameof(HostPlayersLabel));
            HostNotice = notice;
            HostNoticeOffersPort = exit.Problem == ServerProblem.PortInUse;
        }

        Status = notice.Length == 0 ? Localize("Host_Stopped", "The server is stopped, the world is saved") : notice;
        RefreshHostWays();
    }

    private static string DescribeHostExit(HostedServer server, ServerExit exit)
    {
        if (exit.Reason == ServerExitReason.Stopped)
        {
            return string.Empty;
        }

        if (exit.Reason == ServerExitReason.Killed)
        {
            return Localize("Host_NoticeKilled", "The server did not stop in time and was closed by force. The last minutes of play may not have been saved.");
        }

        return exit.Problem switch
        {
            ServerProblem.PortInUse => Localize("Host_NoticePort", "Port {0} is taken by another program - perhaps another Minecraft server is already running.", server.Port),
            ServerProblem.EulaNotAccepted => Localize("Host_NoticeEula", "The server did not start: eula.txt does not agree to the licence."),
            ServerProblem.WrongJava => Localize("Host_NoticeWrongJava", "The server did not start: it needs a newer Java than the one it was started with."),
            ServerProblem.NotEnoughMemory => Localize("Host_NoticeMemory", "Java could not get {0} of memory. Lower the memory on the “Server settings” tab and start again.", FormatHostMemory(server.MemoryMb)),
            ServerProblem.WorldInUse => Localize("Host_NoticeWorldInUse", "The server's world is open in another program - perhaps another server or the game."),
            _ => exit.Reason == ServerExitReason.FailedToStart
                ? Localize("Host_NoticeFailedToStart", "The server could not start (code {0}). The last lines of the console say why.", exit.ExitCode)
                : Localize("Host_NoticeCrash", "The server went down with an error (code {0}). The console below shows what happened.", exit.ExitCode)
        };
    }

    /// <summary>After "port is taken": the next free one, saved for the next start.</summary>
    [RelayCommand]
    private void UseFreeHostPort()
    {
        if (SelectedHostServer is not { } server || !CanEditHost)
        {
            return;
        }

        for (var port = server.Port + 1; port <= Math.Min(server.Port + 200, 65535); port++)
        {
            if (!ServerRunner.IsPortFree(port))
            {
                continue;
            }

            try
            {
                _hosting.Store.SetPort(server, port);
            }
            catch (Exception ex)
            {
                Status = Localize("Host_SaveFailed", "Could not save the server's settings: {0}", ex.Message);
                return;
            }

            _applyingHostServer = true;
            HostPortValue = port;
            _applyingHostServer = false;

            HostNotice = string.Empty;
            HostNoticeOffersPort = false;
            OnPropertyChanged(nameof(HostPortLabel));
            RefreshHostWays();
            Status = Localize("Host_PortChanged", "The server's port is now {0}. Press “Start”.", port);
            return;
        }
    }

    /// <summary>Starts the build the server was made from, straight into the server on this machine.</summary>
    [RelayCommand]
    private async Task PlayOnHostServerAsync()
    {
        if (SelectedHostProcess is not { } process || HostSourceInstance is not { } instance || IsBusy || IsGameRunning)
        {
            return;
        }

        // The owner may have changed their nickname since the server was made; the server
        // would turn its own owner away at the door.
        if (OfflineAuth.IsValidUsername(Username) && !HostWhitelist.Contains(Username))
        {
            try
            {
                if (ServerAccessLists.AddToWhitelist(process.ServerDirectory, Username))
                {
                    HostWhitelist.Add(Username);
                    ReloadHostWhitelist(process.Server);
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"[host] whitelist: {ex.Message}");
            }
        }

        SelectedInstance = instance;
        _friendJoinAddress = "127.0.0.1:" + process.Server.Port;

        try
        {
            await StartAsync(joinServer: false);
        }
        finally
        {
            _friendJoinAddress = null;
        }
    }

    // ===================== Consent before a download =====================
    // A server found half-made, or a Java that has gone missing since: what is needed is
    // listed with its source and size, and nothing moves until the button is pressed.

    [ObservableProperty]
    private bool _isHostConsentOpen;

    [ObservableProperty]
    private string _hostConsentTitle = string.Empty;

    [ObservableProperty]
    private string _hostConsentAction = string.Empty;

    /// <summary>The panel is about Mojang's licence: it carries the link to it.</summary>
    [ObservableProperty]
    private bool _hostConsentHasEula;

    public ObservableCollection<HostPlanLine> HostConsentLines { get; } = new();

    private Func<Task>? _hostConsentRun;

    private void OpenHostConsent(string title, IEnumerable<HostPlanLine> lines, string action, Func<Task> run, bool eula = false)
    {
        HostConsentLines.Clear();

        foreach (var line in lines)
        {
            HostConsentLines.Add(line);
        }

        HostConsentTitle = title;
        HostConsentAction = action;
        HostConsentHasEula = eula;
        _hostConsentRun = run;
        IsHostConsentOpen = true;
    }

    private void CloseHostConsent()
    {
        IsHostConsentOpen = false;
        _hostConsentRun = null;
    }

    [RelayCommand]
    private async Task ConfirmHostConsentAsync()
    {
        var run = _hostConsentRun;
        CloseHostConsent();

        if (run is not null)
        {
            await run();
        }
    }

    [RelayCommand]
    private void CancelHostConsent() => CloseHostConsent();

    [RelayCommand]
    private void OpenHostEula() => OpenUrl(ServerEula.Url);

    private async Task OfferHostInstallAsync(HostedServer server)
    {
        if (!ServerInstaller.IsSupported(server.Loader))
        {
            HostNotice = Localize("Host_ProblemLoader", "The launcher cannot set up a {0} server yet. Builds without a loader and Fabric builds are supported.", server.Loader);
            return;
        }

        ServerInstallPlan plan;

        try
        {
            IsHostBusy = true;
            IsHostProgressUnknown = true;
            HostBusyText = Localize("Host_PlanBusy", "Finding out what has to be downloaded…");
            plan = await _hosting.Installer.PlanAsync(server);
        }
        finally
        {
            IsHostBusy = false;
            IsHostProgressUnknown = false;
            HostBusyText = string.Empty;
        }

        if (!plan.CanInstall)
        {
            HostNotice = DescribeHostPlanFailure(plan);
            return;
        }

        OpenHostConsent(
            Localize("Host_ConsentInstallTitle", "The server has not been downloaded yet"),
            plan.Downloads.Select(d => HostDownloadLine(d, plan.GameVersion, plan.Loader, plan.LoaderVersion, plan.JavaMajor)),
            Localize("Host_ConsentInstallAction", "Download"),
            async () =>
            {
                try
                {
                    IsHostBusy = true;
                    var result = await _hosting.Installer.InstallAsync(server, plan, HostInstallProgress());

                    if (!result.Succeeded)
                    {
                        HostNotice = Localize(
                            "Host_NoticeDownload",
                            "The server could not be downloaded: {0}. Check the connection and press “Start” again.",
                            result.Failure?.Detail ?? result.Outcome.ToString());
                        return;
                    }

                    if (!File.Exists(ServerProperties.PathIn(_hosting.Store.ServerDirectory(server))))
                    {
                        _hosting.Store.WriteStartingFiles(server, Username);
                    }
                }
                catch (Exception ex)
                {
                    HostNotice = Localize("Host_NoticeDownload", "The server could not be downloaded: {0}. Check the connection and press “Start” again.", ex.Message);
                    return;
                }
                finally
                {
                    IsHostBusy = false;
                    HostBusyText = string.Empty;
                    HostProgress = 0;
                }

                RefreshHostCard();
                await StartHostServerAsync();
            });
    }

    private void OfferHostEula(HostedServer server)
    {
        OpenHostConsent(
            Localize("Host_ConsentEulaTitle", "The Minecraft licence agreement"),
            new[]
            {
                new HostPlanLine(
                    Localize("Host_ConsentEulaLine", "A Minecraft server only starts once Mojang's agreement (EULA) is accepted"),
                    Localize("Host_ConsentEulaDetail", "By pressing the button you confirm that you have read it and accept it."))
            },
            Localize("Host_ConsentEulaAction", "I accept, start"),
            async () =>
            {
                try
                {
                    // The click on a button that says so is the player's answer.
                    _hosting.Store.AcceptEula(server, playerAccepted: true);
                }
                catch (Exception ex)
                {
                    HostNotice = Localize("Host_SaveFailed", "Could not save the server's settings: {0}", ex.Message);
                    return;
                }

                await StartHostServerAsync();
            },
            eula: true);
    }

    private async Task OfferHostJavaAsync(HostedServer server, int major)
    {
        HostBusyText = Localize("Host_PlanBusy", "Finding out what has to be downloaded…");
        var download = await _hosting.Java.DescribeDownloadAsync(major);

        if (download is null)
        {
            HostNotice = Localize("Host_NoticeJavaMissing", "The server needs Java {0} and this computer has none. The download site does not answer right now - check the connection and try again.", major);
            return;
        }

        OpenHostConsent(
            Localize("Host_ConsentJavaTitle", "The server needs Java {0}", major),
            new[] { HostDownloadLine(download, server.GameVersion, server.Loader, server.LoaderVersion, major) },
            Localize("Host_ConsentJavaAction", "Download Java and start"),
            async () =>
            {
                try
                {
                    IsHostBusy = true;
                    IsHostProgressUnknown = true;
                    HostBusyText = Localize("Host_BusyJava", "Downloading Java {0}…", major);
                    await _hosting.Java.EnsureAsync(major);
                }
                catch (Exception ex)
                {
                    HostNotice = Localize("Host_NoticeJavaDownload", "Java could not be downloaded: {0}", ex.Message);
                    return;
                }
                finally
                {
                    IsHostBusy = false;
                    IsHostProgressUnknown = false;
                    HostBusyText = string.Empty;
                }

                await StartHostServerAsync();
            });
    }

    private IProgress<ServerInstallProgress> HostInstallProgress()
        => new Progress<ServerInstallProgress>(p =>
        {
            IsHostProgressUnknown = p.BytesTotal <= 0;
            HostProgress = p.BytesTotal > 0 ? Math.Min(100, p.BytesDone * 100d / p.BytesTotal) : 0;
            HostBusyText = p.BytesTotal > 0
                ? Localize("Host_BusyDownloadOf", "Downloading the server: {0} of {1}", FormatSize(p.BytesDone), FormatSize(p.BytesTotal))
                : Localize("Host_BusyDownload", "Downloading the server…");
        });

    private static HostPlanLine HostDownloadLine(ServerDownload download, string gameVersion, LoaderKind loader, string? loaderVersion, int javaMajor)
    {
        var title = download.Kind switch
        {
            ServerDownloadKind.ServerJar => Localize("Host_PlanServerJar", "Minecraft server {0}", gameVersion),
            ServerDownloadKind.LoaderLauncher => Localize("Host_PlanLoaderLauncher", "{0} {1} starter", loader, loaderVersion ?? string.Empty).Trim(),
            ServerDownloadKind.LoaderLibraries => Localize("Host_PlanLoaderLibraries", "{0} libraries", loader),
            _ => Localize("Host_PlanJava", "Java {0}", javaMajor)
        };

        var detail = download.SizeBytes <= 0
            ? Localize("Host_PlanFromUnknown", "{0} · size not known in advance", download.Host)
            : download.AtFirstStart
                ? Localize("Host_PlanAtFirstStart", "{0} · about {1} · the server fetches them itself on its first start", download.Host, FormatSize(download.SizeBytes))
                : download.SizeIsExact
                    ? Localize("Host_PlanFrom", "{0} · {1}", download.Host, FormatSize(download.SizeBytes))
                    : Localize("Host_PlanFromAbout", "{0} · about {1}", download.Host, FormatSize(download.SizeBytes));

        return new HostPlanLine(title, detail, IsDownload: true);
    }

    private static string DescribeHostPlanFailure(ServerInstallPlan plan) => plan.Status switch
    {
        ServerInstallStatus.UnsupportedLoader => Localize("Host_ProblemLoader", "The launcher cannot set up a {0} server yet. Builds without a loader and Fabric builds are supported.", plan.Loader),
        ServerInstallStatus.UnknownGameVersion => Localize("Host_ProblemUnknownVersion", "Mojang does not list version {0}, so there is no server to get for it.", plan.GameVersion),
        ServerInstallStatus.NoServerForVersion => Localize("Host_ProblemNoServer", "Mojang publishes no server for version {0}.", plan.GameVersion),
        ServerInstallStatus.LoaderUnavailable => Localize("Host_ProblemLoaderUnavailable", "{0} has no build for Minecraft {1}.", plan.Loader, plan.GameVersion),
        _ => Localize("Host_ProblemNetwork", "Could not find out what has to be downloaded: {0}. Check the connection and try again.", plan.Failure?.Detail ?? plan.Status.ToString())
    };

    // ===================== Console =====================

    public ObservableCollection<string> HostConsole { get; } = new();

    public bool HasHostConsole => HostConsole.Count > 0;

    [ObservableProperty]
    private string _hostCommand = string.Empty;

    private void StartHostConsolePump()
    {
        if (_hostConsoleTimer is null)
        {
            // Runs only while a server does; it is stopped again when the server exits.
            _hostConsoleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _hostConsoleTimer.Tick += (_, _) => FlushHostConsole();
        }

        _hostConsoleTimer.Start();
    }

    private void FlushHostConsole()
    {
        if (_hostPendingLines.IsEmpty)
        {
            return;
        }

        // Another server is on the card: its console is not this one's. The lines are
        // not lost - the process keeps its recent ones for when the card comes back.
        var show = SelectedHostServer is { } server && _hostProcess is { } process &&
                   string.Equals(process.Server.Id, server.Id, StringComparison.OrdinalIgnoreCase);

        var wasEmpty = HostConsole.Count == 0;

        while (_hostPendingLines.TryDequeue(out var line))
        {
            if (show)
            {
                HostConsole.Add(line);
            }
        }

        while (HostConsole.Count > MaxHostConsoleLines)
        {
            HostConsole.RemoveAt(0);
        }

        if (wasEmpty != (HostConsole.Count == 0))
        {
            OnPropertyChanged(nameof(HasHostConsole));
        }
    }

    [RelayCommand]
    private void SendHostCommand()
    {
        var text = (HostCommand ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return;
        }

        if (SelectedHostProcess is not { } process || !process.SendCommand(text))
        {
            Status = Localize("Host_CommandNoServer", "The server is not running - there is nobody to carry the command out");
            return;
        }

        _hostPendingLines.Enqueue("> " + text.TrimStart('/'));
        HostCommand = string.Empty;
    }

    // ===================== Whitelist =====================

    public ObservableCollection<string> HostWhitelist { get; } = new();

    [ObservableProperty]
    private string _hostWhitelistEntry = string.Empty;

    [RelayCommand]
    private void AddHostWhitelist()
    {
        if (SelectedHostServer is not { } server)
        {
            return;
        }

        var name = (HostWhitelistEntry ?? string.Empty).Trim();

        if (!OfflineAuth.IsValidUsername(name))
        {
            Status = Localize("Status_InvalidNickname", "Nickname: 3-16 characters, letters, digits and underscore");
            return;
        }

        try
        {
            if (!ServerAccessLists.AddToWhitelist(_hosting.Store.ServerDirectory(server), name))
            {
                Status = Localize("Host_WhitelistExists", "{0} is already on the list", name);
                return;
            }

            HostWhitelist.Add(name);
            HostWhitelistEntry = string.Empty;
            ReloadHostWhitelist(server);
            Status = Localize("Host_WhitelistAdded", "{0} can now join the server", name);
        }
        catch (Exception ex)
        {
            Status = Localize("Host_SaveFailed", "Could not save the server's settings: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void RemoveHostWhitelist(string? name)
    {
        if (SelectedHostServer is not { } server || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            ServerAccessLists.RemoveFromWhitelist(_hosting.Store.ServerDirectory(server), name);
            HostWhitelist.Remove(name);
            ReloadHostWhitelist(server);
            Status = Localize("Host_WhitelistRemoved", "{0} can no longer join the server", name);
        }
        catch (Exception ex)
        {
            Status = Localize("Host_SaveFailed", "Could not save the server's settings: {0}", ex.Message);
        }
    }

    /// <summary>A running server reads the list only when told to.</summary>
    private void ReloadHostWhitelist(HostedServer server)
        => _hosting.Runner.Find(server.Id)?.SendCommand("whitelist reload");

    // ===================== Memory, port, folder, removal =====================

    [ObservableProperty]
    private double _hostMemoryMb = HostedServer.DefaultMemoryMb;

    [ObservableProperty]
    private decimal? _hostPortValue = HostedServer.DefaultPort;

    partial void OnHostMemoryMbChanged(double value)
    {
        if (_applyingHostServer || SelectedHostServer is not { } server || !CanEditHost)
        {
            return;
        }

        server.MemoryMb = (int)value;
        SaveHostServer(server);
        OnPropertyChanged(nameof(HostMemoryLabel));
    }

    partial void OnHostPortValueChanged(decimal? value)
    {
        if (_applyingHostServer || SelectedHostServer is not { } server || !CanEditHost ||
            value is not { } port || port < 1024 || port > 65535 || (int)port == server.Port)
        {
            return;
        }

        try
        {
            _hosting.Store.SetPort(server, (int)port);
            OnPropertyChanged(nameof(HostPortLabel));
            RefreshHostWays();
        }
        catch (Exception ex)
        {
            Status = Localize("Host_SaveFailed", "Could not save the server's settings: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenHostFolder()
    {
        if (SelectedHostServer is not { } server)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _hosting.Store.ServerDirectory(server),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }

    /// <summary>Takes the server off the list. Its folder, world included, is moved aside, never deleted.</summary>
    [RelayCommand]
    private void RemoveHostServer()
    {
        if (SelectedHostServer is not { } server || !CanEditHost || _hosting.Runner.IsRunning(server.Id))
        {
            return;
        }

        try
        {
            var kept = _hosting.Store.Remove(server.Id);

            HostServers.Remove(server);
            SelectedHostServer = HostServers.FirstOrDefault();
            RaiseHostListChanged();
            Status = Localize("Host_Removed", "The server “{0}” is off the list. Its folder with the world is intact: {1}", server.Name, kept);
        }
        catch (Exception ex)
        {
            Status = Localize("Host_RemoveFailed", "Could not take the server off the list: {0}", ex.Message);
        }
    }

    // ===================== Creating a server =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHostEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowHostServer))]
    private bool _isHostCreateOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateHostServer))]
    private string _hostCreateName = string.Empty;

    /// <summary>The builds to pick from: a list of its own, so a filter typed on the builds page does not empty it.</summary>
    public ObservableCollection<Instance> HostCreateBuilds { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostCreateVersionLabel))]
    [NotifyPropertyChangedFor(nameof(HostCreateLoaderLabel))]
    [NotifyPropertyChangedFor(nameof(HasHostCreateBuild))]
    private Instance? _hostCreateInstance;

    public bool HasHostCreateBuild => HostCreateInstance is not null;

    public string HostCreateVersionLabel => HostCreateInstance?.VersionId ?? "—";

    public string HostCreateLoaderLabel => HostCreateInstance?.Loader.ToString() ?? string.Empty;

    public ObservableCollection<HostWorldChoice> HostCreateWorlds { get; } = new();

    [ObservableProperty]
    private HostWorldChoice? _hostCreateWorld;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostCreateMemoryLabel))]
    private double _hostCreateMemoryMb = HostedServer.DefaultMemoryMb;

    public string HostCreateMemoryLabel => FormatHostMemory(HostCreateMemoryMb);

    /// <summary>What will be downloaded and copied, shown before the button can be pressed.</summary>
    public ObservableCollection<HostPlanLine> HostPlanLines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateHostServer))]
    private bool _isHostPlanBusy;

    /// <summary>Why a server cannot be made from this build, in plain words; empty when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostCreateProblem))]
    private string _hostCreateProblem = string.Empty;

    public bool HasHostCreateProblem => HostCreateProblem.Length > 0;

    /// <summary>The checkbox under the list. Never ticked by the launcher.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateHostServer))]
    private bool _hostEulaAccepted;

    public bool CanCreateHostServer
        => _hostCreatePlan is { CanInstall: true } && HostEulaAccepted && !IsHostBusy && !IsHostPlanBusy &&
           !string.IsNullOrWhiteSpace(HostCreateName);

    private CancellationTokenSource? _hostPlanCts;
    private ServerInstallPlan? _hostCreatePlan;
    private ModCopyResult? _hostCreateMods;

    /// <summary>The Java runtime the list offers to download; null when one is on the machine or its size could not be learnt.</summary>
    private ServerDownload? _hostCreateJava;

    private bool _hostCreateJavaMissing;

    /// <summary>A server begun by an attempt that did not finish; the next attempt carries on with it.</summary>
    private HostedServer? _hostDraft;

    private string? _hostDraftWorld;

    /// <summary>The name the card filled in by itself, to tell it from one the player typed.</summary>
    private string _hostCreateAutoName = string.Empty;

    [RelayCommand]
    private void OpenHostCreate()
    {
        if (IsHostBusy)
        {
            return;
        }

        HostCreateBuilds.Clear();

        foreach (var instance in _allInstances)
        {
            HostCreateBuilds.Add(instance);
        }

        HostEulaAccepted = false;
        HostCreateMemoryMb = Math.Min(HostedServer.DefaultMemoryMb, MemorySliderMax);
        HostCreateProblem = string.Empty;
        HostCreateName = _hostCreateAutoName = string.Empty;
        IsHostCreateOpen = true;

        var preferred = SelectedInstance is not null && HostCreateBuilds.Contains(SelectedInstance)
            ? SelectedInstance
            : HostCreateBuilds.FirstOrDefault();

        if (ReferenceEquals(preferred, HostCreateInstance))
        {
            // The same build as last time: the handler below would not run by itself.
            OnHostCreateInstanceChanged(preferred);
        }
        else
        {
            HostCreateInstance = preferred;
        }
    }

    [RelayCommand]
    private void CloseHostCreate()
    {
        if (IsHostBusy)
        {
            return;
        }

        _hostPlanCts?.Cancel();
        DiscardHostDraft();
        IsHostCreateOpen = false;
    }

    /// <summary>A server that was begun and not finished goes to servers/.removed: off the list, nothing deleted.</summary>
    private void DiscardHostDraft()
    {
        var draft = _hostDraft;
        _hostDraft = null;
        _hostDraftWorld = null;

        if (draft is null)
        {
            return;
        }

        try
        {
            _hosting.Store.Remove(draft.Id);
        }
        catch (Exception ex)
        {
            AppendConsole($"[host] draft: {ex.Message}");
        }
    }

    partial void OnHostCreateInstanceChanged(Instance? value)
    {
        if (!IsHostCreateOpen)
        {
            return;
        }

        // The server takes the build's name until the player gives it one of their own.
        if (string.IsNullOrWhiteSpace(HostCreateName) || HostCreateName == _hostCreateAutoName)
        {
            HostCreateName = _hostCreateAutoName = value?.Name ?? string.Empty;
        }

        HostCreateWorlds.Clear();
        HostCreateWorlds.Add(new HostWorldChoice(Localize("Host_WorldNew", "A new world"), null));
        HostCreateWorld = HostCreateWorlds[0];

        _ = RefreshHostCreatePlanAsync();
    }

    partial void OnHostCreateWorldChanged(HostWorldChoice? value) => BuildHostPlanLines();

    private async Task RefreshHostCreatePlanAsync()
    {
        _hostPlanCts?.Cancel();
        var cts = _hostPlanCts = new CancellationTokenSource();
        var token = cts.Token;

        _hostCreatePlan = null;
        _hostCreateMods = null;
        _hostCreateJava = null;
        _hostCreateJavaMissing = false;
        HostPlanLines.Clear();
        HostCreateProblem = string.Empty;
        OnPropertyChanged(nameof(CanCreateHostServer));

        var instance = HostCreateInstance;

        if (instance is null)
        {
            HostCreateProblem = Localize("Host_ProblemNoBuilds", "There is no build yet. Make one on the Builds tab - the server takes its game version from it.");
            return;
        }

        if (string.IsNullOrWhiteSpace(instance.VersionId))
        {
            HostCreateProblem = Localize("Host_ProblemNoVersion", "This build names no game version. Pick one in the build's settings, or take another build.");
            return;
        }

        if (!ServerInstaller.IsSupported(instance.Loader))
        {
            HostCreateProblem = Localize("Host_ProblemLoader", "The launcher cannot set up a {0} server yet. Builds without a loader and Fabric builds are supported.", instance.Loader);
            return;
        }

        try
        {
            IsHostPlanBusy = true;

            var gameDirectory = _instances.GameDirectory(instance);

            // Read-only looks at the build: its worlds and which mods a server can take.
            var worlds = await Task.Run(() => ServerContent.ListWorlds(gameDirectory), token);
            var plan = await _hosting.Installer.PlanAsync(instance.VersionId!, instance.Loader, instance.LoaderVersion, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            foreach (var world in worlds)
            {
                HostCreateWorlds.Add(new HostWorldChoice(
                    Localize("Host_WorldCopy", "A copy of “{0}” · {1}", world.FolderName, FormatSize(world.SizeBytes)),
                    world.FolderName,
                    world.SizeBytes));
            }

            if (!plan.CanInstall)
            {
                HostCreateProblem = DescribeHostPlanFailure(plan);
                return;
            }

            var java = await Task.Run(() => _hosting.Java.Find(plan.JavaMajor), token);
            var javaDownload = java is null ? await _hosting.Java.DescribeDownloadAsync(plan.JavaMajor, token) : null;
            var mods = instance.Loader == LoaderKind.Vanilla
                ? null
                : await Task.Run(() => ServerContent.PlanMods(gameDirectory), token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            _hostCreatePlan = plan;
            _hostCreateMods = mods;
            _hostCreateJava = javaDownload;
            _hostCreateJavaMissing = java is null;
            BuildHostPlanLines();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HostCreateProblem = Localize("Host_ProblemNetwork", "Could not find out what has to be downloaded: {0}. Check the connection and try again.", ex.Message);
            AppendConsole($"[host] plan: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_hostPlanCts, cts))
            {
                IsHostPlanBusy = false;
                OnPropertyChanged(nameof(CanCreateHostServer));
            }
        }
    }

    /// <summary>The list under "What will happen": downloads first, then what is copied and what is left alone.</summary>
    private void BuildHostPlanLines()
    {
        HostPlanLines.Clear();

        if (_hostCreatePlan is not { } plan || HostCreateInstance is not { } instance)
        {
            return;
        }

        long total = 0;
        var exact = true;

        foreach (var download in plan.Downloads)
        {
            HostPlanLines.Add(HostDownloadLine(download, plan.GameVersion, plan.Loader, plan.LoaderVersion, plan.JavaMajor));
            total += download.SizeBytes;
            exact &= download.SizeIsExact;
        }

        if (_hostCreateJava is { } javaDownload)
        {
            HostPlanLines.Add(HostDownloadLine(javaDownload, plan.GameVersion, plan.Loader, plan.LoaderVersion, plan.JavaMajor));
            total += javaDownload.SizeBytes;
            exact &= javaDownload.SizeIsExact;
        }

        HostPlanLines.Add(new HostPlanLine(
            Localize("Host_PlanTotal", "To download in all"),
            exact ? FormatSize(total) : Localize("Host_PlanAbout", "about {0}", FormatSize(total)),
            IsDownload: true));

        if (_hostCreateJavaMissing && _hostCreateJava is null)
        {
            HostPlanLines.Add(new HostPlanLine(
                Localize("Host_PlanJavaUnknown", "This computer has no Java {0}", plan.JavaMajor),
                Localize("Host_PlanJavaUnknownDetail", "How big the download is could not be learnt: adoptium.net does not answer. The launcher will ask about it again before the server starts.")));
        }
        else if (!_hostCreateJavaMissing)
        {
            HostPlanLines.Add(new HostPlanLine(
                Localize("Host_PlanJavaFound", "Java {0} is already on this computer", plan.JavaMajor),
                Localize("Host_PlanJavaFoundDetail", "It does not have to be downloaded.")));
        }

        if (_hostCreateMods is { } mods)
        {
            if (mods.Copied.Count == 0)
            {
                HostPlanLines.Add(new HostPlanLine(
                    Localize("Host_PlanModsNone", "The build has no mods for a server"),
                    Localize("Host_PlanModsNoneDetail", "The server will run without mods.")));
            }
            else
            {
                HostPlanLines.Add(new HostPlanLine(
                    Localize("Host_PlanMods", "Mods copied from the build: {0}", mods.Copied.Count),
                    JoinHostNames(mods.Copied.Select(m => m.Name ?? m.FileName))));
            }

            if (mods.Skipped.Count > 0)
            {
                var parts = new List<string>();
                var clientOnly = mods.Skipped.Where(m => m.Reason == ModSkipReason.ClientOnly).Select(m => m.Name ?? m.FileName).ToList();
                var disabled = mods.Skipped.Where(m => m.Reason == ModSkipReason.Disabled).Select(m => m.Name ?? m.FileName).ToList();

                if (clientOnly.Count > 0)
                {
                    parts.Add(Localize("Host_PlanSkippedClient", "Only for the client: {0}.", JoinHostNames(clientOnly)));
                }

                if (disabled.Count > 0)
                {
                    parts.Add(Localize("Host_PlanSkippedDisabled", "Switched off in the build: {0}.", JoinHostNames(disabled)));
                }

                HostPlanLines.Add(new HostPlanLine(
                    Localize("Host_PlanModsSkipped", "Mods left out of the server: {0}", mods.Skipped.Count),
                    string.Join(" ", parts)));
            }
        }

        HostPlanLines.Add(HostCreateWorld?.FolderName is { } folder
            ? new HostPlanLine(
                Localize("Host_PlanWorldCopy", "A copy of the world “{0}”", folder),
                Localize("Host_PlanWorldCopyDetail", "{0}. The original stays in the build, untouched.", FormatSize(HostCreateWorld.SizeBytes)))
            : new HostPlanLine(
                Localize("Host_PlanWorldNew", "A new world"),
                Localize("Host_PlanWorldNewDetail", "Made when the server first starts.")));

        HostPlanLines.Add(new HostPlanLine(
            Localize("Host_PlanUntouched", "The build “{0}” is not changed", instance.Name),
            Localize("Host_PlanUntouchedDetail", "Everything is copied into the server; nothing in the build is moved or deleted.")));

        HostPlanLines.Add(new HostPlanLine(
            Localize("Host_PlanAccess", "Only nicknames on the list can join"),
            Localize("Host_PlanAccessDetail", "You ({0}) go on the list and become the operator. Friends are added afterwards.", Username)));

        HostPlanLines.Add(new HostPlanLine(
            Localize("Host_PlanFirewall", "Windows will ask about Java"),
            Localize("Host_PlanFirewallDetail", "On the first start the firewall asks whether Java may accept connections. That is the server itself - allow it.")));
    }

    /// <summary>A handful of names and how many more: a build of a hundred mods must not push the button off the screen.</summary>
    private static string JoinHostNames(IEnumerable<string> names)
    {
        const int shown = 8;

        var list = names.ToList();
        var text = string.Join(", ", list.Take(shown));

        return list.Count > shown
            ? Localize("Host_PlanAndMore", "{0} and {1} more", text, list.Count - shown)
            : text;
    }

    [RelayCommand]
    private async Task CreateHostServerAsync()
    {
        if (!CanCreateHostServer || HostCreateInstance is not { } instance || _hostCreatePlan is not { } plan)
        {
            return;
        }

        var name = HostCreateName.Trim();
        var world = HostCreateWorld?.FolderName;
        var gameDirectory = _instances.GameDirectory(instance);

        try
        {
            IsHostBusy = true;
            IsHostProgressUnknown = true;
            HostCreateProblem = string.Empty;

            // A retry carries on with the server already begun - unless the build or the
            // world was changed in between, in which case that one is set aside.
            if (_hostDraft is { } begun &&
                (begun.SourceInstanceId != instance.Id || begun.GameVersion != plan.GameVersion || begun.Loader != plan.Loader ||
                 (_hostDraftWorld is not null && _hostDraftWorld != world)))
            {
                DiscardHostDraft();
            }

            var server = _hostDraft ??= _hosting.Store.CreateFrom(instance, name);

            server.Name = name;
            server.MemoryMb = (int)HostCreateMemoryMb;
            _hosting.Store.Save(server);

            var serverDirectory = _hosting.Store.ServerDirectory(server);

            // The world first: if it is open in the game right now, that is found out
            // before anything has been downloaded for nothing.
            if (world is not null && _hostDraftWorld != world)
            {
                var copying = new Progress<long>(bytes => HostBusyText = Localize("Host_BusyWorld", "Copying the world: {0}", FormatSize(bytes)));
                // A world already in the draft is not taken for this one: it is whatever an
                // earlier attempt left - a copy that was cut short, or another world. It is
                // set aside and the copy made afresh.
                var copied = await Task.Run(() => ServerContent.CopyWorld(gameDirectory, world, serverDirectory, replaceExisting: true, copying));

                switch (copied.Status)
                {
                    case WorldCopyStatus.Copied:
                        _hostDraftWorld = world;
                        break;

                    case WorldCopyStatus.SourceInUse:
                        HostCreateProblem = Localize("Host_ProblemWorldInUse", "The world “{0}” is open in the game right now. Close the game and press “Create” again.", world);
                        return;

                    default:
                        HostCreateProblem = Localize("Host_ProblemWorldMissing", "The world “{0}” was not found in the build.", world);
                        return;
                }
            }

            var installed = await _hosting.Installer.InstallAsync(server, plan, HostInstallProgress());

            if (!installed.Succeeded)
            {
                HostCreateProblem = Localize(
                    "Host_ProblemDownload",
                    "The server could not be downloaded: {0}. Check the connection and press “Create” again - what is already downloaded is kept.",
                    installed.Failure?.Detail ?? installed.Outcome.ToString());
                return;
            }

            if (_hostCreateJava is not null)
            {
                // Agreed to together with the rest of the list, where it stood with its size.
                IsHostProgressUnknown = true;
                HostBusyText = Localize("Host_BusyJava", "Downloading Java {0}…", plan.JavaMajor);
                await _hosting.Java.EnsureAsync(plan.JavaMajor);
            }

            _hosting.Store.WriteStartingFiles(server, Username);

            if (instance.Loader != LoaderKind.Vanilla)
            {
                IsHostProgressUnknown = true;
                HostBusyText = Localize("Host_BusyMods", "Copying the mods…");
                await Task.Run(() => ServerContent.CopyMods(gameDirectory, serverDirectory));
            }

            // Only what the player ticked: an unticked box leaves the server unable to start.
            _hosting.Store.AcceptEula(server, HostEulaAccepted);

            _hostDraft = null;
            _hostDraftWorld = null;

            HostServers.Add(server);
            IsHostCreateOpen = false;
            SelectedHostServer = server;
            RaiseHostListChanged();
            Status = Localize("Host_Created", "The server “{0}” is ready. Press “Start” when you are.", server.Name);
        }
        catch (Exception ex)
        {
            HostCreateProblem = Localize("Host_ProblemCreate", "The server could not be made: {0}", ex.Message);
            AppendConsole($"[host] create: {ex}");
        }
        finally
        {
            IsHostBusy = false;
            IsHostProgressUnknown = false;
            HostBusyText = string.Empty;
            HostProgress = 0;
        }
    }

    // ===================== How friends connect =====================

    public ObservableCollection<HostWayItem> HostWays { get; } = new();

    [ObservableProperty]
    private bool _isHostInviteBusy;

    /// <summary>Opens the ways the player switched on, once the server is ready to answer.</summary>
    private void StartHostWays(HostedServer server)
    {
        var session = new FriendsHostSession(server.Port, RelayLocation.Resolve(_loadedCatalog), server.HostKey);

        if (!string.Equals(server.HostKey, session.HostKey, StringComparison.Ordinal))
        {
            // Kept with the server, so invites sent today still open the same room tomorrow.
            server.HostKey = session.HostKey;
            SaveHostServer(server);
        }

        session.Changed += _ => Dispatcher.UIThread.Post(RefreshHostWays);
        session.RememberInvite(server.LastInvite);
        _hostSession = session;
        _hostInviteWentLong = false;

        if (server.FriendsRelay)
        {
            session.StartRelay();
        }

        if (server.FriendsDirect)
        {
            _ = OpenHostDirectAsync(session);
        }

        RefreshHostWays();
    }

    private async Task StopHostWaysAsync()
    {
        var session = Interlocked.Exchange(ref _hostSession, null);
        _hostDirectCts?.Cancel();

        try
        {
            if (session is not null)
            {
                await session.StopAsync();
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[host] closing the ways: {ex.Message}");
        }

        RefreshHostWays();
    }

    private async Task OpenHostDirectAsync(FriendsHostSession session)
    {
        _hostDirectCts?.Cancel();
        var cts = _hostDirectCts = new CancellationTokenSource();

        try
        {
            await Task.Run(() => session.OpenDirectAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendConsole($"[host] direct: {ex}");
        }

        RefreshHostWays();
    }

    // Concurrent on purpose: asking the router takes seconds, and the other two switches
    // must not go dead while one of them is at work.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleHostWayAsync(HostWayItem? way)
    {
        if (way is null || SelectedHostServer is not { } server || !way.CanSwitch)
        {
            return;
        }

        var on = !way.IsOn;

        switch (way.Kind)
        {
            case HostWayKind.Relay:
                server.FriendsRelay = on;
                break;
            default:
                server.FriendsDirect = on;
                break;
        }

        SaveHostServer(server);
        way.IsOn = on;

        var session = SelectedHostProcess is not null ? _hostSession : null;

        try
        {
            switch (way.Kind)
            {
                case HostWayKind.Relay when session is not null:
                    if (on)
                    {
                        session.StartRelay();
                    }
                    else
                    {
                        await session.StopRelayAsync();
                    }

                    break;

                case HostWayKind.Direct when session is not null:
                    if (on)
                    {
                        await OpenHostDirectAsync(session);
                    }
                    else
                    {
                        _hostDirectCts?.Cancel();
                        await session.CloseDirectAsync();
                    }

                    break;

            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[host] {way.Kind}: {ex}");
        }

        RefreshHostWays();
    }

    /// <summary>Puts each way's switch, explanation and status into words. Called whenever any of what they depend on moves.</summary>
    private void RefreshHostWays()
    {
        if (HostWays.Count < 2)
        {
            return;
        }

        var server = SelectedHostServer;
        var live = SelectedHostProcess is { State: ServerState.Running } ? _hostSession : null;
        var status = live?.Status;
        var port = server?.Port ?? HostedServer.DefaultPort;

        var off = Localize("Host_WayOff", "Off");
        var pending = Localize("Host_WayPending", "Comes on when the server is running");

        // ---- Through the relay ----
        var relayWay = HostWays[0];
        var relay = RelayLocation.Resolve(_loadedCatalog);

        relayWay.Title = Localize("Host_WayRelay", "Friends only");
        relayWay.Description = Localize("Host_WayRelayText", "Friends with STlauncher join by the invite. Nothing to set up: the connection goes through a relay server, no ports to open.");
        relayWay.Address = string.Empty;

        if (relay is null)
        {
            relayWay.CanSwitch = false;
            relayWay.IsOn = false;
            relayWay.State = FriendsWayState.Off;
            relayWay.StatusText = Localize("Host_RelayNotConfigured", "The relay server is not set up yet, so this way is not available. The other two work without it.");
        }
        else
        {
            relayWay.CanSwitch = server is not null;
            relayWay.IsOn = server?.FriendsRelay == true;

            if (!relayWay.IsOn)
            {
                relayWay.State = FriendsWayState.Off;
                relayWay.StatusText = off;
            }
            else if (status is null)
            {
                relayWay.State = FriendsWayState.Off;
                relayWay.StatusText = pending;
            }
            else
            {
                relayWay.State = status.Relay == FriendsWayState.Off ? FriendsWayState.Working : status.Relay;
                relayWay.StatusText = status.Relay switch
                {
                    FriendsWayState.Ready => Localize("Host_RelayReady", "Ready: friends join by the invite"),
                    _ => status.RelayFailure switch
                    {
                        RelayFailure.RelayUnreachable => Localize("Host_RelayUnreachable", "The relay {0} does not answer. The launcher keeps trying.", relay),
                        RelayFailure.RelayBusy => Localize("Host_RelayBusy", "The relay is at its limit right now. The launcher keeps trying."),
                        RelayFailure.Rejected => Localize("Host_RelayRejected", "The relay refused the connection: the launcher may need an update."),
                        _ => Localize("Host_RelayWorking", "Connecting to the relay {0}…", relay)
                    }
                };
            }
        }

        // ---- Straight to this machine ----
        var directWay = HostWays[1];

        directWay.Title = Localize("Host_WayDirect", "Directly");
        directWay.Description = Localize("Host_WayDirectText", "Friends connect straight to your computer. The launcher asks the router to open port {0} and checks from outside that the server can be reached. The port closes together with the server.", port);
        directWay.Warning = Localize("Host_WayDirectWarning", "Open ports are required. This works only when your provider gives you a public IP address and the router has UPnP on, or port {0} is forwarded by hand. Otherwise friends will not get through: use the invite way.", port);
        directWay.CanSwitch = server is not null;
        directWay.IsOn = server?.FriendsDirect == true;
        directWay.Address = string.Empty;

        if (!directWay.IsOn)
        {
            directWay.State = FriendsWayState.Off;
            directWay.StatusText = off;
        }
        else if (status is null)
        {
            directWay.State = FriendsWayState.Off;
            directWay.StatusText = pending;
        }
        else
        {
            directWay.State = status.Direct == FriendsWayState.Off ? FriendsWayState.Working : status.Direct;

            switch (status.Direct)
            {
                case FriendsWayState.Ready:
                    directWay.Address = status.DirectAddress ?? string.Empty;
                    directWay.StatusText = status.DirectVerified
                        ? Localize("Host_DirectReady", "Ready, checked from outside")
                        : Localize("Host_DirectReadyUnverified", "The port is open. There is nothing to check it from outside with: no relay is set up");
                    break;

                case FriendsWayState.Failed:
                    directWay.StatusText = status.DirectFailure switch
                    {
                        UpnpFailure.NoGateway => Localize("Host_DirectNoGateway", "The router did not answer: UPnP is switched off in it, or it has none. Switch UPnP on in the router's settings, or pick another way."),
                        UpnpFailure.Refused => Localize("Host_DirectRefused", "The router refused to open the port."),
                        UpnpFailure.BehindAnotherNat => Localize("Host_DirectBehindNat", "Your provider keeps the router behind a shared address, so a direct connection cannot work: use the invite way."),
                        UpnpFailure.NotReachable => Localize("Host_DirectNotReachable", "The router opened the port, yet the server cannot be reached from outside: the provider or the Windows firewall is in the way."),
                        _ => Localize("Host_DirectError", "The router stopped answering. Switch the way off and on again.")
                    };
                    break;

                default:
                    directWay.StatusText = Localize("Host_DirectWorking", "Talking to the router and checking from outside…");
                    break;
            }
        }

        RefreshHostInviteCode();
    }

    private static string HostOfUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    [RelayCommand]
    private async Task CopyHostWayAddressAsync(HostWayItem? way)
    {
        if (way is null || !way.HasAddress)
        {
            return;
        }

        try
        {
            await CopyToClipboardAsync(way.Address);
            Status = Localize("Server_AddressCopied", "Address copied: {0}", way.Address);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Ui", "Something went wrong: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Puts the invite on the clipboard: the server, the ways that are up right now and,
    /// when the server was made from a build, that build's code.
    /// </summary>
    [RelayCommand]
    private async Task CopyHostInviteAsync()
    {
        if (SelectedHostServer is not { } server || IsHostInviteBusy)
        {
            return;
        }

        if (SelectedHostProcess is not { State: ServerState.Running } || _hostSession is not { } session)
        {
            Status = Localize("Host_InviteNeedsServer", "Start the server first: the invite is put together while it runs.");
            return;
        }

        if (!session.Status.Endpoints.HasAny)
        {
            Status = Localize("Host_InviteNeedsWay", "Switch on at least one way of connecting and wait until it is ready.");
            return;
        }

        try
        {
            IsHostInviteBusy = true;
            Status = Localize("Host_InviteBusy", "Putting the invite together…");

            string? buildCode = null;
            var missing = new List<string>();
            var buildFailed = false;

            if (HostSourceInstance is { VersionId: { Length: > 0 } version } instance)
            {
                try
                {
                    (buildCode, _, missing) = await MakeBuildCodeAsync(instance, version);
                }
                catch (Exception ex)
                {
                    // Modrinth is what says which files can be fetched by address; without
                    // it the invite still leads to the server, only without the build.
                    buildFailed = true;
                    AppendConsole($"[host] the build did not go into the invite: {ex.Message}");
                }
            }

            var invite = session.BuildInvite(server.Name, server.GameVersion, server.Loader, server.LoaderVersion, Username, buildCode);
            var code = ServerInviteCode.Encode(invite);

            // The relay keeps the long invite; friends get ten characters. Without the
            // relay way there is nobody to keep it, and the long one goes out as before.
            server.LastInvite = code;
            SaveHostServer(server);

            var shortCode = await session.PublishInviteAsync(code, TimeSpan.FromSeconds(5));

            await CopyToClipboardAsync(shortCode ?? code);
            RememberHostInviteCopy(shortCode, code);
            AppendConsole($"[host] invite for '{server.Name}': {code.Length} characters, build {(buildCode is null ? "not included" : "included")}, {(shortCode is null ? "sent in full" : "left with the relay as " + shortCode)}");

            Status = buildFailed
                ? Localize("Host_InviteCopiedNoBuild", "The invite is copied without the build: Modrinth did not answer. The friend will need a build of their own for {0}.", server.GameVersion)
                : missing.Count > 0
                    ? Localize("Host_InviteCopiedMissing", "The invite is copied. These mods are not on Modrinth and have to be handed over separately: {0}", string.Join(", ", missing))
                    : shortCode is not null
                        ? Localize("Host_InviteCopiedShort", "The invite is copied: {0}. Send it to a friend: they paste the code under “Friends”, on the “Join a friend” tab. The code works while your server is running. Remember to put their nickname on the list.", shortCode)
                        : Localize("Host_InviteCopied", "The invite is copied. Send it to a friend: they paste the line under “Friends”, on the “Join a friend” tab. Remember to put their nickname on the list.");
        }
        catch (Exception ex)
        {
            Status = Localize("Host_InviteFailed", "The invite could not be put together: {0}", ex.Message);
            AppendConsole($"[host] invite: {ex}");
        }
        finally
        {
            IsHostInviteBusy = false;
        }
    }

    // ===================== Closing the launcher =====================
    // A server lives inside the launcher: closing the window would take it down, and the
    // players with it. So the window asks first, and what was opened is closed in order -
    // the world saved, the port taken back off the router, the tunnels shut.

    private bool _hostCloseConfirmed;
    private bool _closeAskedByLauncher;

    /// <summary>The question on screen was put by "restart to update"; its "yes" goes on with the update.</summary>
    private bool _hostCloseForUpdate;

    [ObservableProperty]
    private bool _isHostCloseOpen;

    [ObservableProperty]
    private bool _isHostClosing;

    [ObservableProperty]
    private string _hostCloseTitle = string.Empty;

    [ObservableProperty]
    private string _hostCloseText = string.Empty;

    [ObservableProperty]
    private string _hostCloseAction = string.Empty;

    /// <summary>True while a server of the player's is up, or anything opened for it still is.</summary>
    private bool HasRunningHost => _hosting.Runner.Running.Count > 0 || _hostSession is not null;

    /// <summary>
    /// Asked by the window when something wants to close it. True means "not yet": the
    /// question is on the screen and the window stays.
    /// </summary>
    /// <param name="forUpdate">
    /// The update's restart is asking, not the window: "yes" then stops everything and
    /// goes on with the update instead of closing the window.
    /// </param>
    public bool HoldCloseForHosting(bool forUpdate = false)
    {
        // The launcher closing itself after the game: the game is over, so a tunnel to a
        // friend's server has nobody left to carry.
        var byLauncher = _closeAskedByLauncher;
        _closeAskedByLauncher = false;
        _hostCloseForUpdate = false;

        if (_hostCloseConfirmed)
        {
            return false;
        }

        var tunnel = !byLauncher && _friendJoin is { Way: FriendsWay.Relay } && IsGameRunning;

        if (!HasRunningHost && !tunnel)
        {
            return false;
        }

        if (HasRunningHost)
        {
            var name = _hosting.Runner.Running.FirstOrDefault()?.Server.Name ?? SelectedHostServer?.Name ?? string.Empty;

            HostCloseTitle = Localize("Host_CloseTitle", "The server is still running");
            HostCloseText = Localize("Host_CloseText", "The server “{0}” is running. Closing the launcher stops it: the world is saved, the players are disconnected and the addresses opened for friends are closed.", name);
            HostCloseAction = forUpdate
                ? Localize("Host_CloseConfirmUpdate", "Stop and update")
                : Localize("Host_CloseConfirm", "Stop and close");
        }
        else
        {
            HostCloseTitle = Localize("Host_CloseTunnelTitle", "You are on a friend's server");
            HostCloseText = Localize("Host_CloseTextTunnel", "The game is connected to the friend's server through the launcher. Closing the launcher breaks that connection.");
            HostCloseAction = Localize("Host_CloseConfirmTunnel", "Close anyway");
        }

        _hostCloseForUpdate = forUpdate;
        IsHostCloseOpen = true;
        return true;
    }

    [RelayCommand]
    private async Task ConfirmHostCloseAsync()
    {
        if (IsHostClosing)
        {
            return;
        }

        IsHostClosing = true;
        Status = Localize("Host_Stopping", "Stopping the server, the world is being saved…");

        await StopAllHostingAsync();

        _hostCloseConfirmed = true;
        IsHostClosing = false;
        IsHostCloseOpen = false;

        // Asked on the way into an update: the restart is what was being held.
        if (_hostCloseForUpdate)
        {
            _hostCloseForUpdate = false;
            await InstallUpdateAsync();
            return;
        }

        RequestCloseLauncher?.Invoke();
    }

    [RelayCommand]
    private void CancelHostClose()
    {
        if (!IsHostClosing)
        {
            IsHostCloseOpen = false;
            _hostCloseForUpdate = false;
        }
    }

    /// <summary>
    /// Stops the servers, takes the port mapping back, shuts the tunnels. Touches nothing
    /// of the screen, so it can also run while the UI thread is waiting for it.
    /// </summary>
    private async Task StopAllHostingAsync()
    {
        var session = Interlocked.Exchange(ref _hostSession, null);
        var join = Interlocked.Exchange(ref _friendJoin, null);

        try
        {
            await _hosting.Runner.StopAllAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Each step is tried whatever became of the one before it.
        }

        try
        {
            if (session is not null)
            {
                await session.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (join is not null)
            {
                await join.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// For an exit that asks nobody - the tray's "Quit", a restart after the data was
    /// moved, Windows shutting down: the same orderly stop, waited for, within a limit.
    /// </summary>
    public void StopHostingForExit(TimeSpan limit)
    {
        if (!HasRunningHost && _friendJoin is null)
        {
            return;
        }

        try
        {
            Task.Run(StopAllHostingAsync).Wait(limit);
        }
        catch (Exception)
        {
            // The process is leaving either way; the job object ends what is left.
        }
    }
}
