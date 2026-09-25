using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core.Diagnostics;

namespace STlauncher.App.ViewModels;

/// <summary>One row of the network check: a name, and a verdict once it has one.</summary>
public partial class NetworkCheckItem : ObservableObject
{
    public NetworkCheckItem(NetworkTarget target, string display)
    {
        Target = target;
        Display = display;
    }

    public NetworkTarget Target { get; }

    public string Display { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(Verdict))]
    private NetworkCheckResult? _result;

    public bool IsPending => Result is null;

    public bool IsOk => Result?.Ok == true;

    public bool IsFailed => Result?.Ok == false;

    public string Verdict => Result is null
        ? string.Empty
        : Result.Ok
            ? MainWindowViewModel.Localize("Net_Ok", "ok · {0} ms", Result.Milliseconds)
            : MainWindowViewModel.Localize("Net_Fail", "no answer · {0}", Result.Error);
}

/// <summary>
/// "Nothing downloads" has a dozen possible reasons; this probes each place the launcher
/// talks to and lists which ones answer, so the player can send one report instead of a
/// guess. The targets are the real ones the launcher uses, mirrors included.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<NetworkCheckItem> NetworkChecks { get; } = new();

    [ObservableProperty]
    private bool _isNetworkCheckRunning;

    [ObservableProperty]
    private string _networkCheckSummary = string.Empty;

    private IEnumerable<NetworkCheckItem> NetworkTargets()
    {
        yield return new NetworkCheckItem(new NetworkTarget("github", "https://api.github.com/"), Localize("Net_GitHub", "GitHub (updates, catalog)"));
        yield return new NetworkCheckItem(new NetworkTarget("github-raw", "https://raw.githubusercontent.com/"), Localize("Net_GitHubRaw", "GitHub files"));

        var feeds = _catalog.LoadCached()?.AllUpdateFeeds().ToList() ?? new List<string>();
        foreach (var feed in feeds.Concat(AppSettings.BuiltInUpdateFeeds).Distinct(StringComparer.OrdinalIgnoreCase).Take(2))
        {
            yield return new NetworkCheckItem(new NetworkTarget("mirror", feed.TrimEnd('/') + "/health"), Localize("Net_Mirror", "Update mirror") + " · " + new Uri(feed).Host);
        }

        yield return new NetworkCheckItem(new NetworkTarget("modrinth", "https://api.modrinth.com/v2/"), Localize("Net_Modrinth", "Modrinth (mods)"));
        yield return new NetworkCheckItem(new NetworkTarget("mojang", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"), Localize("Net_Mojang", "Mojang (game files)"));
        yield return new NetworkCheckItem(new NetworkTarget("libraries", "https://libraries.minecraft.net/"), Localize("Net_Libraries", "Mojang (libraries)"));
        yield return new NetworkCheckItem(new NetworkTarget("adoptium", "https://api.adoptium.net/v3/info/available_releases"), Localize("Net_Java", "Adoptium (Java)"));

        // What the game itself needs to show skins: player profiles and the texture files.
        // A provider that swaps these for a web page leaves everyone on the server as Steve.
        yield return new NetworkCheckItem(new NetworkTarget("session", "https://sessionserver.mojang.com/session/minecraft/profile/069a79f444e94726a5befca90e38aaf5", RejectHtml: true), Localize("Net_Session", "Mojang (player profiles)"));
        yield return new NetworkCheckItem(new NetworkTarget("textures", "https://textures.minecraft.net/", RejectHtml: true), Localize("Net_Textures", "Mojang (skin textures)"));

        if (!string.IsNullOrWhiteSpace(ServerAddress))
        {
            var host = ServerAddress.Split(':')[0];
            var port = ServerAddress.Contains(':') && int.TryParse(ServerAddress.Split(':')[1], out var p) ? p : 25565;
            yield return new NetworkCheckItem(new NetworkTarget("server", $"tcp://{host}:{port}"), Localize("Net_Server", "Server") + " · " + ServerAddress);
        }
    }

    /// <summary>
    /// After a download failed for a network-looking reason: runs the check and turns the
    /// status line from a bare error into the list of hosts that did not answer, so the
    /// player knows whether it is their connection, their provider, or a service.
    /// </summary>
    private async Task ExplainDownloadFailureAsync(string what, Exception ex)
    {
        if (!LooksLikeNetworkTrouble(ex) || IsNetworkCheckRunning)
        {
            return;
        }

        AppendConsole($"[net] could not download {what} ({ex.GetType().Name}); running the network check");
        await RunNetworkCheckAsync();

        var failed = NetworkChecks.Where(i => i.IsFailed).Select(i => i.Display).ToList();

        Status = failed.Count == 0
            ? Localize("Net_AfterFailureAllOk", "Could not download {0}, yet every address answers. Try again in a minute.", what)
            : Localize("Net_AfterFailure", "Could not download {0}. Not answering: {1}. Details: Settings → Network check.", what, string.Join(", ", failed));

        AppendConsole("[net] " + string.Join("; ", NetworkChecks.Select(i => i.Result?.ToString() ?? i.Display)));
    }

    private static bool LooksLikeNetworkTrouble(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Http.HttpRequestException
                or System.Net.Sockets.SocketException
                or System.Net.WebException
                or System.IO.IOException
                or TimeoutException
                or TaskCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    [RelayCommand]
    private async Task RunNetworkCheckAsync()
    {
        if (IsNetworkCheckRunning)
        {
            return;
        }

        try
        {
            IsNetworkCheckRunning = true;
            NetworkCheckSummary = Localize("Net_Running", "Checking…");
            NetworkChecks.Clear();

            foreach (var item in NetworkTargets())
            {
                NetworkChecks.Add(item);
            }

            var items = NetworkChecks.ToList();
            var progress = new Progress<NetworkCheckResult>(result =>
            {
                foreach (var item in items.Where(i => ReferenceEquals(i.Target, result.Target)))
                {
                    item.Result = result;
                }
            });

            var results = await _network.RunAsync(items.Select(i => i.Target), progress);
            var failed = results.Count(r => !r.Ok);

            NetworkCheckSummary = failed == 0
                ? Localize("Net_AllOk", "Everything answers. If downloads still fail, the problem is not the network.")
                : Localize("Net_SomeFail", "No answer from {0} of {1}. Copy the report and send it to the server admin.", failed, results.Count);
        }
        catch (Exception ex)
        {
            NetworkCheckSummary = Localize("Net_Error", "The check itself failed: {0}", ex.Message);
        }
        finally
        {
            IsNetworkCheckRunning = false;
        }
    }

    [RelayCommand]
    private async Task CopyNetworkReportAsync()
    {
        var report = new StringBuilder();
        report.AppendLine($"STlauncher {_updates.CurrentVersion ?? "dev"} network check, {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");

        foreach (var item in NetworkChecks)
        {
            report.AppendLine(item.Result is null ? $"{item.Display}: pending" : $"{item.Display}: {item.Result}");
        }

        var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? window.Clipboard
            : null;

        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(report.ToString());
            Status = Localize("Net_Copied", "Report copied");
        }
    }
}
