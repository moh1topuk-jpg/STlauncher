using System;
using System.Text;
using System.Text.Json;

namespace STlauncher.Core.Diagnostics;

/// <summary>
/// The mclo.gs paste service, as its documentation at https://api.mclo.gs describes it:
/// POST /1/log with a <c>content</c> field answers <c>{"success":true,"id":"…","url":"https://mclo.gs/…"}</c>
/// or <c>{"success":false,"error":"…"}</c>. Only the shapes live here; the request is the caller's.
/// </summary>
public static class LogShare
{
    public const string Endpoint = "https://api.mclo.gs/1/log";

    /// <summary>The service's limits, from its /1/limits: 10 MiB and 25 000 lines. It cuts what is over; we cut first.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    public const int MaxLines = 25000;

    /// <summary>
    /// Fits a text into the limits by dropping lines from the middle: the head says what
    /// was running, the tail says how it ended.
    /// </summary>
    public static string Fit(string text, int maxBytes = MaxBytes, int maxLines = MaxLines)
    {
        text ??= string.Empty;

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes && CountLines(text) <= maxLines)
        {
            return text;
        }

        var lines = text.Split('\n');
        const string cut = "… lines left out to fit the size limit …";
        var headCount = Math.Min(lines.Length, Math.Min(200, maxLines / 4));
        var head = new StringBuilder();
        var budget = maxBytes - Encoding.UTF8.GetByteCount(cut) - 2;

        for (var i = 0; i < headCount; i++)
        {
            var size = Encoding.UTF8.GetByteCount(lines[i]) + 1;

            if (size > budget / 4)
            {
                headCount = i;
                break;
            }

            budget -= size;
            head.Append(lines[i]).Append('\n');
        }

        // The tail takes what is left, counted from the end.
        var first = lines.Length;
        var room = maxLines - headCount - 1;

        while (first > headCount && room > 0)
        {
            var size = Encoding.UTF8.GetByteCount(lines[first - 1]) + 1;

            if (size > budget)
            {
                break;
            }

            budget -= size;
            room--;
            first--;
        }

        return head.Append(cut).Append('\n').Append(string.Join('\n', lines, first, lines.Length - first)).ToString();
    }

    /// <summary>The link from the service's answer, or the reason there is none.</summary>
    public static bool TryReadLink(string? json, out string link, out string error)
    {
        link = string.Empty;
        error = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "unexpected answer";
                return false;
            }

            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
            {
                link = parsed.ToString();
                return true;
            }

            error = root.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString() ?? "unknown error"
                : "unexpected answer";
            return false;
        }
        catch (JsonException)
        {
            error = "unexpected answer";
            return false;
        }
    }

    private static int CountLines(string text)
    {
        var count = 1;

        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }
}
