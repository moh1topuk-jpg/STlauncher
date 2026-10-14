using System.Security.Cryptography;
using System.Text;

namespace STlauncher.Relay;

/// <summary>What became of a build somebody left.</summary>
public enum ShareOutcome
{
    /// <summary>Kept under a new code.</summary>
    Stored,

    /// <summary>The very same text is already kept: its code is handed out again and its life starts over.</summary>
    Refreshed,

    /// <summary>The count or the disk limit is reached.</summary>
    Full,

    /// <summary>The disk refused the write.</summary>
    Failed
}

/// <summary>
/// Shared builds on disk: one file per code, named "CODE.HASH.stb", holding the build
/// code as it was sent. Everything the store needs to know is in the directory listing -
/// the code and the hash of the content in the name, the size, and the time of last use
/// as the file's modification time - so there is no index to corrupt, and a restart
/// reads one listing and carries on.
///
/// A code is ten characters of the alphabet the invite codes use, drawn from the system's
/// random generator: 50 bits. Against a store of 200 000 builds that is one hit in five
/// billion guesses, and the relay lets an address guess a few hundred times an hour.
/// The code is NOT made from the content: whoever knows a build must not be able to work
/// out the code somebody else was given for it. The hash only finds a text that is
/// already kept, so sharing an unchanged build again does not mint a new code each time.
/// </summary>
public sealed class ShareStore
{
    public const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int CodeLength = 10;

    private const string Extension = ".stb";
    private const string TemporaryExtension = ".tmp";

    /// <summary>A file's time is moved forward at most this often, to spare the disk.</summary>
    private static readonly TimeSpan TouchEvery = TimeSpan.FromDays(1);

    private readonly string _directory;
    private readonly long _maxTotalBytes;
    private readonly int _maxCount;
    private readonly TimeSpan _life;
    private readonly Func<DateTime> _utcNow;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _codeByHash = new(StringComparer.Ordinal);
    private long _totalBytes;

    public ShareStore(string directory, long maxTotalBytes, int maxCount, TimeSpan life, Func<DateTime>? utcNow = null)
    {
        _directory = Path.GetFullPath(directory);
        _maxTotalBytes = maxTotalBytes;
        _maxCount = maxCount;
        _life = life;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byCode.Count;
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>
    /// Makes the directory if need be and reads what is in it. Throws when the directory
    /// cannot be made or listed: a relay told to keep builds somewhere it cannot write
    /// should say so at start, not at the first share.
    /// </summary>
    /// <returns>How many expired builds were removed.</returns>
    public int Load()
    {
        Directory.CreateDirectory(_directory);

        lock (_gate)
        {
            _byCode.Clear();
            _codeByHash.Clear();
            _totalBytes = 0;

            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*" + Extension))
            {
                // Only files this store named are its own; anything else is left alone.
                if (!TryReadName(file.Name, out var code, out var hash) || _byCode.ContainsKey(code))
                {
                    continue;
                }

                _byCode[code] = new Entry(hash, file.Length, file.LastWriteTimeUtc);
                _codeByHash[hash] = code;
                _totalBytes += file.Length;
            }

            // A write the last run did not finish.
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*" + Extension + TemporaryExtension))
            {
                TryDelete(file.FullName);
            }

            return SweepLocked();
        }
    }

    /// <summary>Removes the builds nobody has asked for in a lifetime.</summary>
    public int Sweep()
    {
        lock (_gate)
        {
            return SweepLocked();
        }
    }

    /// <summary>Keeps a build code and names it. The same text gets the code it already has.</summary>
    public ShareOutcome Put(string payload, out string code)
    {
        var bytes = Encoding.ASCII.GetBytes(payload);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var now = _utcNow();

        lock (_gate)
        {
            if (_codeByHash.TryGetValue(hash, out var known) && _byCode.TryGetValue(known, out var entry))
            {
                code = known;
                Touch(known, entry, now);
                return ShareOutcome.Refreshed;
            }

            if (IsFull(bytes.Length))
            {
                SweepLocked();

                if (IsFull(bytes.Length))
                {
                    code = string.Empty;
                    return ShareOutcome.Full;
                }
            }

            do
            {
                code = NewCode();
            }
            while (_byCode.ContainsKey(code));

            var path = PathOf(code, hash);

            try
            {
                // Whole or not at all: a reader never sees half a build, and a crash
                // leaves a ".tmp" the next start clears away.
                File.WriteAllBytes(path + TemporaryExtension, bytes);
                File.Move(path + TemporaryExtension, path, overwrite: true);
                File.SetLastWriteTimeUtc(path, now);
            }
            catch (Exception)
            {
                TryDelete(path + TemporaryExtension);
                code = string.Empty;
                return ShareOutcome.Failed;
            }

            _byCode[code] = new Entry(hash, bytes.Length, now);
            _codeByHash[hash] = code;
            _totalBytes += bytes.Length;
            return ShareOutcome.Stored;
        }
    }

    /// <summary>The build kept under a code, or null: no such code, or it has expired.</summary>
    public string? Get(string code)
    {
        var now = _utcNow();
        string path;

        lock (_gate)
        {
            if (!_byCode.TryGetValue(code, out var entry))
            {
                return null;
            }

            if (now - entry.Used > _life)
            {
                Remove(code, entry);
                return null;
            }

            Touch(code, entry, now);
            path = PathOf(code, entry.Hash);
        }

        try
        {
            return Encoding.ASCII.GetString(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            // Swept between the lookup and the read, or the disk is in trouble: to the
            // one asking it is the same as a code that is not there.
            return null;
        }
    }

    /// <summary>Whether a text is a code this store could have handed out.</summary>
    public static bool IsCode(string? text)
    {
        if (text is null || text.Length != CodeLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (CodeAlphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string NewCode()
    {
        var code = new char[CodeLength];

        for (var i = 0; i < code.Length; i++)
        {
            code[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(code);
    }

    private bool IsFull(int incoming)
        => _byCode.Count >= _maxCount || _totalBytes + incoming > _maxTotalBytes;

    private int SweepLocked()
    {
        var now = _utcNow();
        var removed = 0;

        foreach (var (code, entry) in _byCode.ToArray())
        {
            if (now - entry.Used > _life)
            {
                Remove(code, entry);
                removed++;
            }
        }

        return removed;
    }

    private void Remove(string code, Entry entry)
    {
        TryDelete(PathOf(code, entry.Hash));
        _byCode.Remove(code);
        _totalBytes -= entry.Size;

        if (_codeByHash.TryGetValue(entry.Hash, out var named) && named == code)
        {
            _codeByHash.Remove(entry.Hash);
        }
    }

    /// <summary>Starts the build's life over, on disk too - but not more often than once a day.</summary>
    private void Touch(string code, Entry entry, DateTime now)
    {
        if (now - entry.Used < TouchEvery)
        {
            return;
        }

        entry.Used = now;

        try
        {
            File.SetLastWriteTimeUtc(PathOf(code, entry.Hash), now);
        }
        catch (Exception)
        {
            // The time in memory still holds until the next restart.
        }
    }

    private string PathOf(string code, string hash) => Path.Combine(_directory, code + "." + hash + Extension);

    private static bool TryReadName(string name, out string code, out string hash)
    {
        code = string.Empty;
        hash = string.Empty;

        // CODE.HASH.stb: ten characters, a dot, sixty-four hex digits.
        if (name.Length != CodeLength + 1 + 64 + Extension.Length || name[CodeLength] != '.')
        {
            return false;
        }

        var first = name[..CodeLength];
        var second = name.Substring(CodeLength + 1, 64);

        if (!IsCode(first))
        {
            return false;
        }

        foreach (var c in second)
        {
            if (!(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        code = first;
        hash = second;
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private sealed class Entry
    {
        public Entry(string hash, long size, DateTime used)
        {
            Hash = hash;
            Size = size;
            Used = used;
        }

        public string Hash { get; }

        public long Size { get; }

        /// <summary>When the build was last left or asked for, to the day.</summary>
        public DateTime Used { get; set; }
    }
}
