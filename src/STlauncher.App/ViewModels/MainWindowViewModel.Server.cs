using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core.Server;

namespace STlauncher.App.ViewModels;

/// <summary>How much history the monitoring chart shows.</summary>
public enum MonitoringRange
{
    Day,
    Week
}

/// <summary>
/// One column of the chart. <see cref="HasData"/> separates "nobody was online" from
/// "the launcher was not running", which are drawn differently on purpose.
/// </summary>
public sealed record ServerChartBar(double Height, bool HasData, string Tooltip);

public partial class MainWindowViewModel
{
    /// <summary>Pixel height of the plot area. Bars are scaled into it.</summary>
    private const double ChartHeight = 96;

    /// <summary>Visible even when it is the lowest bar of the window.</summary>
    private const double MinimumBarHeight = 3;

    private ServerHistoryStore _serverHistory = null!;
    private DispatcherTimer? _serverTimer;

    /// <summary>Statistics published by the collector. Null until one is configured and reachable.</summary>
    private ServerStatsSnapshot? _remoteStats;

    private ServerStatsOrigin _remoteStatsOrigin = ServerStatsOrigin.None;

    /// <summary>
    /// How stale published statistics may be before the launcher stops trusting them.
    /// A collector that died three days ago must not keep a frozen chart on screen.
    /// </summary>
    private static readonly TimeSpan MaxStatsAge = TimeSpan.FromHours(6);

    [ObservableProperty]
    private string _serverName = ServerDefaults.Name;

    [ObservableProperty]
    private string _serverAddress = ServerDefaults.Address;

    [ObservableProperty]
    private Bitmap? _serverLogo;

    /// <summary>Icon reported by the server itself; falls back to the launcher artwork.</summary>
    [ObservableProperty]
    private Bitmap? _serverIcon;

    [ObservableProperty]
    private string _serverMotd = string.Empty;

    [ObservableProperty]
    private string _serverPlayers = string.Empty;

    [ObservableProperty]
    private string _serverVersion = string.Empty;

    [ObservableProperty]
    private bool _isServerOnline;

    [ObservableProperty]
    private bool _isServerStatusBusy;

    /// <summary>Reminder that the server button connects straight to the server.</summary>
    public string ServerJoinHint
        => Localize("Game_ServerJoinHint", "Play on server connects you to {0}", ServerAddress);

    // ===================== Monitoring =====================

    public ObservableCollection<ServerChartBar> ServerChartBars { get; } = new();

    /// <summary>True once there is at least one reading in the selected window.</summary>
    [ObservableProperty]
    private bool _hasServerHistory;

    [ObservableProperty]
    private string _serverAverage = string.Empty;

    [ObservableProperty]
    private string _serverPeak = string.Empty;

    [ObservableProperty]
    private string _serverMonitoringNote = string.Empty;

    /// <summary>Top of the vertical axis, so the bars can be read as numbers.</summary>
    [ObservableProperty]
    private string _chartTopLabel = string.Empty;

    [ObservableProperty]
    private string _chartStartLabel = string.Empty;

    [ObservableProperty]
    private string _chartEndLabel = string.Empty;

    [ObservableProperty]
    private MonitoringRange _monitoringRange = MonitoringRange.Day;

    /// <summary>Figures the monitoring site computes; no amount of local sampling produces them.</summary>
    [ObservableProperty]
    private string _serverAverageWeek = string.Empty;

    [ObservableProperty]
    private string _serverRecord = string.Empty;

    [ObservableProperty]
    private string _serverUptime = string.Empty;

    [ObservableProperty]
    private string _serverRank = string.Empty;

    [ObservableProperty]
    private bool _hasServerSummary;

    /// <summary>Says where the chart came from, so nobody has to guess.</summary>
    [ObservableProperty]
    private string _serverStatsSource = string.Empty;

    /// <summary>
    /// Manual collector address from the developer section. The catalog normally supplies
    /// it; this is the escape hatch for testing a collector before publishing it.
    /// </summary>
    [ObservableProperty]
    private string _serverStatsUrl = string.Empty;

    partial void OnServerStatsUrlChanged(string value)
    {
        PersistSettings();
        ApplyServerStatsUrl(_catalogStatsUrl);
    }

    /// <summary>Last address the catalog published, remembered so the override can fall back to it.</summary>
    private string? _catalogStatsUrl;

    public bool IsDayRange => MonitoringRange == MonitoringRange.Day;

    public bool IsWeekRange => MonitoringRange == MonitoringRange.Week;

    partial void OnMonitoringRangeChanged(MonitoringRange value)
    {
        OnPropertyChanged(nameof(IsDayRange));
        OnPropertyChanged(nameof(IsWeekRange));
        RefreshServerHistory();
    }

    [RelayCommand]
    private void SelectMonitoringRange(string? range)
        => MonitoringRange = string.Equals(range, "Week", StringComparison.OrdinalIgnoreCase)
            ? MonitoringRange.Week
            : MonitoringRange.Day;

    private static TimeSpan RangeWindow(MonitoringRange range)
        => range == MonitoringRange.Week ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);

    /// <summary>
    /// One bar per hour for a day, one per six hours for a week. Fixed counts keep the
    /// chart readable: the old version drew a bar per hour over three days, which was 73
    /// four-pixel columns of which a fresh installation could fill three.
    /// </summary>
    private static int RangeBuckets(MonitoringRange range) => range == MonitoringRange.Week ? 28 : 24;

    private static string BucketFormat(MonitoringRange range) => range == MonitoringRange.Week ? "dd.MM HH:mm" : "HH:mm";

    /// <summary>
    /// Sets up the history store and starts polling. Called once the launcher knows which
    /// server the selected build points at.
    /// </summary>
    private void StartServerMonitoring()
    {
        _serverHistory = new ServerHistoryStore(
            System.IO.Path.Combine(_paths.Root, "server-history.json"));

        LoadServerLogo();
        RefreshServerHistory();

        _ = RefreshServerStatusAsync();
        _ = RefreshServerStatsAsync();

        _serverTimer?.Stop();

        // Five minutes: often enough that a day chart fills in visibly while the launcher
        // is open, rare enough to be invisible to the server.
        _serverTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _serverTimer.Tick += (_, _) =>
        {
            _ = RefreshServerStatusAsync();
            _ = RefreshServerStatsAsync();
        };
        _serverTimer.Start();
    }

    /// <summary>
    /// Reads the statistics published by the collector. Everything about it is optional:
    /// with no collector configured, or none reachable, the chart falls back to the
    /// samples this launcher took itself.
    /// </summary>
    private async Task RefreshServerStatsAsync()
    {
        if (!_stats.IsConfigured)
        {
            return;
        }

        try
        {
            var result = await _stats.LoadAsync();

            if (result.Snapshot is null)
            {
                if (result.Error is not null)
                {
                    AppendConsole($"[stats] {result.Error}");
                }

                return;
            }

            _remoteStats = result.Snapshot;
            _remoteStatsOrigin = result.Origin;
            RefreshServerHistory();
        }
        catch (Exception ex)
        {
            AppendConsole($"[stats] {ex.Message}");
        }
    }

    /// <summary>
    /// Points the stats client at the collector the catalog publishes, falling back to
    /// the address set by hand in the developer section.
    /// </summary>
    public void ApplyServerStatsUrl(string? fromCatalog)
    {
        _catalogStatsUrl = fromCatalog;

        // A hand-entered address wins: it exists precisely to try a collector the
        // catalog does not know about yet.
        var url = string.IsNullOrWhiteSpace(ServerStatsUrl) ? fromCatalog : ServerStatsUrl;

        if (string.Equals(url, _stats.StatsUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _stats.StatsUrl = url;
        _remoteStats = null;
        _remoteStatsOrigin = ServerStatsOrigin.None;

        _ = RefreshServerStatsAsync();
    }

    [RelayCommand]
    private async Task RefreshServerStatusAsync()
    {
        if (IsServerStatusBusy || string.IsNullOrWhiteSpace(ServerAddress))
        {
            return;
        }

        var address = ServerAddress;

        try
        {
            IsServerStatusBusy = true;

            // The ping has its own five-second timeout, so a request nobody is waiting
            // for cannot pile up behind the busy flag for long.
            var (host, port) = SplitAddress(address);
            var status = await ServerPinger.PingAsync(host, port);

            if (status is null)
            {
                IsServerOnline = false;
                ServerMotd = Localize("Server_Offline", "The server did not respond");
                ServerPlayers = string.Empty;
                ServerVersion = string.Empty;
                ServerIcon = ServerLogo;

                // Deliberately not recorded as "0 online": a failed ping means the server
                // was unreachable *from here*, and a chart cannot tell that apart from a
                // dropped connection. The gap is the honest answer.
                RefreshServerHistory();
                return;
            }

            IsServerOnline = true;
            ServerMotd = status.Motd;
            ServerPlayers = Localize("Server_Players", "Players: {0} / {1}", status.Online, status.Max);
            ServerVersion = status.VersionName ?? string.Empty;
            ServerIcon = status.Favicon is { Length: > 0 }
                ? CreateBitmap(status.Favicon) ?? ServerLogo
                : ServerLogo;

            _serverHistory?.Add(address, status.Online);
            RefreshServerHistory();
        }
        catch (Exception ex)
        {
            AppendConsole($"[server] {ex.Message}");
        }
        finally
        {
            IsServerStatusBusy = false;
        }
    }

    /// <summary>
    /// Rebuilds the chart. Published statistics win when there are any: they cover the
    /// hours this launcher was closed, which is most of them. Local samples are the
    /// fallback for when no collector is configured or reachable.
    /// </summary>
    private void RefreshServerHistory()
    {
        ServerChartBars.Clear();

        var window = RangeWindow(MonitoringRange);
        var now = DateTimeOffset.Now;

        var remote = UsableRemoteRange(now);

        var buckets = remote is not null
            ? remote.ToBuckets()
            : _serverHistory?.GetBuckets(ServerAddress, window, RangeBuckets(MonitoringRange), now)
              ?? Array.Empty<ServerHistoryBucket>();

        var localSummary = _serverHistory is null || string.IsNullOrWhiteSpace(ServerAddress)
            ? (Average: 0d, Peak: 0, SampleCount: 0)
            : _serverHistory.GetSummary(ServerAddress, window, now);

        HasServerHistory = remote is not null || localSummary.SampleCount > 0;

        // Scale to the tallest bar of the window, never to zero.
        var scale = Math.Max(1, buckets.Count == 0 ? 0 : buckets.Max(b => b.Peak));
        var format = BucketFormat(MonitoringRange);

        foreach (var bucket in buckets)
        {
            if (!bucket.HasData)
            {
                ServerChartBars.Add(new ServerChartBar(0, false, string.Empty));
                continue;
            }

            var height = Math.Max(MinimumBarHeight, bucket.Average / scale * ChartHeight);

            ServerChartBars.Add(new ServerChartBar(
                height,
                true,
                Localize(
                    "Server_BarTooltip",
                    "{0} - {1} (peak {2})",
                    bucket.Start.ToLocalTime().ToString(format, CultureInfo.CurrentCulture),
                    bucket.Average.ToString("F0", CultureInfo.CurrentCulture),
                    bucket.Peak)));
        }

        ChartTopLabel = HasServerHistory ? scale.ToString(CultureInfo.CurrentCulture) : string.Empty;
        ChartStartLabel = (remote?.From ?? now - window).ToLocalTime().ToString(format, CultureInfo.CurrentCulture);
        ChartEndLabel = Localize("Server_Now", "now");

        // The window average always comes from what is drawn, so the number under the
        // chart and the chart itself can never disagree.
        var drawn = buckets.Where(b => b.HasData).ToList();

        ServerAverage = drawn.Count > 0
            ? Localize("Server_Average", "Average online: {0:F0}", drawn.Average(b => b.Average))
            : localSummary.SampleCount > 0
                ? Localize("Server_Average", "Average online: {0:F0}", localSummary.Average)
                : string.Empty;

        ServerPeak = drawn.Count > 0
            ? Localize("Server_Peak", "Peak: {0}", drawn.Max(b => b.Peak))
            : localSummary.SampleCount > 0
                ? Localize("Server_Peak", "Peak: {0}", localSummary.Peak)
                : string.Empty;

        ApplyStatsSummary();

        ServerMonitoringNote = remote is not null
            ? string.Empty
            : BuildMonitoringNote(localSummary.SampleCount, window);

        ServerStatsSource = remote is null
            ? (_stats.IsConfigured
                ? Localize("Server_SourceLocal", "Collector unavailable - showing this launcher's own samples.")
                : string.Empty)
            : _remoteStatsOrigin == ServerStatsOrigin.Cache
                ? Localize("Server_SourceCache", "Saved copy: the collector is unreachable right now.")
                : string.Empty;
    }

    /// <summary>
    /// The published range for the selected window, or null when there is nothing worth
    /// drawing: no collector, stale data, or a range with no readings at all.
    /// </summary>
    private ServerStatsRange? UsableRemoteRange(DateTimeOffset now)
    {
        if (_remoteStats is null || !_remoteStats.IsFresh(MaxStatsAge, now))
        {
            return null;
        }

        var range = _remoteStats.Range(MonitoringRange == MonitoringRange.Week ? "week" : "day");

        return range is not null && range.HasData ? range : null;
    }

    /// <summary>
    /// Shows the figures the monitoring site computes - average over a week, the all-time
    /// record, uptime, rank. They are the reason a fresh installation has something to
    /// look at before it has collected anything itself.
    /// </summary>
    private void ApplyStatsSummary()
    {
        var summary = _remoteStats?.Summary;

        if (summary is null || !summary.HasAnything)
        {
            HasServerSummary = false;
            ServerAverageWeek = string.Empty;
            ServerRecord = string.Empty;
            ServerUptime = string.Empty;
            ServerRank = string.Empty;
            return;
        }

        HasServerSummary = true;

        ServerAverageWeek = summary.AverageWeek is { } average
            ? Localize("Server_AverageWeek", "Average for the week: {0:F0}", average)
            : string.Empty;

        ServerRecord = summary.Peak is { } peak
            ? summary.PeakAt is { } peakAt
                ? Localize(
                    "Server_RecordAt",
                    "Record: {0} ({1})",
                    peak,
                    peakAt.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture))
                : Localize("Server_Record", "Record: {0}", peak)
            : string.Empty;

        ServerUptime = summary.Uptime is { } uptime
            ? Localize("Server_Uptime", "Uptime: {0:F0}%", uptime)
            : string.Empty;

        ServerRank = summary.Rank is { } rank
            ? Localize("Server_Rank", "Rank: #{0}", rank)
            : string.Empty;
    }

    /// <summary>
    /// Explains what the chart is showing instead of leaving an empty box. A launcher can
    /// only sample while it is open, so "few samples" is the normal state on day one.
    /// </summary>
    private string BuildMonitoringNote(int samples, TimeSpan window)
    {
        if (samples == 0)
        {
            return Localize("Server_NoData", "No data yet - samples appear while the launcher is open.");
        }

        var first = _serverHistory?.FirstSampleAt(ServerAddress);
        var covered = first is null ? TimeSpan.Zero : DateTimeOffset.Now - first.Value;

        // Under a quarter of the window covered: say so, otherwise the sparse chart looks
        // like a bug rather than a launcher that was only open for an hour.
        return covered < window / 4
            ? Localize("Server_Collecting", "Collecting data: {0} sample(s) so far.", samples)
            : string.Empty;
    }

    /// <summary>Splits "host" or "host:port" for the ping.</summary>
    public static (string Host, int Port) SplitAddress(string address)
    {
        var trimmed = (address ?? string.Empty).Trim();
        var separator = trimmed.LastIndexOf(':');

        if (separator > 0 &&
            int.TryParse(trimmed[(separator + 1)..], out var port) &&
            port is > 0 and <= 65535)
        {
            return (trimmed[..separator], port);
        }

        return (trimmed, ServerPinger.DefaultPort);
    }

    /// <summary>
    /// Points the launcher at the server the selected build pins, falling back to the
    /// launcher default. The catalog can therefore move the server without a release.
    /// </summary>
    private void ApplyServerFromInstance()
    {
        var name = string.IsNullOrWhiteSpace(SelectedInstance?.ServerName)
            ? ServerDefaults.Name
            : SelectedInstance!.ServerName!;

        var address = string.IsNullOrWhiteSpace(SelectedInstance?.ServerAddress)
            ? ServerDefaults.Address
            : SelectedInstance!.ServerAddress!;

        var addressChanged = !string.Equals(address, ServerAddress, StringComparison.OrdinalIgnoreCase);

        ServerName = name;
        ServerAddress = address;

        if (!addressChanged)
        {
            return;
        }

        OnPropertyChanged(nameof(ServerJoinHint));
        RefreshServerHistory();
        _ = RefreshServerStatusAsync();
    }

    private static Bitmap? CreateBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new System.IO.MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads Assets/server-logo.png when it exists. A missing file simply leaves the
    /// placeholder in place, so the build never depends on an artwork asset.
    /// </summary>
    private void LoadServerLogo()
    {
        try
        {
            var uri = new Uri("avares://STlauncher.App/Assets/server-logo.png");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            ServerLogo = new Bitmap(stream);
            ServerIcon ??= ServerLogo;
        }
        catch (Exception)
        {
            ServerLogo = null;
        }
    }
}
