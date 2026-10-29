using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core.Diagnostics;

namespace STlauncher.App.ViewModels;

/// <summary>One row of "why does it lag": a place, where it is, and its round trip once measured.</summary>
public partial class LagCheckRow : ObservableObject
{
    public LagCheckRow(LagTarget target, string name, string where)
    {
        Target = target;
        Name = name;
        Where = where;
    }

    public LagTarget Target { get; }

    public string Name { get; }

    /// <summary>The host, in small print beside the name.</summary>
    public string Where { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsGood))]
    [NotifyPropertyChangedFor(nameof(IsSlow))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(Verdict))]
    private LagMeasurement? _result;

    public bool IsPending => Result is null;

    public bool IsFailed => Result is { Ok: false };

    /// <summary>Answered, but slowly for what it is: the amber dot.</summary>
    public bool IsSlow => Result is { Ok: true, Milliseconds: { } ms } && ms >= SlowFrom(Target.Kind);

    public bool IsGood => Result is { Ok: true } && !IsSlow;

    public string Verdict => Result switch
    {
        null => MainWindowViewModel.Localize("Lag_Measuring", "measuring…"),
        { Ok: true, Milliseconds: { } ms } => MainWindowViewModel.Localize("Lag_Ms", "{0} ms", ms),
        _ => MainWindowViewModel.Localize("Lag_NoAnswer", "no answer")
    };

    private static int SlowFrom(LagProbeKind kind) => kind switch
    {
        LagProbeKind.GameServer => LagCheck.ServerSlowMs,
        LagProbeKind.Dns => 1000,
        _ => LagCheck.SlowMs
    };
}

/// <summary>
/// "Why does it lag?": the game server, the relay, the mirrors, Modrinth, CurseForge and
/// DNS measured at once, and a sentence that says what the numbers mean. Two buttons lead
/// here, in Settings and on the Showtime page; both show the same result. Nothing is sent
/// anywhere but the probes themselves.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<LagCheckRow> LagChecks { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyLagCheck))]
    private bool _isLagCheckRunning;

    /// <summary>Set once the panel has something in it: the rows of the last (or current) check.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyLagCheck))]
    private bool _hasLagCheck;

    [ObservableProperty]
    private string _lagConclusion = string.Empty;

    /// <summary>The colour of the conclusion's dot: all well, worth a look, or bad.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLagVerdictWarn))]
    private bool _isLagVerdictGood;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLagVerdictWarn))]
    private bool _isLagVerdictBad;

    public bool IsLagVerdictWarn => HasLagConclusion && !IsLagVerdictGood && !IsLagVerdictBad;

    public bool HasLagConclusion => LagConclusion.Length > 0 && !IsLagCheckRunning;

    public bool CanCopyLagCheck => HasLagCheck && !IsLagCheckRunning;

    partial void OnLagConclusionChanged(string value) => NotifyLagConclusion();

    partial void OnIsLagCheckRunningChanged(bool value) => NotifyLagConclusion();

    private void NotifyLagConclusion()
    {
        OnPropertyChanged(nameof(HasLagConclusion));
        OnPropertyChanged(nameof(IsLagVerdictWarn));
    }

    private LagProbe? _lagProbe;

    private IEnumerable<LagCheckRow> LagTargets()
    {
        if (!string.IsNullOrWhiteSpace(ServerAddress))
        {
            var (host, _) = LagProbe.SplitHostPort(ServerAddress, STlauncher.Core.Server.ServerPinger.DefaultPort);

            yield return new LagCheckRow(new LagTarget("server", LagProbeKind.GameServer, ServerAddress.Trim()),
                Localize("Lag_Server", "Showtime server"), ServerAddress.Trim());

            if (!IPAddress.TryParse(host, out _))
            {
                yield return new LagCheckRow(new LagTarget("dns", LagProbeKind.Dns, host),
                    Localize("Lag_Dns", "Finding its address (DNS)"), host);
            }
        }

        if (STlauncher.Core.Friends.RelayLocation.Resolve(_loadedCatalog) is { } relay)
        {
            yield return new LagCheckRow(new LagTarget("relay", LagProbeKind.Relay, $"{relay.Host}:{relay.Port}"),
                Localize("Lag_Relay", "The launcher's server (relay)"), relay.Host);
        }

        var feeds = _catalog.LoadCached()?.AllUpdateFeeds().ToList() ?? new List<string>();

        foreach (var feed in feeds.Concat(AppSettings.BuiltInUpdateFeeds).Distinct(StringComparer.OrdinalIgnoreCase).Take(2))
        {
            if (Uri.TryCreate(feed, UriKind.Absolute, out var uri))
            {
                yield return new LagCheckRow(new LagTarget("mirror", LagProbeKind.Mirror, feed.TrimEnd('/') + "/health"),
                    Localize("Lag_Mirror", "Update mirror"), uri.Host);
            }
        }

        yield return new LagCheckRow(new LagTarget("modrinth", LagProbeKind.Modrinth, "https://api.modrinth.com/"),
            Localize("Lag_Modrinth", "Modrinth (mods)"), "api.modrinth.com");

        if (!string.IsNullOrWhiteSpace(_curseForge.BaseUrl) && Uri.TryCreate(_curseForge.BaseUrl, UriKind.Absolute, out var cf))
        {
            yield return new LagCheckRow(new LagTarget("curseforge", LagProbeKind.CurseForge, _curseForge.BaseUrl.TrimEnd('/') + "/ping"),
                Localize("Lag_CurseForge", "CurseForge (through the mirror)"), cf.Host);
        }
    }

    [RelayCommand]
    private async Task RunLagCheckAsync()
    {
        if (IsLagCheckRunning)
        {
            return;
        }

        try
        {
            IsLagCheckRunning = true;
            HasLagCheck = true;
            LagConclusion = string.Empty;
            LagChecks.Clear();

            foreach (var row in LagTargets())
            {
                LagChecks.Add(row);
            }

            var rows = LagChecks.ToList();
            var progress = new Progress<LagMeasurement>(result =>
            {
                foreach (var row in rows.Where(r => ReferenceEquals(r.Target, result.Target)))
                {
                    row.Result = result;
                }
            });

            _lagProbe ??= new LagProbe(_network.Client);
            var results = await Task.Run(() => _lagProbe.RunAsync(rows.Select(r => r.Target), progress));

            foreach (var result in results)
            {
                foreach (var row in rows.Where(r => ReferenceEquals(r.Target, result.Target)))
                {
                    row.Result = result;
                }
            }

            var conclusion = LagCheck.Conclude(results);
            IsLagVerdictGood = conclusion.Verdict == LagVerdict.AllGood;
            IsLagVerdictBad = conclusion.Verdict is LagVerdict.ServerDown or LagVerdict.Offline or LagVerdict.DnsFailed;
            LagConclusion = LagConclusionText(conclusion, rows);
            AppendConsole("[lag] " + string.Join("; ", results) + " => " + conclusion.Verdict);
        }
        catch (Exception ex)
        {
            IsLagVerdictGood = false;
            IsLagVerdictBad = true;
            LagConclusion = Localize("Lag_Error", "The check itself failed: {0}", ex.Message);
        }
        finally
        {
            IsLagCheckRunning = false;
        }
    }

    /// <summary>The verdict in words, with the numbers in it.</summary>
    private static string LagConclusionText(LagConclusion conclusion, IReadOnlyList<LagCheckRow> rows)
    {
        var failed = string.Join(", ", conclusion.Failed
            .Select(t => rows.FirstOrDefault(r => ReferenceEquals(r.Target, t))?.Name ?? t.Key)
            .Distinct());
        var serverHost = rows.FirstOrDefault(r => r.Target.Kind == LagProbeKind.Dns)?.Where
                         ?? rows.FirstOrDefault(r => r.Target.Kind == LagProbeKind.GameServer)?.Where
                         ?? string.Empty;

        return conclusion.Verdict switch
        {
            LagVerdict.AllGood => conclusion.ServerMs is { } ms
                ? Localize("Lag_VerdictGood", "Showtime is {0} ms away and everything else answers quickly: the connection is fine. If the game still lags, the cause is the server itself or the computer, not the network.", ms)
                : Localize("Lag_VerdictGoodNoServer", "Everything answers quickly: the connection is fine."),
            LagVerdict.ServerPathSlow => Localize("Lag_VerdictServerPath", "Showtime is {0} ms away, other places {1} ms: the delay is on the way to the game server, not on your side.", conclusion.ServerMs ?? 0, conclusion.OthersMs ?? 0),
            LagVerdict.EverythingSlow => Localize("Lag_VerdictAllSlow", "Everything answers slowly: the problem is your connection or Wi-Fi. A cable instead of Wi-Fi, or pausing downloads and streams, helps."),
            LagVerdict.ServerDown => Localize("Lag_VerdictServerDown", "The Showtime server does not answer, the rest is fine: the server is off or overloaded."),
            LagVerdict.ModrinthBlocked => Localize("Lag_VerdictModrinth", "Modrinth does not open, the rest works: the provider is in Modrinth's way; mods will go the spare way."),
            LagVerdict.SomethingBlocked => conclusion.ServerMs is { } server
                ? Localize("Lag_VerdictBlocked", "Showtime is {0} ms away, so the game is not affected. Not opening: {1}; if something will not download, this is why.", server, failed)
                : Localize("Lag_VerdictBlockedNoServer", "Not opening: {0}; if something will not download, this is why.", failed),
            LagVerdict.DnsFailed => Localize("Lag_VerdictDns", "The address {0} cannot be found: the provider's DNS does not answer. Setting DNS 1.1.1.1 or 8.8.8.8 in Windows helps.", serverHost),
            _ => Localize("Lag_VerdictOffline", "Almost nothing answers: there is no internet, or a firewall, an antivirus or a VPN cuts it off.")
        };
    }

    [RelayCommand]
    private void CloseLagCheck()
    {
        if (!IsLagCheckRunning)
        {
            HasLagCheck = false;
        }
    }

    /// <summary>A short text for support: the rows and the conclusion, nothing else.</summary>
    [RelayCommand]
    private async Task CopyLagCheckAsync()
    {
        if (!CanCopyLagCheck)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine($"STlauncher {_updates.CurrentVersion ?? "dev"} · {Localize("Lag_Title", "Why does it lag?")} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");

        foreach (var row in LagChecks)
        {
            text.AppendLine($"{row.Name} ({row.Where}): {row.Verdict}");
        }

        if (LagConclusion.Length > 0)
        {
            text.AppendLine(LagConclusion);
        }

        await CopyToClipboardAsync(text.ToString());
        Status = Localize("Lag_Copied", "The check's result is in the clipboard");
    }
}
