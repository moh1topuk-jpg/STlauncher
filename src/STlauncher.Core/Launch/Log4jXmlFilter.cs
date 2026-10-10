using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Launch;

/// <summary>
/// Turns the XML that Mojang's logging configuration prints back into the plain lines the
/// game prints without it. The configuration is what closes Log4Shell on old versions, and
/// it writes every message as a &lt;log4j:Event&gt; block; the console, the start markers
/// and the crash analyzer all read "[12:00:00] [main/INFO]: text". One instance per stream:
/// an event spans several lines.
/// </summary>
public sealed partial class Log4jXmlFilter
{
    /// <summary>An event that never closes must not hold the output back for ever.</summary>
    private const int MaxEventChars = 256 * 1024;

    private readonly StringBuilder _event = new();
    private bool _inEvent;

    [GeneratedRegex(@"<log4j:Event\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex EventOpen();

    [GeneratedRegex(@"(\w+)=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<log4j:Message>(.*?)</log4j:Message>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Message();

    [GeneratedRegex(@"<log4j:Throwable>(.*?)</log4j:Throwable>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Throwable();

    /// <summary>Takes one line of output and returns the plain lines it completes, if any.</summary>
    public IReadOnlyList<string> Feed(string line)
    {
        if (!_inEvent)
        {
            if (!line.Contains("<log4j:Event", StringComparison.Ordinal))
            {
                return new[] { line };
            }

            _inEvent = true;
            _event.Clear();
        }

        _event.Append(line).Append('\n');

        if (line.Contains("</log4j:Event>", StringComparison.Ordinal))
        {
            _inEvent = false;
            return Render(_event.ToString());
        }

        if (_event.Length > MaxEventChars)
        {
            // Not an event after all, or a broken one: hand the text over as it came.
            _inEvent = false;
            return _event.ToString().TrimEnd('\n').Split('\n');
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> Render(string xml)
    {
        string level = "INFO", thread = "main", time = string.Empty;

        if (EventOpen().Match(xml) is { Success: true } open)
        {
            foreach (Match attribute in Attribute().Matches(open.Value))
            {
                switch (attribute.Groups[1].Value)
                {
                    case "level":
                        level = attribute.Groups[2].Value;
                        break;
                    case "thread":
                        thread = Unescape(attribute.Groups[2].Value);
                        break;
                    case "timestamp" when long.TryParse(attribute.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms):
                        time = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                        break;
                }
            }
        }

        var lines = new List<string>();
        var prefix = time.Length > 0 ? $"[{time}] [{thread}/{level}]: " : $"[{thread}/{level}]: ";
        var message = Message().Match(xml) is { Success: true } m ? Text(m.Groups[1].Value) : string.Empty;

        var first = true;

        foreach (var part in message.Split('\n'))
        {
            lines.Add(first ? prefix + part.TrimEnd('\r') : part.TrimEnd('\r'));
            first = false;
        }

        if (Throwable().Match(xml) is { Success: true } t)
        {
            foreach (var part in Text(t.Groups[1].Value).TrimEnd().Split('\n'))
            {
                lines.Add(part.TrimEnd('\r'));
            }
        }

        return lines;
    }

    /// <summary>The inside of an element: CDATA sections joined, or escaped text unescaped.</summary>
    private static string Text(string inner)
    {
        if (!inner.Contains("<![CDATA[", StringComparison.Ordinal))
        {
            return Unescape(inner.Trim());
        }

        // "]]>" inside a message is written as two sections; joining them restores it.
        var text = new StringBuilder();
        var at = 0;

        while (true)
        {
            var start = inner.IndexOf("<![CDATA[", at, StringComparison.Ordinal);

            if (start < 0)
            {
                break;
            }

            start += "<![CDATA[".Length;
            var end = inner.IndexOf("]]>", start, StringComparison.Ordinal);

            if (end < 0)
            {
                text.Append(inner, start, inner.Length - start);
                break;
            }

            text.Append(inner, start, end - start);
            at = end + 3;
        }

        return text.ToString();
    }

    private static string Unescape(string text)
        => text.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
}
