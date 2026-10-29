using System.Globalization;
using System.Security.Cryptography;

namespace STlauncher.Relay;

/// <summary>What became of a crash report somebody left.</summary>
public enum ReportOutcome
{
    Stored,

    /// <summary>The disk limit is reached.</summary>
    Full,

    /// <summary>The disk refused the write.</summary>
    Failed
}

/// <summary>One kept report, as the listing knows it.</summary>
public sealed record StoredReport(string Id, DateTime ReceivedUtc, long Size, string Path);

/// <summary>
/// Crash reports on disk, for the developer to read over SSH: one UTF-8 text file per
/// report, named "yyyyMMdd-HHmmss-R-XXXXX.txt" after the time it came (UTC) and its id,
/// so a plain `ls` lists them in order and `cat *R-XXXXX*` shows one. As with the shared
/// builds there is no index: the directory listing is the store.
///
/// Nothing here is ever read back over the network. The relay writes and sweeps; reading
/// is the "reports" command of the same program, or ls and cat.
/// </summary>
public sealed class ReportStore
{
    /// <summary>The invite codes' alphabet: no I, L, O or U.</summary>
    public const string IdAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const string IdPrefix = "R-";

    /// <summary>Five characters, 25 bits: enough to tell two reports apart, short enough to read out.</summary>
    public const int IdLength = 5;

    private const string Extension = ".txt";
    private const string TemporaryExtension = ".tmp";
    private const string TimeFormat = "yyyyMMdd-HHmmss";

    private readonly string _directory;
    private readonly long _maxTotalBytes;
    private readonly TimeSpan _life;
    private readonly Func<DateTime> _utcNow;

    private readonly object _gate = new();
    private readonly Dictionary<string, StoredReport> _byId = new(StringComparer.Ordinal);
    private long _totalBytes;

    public ReportStore(string directory, long maxTotalBytes, TimeSpan life, Func<DateTime>? utcNow = null)
    {
        _directory = System.IO.Path.GetFullPath(directory);
        _maxTotalBytes = maxTotalBytes;
        _life = life;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Directory => _directory;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
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
    /// Makes the directory if need be and reads what is in it. Throws when it cannot be
    /// made or listed, so a relay told to keep reports somewhere it cannot write says so
    /// at start.
    /// </summary>
    /// <param name="readOnly">
    /// For the reading command: list what is there, touch nothing - the running relay may
    /// be in the middle of writing one.
    /// </param>
    /// <returns>How many expired reports were removed.</returns>
    public int Load(bool readOnly = false)
    {
        if (!readOnly)
        {
            CreatePrivateDirectory(_directory);
        }

        lock (_gate)
        {
            _byId.Clear();
            _totalBytes = 0;

            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*" + Extension))
            {
                if (!TryReadName(file.Name, out var id, out var received) || _byId.ContainsKey(id))
                {
                    continue;
                }

                _byId[id] = new StoredReport(id, received, file.Length, file.FullName);
                _totalBytes += file.Length;
            }

            if (readOnly)
            {
                return 0;
            }

            // A write the last run did not finish.
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*" + Extension + TemporaryExtension))
            {
                TryDelete(file.FullName);
            }

            return SweepLocked();
        }
    }

    /// <summary>Removes the reports older than their life.</summary>
    public int Sweep()
    {
        lock (_gate)
        {
            return SweepLocked();
        }
    }

    /// <summary>Keeps a report, already checked to be UTF-8 text, under a new id.</summary>
    public ReportOutcome Put(byte[] utf8Text, out string id)
    {
        var now = _utcNow();

        lock (_gate)
        {
            if (_totalBytes + utf8Text.Length > _maxTotalBytes)
            {
                SweepLocked();

                if (_totalBytes + utf8Text.Length > _maxTotalBytes)
                {
                    id = string.Empty;
                    return ReportOutcome.Full;
                }
            }

            do
            {
                id = NewId();
            }
            while (_byId.ContainsKey(id));

            var path = System.IO.Path.Combine(_directory, NameOf(id, now));

            try
            {
                // Whole or not at all, and readable by the relay's own user only: a report
                // is somebody's logs.
                WritePrivate(path + TemporaryExtension, utf8Text);
                File.Move(path + TemporaryExtension, path, overwrite: true);
            }
            catch (Exception)
            {
                TryDelete(path + TemporaryExtension);
                id = string.Empty;
                return ReportOutcome.Failed;
            }

            _byId[id] = new StoredReport(id, Truncate(now), utf8Text.Length, path);
            _totalBytes += utf8Text.Length;
            return ReportOutcome.Stored;
        }
    }

    /// <summary>Every kept report, newest first.</summary>
    public IReadOnlyList<StoredReport> List()
    {
        lock (_gate)
        {
            return _byId.Values.OrderByDescending(r => r.ReceivedUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>The report under an id in any case, with or without "R-"; null when there is none.</summary>
    public StoredReport? Find(string text)
    {
        var id = NormalizeId(text);

        if (id is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _byId.TryGetValue(id, out var report) ? report : null;
        }
    }

    /// <summary>"r-7kq2m", "7KQ2M", "R-7KQ2M" all name the same report; anything else is null.</summary>
    public static string? NormalizeId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var bare = text.Trim().ToUpperInvariant();

        if (bare.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            bare = bare[IdPrefix.Length..];
        }

        if (bare.Length != IdLength)
        {
            return null;
        }

        foreach (var c in bare)
        {
            if (IdAlphabet.IndexOf(c) < 0)
            {
                return null;
            }
        }

        return IdPrefix + bare;
    }

    private static string NewId()
    {
        var id = new char[IdLength];

        for (var i = 0; i < id.Length; i++)
        {
            id[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
        }

        return IdPrefix + new string(id);
    }

    private static string NameOf(string id, DateTime utc)
        => utc.ToString(TimeFormat, CultureInfo.InvariantCulture) + "-" + id + Extension;

    private static DateTime Truncate(DateTime utc) => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    /// <summary>"yyyyMMdd-HHmmss-R-XXXXX.txt": anything else in the directory is not the store's and is left alone.</summary>
    private static bool TryReadName(string name, out string id, out DateTime received)
    {
        id = string.Empty;
        received = default;

        var expected = TimeFormat.Length + 1 + IdPrefix.Length + IdLength + Extension.Length;

        if (name.Length != expected || name[TimeFormat.Length] != '-' ||
            !DateTime.TryParseExact(name[..TimeFormat.Length], TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out received))
        {
            return false;
        }

        var candidate = name.Substring(TimeFormat.Length + 1, IdPrefix.Length + IdLength);

        if (NormalizeId(candidate) is not { } normalized || normalized != candidate)
        {
            return false;
        }

        id = candidate;
        received = DateTime.SpecifyKind(received, DateTimeKind.Utc);
        return true;
    }

    private int SweepLocked()
    {
        var now = _utcNow();
        var removed = 0;

        foreach (var (id, report) in _byId.ToArray())
        {
            if (now - report.ReceivedUtc > _life)
            {
                TryDelete(report.Path);
                _byId.Remove(id);
                _totalBytes -= report.Size;
                removed++;
            }
        }

        return removed;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
        }
        else
        {
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void WritePrivate(string path, byte[] bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(bytes);
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
}
