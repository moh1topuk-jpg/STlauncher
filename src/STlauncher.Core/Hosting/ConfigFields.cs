using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Hosting;

public enum ConfigValueKind
{
    /// <summary>Any one line of text.</summary>
    Text,

    /// <summary>true or false: a switch on the screen.</summary>
    Bool,

    Number,

    /// <summary>Something written in the file's own syntax - a list, a date, a string with escapes - and edited as it stands.</summary>
    Raw
}

/// <summary>One setting of a file, as a labelled field.</summary>
/// <param name="Line">The line of the file the value is on; a field is addressed by it.</param>
/// <param name="Section">The table or object the key is in, dotted; empty at the top.</param>
/// <param name="Comment">What the file says above the key, without the comment marks; empty when nothing.</param>
public sealed record ConfigField(int Line, string Section, string Key, string Value, string Comment, ConfigValueKind Kind);

/// <summary>
/// A settings file read as a list of fields: .properties, and .toml or .json written one
/// key to a line. This is not a parser of those formats, and does not try to be: it
/// reads lines, and the moment a file has something a line reader cannot be sure of - a
/// value over several lines, a table array, a comment inside a value - it gives up and
/// the file is edited as text. Applying new values rewrites only the value on its own
/// line, so comments, order, blank lines and anything it did not understand stay as they were.
/// </summary>
public sealed partial class ConfigFieldDocument
{
    /// <summary>More fields than this is not a form any more; the file opens as text.</summary>
    private const int MaxFields = 400;

    private enum Style
    {
        /// <summary>Written as typed: a .properties value, a number, true/false, raw syntax.</summary>
        Bare,
        TomlBasic,
        TomlLiteral,
        JsonString
    }

    private sealed record Slot(ConfigField Field, string Prefix, string Suffix, Style Style);

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions LenientJson = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private readonly ConfigFormat _format;
    private readonly string[] _lines;
    private readonly List<Slot> _slots;

    private ConfigFieldDocument(ConfigFormat format, string[] lines, List<Slot> slots)
    {
        _format = format;
        _lines = lines;
        _slots = slots;
        Fields = slots.Select(s => s.Field).ToList();
    }

    public IReadOnlyList<ConfigField> Fields { get; }

    [GeneratedRegex(@"^[+-]?\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?$")]
    private static partial Regex TomlNumber();

    [GeneratedRegex(@"^-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?$")]
    private static partial Regex JsonNumber();

    [GeneratedRegex(@"^(\s*)((?:[A-Za-z0-9_\-]+|""[^""]*""|'[^']*')(?:\s*\.\s*(?:[A-Za-z0-9_\-]+|""[^""]*""|'[^']*'))*)(\s*=\s*)(.*)$")]
    private static partial Regex TomlKeyLine();

    [GeneratedRegex(@"^(\s*""((?:[^""\\]|\\.)*)""\s*:\s*)(.*)$")]
    private static partial Regex JsonKeyLine();

    [GeneratedRegex(@"^""(?:[^""\\]|\\.)*""$")]
    private static partial Regex JsonStringToken();

    /// <summary>
    /// The file as fields, or null when it is not a format read this way, has nothing to
    /// show, or has anything in it the line reader is not sure about.
    /// </summary>
    public static ConfigFieldDocument? TryParse(ConfigFormat format, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Split on "\n" only: a "\r" before it stays at the end of its line and goes back
        // out untouched, so a file keeps the line endings it came with.
        var lines = text!.Split('\n');

        var slots = format switch
        {
            ConfigFormat.Properties => ReadProperties(lines),
            ConfigFormat.Toml => ReadToml(lines),
            ConfigFormat.Json or ConfigFormat.Json5 => ReadJson(text, lines),
            _ => null
        };

        return slots is null || slots.Count == 0 || slots.Count > MaxFields
            ? null
            : new ConfigFieldDocument(format, lines, slots);
    }

    /// <summary>True when the value can be written into this field's place without breaking the file.</summary>
    public bool Accepts(ConfigField field, string? value)
    {
        if (value is null || value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            return false;
        }

        switch (field.Kind)
        {
            case ConfigValueKind.Bool:
                return value.Trim() is "true" or "false";

            case ConfigValueKind.Number:
                return (_format == ConfigFormat.Toml ? TomlNumber() : JsonNumber()).IsMatch(value.Trim());

            case ConfigValueKind.Raw:
                return value.Trim().Length > 0 && (_format == ConfigFormat.Toml || IsJsonValue(value.Trim()));

            default:
                // A backslash at the end of a .properties line joins it with the next one.
                return _format != ConfigFormat.Properties || !EndsWithOddBackslashes(value);
        }
    }

    /// <summary>
    /// The file's text with the given values in place, keyed by <see cref="ConfigField.Line"/>.
    /// Fields that are not mentioned, or are given the value they already have, keep
    /// their line byte for byte.
    /// </summary>
    /// <exception cref="FormatException">A value does not fit its field; the message is the field's key.</exception>
    public string Apply(IReadOnlyDictionary<int, string> values)
    {
        var lines = (string[])_lines.Clone();

        foreach (var slot in _slots)
        {
            if (!values.TryGetValue(slot.Field.Line, out var value) || string.Equals(value, slot.Field.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (!Accepts(slot.Field, value))
            {
                throw new FormatException(slot.Field.Key);
            }

            lines[slot.Field.Line] = slot.Prefix + Token(slot, value) + slot.Suffix;
        }

        return string.Join('\n', lines);
    }

    private static string Token(Slot slot, string value) => slot.Style switch
    {
        Style.TomlBasic => TomlBasicString(value),

        // A literal string has no way to hold an apostrophe; it becomes a basic one.
        Style.TomlLiteral => value.Contains('\'') ? TomlBasicString(value) : "'" + value + "'",
        Style.JsonString => JsonSerializer.Serialize(value, RelaxedJson),
        _ => slot.Field.Kind == ConfigValueKind.Text ? value : value.Trim()
    };

    private static string TomlBasicString(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\t", "\\t") + "\"";

    // ===================== .properties =====================

    private static List<Slot>? ReadProperties(string[] lines)
    {
        var slots = new List<Slot>();
        var comment = new List<string>();

        for (var n = 0; n < lines.Length; n++)
        {
            var (body, end) = SplitEnd(lines[n]);
            var trimmed = body.TrimStart();

            if (trimmed.Length == 0)
            {
                comment.Clear();
                continue;
            }

            if (trimmed[0] is '#' or '!')
            {
                comment.Add(trimmed.TrimStart('#', '!').Trim());
                continue;
            }

            var start = body.Length - trimmed.Length;
            var i = start;

            while (i < body.Length && body[i] is not ('=' or ':') && !char.IsWhiteSpace(body[i]))
            {
                // An escaped character in a key: rare, and not worth guessing at.
                if (body[i] == '\\')
                {
                    return null;
                }

                i++;
            }

            if (i == start)
            {
                return null;
            }

            var key = body[start..i];
            var j = i;

            while (j < body.Length && body[j] is ' ' or '\t')
            {
                j++;
            }

            if (j < body.Length && body[j] is '=' or ':')
            {
                j++;

                while (j < body.Length && body[j] is ' ' or '\t')
                {
                    j++;
                }
            }

            var value = body[j..];

            // The value goes on to the next line: not one line, not one field.
            if (EndsWithOddBackslashes(value))
            {
                return null;
            }

            // A key standing alone has no separator yet; a value needs one before it.
            var prefix = j == i ? body + "=" : body[..j];

            // A .properties file has no types; a value written as a number is taken to be
            // one, so a port cannot be saved as a word the mod would fail to read.
            var kind = value is "true" or "false" ? ConfigValueKind.Bool
                : JsonNumber().IsMatch(value) ? ConfigValueKind.Number
                : ConfigValueKind.Text;

            slots.Add(new Slot(new ConfigField(n, string.Empty, key, value, TakeComment(comment), kind), prefix, end, Style.Bare));
        }

        return slots;
    }

    // ===================== .toml =====================

    private static List<Slot>? ReadToml(string[] lines)
    {
        var slots = new List<Slot>();
        var comment = new List<string>();
        var section = string.Empty;

        for (var n = 0; n < lines.Length; n++)
        {
            var (body, end) = SplitEnd(lines[n]);
            var trimmed = body.Trim();

            if (trimmed.Length == 0)
            {
                comment.Clear();
                continue;
            }

            if (trimmed[0] == '#')
            {
                comment.Add(trimmed.TrimStart('#').Trim());
                continue;
            }

            if (trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                return null;
            }

            if (trimmed[0] == '[')
            {
                var close = trimmed.IndexOf(']');

                if (close < 0 || !IsTomlTail(trimmed[(close + 1)..]))
                {
                    return null;
                }

                section = trimmed[1..close].Trim();
                comment.Clear();
                continue;
            }

            var match = TomlKeyLine().Match(body);

            if (!match.Success)
            {
                return null;
            }

            var prefix = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
            var rest = match.Groups[4].Value;
            var key = match.Groups[2].Value.Trim('"', '\'');

            if (rest.Length == 0 ||
                rest.StartsWith("\"\"\"", StringComparison.Ordinal) ||
                rest.StartsWith("'''", StringComparison.Ordinal))
            {
                return null;
            }

            string value;
            string tail;
            ConfigValueKind kind;
            var style = Style.Bare;

            if (rest[0] == '"')
            {
                var i = 1;

                while (i < rest.Length && rest[i] != '"')
                {
                    i += rest[i] == '\\' ? 2 : 1;
                }

                if (i >= rest.Length)
                {
                    return null;
                }

                var inner = rest[1..i];
                tail = rest[(i + 1)..];

                // With escapes inside, the string is shown and kept exactly as written.
                (value, kind, style) = inner.Contains('\\')
                    ? (rest[..(i + 1)], ConfigValueKind.Raw, Style.Bare)
                    : (inner, ConfigValueKind.Text, Style.TomlBasic);
            }
            else if (rest[0] == '\'')
            {
                var close = rest.IndexOf('\'', 1);

                if (close < 0)
                {
                    return null;
                }

                (value, kind, style) = (rest[1..close], ConfigValueKind.Text, Style.TomlLiteral);
                tail = rest[(close + 1)..];
            }
            else if (rest[0] is '[' or '{')
            {
                var close = ClosingBracket(rest);

                if (close < 0)
                {
                    return null;
                }

                (value, kind) = (rest[..(close + 1)], ConfigValueKind.Raw);
                tail = rest[(close + 1)..];
            }
            else
            {
                var hash = rest.IndexOf('#');
                value = (hash < 0 ? rest : rest[..hash]).TrimEnd();
                tail = rest[value.Length..];

                if (value.Length == 0)
                {
                    return null;
                }

                kind = value is "true" or "false" ? ConfigValueKind.Bool
                    : TomlNumber().IsMatch(value) ? ConfigValueKind.Number
                    : ConfigValueKind.Raw;
            }

            if (!IsTomlTail(tail))
            {
                return null;
            }

            slots.Add(new Slot(new ConfigField(n, section, key, value, TakeComment(comment), kind), prefix, tail + end, style));
        }

        return slots;
    }

    /// <summary>After a value on its line there may be only spaces and a comment.</summary>
    private static bool IsTomlTail(string tail)
    {
        var trimmed = tail.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    /// <summary>Where the list or inline table that opens the text closes, on this same line; -1 when it does not.</summary>
    private static int ClosingBracket(string text)
    {
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;

                case '[' or '{':
                    depth++;
                    break;

                case ']' or '}':
                    if (--depth == 0)
                    {
                        return i;
                    }

                    break;

                case '#':
                    return -1;
            }
        }

        return -1;
    }

    // ===================== .json =====================

    private static List<Slot>? ReadJson(string text, string[] lines)
    {
        try
        {
            using var document = JsonDocument.Parse(text, LenientJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
        }
        catch (JsonException)
        {
            // Not JSON as the mod would read it either (.json5 with bare keys, say): text.
            return null;
        }

        var slots = new List<Slot>();
        var comment = new List<string>();
        var path = new List<string>();
        var opened = false;

        for (var n = 0; n < lines.Length; n++)
        {
            var (body, end) = SplitEnd(lines[n]);
            var trimmed = body.Trim();

            if (trimmed.Length == 0)
            {
                comment.Clear();
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                comment.Add(trimmed.TrimStart('/').Trim());
                continue;
            }

            if (trimmed == "{")
            {
                if (opened)
                {
                    return null;
                }

                opened = true;
                continue;
            }

            if (trimmed is "}" or "},")
            {
                if (path.Count > 0)
                {
                    path.RemoveAt(path.Count - 1);
                }

                comment.Clear();
                continue;
            }

            var match = JsonKeyLine().Match(body);

            if (!opened || !match.Success)
            {
                return null;
            }

            var prefix = match.Groups[1].Value;
            var rest = match.Groups[3].Value;
            var token = rest.TrimEnd();

            if (token.EndsWith(','))
            {
                token = token[..^1].TrimEnd();
            }

            var suffix = rest[token.Length..] + end;
            var key = match.Groups[2].Value;

            if (key.Contains('\\'))
            {
                key = JsonSerializer.Deserialize<string>("\"" + key + "\"") ?? key;
            }

            if (token == "{")
            {
                path.Add(key);
                comment.Clear();
                continue;
            }

            string value;
            ConfigValueKind kind;
            var style = Style.Bare;

            if (token.StartsWith('"'))
            {
                if (!JsonStringToken().IsMatch(token))
                {
                    return null;
                }

                var decoded = JsonSerializer.Deserialize<string>(token) ?? string.Empty;

                // A string holding a line break cannot be a one-line field; it stays as written.
                (value, kind, style) = decoded.IndexOfAny(new[] { '\r', '\n' }) >= 0
                    ? (token, ConfigValueKind.Raw, Style.Bare)
                    : (decoded, ConfigValueKind.Text, Style.JsonString);
            }
            else if (token is "true" or "false")
            {
                (value, kind) = (token, ConfigValueKind.Bool);
            }
            else if (JsonNumber().IsMatch(token))
            {
                (value, kind) = (token, ConfigValueKind.Number);
            }
            else if (token.Length > 0 && IsJsonValue(token))
            {
                // null, or a list or object that fits on its line.
                (value, kind) = (token, ConfigValueKind.Raw);
            }
            else
            {
                return null;
            }

            slots.Add(new Slot(new ConfigField(n, string.Join('.', path), key, value, TakeComment(comment), kind), prefix, suffix, style));
        }

        return slots;
    }

    private static bool IsJsonValue(string token)
    {
        try
        {
            using var document = JsonDocument.Parse(token);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ===================== Shared =====================

    /// <summary>A line without the "\r" a Windows file ends it with, and that "\r" apart.</summary>
    private static (string Body, string End) SplitEnd(string line)
        => line.EndsWith('\r') ? (line[..^1], "\r") : (line, string.Empty);

    private static string TakeComment(List<string> comment)
    {
        var text = string.Join('\n', comment.Where(c => c.Length > 0));
        comment.Clear();
        return text;
    }

    private static bool EndsWithOddBackslashes(string value)
    {
        var count = 0;

        for (var i = value.Length - 1; i >= 0 && value[i] == '\\'; i--)
        {
            count++;
        }

        return count % 2 == 1;
    }
}
