using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.App.Services;

/// <summary>
/// One anonymous "I started" per launch, so the server owner knows how many people use
/// the launcher. What goes out: a random id this installation made up for itself, the
/// launcher version, the OS and the interface language. No nickname, no files, nothing
/// from the machine. The player can switch it off in the settings.
/// </summary>
public sealed class UsageReporter
{
    private readonly HttpClient _http;
    private bool _sent;

    public UsageReporter(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Sends the ping once per process. Failures are silent: this is bookkeeping, not a feature.</summary>
    public async Task ReportAsync(string? statsUrl, string installId, string version, string language, CancellationToken cancellationToken = default)
    {
        if (_sent || string.IsNullOrWhiteSpace(statsUrl) || string.IsNullOrWhiteSpace(installId))
        {
            return;
        }

        _sent = true;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var url = statsUrl!.TrimEnd('/') + "/ping";

            using var response = await _http.PostAsJsonAsync(url, new
            {
                id = installId,
                v = version,
                os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other",
                lang = language
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A collector without the endpoint, or no network: nothing to do about it.
        }
    }
}
