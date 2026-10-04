using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace STlauncher.Core.Hosting;

/// <summary>
/// <c>server.properties</c>, edited in place. The file belongs to the game and to the
/// player as much as to the launcher: it holds dozens of keys the launcher knows nothing
/// about, and people leave themselves comments in it. So a line that was not changed is
/// written back exactly as it was read, and only the keys that were set are rewritten.
/// </summary>
public sealed class ServerProperties
{
    public const string FileName = "server.properties";

    public const string PortKey = "server-port";
    public const string MotdKey = "motd";
    public const string OnlineModeKey = "online-mode";
    public const string WhitelistKey = "white-list";
    public const string EnforceWhitelistKey = "enforce-whitelist";
    public const string LevelNameKey = "level-name";
    public const string MaxPlayersKey = "max-players";
    public const string HideOnlinePlayersKey = "hide-online-players";

    /// <summary>The folder the game keeps the world in unless <c>level-name</c> says otherwise.</summary>
    public const string DefaultLevelName = "world";

    private readonly List<Line> _lines = new();
    private string _newLine = "\n";

    public static ServerProperties Parse(string? text)
    {
        var result = new ServerProperties();

        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            result._newLine = "\r\n";
        }

        var physical = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // The split leaves one empty piece after the final line break; it is not a line.
        var count = physical.Length > 0 && physical[^1].Length == 0 ? physical.Length - 1 : physical.Length;

        for (var i = 0; i < count; i++)
        {
            var raw = physical[i];
            var logical = raw;

            // A trailing backslash carries the entry onto the next line. The game never
            // writes these, but a hand-edited file may have them.
            if (!IsCommentOrBlank(raw))
            {
                var joined = new StringBuilder();
                var rawJoined = new StringBuilder(raw);
                var current = raw;

                while (EndsWithContinuation(current) && i + 1 < count)
                {
                    joined.Append(current, 0, current.Length - 1);
                    i++;
                    current = physical[i].TrimStart(' ', '\t', '\f');
                    rawJoined.Append('\n').Append(physical[i]);
                }

                joined.Append(current);
                logical = joined.ToString();
                raw = rawJoined.ToString();
            }

            result._lines.Add(ParseLine(raw, logical));
        }

        return result;
    }

    /// <summary>A missing file is an empty set of properties: the game fills in the rest.</summary>
    public static ServerProperties Load(string path)
        => File.Exists(path) ? Parse(ReadText(path)) : new ServerProperties();

    public static string PathIn(string serverDirectory) => Path.Combine(serverDirectory, FileName);

    public IReadOnlyList<string> Keys
        => _lines.Where(l => l.Key is not null).Select(l => l.Key!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The value of a key, or null when the file does not have it.</summary>
    public string? Get(string key)
    {
        // Java keeps the last one when a key is repeated, and so does the game.
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_lines[i].Key, key, StringComparison.Ordinal))
            {
                return _lines[i].Value;
            }
        }

        return null;
    }

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback)
        => bool.TryParse(Get(key)?.Trim(), out var value) ? value : fallback;

    /// <summary>Changes a key where it stands, or adds it at the end.</summary>
    public void Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A property needs a key.", nameof(key));
        }

        value ??= string.Empty;

        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(_lines[i].Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            // Untouched when the value is already this: the line keeps its own spelling.
            if (!string.Equals(_lines[i].Value, value, StringComparison.Ordinal))
            {
                _lines[i] = new Line(Escape(key, isKey: true) + "=" + Escape(value, isKey: false), key, value);
            }

            return;
        }

        _lines.Add(new Line(Escape(key, isKey: true) + "=" + Escape(value, isKey: false), key, value));
    }

    public void Set(string key, bool value) => Set(key, value ? "true" : "false");

    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// What a server for friends starts from. The launcher's accounts are offline ones, so
    /// the server cannot check them with Mojang; the whitelist is what keeps strangers out
    /// instead, and it is enforced so that taking a name off it takes effect at once.
    /// </summary>
    public void ApplyFriendsDefaults(string serverName, int port)
    {
        Set(OnlineModeKey, false);
        Set(WhitelistKey, true);
        Set(EnforceWhitelistKey, true);

        // In offline mode a nickname on the list is all a player is known by, and the
        // server's answer to a ping names whoever is online. Anybody can ping an address
        // that is open to the internet, so the names are kept out of that answer; the
        // count of players is still given. Servers before 1.18 do not know the key and
        // leave it alone.
        Set(HideOnlinePlayersKey, true);
        Set(PortKey, port);
        Set(MotdKey, serverName ?? string.Empty);
    }

    public override string ToString()
    {
        var builder = new StringBuilder();

        foreach (var line in _lines)
        {
            builder.Append(line.Raw.Replace("\n", _newLine)).Append(_newLine);
        }

        return builder.ToString();
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, ToString());

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);

        try
        {
            // Recent versions of the game write UTF-8; older ones wrote Latin-1 with
            // \u escapes, which is plain ASCII and reads the same either way.
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static bool IsCommentOrBlank(string raw)
    {
        var trimmed = raw.TrimStart(' ', '\t', '\f');
        return trimmed.Length == 0 || trimmed[0] is '#' or '!';
    }

    private static bool EndsWithContinuation(string line)
    {
        var slashes = 0;

        for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--)
        {
            slashes++;
        }

        // An even number is escaped backslashes; only an odd one continues the line.
        return slashes % 2 == 1;
    }

    private static Line ParseLine(string raw, string logical)
    {
        if (IsCommentOrBlank(logical))
        {
            return new Line(raw, null, null);
        }

        var text = logical.TrimStart(' ', '\t', '\f');
        var key = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            var ch = text[index];

            if (ch == '\\' && index + 1 < text.Length)
            {
                index = Unescape(text, index, key);
                continue;
            }

            if (ch is '=' or ':' or ' ' or '\t' or '\f')
            {
                break;
            }

            key.Append(ch);
            index++;
        }

        // Whitespace, then at most one '=' or ':', then whitespace again.
        while (index < text.Length && text[index] is ' ' or '\t' or '\f')
        {
            index++;
        }

        if (index < text.Length && text[index] is '=' or ':')
        {
            index++;
        }

        while (index < text.Length && text[index] is ' ' or '\t' or '\f')
        {
            index++;
        }

        var value = new StringBuilder();

        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index = Unescape(text, index, value);
                continue;
            }

            value.Append(text[index]);
            index++;
        }

        return new Line(raw, key.ToString(), value.ToString());
    }

    /// <summary>Reads one escape starting at the backslash; returns the index after it.</summary>
    private static int Unescape(string text, int index, StringBuilder target)
    {
        var next = text[index + 1];

        switch (next)
        {
            case 'n': target.Append('\n'); return index + 2;
            case 'r': target.Append('\r'); return index + 2;
            case 't': target.Append('\t'); return index + 2;
            case 'f': target.Append('\f'); return index + 2;

            case 'u' when index + 5 < text.Length &&
                          int.TryParse(text.AsSpan(index + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                target.Append((char)code);
                return index + 6;

            default:
                target.Append(next);
                return index + 2;
        }
    }

    private static string Escape(string text, bool isKey)
    {
        var builder = new StringBuilder(text.Length + 8);

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\f': builder.Append("\\f"); break;

                // The same characters Java's own writer escapes, so the game reads back
                // exactly what was meant whichever separator rule it applies.
                case '=' or ':' or '#' or '!':
                    builder.Append('\\').Append(ch);
                    break;

                case ' ' when isKey || i == 0:
                    builder.Append("\\ ");
                    break;

                default:
                    if (ch < 0x20 || ch > 0x7E)
                    {
                        // Old versions read the file as Latin-1 and new ones as UTF-8;
                        // an escape means the same in both, so a Russian motd survives.
                        builder.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private sealed record Line(string Raw, string? Key, string? Value);
}
