using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace STlauncher.Core.Boost;

/// <summary>
/// options.txt as the bytes it is, for changing a few keys and leaving every other byte
/// where it was. The game of the 1.7.10 era wrote this file in the system code page, a
/// later one in UTF-8, and nothing in the file says which; decoding it to text and
/// encoding it back turns a Russian resource pack name into question marks. So nothing
/// is decoded: a line is found by the bytes before its first colon (keys are ASCII), and
/// only the lines that are set or removed change. Lines without a colon, keys nobody
/// here knows, the order, the byte order mark and each line's own ending all stay.
/// </summary>
public sealed class OptionsFile
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    // Latin-1 maps every byte to the character of the same number and back, so a value
    // read as a string and written again is the same bytes, whatever encoding they were.
    private static readonly Encoding Bytes = Encoding.Latin1;

    private readonly List<byte[]> _lines;
    private readonly bool _hasBom;
    private readonly byte[] _newline;

    private OptionsFile(List<byte[]> lines, bool hasBom, byte[] newline)
    {
        _lines = lines;
        _hasBom = hasBom;
        _newline = newline;
    }

    /// <summary>True when something was set or removed since the file was read.</summary>
    public bool Changed { get; private set; }

    /// <summary>
    /// Reads the file; a missing one is an empty file. Null when what is there cannot be
    /// taken for text - such a file is never rewritten.
    /// </summary>
    public static OptionsFile? TryLoad(string path)
    {
        byte[] bytes;

        try
        {
            bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        }
        catch (Exception)
        {
            return null;
        }

        return Parse(bytes);
    }

    /// <summary>Null when the bytes are not a text file in any single-byte or UTF-8 encoding.</summary>
    public static OptionsFile? Parse(byte[] bytes)
    {
        // A zero byte or a control character is in no options.txt: that is UTF-16, or
        // not text at all, and lines cannot be told apart in it.
        foreach (var b in bytes)
        {
            if (b < 0x20 && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
            {
                return null;
            }
        }

        var hasBom = bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Utf8Bom);
        var start = hasBom ? 3 : 0;
        var lines = new List<byte[]>();

        for (var i = start; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lines.Add(bytes[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < bytes.Length)
        {
            lines.Add(bytes[start..]);
        }

        // A new line ends the way the file's lines do.
        var newline = Bytes.GetBytes(Environment.NewLine);

        if (lines.Count > 0 && EndingLength(lines[0]) is > 0 and var length)
        {
            newline = lines[0][^length..];
        }

        return new OptionsFile(lines, hasBom, newline);
    }

    /// <summary>The value of a key, or null when the file has no such key. The last line of a key is the one the game goes by.</summary>
    public string? Get(string key)
    {
        var index = Find(key);

        if (index < 0)
        {
            return null;
        }

        var line = _lines[index];
        var colon = Array.IndexOf(line, (byte)':');

        return Bytes.GetString(line, colon + 1, line.Length - EndingLength(line) - colon - 1);
    }

    /// <summary>Sets a key, in place when the file has it and as a new last line when it does not.</summary>
    public void Set(string key, string value)
    {
        var index = Find(key);
        var valueBytes = Bytes.GetBytes(value);

        if (index >= 0)
        {
            var line = _lines[index];
            var colon = Array.IndexOf(line, (byte)':');
            var ending = line[^EndingLength(line)..];

            // The key is kept as the file spelled it.
            _lines[index] = line[..(colon + 1)].Concat(valueBytes).Concat(ending).ToArray();
        }
        else
        {
            if (_lines.Count > 0 && EndingLength(_lines[^1]) == 0)
            {
                _lines[^1] = _lines[^1].Concat(_newline).ToArray();
            }

            _lines.Add(Bytes.GetBytes(key + ":").Concat(valueBytes).Concat(_newline).ToArray());
        }

        Changed = true;
    }

    /// <summary>Takes the key's line out. False when the file has no such key.</summary>
    public bool Remove(string key)
    {
        var index = Find(key);

        if (index < 0)
        {
            return false;
        }

        _lines.RemoveAt(index);
        Changed = true;
        return true;
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();

        if (_hasBom)
        {
            stream.Write(Utf8Bom);
        }

        foreach (var line in _lines)
        {
            stream.Write(line);
        }

        return stream.ToArray();
    }

    public void Save(string path)
    {
        var bytes = ToBytes();
        AtomicFile.Write(path, stream => stream.Write(bytes));
        Changed = false;
    }

    private int Find(string key)
    {
        var wanted = Bytes.GetBytes(key);

        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var line = _lines[i];

            if (line.Length > wanted.Length &&
                line[wanted.Length] == (byte)':' &&
                line.AsSpan(0, wanted.Length).SequenceEqual(wanted))
            {
                return i;
            }
        }

        return -1;
    }

    private static int EndingLength(byte[] line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\n')
        {
            return line.Length > 1 && line[^2] == (byte)'\r' ? 2 : 1;
        }

        return 0;
    }
}
