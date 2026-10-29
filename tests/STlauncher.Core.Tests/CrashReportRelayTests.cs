using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Diagnostics;
using STlauncher.Core.Friends;
using STlauncher.Relay;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Crash reports left with the relay for the developer: the launcher's side and the
/// relay's together on the loopback address, the relay keeping them in a scratch folder.
/// </summary>
public sealed class CrashReportRelayTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stlauncher-report-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private RelayServer StartRelay(Action<RelayOptions>? tweak = null, bool keepsReports = true, List<string>? log = null)
    {
        var options = new RelayOptions
        {
            Port = 0,
            Bind = IPAddress.Loopback,
            StatsInterval = TimeSpan.Zero,
            MaxAttemptsPerMinute = 10_000,
            ReportDirectory = keepsReports ? _directory : null
        };

        tweak?.Invoke(options);

        var relay = new RelayServer(options, log is null ? null : line => { lock (log) { log.Add(line); } });
        relay.Start();
        return relay;
    }

    private static RelayEndpoint EndpointOf(RelayServer relay) => new("127.0.0.1", relay.Port);

    private string[] StoredFiles() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.txt") : Array.Empty<string>();

    private static async Task<List<string>> Talk(int port, params string[] lines)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Join("\n", lines) + "\n"));

        using var limit = new CancellationTokenSource(8000);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var answers = new List<string>();

        try
        {
            while (await reader.ReadLineAsync(limit.Token) is { } line)
            {
                answers.Add(line);
            }
        }
        catch (IOException)
        {
        }

        return answers;
    }

    [Fact]
    public async Task A_report_is_kept_as_text_under_an_id_and_logged_by_its_size()
    {
        var log = new List<string>();
        await using var relay = StartRelay(log: log);
        var text = "STlauncher 0.6.4 report from Steve\n===== game.log =====\nКрашнулось: java.lang.NullPointerException\n";

        var (id, failure) = await CrashReportUpload.SendAsync(EndpointOf(relay), text);

        Assert.Equal(CrashReportUploadFailure.None, failure);
        Assert.Matches("^R-[0-9A-HJKMNP-TV-Z]{5}$", id);

        var file = Assert.Single(StoredFiles());
        Assert.EndsWith("-" + id + ".txt", file);
        Assert.Equal(text, File.ReadAllText(file, Encoding.UTF8));
        Assert.Contains(log, line => line == $"report {id} bytes={Encoding.UTF8.GetByteCount(text)}");
    }

    [Fact]
    public async Task Binary_and_broken_text_are_refused()
    {
        await using var relay = StartRelay();

        var zip = Convert.ToBase64String(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 });
        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "REPORT BEGIN", "REPORT + " + zip, "REPORT END"));

        var brokenUtf8 = Convert.ToBase64String(new byte[] { 0x68, 0x69, 0xC3, 0x28 });
        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "REPORT BEGIN", "REPORT + " + brokenUtf8, "REPORT END"));

        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "REPORT BEGIN", "REPORT + not*base64!", "REPORT END"));
        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "REPORT BEGIN", "REPORT END"));

        Assert.Empty(StoredFiles());
    }

    [Fact]
    public void Report_text_is_utf8_without_control_characters()
    {
        Assert.True(RelayServer.IsReportText(Encoding.UTF8.GetBytes("строка\tс табом\r\nи ещё одна\n")));
        Assert.False(RelayServer.IsReportText(Encoding.UTF8.GetBytes("bell\u0007")));
        Assert.False(RelayServer.IsReportText(new byte[] { 0x00, 0x41 }));
        Assert.False(RelayServer.IsReportText(Array.Empty<byte>()));
    }

    [Fact]
    public async Task A_report_over_the_limit_is_refused_and_the_launcher_cuts_its_logs_to_fit()
    {
        await using var relay = StartRelay(o => o.ReportMaxBytes = 4096);
        var big = new string('x', 5000);

        Assert.Equal((null, CrashReportUploadFailure.Rejected), await CrashReportUpload.SendAsync(EndpointOf(relay), big + big + big + string.Concat(Enumerable.Repeat(big, 300))));

        var over = Convert.ToBase64String(Encoding.ASCII.GetBytes(new string('a', 150)));
        var lines = new List<string> { "REPORT BEGIN" };
        lines.AddRange(Enumerable.Repeat("REPORT + " + over, 30));
        lines.Add("REPORT END");
        Assert.Equal(new[] { "OK", "ERR TOOBIG" }, await Talk(relay.Port, lines.ToArray()));

        var fitted = CrashReportUpload.Compose("head", new[] { new CrashReportSection("game.log", big, KeepTail: true) }, null, 4096);
        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= 4096);

        var (id, failure) = await CrashReportUpload.SendAsync(EndpointOf(relay), fitted);
        Assert.Equal(CrashReportUploadFailure.None, failure);
        Assert.NotNull(id);
    }

    [Fact]
    public async Task Ten_reports_an_hour_from_one_address()
    {
        await using var relay = StartRelay(o => o.ReportsPerHour = 2);

        Assert.Equal(CrashReportUploadFailure.None, (await CrashReportUpload.SendAsync(EndpointOf(relay), "one")).Failure);
        Assert.Equal(CrashReportUploadFailure.None, (await CrashReportUpload.SendAsync(EndpointOf(relay), "two")).Failure);
        Assert.Equal(CrashReportUploadFailure.RateLimited, (await CrashReportUpload.SendAsync(EndpointOf(relay), "three")).Failure);
        Assert.Equal(2, StoredFiles().Length);
    }

    [Fact]
    public async Task Without_a_report_directory_or_on_an_old_relay_nothing_is_kept_and_nothing_is_wrong()
    {
        await using var relay = StartRelay(keepsReports: false);

        Assert.Equal(new[] { "ERR NOREPORT" }, await Talk(relay.Port, "REPORT BEGIN"));
        Assert.Equal(CrashReportUploadFailure.Unsupported, (await CrashReportUpload.SendAsync(EndpointOf(relay), "text")).Failure);
        Assert.False(Directory.Exists(_directory));

        // A relay from before reports.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var old = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            await reader.ReadLineAsync();
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("ERR BADCMD\n"));
        });

        Assert.Equal(CrashReportUploadFailure.Unsupported, (await CrashReportUpload.SendAsync(new RelayEndpoint("127.0.0.1", port), "text")).Failure);
        await old;
        listener.Stop();

        Assert.Equal(CrashReportUploadFailure.Unreachable, (await CrashReportUpload.SendAsync(new RelayEndpoint("127.0.0.1", port), "text")).Failure);
    }

    [Fact]
    public async Task There_is_no_way_to_read_a_report_back_over_the_network()
    {
        await using var relay = StartRelay();
        var (id, _) = await CrashReportUpload.SendAsync(EndpointOf(relay), "secret-ish log");

        Assert.Equal(new[] { "ERR BADCMD" }, await Talk(relay.Port, "GETREPORT " + id));
        Assert.Equal(new[] { "ERR BADCMD" }, await Talk(relay.Port, "REPORT " + id));
        Assert.Equal(new[] { "ERR BADCMD" }, await Talk(relay.Port, "REPORT SHOW " + id));
    }

    [Fact]
    public void Reports_older_than_their_life_are_swept_and_others_are_left_alone()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var store = new ReportStore(_directory, 1024 * 1024, TimeSpan.FromDays(30), () => now);
        store.Load();

        Assert.Equal(ReportOutcome.Stored, store.Put(Encoding.UTF8.GetBytes("old"), out var oldId));
        File.WriteAllText(Path.Combine(_directory, "README.txt"), "not a report");

        now = now.AddDays(20);
        Assert.Equal(ReportOutcome.Stored, store.Put(Encoding.UTF8.GetBytes("new"), out var newId));

        now = now.AddDays(11);
        Assert.Equal(1, store.Sweep());
        Assert.Null(store.Find(oldId));
        Assert.NotNull(store.Find(newId.ToLowerInvariant()));
        Assert.NotNull(store.Find(newId[2..]));

        // A restart reads the names, the reader command too.
        var again = new ReportStore(_directory, 1024 * 1024, TimeSpan.FromDays(30), () => now);
        again.Load(readOnly: true);
        Assert.Equal(new[] { newId }, again.List().Select(r => r.Id));
        Assert.True(File.Exists(Path.Combine(_directory, "README.txt")));
    }

    [Fact]
    public void The_store_refuses_when_full()
    {
        var store = new ReportStore(_directory, 10, TimeSpan.FromDays(30));
        store.Load();

        Assert.Equal(ReportOutcome.Stored, store.Put(Encoding.UTF8.GetBytes("12345678"), out _));
        Assert.Equal(ReportOutcome.Full, store.Put(Encoding.UTF8.GetBytes("123"), out _));
    }

    [Fact]
    public void The_reader_command_lists_and_shows_reports()
    {
        var store = new ReportStore(_directory, 1024 * 1024, TimeSpan.FromDays(30));
        store.Load();
        store.Put(Encoding.UTF8.GetBytes("STlauncher 0.6.4 report from Alex\nбольше текста\n"), out var id);

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, ReportCommand.Run(new[] { "reports", "list", "--report-dir", _directory }, _ => null, output, error));
        Assert.Contains(id, output.ToString());
        Assert.Contains("report from Alex", output.ToString());

        output = new StringWriter();
        Assert.Equal(0, ReportCommand.Run(new[] { "reports", "show", id.ToLowerInvariant() }, n => n == "STRELAY_REPORT_DIR" ? _directory : null, output, error));
        Assert.Contains("больше текста", output.ToString());

        Assert.Equal(1, ReportCommand.Run(new[] { "reports", "show", "R-00000", "--report-dir", _directory }, _ => null, output, error));
        Assert.Equal(0, ReportCommand.Run(new[] { "reports", "sweep", "--report-dir", _directory }, _ => null, output, error));
        Assert.Equal(2, ReportCommand.Run(new[] { "reports", "delete" }, _ => null, output, error));
    }

    [Fact]
    public void The_relay_reads_where_to_keep_reports_from_its_options()
    {
        Assert.True(RelayOptions.TryParse(Array.Empty<string>(), _ => null, out var off, out _));
        Assert.Null(off.ReportDirectory);
        Assert.Equal(1024 * 1024, off.ReportMaxBytes);
        Assert.Equal(10, off.ReportsPerHour);
        Assert.Equal(TimeSpan.FromDays(30), off.ReportLife);
        Assert.Equal(2048L * 1024 * 1024, off.ReportMaxTotalBytes);

        Assert.True(RelayOptions.TryParse(new[] { "--report-dir", "/var/lib/stlauncher-relay/reports", "--report-days", "7" }, _ => null, out var on, out _));
        Assert.Equal("/var/lib/stlauncher-relay/reports", on.ReportDirectory);
        Assert.Equal(TimeSpan.FromDays(7), on.ReportLife);
    }

    [Fact]
    public void The_composed_report_is_redacted_and_keeps_the_end_of_a_long_log()
    {
        var redaction = new LogRedactionContext(@"C:\Users\ivan", "ivan", new[] { "mc.showtime.su" });
        var log = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => $"[12:00:{i % 60:00}] line {i} loading C:\\Users\\ivan\\AppData\\Roaming\\.minecraft"));
        log += "\nLAST LINE --accessToken abc.def.ghi friend at 93.184.216.34";

        var text = CrashReportUpload.Compose(
            @"STlauncher 0.6.4 report, data folder C:\Users\ivan\AppData\Roaming\STlauncher",
            new[]
            {
                new CrashReportSection("summary", "user dir D:\\Games\\ivan\\mc, server mc.showtime.su"),
                new CrashReportSection("game.log", log, KeepTail: true)
            },
            redaction,
            64 * 1024);

        Assert.True(Encoding.UTF8.GetByteCount(text) <= 64 * 1024);
        Assert.DoesNotContain("ivan", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc.def.ghi", text);
        Assert.DoesNotContain("93.184.216.34", text);
        Assert.Contains("mc.showtime.su", text);
        Assert.Contains("LAST LINE", text);
        Assert.Contains("the beginning is cut to fit", text);
        Assert.DoesNotContain("line 0 loading", text);
        Assert.StartsWith("STlauncher 0.6.4 report", text);
    }
}
