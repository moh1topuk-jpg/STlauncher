using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace STlauncher.Core.Diagnostics;

/// <summary>One freeze of the interface, as it is written to disk.</summary>
/// <param name="Since">When the interface stopped answering.</param>
/// <param name="Duration">How long it has been silent so far, sleep excluded.</param>
/// <param name="Recovered">False while it is still frozen (or the launcher was closed frozen).</param>
/// <param name="Details">What was known at the moment the freeze was noticed.</param>
/// <param name="Afterword">What became known on recovery.</param>
public sealed record FreezeReport(
    DateTimeOffset Since,
    TimeSpan Duration,
    bool Recovered,
    string Version,
    string OperatingSystem,
    IReadOnlyList<string> Details,
    IReadOnlyList<string>? Afterword = null)
{
    public string Format()
    {
        var sb = new StringBuilder();
        var seconds = Duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);

        sb.AppendLine($"STlauncher {Version} freeze report");
        sb.AppendLine($"when = {Since:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine(Recovered
            ? $"duration = {seconds} s, the interface answered again"
            : $"duration = at least {seconds} s, still not answering when this was written");
        sb.AppendLine($"os = {OperatingSystem}");
        sb.AppendLine();
        sb.AppendLine("[when the freeze was noticed]");

        foreach (var line in Details)
        {
            sb.AppendLine(line);
        }

        if (Afterword is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("[on recovery]");

            foreach (var line in Afterword)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Freeze reports on disk: one file per freeze, rewritten as the freeze goes on, and only
/// the last few kept. The files are the launcher's own diagnostics, so the old ones go.
/// </summary>
public sealed class FreezeReportStore
{
    public const string DirectoryName = "freezes";
    public const int DefaultKeep = 5;

    private const string Prefix = "freeze-";
    private const string Extension = ".txt";

    private readonly int _keep;

    public FreezeReportStore(string directory, int keep = DefaultKeep)
    {
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _keep = Math.Max(1, keep);
    }

    /// <summary>The folder next to the crash log.</summary>
    public static FreezeReportStore In(string dataRoot) => new(Path.Combine(dataRoot, DirectoryName));

    public string Directory { get; }

    /// <summary>
    /// Writes the report and returns its path. The name comes from the moment the freeze
    /// began, so writing the same freeze again replaces the file instead of adding one.
    /// </summary>
    public string Write(FreezeReport report)
    {
        var path = Path.Combine(
            Directory,
            Prefix + report.Since.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Extension);

        var isNew = !File.Exists(path);
        AtomicFile.WriteAllText(path, report.Format());

        if (isNew)
        {
            Prune();
        }

        return path;
    }

    /// <summary>The kept reports, newest first.</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return Array.Empty<string>();
            }

            // The name carries the time, so ordering by name is ordering by freeze.
            return System.IO.Directory
                .EnumerateFiles(Directory, Prefix + "*" + Extension)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>How many kept reports are for freezes that began at or after the given moment.</summary>
    public int CountSince(DateTimeOffset moment)
    {
        var floor = Prefix + moment.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        return List().Count(path =>
            string.CompareOrdinal(Path.GetFileNameWithoutExtension(path), floor) >= 0);
    }

    private void Prune()
    {
        foreach (var path in List().Skip(_keep))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // A report someone has open stays until the next freeze.
            }
        }
    }
}
