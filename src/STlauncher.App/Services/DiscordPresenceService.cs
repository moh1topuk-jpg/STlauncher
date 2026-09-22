using System;
using DiscordRPC;

namespace STlauncher.App.Services;

/// <summary>
/// "Playing Showtime" under the player's name in Discord. Free word of mouth for the
/// server and nothing more: the application id comes from the catalog, so the whole
/// thing can be switched on, renamed or switched off without a release.
/// </summary>
/// <remarks>
/// The client keeps its own thread and reconnects on its own when Discord is not
/// running yet, so nothing here blocks or throws when there is no Discord at all.
/// </remarks>
public sealed class DiscordPresenceService : IDisposable
{
    private DiscordRpcClient? _client;
    private string? _appId;
    private bool _enabled;
    private RichPresence? _last;

    /// <summary>Applies the catalog's id and the player's switch. Idempotent.</summary>
    public void Configure(string? appId, bool enabled)
    {
        appId = string.IsNullOrWhiteSpace(appId) ? null : appId!.Trim();

        var restart = !string.Equals(appId, _appId, StringComparison.Ordinal);

        _appId = appId;
        _enabled = enabled;

        if (!enabled || appId is null)
        {
            Shutdown();
            return;
        }

        if (restart || _client is null || _client.IsDisposed)
        {
            Shutdown();

            try
            {
                _client = new DiscordRpcClient(appId) { SkipIdenticalPresence = true };
                _client.Initialize();
            }
            catch (Exception)
            {
                // No Discord, no pipe, a blocked id: presence is decoration, never a failure.
                _client = null;
                return;
            }
        }

        if (_last is not null)
        {
            Push(_last);
        }
    }

    /// <summary>Between games: in the launcher, with the server's site one click away.</summary>
    public void SetIdle(string serverName, string? website, string? imageUrl)
        => Push(Build(
            details: Localize("Discord_Idle", "In the launcher"),
            state: Localize("Discord_Server", "{0} server", serverName),
            serverName,
            website,
            imageUrl,
            withTimer: false));

    /// <summary>In game: the build and version, with a running clock.</summary>
    public void SetPlaying(string buildName, string? version, string loader, string serverName, string? website, string? imageUrl, bool onServer)
        => Push(Build(
            details: onServer
                ? Localize("Discord_PlayingOn", "Playing on {0}", serverName)
                : Localize("Discord_Playing", "Playing {0}", buildName),
            state: string.IsNullOrWhiteSpace(version) ? loader : $"{version} · {loader}",
            serverName,
            website,
            imageUrl,
            withTimer: true));

    public void Clear()
    {
        _last = null;

        try
        {
            _client?.ClearPresence();
        }
        catch (Exception)
        {
        }
    }

    private static RichPresence Build(string details, string state, string serverName, string? website, string? imageUrl, bool withTimer)
    {
        var presence = new RichPresence
        {
            Details = details,
            State = state,
            Assets = new Assets
            {
                // A URL works in current Discord builds; "logo" is the asset key to upload
                // in the developer portal for older ones.
                LargeImageKey = string.IsNullOrWhiteSpace(imageUrl) ? "logo" : imageUrl,
                LargeImageText = serverName
            }
        };

        if (withTimer)
        {
            presence.Timestamps = Timestamps.Now;
        }

        if (!string.IsNullOrWhiteSpace(website) && Uri.TryCreate(website, UriKind.Absolute, out _))
        {
            presence.Buttons = new[]
            {
                new Button { Label = Localize("Discord_Button", "Server website"), Url = website }
            };
        }

        return presence;
    }

    private void Push(RichPresence presence)
    {
        _last = presence;

        if (!_enabled || _client is null || _client.IsDisposed)
        {
            return;
        }

        try
        {
            _client.SetPresence(presence);
        }
        catch (Exception)
        {
        }
    }

    private void Shutdown()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            _client.ClearPresence();
            _client.Dispose();
        }
        catch (Exception)
        {
        }

        _client = null;
    }

    public void Dispose() => Shutdown();

    private static string Localize(string key, string fallback, params object?[] args)
        => ViewModels.MainWindowViewModel.Localize(key, fallback, args);
}
