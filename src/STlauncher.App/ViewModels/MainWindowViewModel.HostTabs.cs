using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Friends;
using STlauncher.Core.Hosting;

namespace STlauncher.App.ViewModels;

/// <summary>The two halves of "Playing with friends": the player's own server, or a friend's.</summary>
public enum FriendsTab
{
    MyServer,
    Join
}

/// <summary>The tabs under the card of the player's own server.</summary>
public enum HostTab
{
    Overview,
    Players,
    Mods,
    Settings,
    Console
}

/// <summary>One of the player's own servers as a row of the hub: what it is and whether it runs.</summary>
public sealed class HostServerRow
{
    public HostServerRow(HostedServer server, string subtitle, HostServerState state, string stateText)
    {
        Server = server;
        Subtitle = subtitle;
        State = state;
        StateText = stateText;
        Initial = HostModItem.InitialOf(server.Name);
    }

    public HostedServer Server { get; }

    public string Name => Server.Name;

    public string Initial { get; }

    /// <summary>"Fabric 1.21.11 · 2 GB of memory".</summary>
    public string Subtitle { get; }

    public HostServerState State { get; }

    public string StateText { get; }

    public bool IsRunning => State == HostServerState.Running;

    public bool IsWorking => State is HostServerState.Starting or HostServerState.Stopping;

    public bool IsOff => !IsRunning && !IsWorking;
}

/// <summary>
/// How the "Playing with friends" page is laid out: which half is open, which tab of the
/// server, the three steps of inviting a friend and the box an invite is typed into.
/// What the server and the invites actually do lives in the Host and FriendServers
/// parts; this part only arranges it. The "Mods" tab is HostModsView's own business.
/// </summary>
public partial class MainWindowViewModel
{
    // ===================== My server | Join a friend =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFriendsMyServer))]
    [NotifyPropertyChangedFor(nameof(IsFriendsJoin))]
    [NotifyPropertyChangedFor(nameof(ShowFriendsHub))]
    [NotifyPropertyChangedFor(nameof(ShowFriendsServer))]
    private FriendsTab _friendsTab = FriendsTab.MyServer;

    public bool IsFriendsMyServer => FriendsTab == FriendsTab.MyServer;

    public bool IsFriendsJoin => FriendsTab == FriendsTab.Join;

    // ===================== The hub and the server behind it =====================
    // The page no longer wears a switch: it opens on a hub - call friends over, join a
    // friend, the servers known so far - and a server of the player's own is a view
    // opened from there. The two halves are still the same saved choice: "Join" is the
    // hub, "MyServer" the server (or the hub again while there is no server to show).

    public bool ShowFriendsHub => IsFriendsJoin || ShowHostEmpty;

    public bool ShowFriendsServer => !ShowFriendsHub;

    /// <summary>Nothing under "Recent" yet: no friend's server, none of the player's own.</summary>
    public bool ShowHubEmpty => !HasFriendServers && !HasHostServers;

    /// <summary>"from the build X" beside "Create a server": the build the creation form will open on.</summary>
    public string HostHubBuildLine => SelectedInstance is { } instance
        ? Localize("Host_HubFromBuild", "from the build “{0}”", instance.Name)
        : string.Empty;

    /// <summary>The player's own servers as rows of the hub.</summary>
    public ObservableCollection<HostServerRow> HostServerRows { get; } = new();

    /// <summary>Rebuilds the rows: a handful at most, so nothing is patched in place.</summary>
    private void RefreshHostServerRows()
    {
        var running = _hostProcess is { HasExited: false } process ? process : null;

        HostServerRows.Clear();

        foreach (var server in HostServers.OrderByDescending(s => s.LastStartedAt ?? s.CreatedAt))
        {
            var state = running is not null && string.Equals(running.Server.Id, server.Id, StringComparison.OrdinalIgnoreCase)
                ? MapHostState(running.State)
                : HostServerState.Stopped;

            HostServerRows.Add(new HostServerRow(
                server,
                Localize("Host_HubServerLine", "{0} {1} · {2}", server.Loader, server.GameVersion,
                    Localize("Host_MemoryChip", "{0} of memory", FormatHostMemory(server.MemoryMb))),
                state,
                state switch
                {
                    HostServerState.Starting => Localize("Host_StateStarting", "starting"),
                    HostServerState.Running => Localize("Host_StateRunning", "running"),
                    HostServerState.Stopping => Localize("Host_StateStopping", "stopping"),
                    _ => Localize("Host_HubServerOff", "switched off")
                }));
        }
    }

    /// <summary>"Create a server" on the hub: the form lives in the server's view.</summary>
    [RelayCommand]
    private void CreateHostFromHub()
    {
        FriendsTab = FriendsTab.MyServer;
        OpenHostCreateCommand.Execute(null);
    }

    private void ShowHostServerRow(HostServerRow? row, HostTab tab)
    {
        if (row is null || !HostServers.Contains(row.Server))
        {
            return;
        }

        // A creation form left open would hide the server that was asked for.
        if (IsHostCreateOpen)
        {
            CloseHostCreateCommand.Execute(null);

            if (IsHostCreateOpen)
            {
                return;
            }
        }

        SelectedHostServer = row.Server;
        HostTab = tab;
        FriendsTab = FriendsTab.MyServer;
    }

    [RelayCommand]
    private void OpenHostServerRow(HostServerRow? row) => ShowHostServerRow(row, HostTab.Overview);

    [RelayCommand]
    private void ConfigureHostServerRow(HostServerRow? row) => ShowHostServerRow(row, HostTab.Settings);

    /// <summary>"Start" on a row: the server's view opens, because that is where a start asks its questions.</summary>
    [RelayCommand]
    private void StartHostServerRow(HostServerRow? row)
    {
        ShowHostServerRow(row, HostTab.Overview);

        if (row is not null && ReferenceEquals(SelectedHostServer, row.Server) && ShowFriendsServer && CanStartHost)
        {
            StartHostServerCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void OpenHostServerRowFolder(HostServerRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _hosting.Store.ServerDirectory(row.Server),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }

    /// <summary>True while the saved choice is being put back: that is a load, not a change to save.</summary>
    private bool _restoringFriendsTab;

    // Remembered between sessions: a player who only ever joins friends should not be
    // met by "make a server" every time.
    partial void OnFriendsTabChanged(FriendsTab value)
    {
        if (!_restoringFriendsTab)
        {
            PersistSettings();
        }
    }

    /// <summary>
    /// Puts back the half that was open last time. Called halfway through loading the
    /// settings, when a save would write the not-yet-loaded rest as defaults.
    /// </summary>
    private void RestoreFriendsTab(string? saved)
    {
        _restoringFriendsTab = true;

        try
        {
            FriendsTab = Enum.TryParse<FriendsTab>(saved, ignoreCase: true, out var tab) ? tab : FriendsTab.MyServer;
        }
        finally
        {
            _restoringFriendsTab = false;
        }
    }

    [RelayCommand]
    private void SelectFriendsTab(string? tab)
    {
        if (Enum.TryParse<FriendsTab>(tab, ignoreCase: true, out var parsed))
        {
            FriendsTab = parsed;
        }
    }

    // ===================== The tabs of a server =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostOverview))]
    [NotifyPropertyChangedFor(nameof(IsHostPlayersTab))]
    [NotifyPropertyChangedFor(nameof(IsHostModsTab))]
    [NotifyPropertyChangedFor(nameof(IsHostSettingsTab))]
    [NotifyPropertyChangedFor(nameof(IsHostConsoleTab))]
    private HostTab _hostTab = HostTab.Overview;

    public bool IsHostOverview => HostTab == HostTab.Overview;

    public bool IsHostPlayersTab => HostTab == HostTab.Players;

    public bool IsHostModsTab => HostTab == HostTab.Mods;

    public bool IsHostSettingsTab => HostTab == HostTab.Settings;

    public bool IsHostConsoleTab => HostTab == HostTab.Console;

    [RelayCommand]
    private void SelectHostTab(string? tab)
    {
        if (Enum.TryParse<HostTab>(tab, ignoreCase: true, out var parsed))
        {
            HostTab = parsed;
        }
    }

    /// <summary>Called once from <see cref="AttachHosting"/>: keeps the steps and the name box in step with what they describe.</summary>
    private void AttachHostTabs()
    {
        HostWhitelist.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHostFriendListed));
        HostServers.CollectionChanged += (_, _) => RefreshHostServerRows();

        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SelectedHostServer):
                    ApplyHostName();
                    break;

                case nameof(Username):
                    OnPropertyChanged(nameof(HasHostFriendListed));
                    break;

                // The hub is what is left when there is no server to show.
                case nameof(ShowHostEmpty):
                    OnPropertyChanged(nameof(ShowFriendsHub));
                    OnPropertyChanged(nameof(ShowFriendsServer));
                    break;

                case nameof(HasHostServers):
                case nameof(HasFriendServers):
                    OnPropertyChanged(nameof(ShowHubEmpty));
                    break;

                case nameof(SelectedInstance):
                    OnPropertyChanged(nameof(HostHubBuildLine));
                    break;

                // What a row of the hub says about a server: its state, its name, its memory.
                case nameof(HostState):
                case nameof(HostServerName):
                case nameof(HostMemoryLabel):
                case nameof(FriendsTab):
                case nameof(Language):
                    RefreshHostServerRows();

                    if (e.PropertyName == nameof(Language))
                    {
                        OnPropertyChanged(nameof(HostHubBuildLine));
                    }

                    break;
            }
        };
    }

    // ===================== How to invite a friend: three steps =====================

    /// <summary>Step two is done once somebody other than the player is on the list.</summary>
    public bool HasHostFriendListed
        => HostWhitelist.Any(name => !string.Equals(name, Username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The invite as it was last handed out for the running server: "ST-XXXXX-XXXXX" while
    /// the relay keeps it, or the long line when there was no relay to keep it. Empty
    /// until there is something a friend could really use.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostInviteCode))]
    [NotifyPropertyChangedFor(nameof(IsHostInviteShort))]
    [NotifyPropertyChangedFor(nameof(IsHostInviteLong))]
    private string _hostInviteCode = string.Empty;

    public bool HasHostInviteCode => HostInviteCode.Length > 0;

    public bool IsHostInviteShort => HasHostInviteCode && !_hostInviteWentLong;

    public bool IsHostInviteLong => HasHostInviteCode && _hostInviteWentLong;

    /// <summary>The last copy went out as the long line: the relay did not take the invite.</summary>
    private bool _hostInviteWentLong;

    /// <summary>Called after "Copy": what went to the clipboard stays on the card.</summary>
    private void RememberHostInviteCopy(string? shortCode, string longCode)
    {
        _hostInviteWentLong = shortCode is null;
        HostInviteCode = string.Empty;
        HostInviteCode = shortCode ?? longCode;
    }

    /// <summary>
    /// Shows the short code whenever the relay holds an invite for this server - which it
    /// also does for one handed out on an earlier day, left there again when the room
    /// opens - and takes it off the card when the server is no longer running.
    /// </summary>
    private void RefreshHostInviteCode()
    {
        var live = SelectedHostProcess is { State: ServerState.Running } ? _hostSession : null;

        if (live is null || SelectedHostServer is not { } server)
        {
            _hostInviteWentLong = false;
            HostInviteCode = string.Empty;
            return;
        }

        if (_hostInviteWentLong)
        {
            // The long line was copied by hand a moment ago; it stays until the next copy.
            return;
        }

        var status = live.Status;

        HostInviteCode = status.Relay == FriendsWayState.Ready &&
                         status.RoomKey is { Length: > 0 } room &&
                         !string.IsNullOrWhiteSpace(server.LastInvite)
            ? RelayKeys.FormatInviteCode(RelayKeys.InviteCodeFor(room))
            : string.Empty;
    }

    // ===================== The server's name =====================

    [ObservableProperty]
    private string _hostNameEntry = string.Empty;

    private void ApplyHostName()
    {
        _applyingHostServer = true;

        try
        {
            HostNameEntry = SelectedHostServer?.Name ?? string.Empty;
        }
        finally
        {
            _applyingHostServer = false;
        }
    }

    partial void OnHostNameEntryChanged(string value)
    {
        var name = (value ?? string.Empty).Trim();

        // An emptied box is a name being retyped, not a wish for a nameless server.
        if (_applyingHostServer || SelectedHostServer is not { } server || !CanEditHost ||
            name.Length == 0 || string.Equals(name, server.Name, StringComparison.Ordinal))
        {
            return;
        }

        server.Name = name;
        SaveHostServer(server);
        OnPropertyChanged(nameof(HostServerName));
    }

    // ===================== An invite typed or pasted into the box =====================

    [ObservableProperty]
    private string _friendInviteEntry = string.Empty;

    /// <summary>Why the code in the box led nowhere; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFriendInviteNote))]
    private string _friendInviteNote = string.Empty;

    public bool HasFriendInviteNote => FriendInviteNote.Length > 0;

    /// <summary>The relay is being asked what a short code stands for.</summary>
    [ObservableProperty]
    private bool _isFriendInviteLooking;

    partial void OnFriendInviteEntryChanged(string value) => FriendInviteNote = string.Empty;

    /// <summary>Puts the clipboard's text into the box. Nothing is looked up until "Find the server" is pressed.</summary>
    [RelayCommand]
    private async Task PasteFriendInviteAsync()
    {
        try
        {
            var text = (await ReadClipboardAsync())?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                FriendInviteNote = Localize("Friends_BoxClipboardEmpty", "The clipboard is empty. Copy the code your friend sent and press the button again.");
                return;
            }

            FriendInviteEntry = text;
        }
        catch (Exception ex)
        {
            FriendInviteNote = Localize("Error_Ui", "Something went wrong: {0}", ex.Message);
        }
    }

    /// <summary>Reads the code in the box the way "A friend's build" reads the clipboard; an invite is shown and waits for a yes.</summary>
    [RelayCommand]
    private async Task FindFriendInviteAsync()
    {
        if (IsFriendInviteLooking)
        {
            return;
        }

        var text = (FriendInviteEntry ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            FriendInviteNote = Localize("Friends_BoxEmpty", "Put the code your friend sent into the box first.");
            return;
        }

        if (IsBusy || IsModsBusy)
        {
            FriendInviteNote = Localize("Friends_BoxBusy", "The launcher is busy with a build right now. Wait until it is done and press the button again.");
            return;
        }

        try
        {
            IsFriendInviteLooking = true;
            FriendInviteNote = string.Empty;

            if (await AddFromCodeTextAsync(text, fromClipboard: false))
            {
                FriendInviteEntry = string.Empty;
            }
            else
            {
                // The status line at the bottom says it too; here it is next to the box it is about.
                FriendInviteNote = Status;
            }
        }
        finally
        {
            IsFriendInviteLooking = false;
        }
    }
}
