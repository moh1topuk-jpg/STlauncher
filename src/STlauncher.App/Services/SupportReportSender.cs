using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.App.Services;

/// <summary>What the report endpoint answered, in words the status line can show.</summary>
public sealed record SupportReportOutcome(bool Sent, string Message);

/// <summary>
/// Posts a support report to the server owner's endpoint, which forwards it to their
/// Telegram. One multipart request: the zip and a few fields for the caption. Nothing
/// is sent without the player pressing the button, and the launcher never retries on
/// its own - a report is not telemetry.
/// </summary>
public sealed class SupportReportSender
{
    /// <summary>Above this the endpoint refuses, and Telegram would too.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;

    public SupportReportSender(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<SupportReportOutcome> SendAsync(
        string url,
        string zipPath,
        string nickname,
        string version,
        string comment,
        string installId,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(zipPath);

        if (!info.Exists)
        {
            return new SupportReportOutcome(false, "the report file is missing");
        }

        if (info.Length > MaxBytes)
        {
            return new SupportReportOutcome(false, $"the report is too large ({info.Length / 1024 / 1024} MB)");
        }

        using var form = new MultipartFormDataContent();
        // Quoted names: .NET leaves plain tokens bare, and not every multipart parser takes that.
        form.Add(new StringContent(nickname), "\"nick\"");
        form.Add(new StringContent(version), "\"version\"");
        form.Add(new StringContent(comment), "\"comment\"");
        form.Add(new StringContent(installId), "\"install\"");

        await using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "\"file\"", "\"" + info.Name + "\"");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Add("X-STlauncher", version);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? new SupportReportOutcome(true, body.Trim())
            : new SupportReportOutcome(false, $"{(int)response.StatusCode}: {Truncate(body)}");
    }

    /// <summary>
    /// Publishes a text at mclo.gs and returns the link. Whoever calls this has already
    /// redacted the text and has the player's click: the paste is readable by anyone.
    /// </summary>
    public async Task<SupportReportOutcome> ShareLogAsync(string text, CancellationToken cancellationToken = default)
    {
        using var form = new FormUrlEncodedContent(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("content", STlauncher.Core.Diagnostics.LogShare.Fit(text))
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        using var response = await _http.PostAsync(STlauncher.Core.Diagnostics.LogShare.Endpoint, form, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (STlauncher.Core.Diagnostics.LogShare.TryReadLink(body, out var link, out var error))
        {
            return new SupportReportOutcome(true, link);
        }

        return new SupportReportOutcome(false, response.IsSuccessStatusCode ? error : $"{(int)response.StatusCode}: {error}");
    }

    /// <summary>
    /// True when the endpoint is there to take reports: it answers a GET with 200. An
    /// older mirror answers 405, and the button stays hidden rather than failing.
    /// </summary>
    public async Task<bool> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}
