using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Controls;
using STlauncher.Core.Diagnostics;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Streamer mode: while it is on, the launcher's own screens show dots where a stream
/// should not show something - the nickname, the addresses of friends' servers and of the
/// player's own, invite and build codes, the Windows user name in paths, and the same in
/// the console's lines. Display only: nothing on disk changes, the copy buttons copy the
/// real thing, and the game itself is not touched.
/// <para>
/// Pages do not hold the rules. The nickname goes through <see cref="DisplayUsername"/>,
/// everything else through <see cref="Streamer"/>'s attached properties, which this file
/// keeps informed.
/// </para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>How long the eye button shows a code.</summary>
    private static readonly TimeSpan StreamerRevealTime = TimeSpan.FromSeconds(5);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayUsername))]
    private bool _streamerMode;

    partial void OnStreamerModeChanged(bool value)
    {
        PersistSettings();
        PushStreamerState();
    }

    /// <summary>The nickname wherever it is only shown, not edited: "Player" while streamer mode is on.</summary>
    public string DisplayUsername => StreamerMode ? StreamerNicknameStandIn : Username;

    private static string StreamerNicknameStandIn => Localize("Streamer_Nickname", "Player");

    /// <summary>Called once from the constructor.</summary>
    private void AttachStreamerMode()
    {
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Username):
                case nameof(Language):
                    OnPropertyChanged(nameof(DisplayUsername));
                    PushStreamerState();
                    break;

                // The friends' servers and an invite on screen name hosts that are hidden by name.
                case nameof(HasFriendServers):
                case nameof(HasFriendInvite):
                    PushStreamerState();
                    break;
            }
        };

        FriendServers.CollectionChanged += (_, _) => PushStreamerState();
    }

    /// <summary>Tells the pages what to hide now. Cheap: with the mode off nothing is computed on screen.</summary>
    private void PushStreamerState()
    {
        // With the mode off the context is not read; built anyway so a switch-on finds it ready.
        Streamer.Configure(
            StreamerMode,
            new StreamerRedactionContext(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.UserName,
                Username,
                StreamerNicknameStandIn,
                StreamerMode ? PublicServerAddresses() : null,
                StreamerMode ? StreamerPrivateHosts() : null));
    }

    /// <summary>Host names of friends' servers and of an invite on screen: hidden wherever they are written.</summary>
    private IReadOnlyCollection<string> StreamerPrivateHosts()
    {
        var hosts = new List<string>();

        foreach (var server in _friendServerModels)
        {
            hosts.Add(server.Direct ?? string.Empty);
            hosts.Add(server.Relay ?? string.Empty);
            hosts.Add(server.Public ?? string.Empty);
        }

        if (_pendingInvite is { } invite)
        {
            hosts.Add(invite.Endpoints.Direct ?? string.Empty);
            hosts.Add(invite.Endpoints.Relay ?? string.Empty);
            hosts.Add(invite.Endpoints.Public ?? string.Empty);
        }

        return hosts.Where(h => h.Length > 0).ToList();
    }

    /// <summary>The eye beside a hidden code: the code with this key shows in full for five seconds.</summary>
    [RelayCommand]
    private void RevealStreamerCode(string? key) => Streamer.Reveal(key, StreamerRevealTime);

    /// <summary>The chip in the top strip: opens the setting, so the player sees why things show dots.</summary>
    [RelayCommand]
    private void OpenStreamerSetting() => OpenSettingFromSearch(Localize("Settings_StreamerMode", "Streamer mode"));
}
