using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Diagnostics;

namespace STlauncher.Core.Friends;

/// <summary>Why a report did not reach the relay. None of these is worth troubling the player with.</summary>
public enum CrashReportUploadFailure
{
    None,

    /// <summary>The relay did not answer.</summary>
    Unreachable,

    /// <summary>A relay from before reports (ERR BADCMD), or one that keeps none (ERR NOREPORT).</summary>
    Unsupported,

    /// <summary>Too many from this address this hour.</summary>
    RateLimited,

    /// <summary>The relay's store is full or its disk refused.</summary>
    Busy,

    /// <summary>Anything else the relay said.</summary>
    Rejected
}

/// <summary>One part of the text report: a heading and what goes under it.</summary>
/// <param name="KeepTail">
/// True for a log: when the report has to be cut, this part loses its beginning and keeps
/// its end, where the trouble is. False for what must stay whole if at all possible: the
/// summary, the player's words, the crash report.
/// </param>
public sealed record CrashReportSection(string Name, string Text, bool KeepTail = false);

/// <summary>
/// The crash report as one text, left with the launcher's server for the developer, beside
/// the zip that goes to the owner's Telegram. Same contents, but as text and redacted: the
/// home folder, the Windows account name, tokens, uuids and other people's addresses come
/// out (<see cref="LogRedactor"/>). The relay keeps it thirty days and never gives it back
/// over the network; the player gets an id ("R-7KQ2M") to quote.
/// </summary>
public static class CrashReportUpload
{
    /// <summary>What the relay keeps at most, less a little: logs are cut to fit, never refused.</summary>
    public const int MaxBytes = (1024 * 1024) - 4096;

    /// <summary>Bytes of the report in one line: 150 bytes make 200 base64 characters, the relay's limit.</summary>
    private const int ChunkBytes = 150;

    /// <summary>The report as the relay will keep it: redacted, in order, within <paramref name="maxBytes"/> of UTF-8.</summary>
    public static string Compose(
        string header,
        IReadOnlyList<CrashReportSection> sections,
        LogRedactionContext? redaction,
        int maxBytes = MaxBytes)
    {
        // One kind of line break in the kept file, whatever Windows wrote.
        string Clean(string text) => (redaction is null ? text : LogRedactor.Redact(text, redaction)).Replace("\r\n", "\n");

        var head = Clean(header).TrimEnd() + "\n";
        var parts = sections
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => (Title: $"\n===== {s.Name} =====\n", Body: Clean(s.Text).TrimEnd() + "\n", s.KeepTail))
            .ToList();

        // Each part may get a note that it was cut: room for those is set aside first.
        var fixedBytes = Bytes(head) + parts.Sum(p => Bytes(p.Title) + 64);
        var whole = parts.Where(p => !p.KeepTail).Sum(p => Bytes(p.Body));
        var tails = parts.Where(p => p.KeepTail).Sum(p => Bytes(p.Body));

        // Room for the logs once the rest is in. The logs share it in proportion to their
        // size, so a huge game log does not push the launcher's log out entirely.
        var forTails = Math.Max(0, maxBytes - fixedBytes - whole);
        var forWhole = Math.Max(0, maxBytes - fixedBytes);

        var text = new StringBuilder(head);

        foreach (var (title, body, keepTail) in parts)
        {
            text.Append(title);
            var size = Bytes(body);

            if (keepTail)
            {
                var budget = tails <= forTails ? size : (int)((long)forTails * size / Math.Max(1, tails));
                text.Append(size <= budget ? body : Tail(body, budget));
            }
            else
            {
                var budget = whole <= forWhole ? size : (int)((long)forWhole * size / Math.Max(1, whole));
                text.Append(size <= budget ? body : Head(body, budget));
            }
        }

        // The markers added by the cuts are a few dozen bytes; a last cut, if they tipped it over.
        var result = text.ToString();
        return Bytes(result) <= maxBytes ? result : Head(result, maxBytes);
    }

    /// <summary>Leaves the report with the relay.</summary>
    /// <returns>The id the relay gave ("R-XXXXX"), or null with the reason.</returns>
    public static async Task<(string? Id, CrashReportUploadFailure Failure)> SendAsync(
        RelayEndpoint relay,
        string text,
        CancellationToken cancellationToken = default,
        RelayClientOptions? options = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);

        if (bytes.Length == 0 || bytes.Length > MaxBytes + 4096)
        {
            return (null, CrashReportUploadFailure.Rejected);
        }

        options ??= new RelayClientOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AnswerTimeout = TimeSpan.FromSeconds(8)
        };

        try
        {
            using var client = await RelayWire.DialAsync(relay, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            // Asked first and alone: a relay from before reports says ERR BADCMD and hangs
            // up, and a megabyte written after that would only reset the connection.
            var begin = await RelayWire.AskAsync(stream, "REPORT BEGIN", options.AnswerTimeout, cancellationToken).ConfigureAwait(false);

            if (begin != "OK")
            {
                return (null, FailureOf(begin));
            }

            var body = new StringBuilder((bytes.Length * 4 / 3) + (bytes.Length / ChunkBytes * 10) + 32);

            for (var at = 0; at < bytes.Length; at += ChunkBytes)
            {
                body.Append("REPORT + ").Append(Convert.ToBase64String(bytes, at, Math.Min(ChunkBytes, bytes.Length - at))).Append('\n');
            }

            body.Append("REPORT END\n");

            // A megabyte and a half up a slow line: more time than a handshake, less than the relay gives.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(60));

            await stream.WriteAsync(Encoding.ASCII.GetBytes(body.ToString()), limit.Token).ConfigureAwait(false);
            await stream.FlushAsync(limit.Token).ConfigureAwait(false);

            var answer = await RelayWire.ReadLineAsync(stream, limit.Token).ConfigureAwait(false);

            if (answer is not null && answer.StartsWith("REPORTED ", StringComparison.Ordinal))
            {
                var id = answer[9..].Trim();
                return IsId(id) ? (id, CrashReportUploadFailure.None) : (null, CrashReportUploadFailure.Rejected);
            }

            return (null, FailureOf(answer));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, CrashReportUploadFailure.Unreachable);
        }
    }

    /// <summary>"R-" and five characters of the invite alphabet.</summary>
    public static bool IsId(string? text)
    {
        if (text is null || text.Length != 7 || !text.StartsWith("R-", StringComparison.Ordinal))
        {
            return false;
        }

        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        return text.Skip(2).All(c => alphabet.IndexOf(c) >= 0);
    }

    private static CrashReportUploadFailure FailureOf(string? answer) => answer switch
    {
        null => CrashReportUploadFailure.Unreachable,
        "ERR BADCMD" or "ERR NOREPORT" => CrashReportUploadFailure.Unsupported,
        "ERR LIMIT" => CrashReportUploadFailure.RateLimited,
        "ERR BUSY" or "ERR FULL" or "ERR FAILED" => CrashReportUploadFailure.Busy,
        _ => CrashReportUploadFailure.Rejected
    };

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>The end of a text within a byte budget, from a line start, with a note of what went.</summary>
    private static string Tail(string text, int budget)
    {
        const string note = "[… the beginning is cut to fit …]\n";
        budget -= Bytes(note);

        if (budget <= 0)
        {
            return note;
        }

        var start = text.Length;
        var used = 0;

        while (start > 0)
        {
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(start - 1, 1));

            if (char.IsLowSurrogate(text[start - 1]) && start >= 2)
            {
                size = Encoding.UTF8.GetByteCount(text.AsSpan(start - 2, 2));

                if (used + size > budget)
                {
                    break;
                }

                used += size;
                start -= 2;
                continue;
            }

            if (used + size > budget)
            {
                break;
            }

            used += size;
            start--;
        }

        var newline = text.IndexOf('\n', start);

        if (start > 0 && newline >= 0 && newline + 1 < text.Length)
        {
            start = newline + 1;
        }

        return note + text[start..];
    }

    /// <summary>The start of a text within a byte budget, with a note of what went.</summary>
    private static string Head(string text, int budget)
    {
        const string note = "\n[… the rest is cut to fit …]\n";
        budget -= Bytes(note);

        if (budget <= 0)
        {
            return note.TrimStart('\n');
        }

        var end = 0;
        var used = 0;

        while (end < text.Length)
        {
            var width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(end, width));

            if (used + size > budget)
            {
                break;
            }

            used += size;
            end += width;
        }

        return text[..end] + note;
    }
}
