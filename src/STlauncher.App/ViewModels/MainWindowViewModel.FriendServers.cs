using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Friends;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;

namespace STlauncher.App.ViewModels;

/// <summary>A friend's server as a row under "Friends' servers".</summary>
public partial class FriendServerItem : ObservableObject
{
    public FriendServerItem(FriendServer model)
    {
        Model = model;
    }

    public FriendServer Model { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>"Steve · Minecraft 1.21.1 · Fabric".</summary>
    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>The build it is played with, or that the build is gone.</summary>
    [ObservableProperty]
    private string _buildLine = string.Empty;

    [ObservableProperty]
    private bool _hasBuild;

    /// <summary>How the last "Play" went: which way answered, or why none did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// The guest's side of "a server for friends". An invite pasted from the clipboard is
/// first shown for what it is - the server, the build that would be added, the addresses
/// it would connect to - and only becomes an entry under "Friends' servers" when the
/// player says so. "Play" then finds the way that answers, starts the game straight into
/// the server, and closes the tunnel again when the game exits.
/// </summary>
public partial class MainWindowViewModel
{
    private List<FriendServer> _friendServerModels = new();

    /// <summary>The connection of the game that is running now. For the relay way it holds a local port open.</summary>
    private FriendsJoin? _friendJoin;

    /// <summary>Where the next launch joins; set only for the length of that launch.</summary>
    private string? _friendJoinAddress;

    public ObservableCollection<FriendServerItem> FriendServers { get; } = new();

    public bool HasFriendServers => FriendServers.Count > 0;

    private void LoadFriendServers()
    {
        _friendServerModels = _hosting.Friends.Load();
        RefreshFriendServers();
    }

    /// <summary>Rebuilds the rows: names of builds change, builds get deleted, the language switches.</summary>
    private void RefreshFriendServers()
    {
        if (!_hostLoaded)
        {
            return;
        }

        var notes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var shown in FriendServers)
        {
            notes[shown.Model.Id] = shown.Note;
        }

        FriendServers.Clear();

        foreach (var model in _friendServerModels)
        {
            var instance = FriendServerInstance(model);
            var loader = model.Loader.ToString();

            FriendServers.Add(new FriendServerItem(model)
            {
                Name = string.IsNullOrWhiteSpace(model.Name) ? Localize("Friends_DefaultName", "A friend's server") : model.Name,
                Subtitle = string.IsNullOrWhiteSpace(model.HostNickname)
                    ? Localize("Friends_SubtitleNoHost", "Minecraft {0} · {1}", model.GameVersion, loader)
                    : Localize("Friends_Subtitle", "{0} · Minecraft {1} · {2}", model.HostNickname, model.GameVersion, loader),
                HasBuild = instance is not null,
                BuildLine = instance is not null
                    ? Localize("Friends_Build", "The build “{0}” is on this computer", instance.Name)
                    : Localize("Friends_BuildGone", "The build for this server was deleted. Paste the invite again to bring it back."),
                Note = notes.TryGetValue(model.Id, out var note) ? note : string.Empty
            });
        }

        OnPropertyChanged(nameof(HasFriendServers));
    }

    private Instance? FriendServerInstance(FriendServer server)
        => string.IsNullOrWhiteSpace(server.InstanceId)
            ? null
            : _allInstances.FirstOrDefault(i => string.Equals(i.Id, server.InstanceId, StringComparison.OrdinalIgnoreCase));

    private void SaveFriendServers()
    {
        try
        {
            _hosting.Friends.Save(_friendServerModels);
        }
        catch (Exception ex)
        {
            Status = Localize("Friends_SaveFailed", "Could not save the list of friends' servers: {0}", ex.Message);
        }
    }

    // ===================== An invite, before anything is done =====================

    private ServerInvite? _pendingInvite;
    private BuildCodePayload? _pendingInviteBuild;

    /// <summary>The build of the player's own that the screen said the server would be played with.</summary>
    private Instance? _pendingInviteExisting;

    /// <summary>
    /// Which build to join a server with. Builds that follow the catalog or came whole
    /// from another launcher are left out of the choice: the first belong to their own
    /// server, the second do not say what loader they really carry. The build made for
    /// this server is always in.
    /// </summary>
    private JoinDecision PlanJoin(string? gameVersion, LoaderKind? loader, bool inviteBringsBuild, Instance? dedicated, bool canCreate = true)
        => JoinPlan.Decide(
            gameVersion,
            loader,
            inviteBringsBuild,
            _allInstances
                .Select(i => new JoinBuild(
                    i.Id,
                    string.IsNullOrWhiteSpace(i.CatalogBuildId) && string.IsNullOrWhiteSpace(i.ProfileVersionId) ? i.VersionId : null,
                    i.Loader,
                    ReferenceEquals(i, dedicated)))
                .ToList(),
            SelectedInstance?.Id,
            canCreate);

    private Instance? InstanceById(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : _allInstances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    [ObservableProperty]
    private bool _hasFriendInvite;

    [ObservableProperty]
    private string _friendInviteTitle = string.Empty;

    [ObservableProperty]
    private bool _isFriendInviteBusy;

    public ObservableCollection<HostPlanLine> FriendInviteLines { get; } = new();

    /// <summary>
    /// Puts an invite on the "My server" page: the server it leads to, the build it would
    /// add and the addresses it would connect to. Nothing is created or downloaded here.
    /// </summary>
    private void ShowFriendInvite(ServerInvite invite)
    {
        // Wherever the code was read - the home screen's tile or the box on the page -
        // the question is asked on the tab the friends' servers live on.
        Section = ShellSection.Host;
        FriendsTab = FriendsTab.Join;

        _pendingInvite = invite;
        _pendingInviteBuild = BuildCode.TryDecode(invite.BuildCode, out var build) ? build : null;

        var loader = invite.Loader.ToString();
        var known = FriendServerStore.FindSame(_friendServerModels, invite);
        var knownBuild = known is null ? null : FriendServerInstance(known);

        // The server's own build and the build inside the invite go first, as before;
        // only an invite without a build looks among the builds the player has.
        var plan = PlanJoin(invite.GameVersion, invite.Loader, _pendingInviteBuild is not null, knownBuild);
        _pendingInviteExisting = plan.Action == JoinAction.UseExisting ? InstanceById(plan.BuildId) : null;

        FriendInviteTitle = string.IsNullOrWhiteSpace(invite.HostNickname)
            ? Localize("Friends_InviteTitleNoHost", "An invite to a server")
            : Localize("Friends_InviteTitle", "An invite from {0}", invite.HostNickname);

        FriendInviteLines.Clear();

        FriendInviteLines.Add(new HostPlanLine(
            Localize("Friends_InviteServer", "The server “{0}”", string.IsNullOrWhiteSpace(invite.Name) ? Localize("Friends_DefaultName", "A friend's server") : invite.Name),
            Localize("Friends_SubtitleNoHost", "Minecraft {0} · {1}", invite.GameVersion, loader)));

        if (knownBuild is not null)
        {
            FriendInviteLines.Add(new HostPlanLine(
                Localize("Friends_InviteKnown", "You already have this server"),
                Localize("Friends_InviteKnownDetail", "Only the addresses to connect by are updated. The build “{0}” stays as it is.", knownBuild.Name)));
        }
        else if (_pendingInviteBuild is { } payload)
        {
            var hosts = payload.Files
                .Select(f => HostOfUrl(f.Url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var detail = payload.Files.Count == 0
                ? Localize("Friends_InviteBuildNoFiles", "Minecraft {0} · {1}, no mods. The game's files are downloaded on the first start.", payload.GameVersion, payload.Loader)
                : Localize(
                    "Friends_InviteBuildDetail",
                    "Files: {0}, {1}. Downloaded from {2}.",
                    payload.Files.Count,
                    FormatSize(payload.Files.Sum(f => f.Size)),
                    string.Join(", ", hosts));

            if (payload.Missing.Count > 0)
            {
                detail += " " + Localize("Friends_InviteBuildMissing", "Not in the invite, your friend hands them over: {0}.", JoinHostNames(payload.Missing));
            }

            FriendInviteLines.Add(new HostPlanLine(
                Localize("Friends_InviteBuildAdd", "The build “{0}” is added", payload.Name),
                detail,
                IsDownload: payload.Files.Count > 0));
        }
        else if (_pendingInviteExisting is { } existing)
        {
            FriendInviteLines.Add(new HostPlanLine(
                Localize("Friends_InviteBuildExisting", "Played with your build “{0}”", existing.Name),
                Localize("Friends_InviteBuildExistingDetail", "Minecraft {0} · {1}: it fits this server. Nothing is created or downloaded, and the build is not changed.", existing.VersionId, existing.Loader)));
        }
        else
        {
            FriendInviteLines.Add(new HostPlanLine(
                Localize("Friends_InviteBuildEmpty", "A build without mods is made"),
                Localize("Friends_InviteBuildNoFiles", "Minecraft {0} · {1}, no mods. The game's files are downloaded on the first start.", invite.GameVersion, loader)));
        }

        var ways = new List<string>();

        if (invite.Endpoints.Direct is { } direct)
        {
            ways.Add(Localize("Friends_WayDirect", "directly, {0}", direct));
        }

        if (invite.Endpoints.HasRelay)
        {
            ways.Add(Localize("Friends_WayRelay", "through the relay {0}", invite.Endpoints.Relay));
        }

        if (invite.Endpoints.Public is { } open)
        {
            ways.Add(Localize("Friends_WayPublic", "by the public address {0}", open));
        }

        FriendInviteLines.Add(new HostPlanLine(
            Localize("Friends_InviteWays", "How the game connects"),
            string.Join("; ", ways)));

        FriendInviteLines.Add(new HostPlanLine(
            Localize("Friends_InviteNothingStarts", "Nothing starts now"),
            Localize("Friends_InviteNothingStartsDetail", "The server appears in the list below. The game starts when you press “Play”.")));

        HasFriendInvite = true;
    }

    [RelayCommand]
    private async Task AcceptFriendInviteAsync()
    {
        if (_pendingInvite is not { } invite || IsFriendInviteBusy || IsBusy || IsModsBusy)
        {
            return;
        }

        try
        {
            IsFriendInviteBusy = true;

            var known = FriendServerStore.FindSame(_friendServerModels, invite);
            var instance = known is null ? null : FriendServerInstance(known);

            var hadBuild = instance is not null;

            if (instance is null)
            {
                if (_pendingInviteExisting is { } existing)
                {
                    if (!_allInstances.Contains(existing))
                    {
                        // Deleted while the invite was on screen: what was promised cannot
                        // be done, so the screen says anew what would happen now.
                        ShowFriendInvite(invite);
                        return;
                    }

                    instance = existing;
                }
                else
                {
                    instance = _pendingInviteBuild is { } payload
                        ? await AddBuildFromPayloadAsync(payload, showBuild: false)
                        : CreateCleanBuild(
                            string.IsNullOrWhiteSpace(invite.Name) ? Localize("Friends_DefaultName", "A friend's server") : invite.Name,
                            invite.GameVersion,
                            invite.Loader,
                            invite.LoaderVersion);
                }

                if (instance is null)
                {
                    // The status line already says what went wrong; the invite stays on screen.
                    return;
                }
            }

            FriendServerStore.Remember(_friendServerModels, invite, instance.Id);
            SaveFriendServers();
            RefreshFriendServers();

            HasFriendInvite = false;
            _pendingInvite = null;
            _pendingInviteBuild = null;
            _pendingInviteExisting = null;

            Status = known is null
                ? Localize("Friends_Added", "The server “{0}” is added. Press “Play” once your friend has started it.", invite.Name)
                : hadBuild
                    ? Localize("Friends_Updated", "The addresses of the server “{0}” are updated", invite.Name)
                    : Localize("Friends_BuildBack", "The server “{0}” has a build again: “{1}”. Press “Play”.", invite.Name, instance.Name);
        }
        catch (Exception ex)
        {
            Status = Localize("Friends_AddFailed", "The server could not be added: {0}", ex.Message);
            AppendConsole($"[friends] {ex}");
        }
        finally
        {
            IsFriendInviteBusy = false;
        }
    }

    [RelayCommand]
    private void DeclineFriendInvite()
    {
        if (IsFriendInviteBusy)
        {
            return;
        }

        HasFriendInvite = false;
        _pendingInvite = null;
        _pendingInviteBuild = null;
        _pendingInviteExisting = null;
    }

    /// <summary>
    /// A build of a server's version and loader with nothing in it: for an invite to a
    /// server that was not made from a build, and for the player's own server whose build
    /// is gone. Only ever called after the player agreed to it on the screen.
    /// </summary>
    private Instance CreateCleanBuild(string name, string gameVersion, LoaderKind loader, string? loaderVersion)
    {
        var instance = _instances.Create(UniqueInstanceName(name));

        instance.VersionId = gameVersion;
        instance.Loader = loader;
        instance.LoaderVersion = loaderVersion;
        instance.MaxMemoryMb = MemoryForNewBuild(mods: 0);
        _instances.Save(instance);

        _allInstances.Add(instance);
        ApplyBuildFilter();
        return instance;
    }

    /// <summary>
    /// "Play" on a server whose build was deleted. A build of the player's that fits is
    /// taken as it is; a new one is only offered, on the same screen an invite is agreed
    /// to on; and with nothing to go by, nothing starts.
    /// </summary>
    private async Task<Instance?> FindBuildForFriendServerAsync(FriendServerItem item)
    {
        var server = item.Model;

        if (string.IsNullOrWhiteSpace(server.GameVersion))
        {
            try
            {
                item.IsBusy = true;
                item.Note = Localize("Friends_AskingVersion", "Asking the server which version it runs…");

                if (await PingFriendServerVersionAsync(server) is { } learnt)
                {
                    server.GameVersion = learnt;
                    SaveFriendServers();
                }
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        var plan = PlanJoin(server.GameVersion, server.Loader, inviteBringsBuild: false, dedicated: null);

        if (plan.Action == JoinAction.UseExisting && InstanceById(plan.BuildId) is { } build)
        {
            server.InstanceId = build.Id;
            SaveFriendServers();

            item.HasBuild = true;
            item.BuildLine = Localize("Friends_Build", "The build “{0}” is on this computer", build.Name);
            return build;
        }

        if (plan.Action == JoinAction.CreateClean)
        {
            // The same screen an invite is agreed to on: it says which build would be made.
            ShowFriendInvite(new ServerInvite(server.Name, plan.GameVersion!, plan.Loader, server.LoaderVersion, server.HostNickname, null, server.Endpoints));
            item.Note = Localize("Friends_BuildGoneConfirm", "The build for this server was deleted and none of yours fits it. Above is what would be made instead; nothing is made until you agree.");
            return null;
        }

        item.Note = Localize("Friends_VersionUnknown", "The launcher does not know which version this server runs and will not guess a build for it. Ask your friend for a new invite.");
        return null;
    }

    /// <summary>What the server says its version is, asked directly. Null when it does not answer or names no single version.</summary>
    private static async Task<string?> PingFriendServerVersionAsync(FriendServer server)
    {
        foreach (var address in new[] { server.Direct, server.Public })
        {
            if (!HostPort.TryParse(address, out var host, out var port))
            {
                continue;
            }

            // A friend's home connection through a relay of the provider's can take its time.
            var status = await Core.Server.ServerPinger.PingAsync(host, port ?? Core.Server.ServerPinger.DefaultPort, TimeSpan.FromSeconds(6));

            if (JoinPlan.VersionFromStatus(status?.VersionName) is { } version)
            {
                return version;
            }
        }

        return null;
    }

    // ===================== Play =====================

    [RelayCommand]
    private async Task PlayFriendServerAsync(FriendServerItem? item)
    {
        if (item is null || item.IsBusy)
        {
            return;
        }

        if (IsBusy || IsGameRunning || _friendJoin is not null)
        {
            Status = Localize("Friends_GameBusy", "The game is already running or starting");
            return;
        }

        var server = item.Model;

        var instance = FriendServerInstance(server);

        if (instance is null)
        {
            try
            {
                instance = await FindBuildForFriendServerAsync(item);
            }
            catch (Exception ex)
            {
                item.Note = Localize("Friends_PlayFailed", "Could not connect: {0}", ex.Message);
                AppendConsole($"[friends] {ex}");
            }

            if (instance is null)
            {
                return;
            }
        }

        FriendsJoin? join = null;

        try
        {
            item.IsBusy = true;
            item.Note = Localize("Friends_Connecting", "Looking for the server…");

            // Tries the ways in turn and keeps the first that a Minecraft server answers on.
            join = await FriendsJoin.ConnectAsync(server.Endpoints, null, server.LocalPort);

            if (join.Way == FriendsWay.None || string.IsNullOrWhiteSpace(join.Address))
            {
                item.Note = join.RelayFailure switch
                {
                    RelayFailure.RelayUnreachable => Localize("Friends_OfflineRelayDown", "The relay does not answer and there is no other way to the server. Try again later."),
                    RelayFailure.RelayBusy => Localize("Friends_OfflineRelayBusy", "The relay is at its limit right now. Try again in a minute."),
                    RelayFailure.HostOffline => Localize("Friends_OfflineHost", "Your friend's launcher is not there, or their server is off. Ask them to start the server."),
                    _ => Localize("Friends_Offline", "The server does not answer. Ask your friend to start it and press “Play” again. If their address has changed, ask for a new invite.")
                };

                return;
            }

            item.Note = join.Way switch
            {
                FriendsWay.Direct => Localize("Friends_ConnectedDirect", "Connected directly. If the game does not join by itself, the server's address is {0}", join.Address),
                FriendsWay.Relay => Localize("Friends_ConnectedRelay", "Connected through the relay. If the game does not join by itself, the server's address is {0}", join.Address),
                _ => Localize("Friends_ConnectedPublic", "Connected by the public address. If the game does not join by itself, the server's address is {0}", join.Address)
            };

            if (join.Voice == FriendsVoice.PortTaken)
            {
                // The game goes ahead regardless; only the voice chat has nowhere to land.
                item.Note += " " + Localize("Friends_VoicePortTaken", "Voice chat will not work: port {0} on this computer is taken by another program. The game itself is not affected.", join.VoicePort ?? 0);
                AppendConsole($"[friends] voice chat: UDP port {join.VoicePort} on 127.0.0.1 is taken, carrying the game only");
            }
            else if (join.Voice == FriendsVoice.Carried)
            {
                AppendConsole($"[friends] voice chat carried through the relay on UDP 127.0.0.1:{join.VoicePort}");
            }

            if (join.Way == FriendsWay.Relay &&
                HostPort.TryParse(join.Address, out _, out var localPort) && localPort is { } port)
            {
                // The same local port next time, so the server in the game's own list keeps working.
                server.LocalPort = port;
            }

            server.LastPlayedAt = DateTimeOffset.UtcNow;
            SaveFriendServers();

            SelectedInstance = instance;
            _friendJoin = join;
            _friendJoinAddress = join.Address;
            item.IsBusy = false;

            // Returns when the game has closed.
            await StartAsync(joinServer: false);
        }
        catch (Exception ex)
        {
            item.Note = Localize("Friends_PlayFailed", "Could not connect: {0}", ex.Message);
            AppendConsole($"[friends] {ex}");
        }
        finally
        {
            item.IsBusy = false;
            _friendJoinAddress = null;
            _friendJoin = null;

            if (join is not null)
            {
                try
                {
                    // The game is closed: the local port of the relay way has nothing left to carry.
                    await join.DisposeAsync();
                }
                catch (Exception ex)
                {
                    AppendConsole($"[friends] closing the tunnel: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Takes a friend's server off the list. The build it was played with stays among the builds.</summary>
    [RelayCommand]
    private void RemoveFriendServer(FriendServerItem? item)
    {
        if (item is null || item.IsBusy)
        {
            return;
        }

        _friendServerModels.Remove(item.Model);
        SaveFriendServers();
        RefreshFriendServers();
        Status = Localize("Friends_Removed", "The server “{0}” is off the list. Its build stays on the Builds tab.", item.Name);
    }
}
