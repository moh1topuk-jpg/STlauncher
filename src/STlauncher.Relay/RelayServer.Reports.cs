using System.Net;
using System.Net.Sockets;
using System.Text;

namespace STlauncher.Relay;

/// <summary>
/// Crash reports: a player presses "send to the admin" and, besides the owner's Telegram,
/// the redacted report is left here for thirty days so the developer can read it on the
/// server. The relay only keeps them; nothing asks for a report back over the network.
///
/// On the same port, in the same style - one ASCII line at a time:
///   REPORT BEGIN          answered OK, or ERR NOREPORT (this relay keeps no reports) or
///                         ERR LIMIT (too many from this address this hour).
///   REPORT + chunk        base64 of the report's UTF-8 bytes, up to 200 characters a
///                         line (whole groups of four). Not answered.
///   REPORT END            answered REPORTED R-XXXXX, or ERR TOOBIG, ERR BADDATA (not
///                         base64, not UTF-8, or not text), ERR FULL, ERR FAILED.
///
/// A relay from before this answers REPORT BEGIN with ERR BADCMD, and the launcher sends
/// the report to Telegram alone, as it always did.
/// </summary>
public sealed partial class RelayServer
{
    /// <summary>Base64 characters in one line: 150 bytes of the report.</summary>
    private const int ReportChunk = 200;

    /// <summary>A megabyte on a slow upstream: the whole upload has this long.</summary>
    private static readonly TimeSpan ReportUploadTime = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan ReportSweepEvery = TimeSpan.FromDays(1);

    private ReportStore? _reports;
    private HourlyLimit? _reportLimit;

    /// <summary>How many reports are kept right now; zero when the relay keeps none.</summary>
    public int StoredReports => _reports?.Count ?? 0;

    private void StartReports()
    {
        if (string.IsNullOrWhiteSpace(_options.ReportDirectory))
        {
            return;
        }

        var store = new ReportStore(_options.ReportDirectory, _options.ReportMaxTotalBytes, _options.ReportLife);

        try
        {
            var expired = store.Load();
            _log?.Invoke($"reports: {store.Count} kept, {store.TotalBytes} bytes, {expired} expired and removed");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"reports: off, the directory cannot be used ({ex.GetType().Name})");
            return;
        }

        _reports = store;
        _reportLimit = new HourlyLimit(_options.ReportsPerHour);
        _ = Task.Run(() => SweepReportsAsync(store));
    }

    private async Task SweepReportsAsync(ReportStore store)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ReportSweepEvery, _stop.Token).ConfigureAwait(false);

                var expired = store.Sweep();
                _log?.Invoke($"reports: {store.Count} kept, {store.TotalBytes} bytes, {expired} expired and removed");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeReportAsync(Socket socket, IPAddress gateKey)
    {
        if (_reports is not { } store)
        {
            await SendLineAsync(socket, "ERR NOREPORT").ConfigureAwait(false);
            return;
        }

        if (!_reportLimit!.TryTake(gateKey))
        {
            await SendLineAsync(socket, "ERR LIMIT").ConfigureAwait(false);
            return;
        }

        if (!await SendLineAsync(socket, "OK").ConfigureAwait(false))
        {
            return;
        }

        var max = _options.ReportMaxBytes;
        using var bytes = new MemoryStream();
        var decoded = new byte[ReportChunk];
        string? refusal = null;

        // Past the limit the rest is still read, a little way, and thrown away, so the
        // client hears why instead of a reset.
        var linesLeft = (max / (ReportChunk / 4 * 3) * 2) + 64;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(ReportUploadTime);
        var reader = new LineReader(socket, _options.MaxLineLength);

        while (true)
        {
            var line = await reader.ReadAsync(limit.Token).ConfigureAwait(false);

            if (line is null || --linesLeft < 0)
            {
                return;
            }

            if (line == "REPORT END")
            {
                break;
            }

            if (!line.StartsWith("REPORT + ", StringComparison.Ordinal) || line.Length == 9 || line.Length - 9 > ReportChunk)
            {
                return;
            }

            if (refusal is not null)
            {
                continue;
            }

            if (!Convert.TryFromBase64Chars(line.AsSpan(9), decoded, out var written))
            {
                refusal = "ERR BADDATA";
                continue;
            }

            if (bytes.Length + written > max)
            {
                refusal = "ERR TOOBIG";
                bytes.SetLength(0);
                continue;
            }

            bytes.Write(decoded, 0, written);
        }

        var body = bytes.ToArray();

        if (refusal is null && !IsReportText(body))
        {
            refusal = "ERR BADDATA";
        }

        if (refusal is not null)
        {
            _log?.Invoke($"report refused: {refusal[4..].ToLowerInvariant()}");
            await SendLineAsync(socket, refusal).ConfigureAwait(false);
            return;
        }

        switch (store.Put(body, out var id))
        {
            case ReportOutcome.Stored:
                // The id is not a secret: nothing can be fetched by it over the network,
                // and the player quotes it so that the developer finds the file.
                _log?.Invoke($"report {id} bytes={body.Length}");
                await SendLineAsync(socket, "REPORTED " + id).ConfigureAwait(false);
                break;

            case ReportOutcome.Full:
                _log?.Invoke("report refused: full");
                await SendLineAsync(socket, "ERR FULL").ConfigureAwait(false);
                break;

            default:
                _log?.Invoke("report refused: the disk did not take it");
                await SendLineAsync(socket, "ERR FAILED").ConfigureAwait(false);
                break;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Readable text and nothing else: valid UTF-8, not empty, and no control characters
    /// but tabs and line breaks. A zip, an image or an executable fails on the first count
    /// or the last.
    /// </summary>
    public static bool IsReportText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return false;
        }

        string text;

        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
            {
                return false;
            }

            if (c == 0x7F)
            {
                return false;
            }
        }

        return true;
    }
}
