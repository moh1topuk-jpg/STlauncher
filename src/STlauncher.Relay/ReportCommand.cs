using System.Globalization;
using System.Text;

namespace STlauncher.Relay;

/// <summary>
/// "stlauncher-relay reports list | show R-XXXXX | sweep": the developer's way to read the
/// crash reports over SSH without remembering the file layout. It reads the directory the
/// relay writes and never talks to the running relay.
/// </summary>
public static class ReportCommand
{
    /// <summary>Where the systemd unit in docs/relay.md keeps them, for when no directory is given.</summary>
    public const string DefaultDirectory = "/var/lib/stlauncher-relay/reports";

    public static bool IsReportCommand(string[] args) => args.Length > 0 && args[0] == "reports";

    public static int Run(string[] args, Func<string, string?> environment, TextWriter output, TextWriter error)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Usage: stlauncher-relay reports list | show R-XXXXX | sweep [--report-dir <directory>] [--report-days <n>]");
            return 2;
        }

        var verb = args[1];
        var rest = args.Skip(2).ToList();
        string? id = null;

        if (verb == "show")
        {
            if (rest.Count == 0 || rest[0].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine("Which report? stlauncher-relay reports show R-XXXXX");
                return 2;
            }

            id = rest[0];
            rest.RemoveAt(0);
        }
        else if (verb is not ("list" or "sweep"))
        {
            error.WriteLine($"Unknown: reports {verb}. Use list, show or sweep.");
            return 2;
        }

        if (!RelayOptions.TryParse(rest.ToArray(), environment, out var options, out var problem))
        {
            error.WriteLine(problem);
            return 2;
        }

        var directory = options.ReportDirectory ?? DefaultDirectory;

        if (!Directory.Exists(directory))
        {
            error.WriteLine($"No reports directory at {directory}. Is the relay started with --report-dir?");
            return 1;
        }

        var store = new ReportStore(directory, options.ReportMaxTotalBytes, options.ReportLife);
        store.Load(readOnly: true);

        switch (verb)
        {
            case "list":
                var all = store.List();
                output.WriteLine($"{all.Count} report(s), {Size(store.TotalBytes)}, in {store.Directory}");

                foreach (var report in all)
                {
                    output.WriteLine(string.Join("  ",
                        report.Id,
                        report.ReceivedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z",
                        Size(report.Size).PadLeft(8),
                        FirstLine(report.Path)));
                }

                return 0;

            case "show":
                if (store.Find(id!) is not { } found)
                {
                    error.WriteLine($"No report {id}. It may be older than {options.ReportLife.TotalDays:0} days and swept.");
                    return 1;
                }

                output.Write(File.ReadAllText(found.Path, Encoding.UTF8));
                return 0;

            default:
                var removed = store.Sweep();
                output.WriteLine($"{removed} report(s) older than {options.ReportLife.TotalDays:0} days removed, {store.Count} left.");
                return 0;
        }
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, (bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB";

    /// <summary>The report's own first line: the launcher's version, the nickname and when.</summary>
    private static string FirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            var line = reader.ReadLine() ?? string.Empty;
            return line.Length > 100 ? line[..100] + "…" : line;
        }
        catch (Exception)
        {
            return "(unreadable)";
        }
    }
}
