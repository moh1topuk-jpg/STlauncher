using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Input.Platform;
using STlauncher.App.Services;

namespace STlauncher.App.ViewModels;

/// <summary>
/// One file for the server admin: both logs, the crash report, the build's makeup, the
/// settings and the machine, zipped, with the path in the clipboard and the file shown in
/// the file manager. Half of every support conversation used to be collecting these.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>A log is sent from its end: the last part is where the trouble is.</summary>
    private const long LogTailBytes = 2 * 1024 * 1024;

    [RelayCommand]
    private async Task CollectSupportReportAsync()
    {
        try
        {
            var (zipPath, name) = WriteSupportReport();
            AppendConsole($"[report] saved {zipPath}");
            await CopyToClipboardAsync(zipPath);
            RevealInFileManager(zipPath);
            Status = Localize("Report_Saved", "Report saved: {0}. The path is in the clipboard; send the file to the server admin.", name);
        }
        catch (Exception ex)
        {
            Status = Localize("Report_Failed", "Could not collect the report: {0}", ex.Message);
            AppendConsole($"[report] failed: {ex}");
        }
    }

    // ===================== Straight to the admin =====================

    /// <summary>Where the report goes: the catalog's address, else the mirror's. Empty means the button is not shown.</summary>
    public string ReportUrl => _loadedCatalog is { ReportUrl: { } configured }
        ? configured.Trim()
        : AppSettings.DefaultReportUrl;

    /// <summary>Set once the endpoint has answered that it takes reports; the button waits for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendReport))]
    private bool _isReportEndpointReady;

    private string? _probedReportUrl;

    public bool CanSendReport => ReportUrl.Length > 0 && IsReportEndpointReady;

    /// <summary>Asks the endpoint once per address whether it is there. Off the launch path, never blocking.</summary>
    private async Task ProbeReportEndpointAsync()
    {
        var url = ReportUrl;

        if (url.Length == 0 || string.Equals(url, _probedReportUrl, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(CanSendReport));
            return;
        }

        _probedReportUrl = url;
        var ready = await _reportSender.ProbeAsync(url);
        IsReportEndpointReady = ready;
        AppendConsole(ready ? $"[report] endpoint ready: {url}" : $"[report] endpoint not taking reports yet: {url}");
    }

    /// <summary>True while the "this is what will be sent" card is up.</summary>
    [ObservableProperty]
    private bool _isReportSendOpen;

    [ObservableProperty]
    private bool _isReportSending;

    /// <summary>A line from the player for the admin: what they did, what they saw.</summary>
    [ObservableProperty]
    private string _reportComment = string.Empty;

    /// <summary>What the zip holds, in words, so the player agrees to something concrete.</summary>
    public string ReportSendSummary => Localize(
        "Report_SendSummary",
        "The admin will get: launcher and game logs, the crash report, the launcher's freeze reports, the build's mod list, launcher settings, and what this machine is. Your nickname: {0}. Nothing else.",
        Username);

    [RelayCommand]
    private void OpenReportSend()
    {
        OnPropertyChanged(nameof(ReportSendSummary));
        IsReportSendOpen = true;
    }

    [RelayCommand]
    private void CancelReportSend() => IsReportSendOpen = false;

    /// <summary>Collects the zip and posts it. Only ever from the button on the card.</summary>
    [RelayCommand]
    private async Task SendSupportReportAsync()
    {
        if (IsReportSending || !CanSendReport)
        {
            return;
        }

        try
        {
            IsReportSending = true;
            Status = Localize("Report_Sending", "Sending the report…");

            var (zipPath, name) = WriteSupportReport();
            var version = _updates.CurrentVersion ?? "dev";
            var outcome = await _reportSender.SendAsync(ReportUrl, zipPath, Username, version, ReportComment.Trim(), _installId);

            if (outcome.Sent)
            {
                AppendConsole($"[report] sent {name} to {ReportUrl}: {outcome.Message}");
                Status = Localize("Report_Sent", "Report sent. The admin has it; the copy stayed in the reports folder.");
                IsReportSendOpen = false;
                ReportComment = string.Empty;
            }
            else
            {
                AppendConsole($"[report] not sent: {outcome.Message}");
                Status = Localize("Report_SendFailed", "Could not send the report ({0}). Use “Collect a report” and send the file by hand.", outcome.Message);
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[report] send failed: {ex}");
            Status = Localize("Report_SendFailed", "Could not send the report ({0}). Use “Collect a report” and send the file by hand.", ex.Message);
        }
        finally
        {
            IsReportSending = false;
        }
    }

    /// <summary>The zip itself, in the reports folder. Shared by the two buttons.</summary>
    private (string Path, string Name) WriteSupportReport()
    {
        {
            var directory = Path.Combine(_paths.Root, "reports");
            Directory.CreateDirectory(directory);

            var name = $"STlauncher-report-{DateTime.Now:yyyyMMdd-HHmm}.zip";
            var zipPath = Path.Combine(directory, name);

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddText(zip, "summary.txt", BuildSupportSummary());
                AddTail(zip, LauncherLogPath, "launcher.log");
                AddTail(zip, LauncherLogPath + ".old", "launcher.old.log");
                AddTail(zip, LastGameLogPath, "game.log");
                AddTail(zip, CrashReportPath, "crash-report.txt");
                AddTail(zip, Path.Combine(_paths.Root, "crash.log"), "launcher-crash.log");
                AddTail(zip, Path.Combine(_paths.Root, "settings.json"), "settings.json");

                // The launcher's own health: interface freezes and how the last start went.
                AddFreezeReports(zip);
                AddTail(zip, Path.Combine(_paths.Root, STlauncher.Core.Diagnostics.StartupGuard.FileName), "startup-state.json");

                if (SelectedInstance is not null)
                {
                    AddInstanceDefinition(zip, _instances.DefinitionPath(SelectedInstance.Id));
                }
            }

            return (zipPath, name);
        }
    }

    /// <summary>Whatever text is in the clipboard, or null when the window is not there to ask.</summary>
    private static async Task<string?> ReadClipboardAsync()
    {
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? window.Clipboard
            : null;

        return clipboard is null ? null : await clipboard.TryGetTextAsync();
    }

    private string BuildSupportSummary()
    {
        var process = Process.GetCurrentProcess();
        var memory = GC.GetGCMemoryInfo();
        var sb = new StringBuilder();

        sb.AppendLine($"STlauncher {_updates.CurrentVersion ?? "dev"} support report, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        sb.AppendLine();
        sb.AppendLine("[machine]");
        sb.AppendLine($"os = {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"runtime = {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"cpu cores = {Environment.ProcessorCount}");
        sb.AppendLine($"ram total = {memory.TotalAvailableMemoryBytes / 1024 / 1024} MB");
        sb.AppendLine($"launcher working set = {process.WorkingSet64 / 1024 / 1024} MB, managed heap = {GC.GetTotalMemory(false) / 1024 / 1024} MB");
        sb.AppendLine($"data folder = {_paths.Root}");
        sb.AppendLine();
        sb.AppendLine("[settings]");
        sb.AppendLine($"language = {Language}, theme = {(IsThemeLight ? "light" : "dark")}");
        sb.AppendLine($"memory for the game = {(int)MaxMemoryMb} MB, recommended = {RecommendedMemoryMb} MB");
        sb.AppendLine($"java = {SelectedJavaChoice?.Display ?? "automatic"}");
        sb.AppendLine($"backups = {(BackupsEnabled ? "on" : "off")}");
        sb.AppendLine();
        sb.AppendLine("[build]");

        if (SelectedInstance is { } instance)
        {
            sb.AppendLine($"name = {instance.Name}");
            sb.AppendLine($"game = {instance.VersionId}, loader = {instance.Loader} {instance.LoaderVersion}");
            sb.AppendLine($"catalog build = {instance.CatalogBuildId ?? "-"}");
            sb.AppendLine($"external folder = {instance.ExternalGameDirectory ?? "-"}");
            sb.AppendLine($"last played = {instance.LastPlayedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-"}");
            sb.AppendLine($"last exit code = {_crashExitCode}");
            sb.AppendLine();
            sb.AppendLine("[mods]");

            foreach (var mod in InstalledMods.Where(m => m.IsMod))
            {
                sb.AppendLine($"{(mod.Enabled ? "on " : "off")} {mod.FileName}{(mod.IsCatalog ? "  (catalog)" : string.Empty)}");
            }
        }
        else
        {
            sb.AppendLine("no build selected");
        }

        if (BuildIssues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[build check]");

            foreach (var issue in BuildIssues)
            {
                sb.AppendLine(issue.Title);
            }
        }

        if (NetworkChecks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[network]");

            foreach (var item in NetworkChecks)
            {
                sb.AppendLine(item.Result is null ? $"{item.Display}: pending" : $"{item.Display}: {item.Result}");
            }
        }

        return sb.ToString();
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>
    /// The build's definition without "where you played": that list holds the addresses
    /// of servers the player joined, a friend's home among them, and the admin of one
    /// server has no business knowing the others.
    /// </summary>
    private static void AddInstanceDefinition(ZipArchive zip, string path)
    {
        try
        {
            if (!File.Exists(path) || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject definition)
            {
                return;
            }

            definition.Remove("playPlaces");
            AddText(zip, "instance.json", definition.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Unreadable as JSON: left out rather than sent as it is.
        }
    }

    /// <summary>The end of a file, up to the limit; a missing file is simply left out.</summary>
    private static void AddTail(ZipArchive zip, string? path, string entryName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (source.Length > LogTailBytes)
            {
                source.Seek(-LogTailBytes, SeekOrigin.End);
            }

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        catch (Exception)
        {
            // A locked or vanished log is not a reason to lose the rest of the report.
        }
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? window.Clipboard
            : null;

        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private static void RevealInFileManager(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // Showing the file is a courtesy; the path is in the clipboard anyway.
        }
    }
}
