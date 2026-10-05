using System;
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
    private FriendsTab _friendsTab = FriendsTab.MyServer;

    public bool IsFriendsMyServer => FriendsTab == FriendsTab.MyServer;

    public bool IsFriendsJoin => FriendsTab == FriendsTab.Join;

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
