using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Server;

public sealed class ServerSample
{
    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    [JsonPropertyName("online")]
    public int Online { get; set; }

    /// <summary>
    /// Server this sample belongs to. Null in files written before the address was
    /// recorded; those samples all came from the single server the launcher shipped with,
    /// so they are matched against any address rather than thrown away.
    /// </summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

/// <summary>
/// One column of the monitoring chart.
/// </summary>
/// <param name="HasData">
/// False when the launcher was not running during this slice. Such a slice is a gap, not
/// an hour with nobody online - drawing it as zero is what made the old chart read as an
/// empty strip on every fresh installation.
/// </param>
public sealed record ServerHistoryBucket(
    DateTimeOffset Start,
    DateTimeOffset End,
    bool HasData,
    double Average,
    int Peak,
    int SampleCount);

/// <summary>
/// Local online history. Samples are collected whenever the launcher pings the server,
/// which is all a launcher can do without a backend of its own.
/// </summary>
public sealed class ServerHistoryStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>Two samples closer together than this add nothing to an hourly chart.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<ServerSample> _samples = new();

    public ServerHistoryStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        Load();
    }

    public IReadOnlyList<ServerSample> Samples
    {
        get
        {
            lock (_gate)
            {
                return _samples.ToList();
            }
        }
    }

    /// <summary>Records a reading. Returns false when it was too close to the previous one.</summary>
    public bool Add(string address, int online, DateTimeOffset? at = null)
    {
        List<ServerSample> snapshot;

        lock (_gate)
        {
            var time = at ?? DateTimeOffset.Now;

            var last = _samples.LastOrDefault(s => Matches(s, address));

            if (last is not null && time - last.Time < MinimumInterval)
            {
                return false;
            }

            _samples.Add(new ServerSample
            {
                Time = time,
                Online = online,
                Address = Normalize(address)
            });

            _samples.RemoveAll(s => time - s.Time > Retention);
            _samples.Sort((a, b) => a.Time.CompareTo(b.Time));

            // Snapshot under the lock, then write outside it: the file write is disk I/O
            // and has no business blocking every other reader.
            snapshot = new List<ServerSample>(_samples);
        }

        Save(snapshot);
        return true;
    }

    /// <summary>
    /// Splits the window into equal slices, oldest first. Slices the launcher was not
    /// running for come back with <see cref="ServerHistoryBucket.HasData"/> false so the
    /// caller can leave a gap instead of drawing a zero.
    /// </summary>
    public IReadOnlyList<ServerHistoryBucket> GetBuckets(
        string address,
        TimeSpan window,
        int bucketCount,
        DateTimeOffset? now = null)
    {
        if (bucketCount <= 0 || window <= TimeSpan.Zero)
        {
            return Array.Empty<ServerHistoryBucket>();
        }

        var end = now ?? DateTimeOffset.Now;
        var start = end - window;
        var slice = TimeSpan.FromTicks(window.Ticks / bucketCount);

        lock (_gate)
        {
            // One ordered pass instead of a LINQ filter per bucket: a week at five-minute
            // resolution is 2016 buckets, and the quadratic version was visibly slow.
            var relevant = _samples
                .Where(s => Matches(s, address) && s.Time >= start && s.Time <= end)
                .OrderBy(s => s.Time)
                .ToList();

            var buckets = new List<ServerHistoryBucket>(bucketCount);
            var index = 0;

            for (var i = 0; i < bucketCount; i++)
            {
                var isLast = i == bucketCount - 1;
                var bucketStart = start + TimeSpan.FromTicks(slice.Ticks * i);
                var bucketEnd = isLast ? end : bucketStart + slice;

                // The newest reading sits exactly on "now" often enough to matter: the
                // last bucket therefore includes its own upper bound.
                var limit = isLast ? bucketEnd.AddTicks(1) : bucketEnd;

                var count = 0;
                long total = 0;
                var peak = 0;

                while (index < relevant.Count && relevant[index].Time < limit)
                {
                    var online = relevant[index].Online;
                    total += online;
                    peak = Math.Max(peak, online);
                    count++;
                    index++;
                }

                buckets.Add(count == 0
                    ? new ServerHistoryBucket(bucketStart, bucketEnd, false, 0, 0, 0)
                    : new ServerHistoryBucket(bucketStart, bucketEnd, true, (double)total / count, peak, count));
            }

            return buckets;
        }
    }

    public (double Average, int Peak, int SampleCount) GetSummary(
        string address,
        TimeSpan window,
        DateTimeOffset? now = null)
    {
        var end = now ?? DateTimeOffset.Now;
        var from = end - window;

        lock (_gate)
        {
            var relevant = _samples
                .Where(s => Matches(s, address) && s.Time >= from && s.Time <= end)
                .ToList();

            return relevant.Count == 0
                ? (0, 0, 0)
                : (relevant.Average(s => s.Online), relevant.Max(s => s.Online), relevant.Count);
        }
    }

    /// <summary>Oldest reading still kept for this server, or null when there is none.</summary>
    public DateTimeOffset? FirstSampleAt(string address)
    {
        lock (_gate)
        {
            var first = _samples.FirstOrDefault(s => Matches(s, address));
            return first?.Time;
        }
    }

    private static string? Normalize(string? address)
        => string.IsNullOrWhiteSpace(address) ? null : address.Trim().ToLowerInvariant();

    private static bool Matches(ServerSample sample, string address)
        => sample.Address is null ||
           string.Equals(sample.Address, Normalize(address), StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<ServerSample>>(json, JsonOptions);

            if (loaded is null)
            {
                return;
            }

            // Sorted on the way in: everything downstream assumes chronological order, and
            // a hand-edited or clock-shifted file must not be able to break the chart.
            _samples.AddRange(loaded.OrderBy(s => s.Time));
        }
        catch (Exception)
        {
            // A damaged history file must not stop the launcher.
        }
    }

    private void Save(IReadOnlyList<ServerSample> samples)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(samples, JsonOptions));
        }
        catch (Exception)
        {
        }
    }
}
