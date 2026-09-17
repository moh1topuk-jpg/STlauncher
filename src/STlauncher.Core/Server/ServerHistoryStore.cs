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
}

/// <summary>One bar of the monitoring chart: an hour with its average and peak.</summary>
public sealed record ServerHistoryBar(DateTimeOffset Start, double Average, int Peak)
{
    public string Label => Start.ToLocalTime().ToString("dd.MM HH:mm");
}

/// <summary>
/// Local online history. Samples are collected whenever the launcher pings the server,
/// which is all a launcher can do without a backend of its own.
/// </summary>
public sealed class ServerHistoryStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

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

    public void Add(int online, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            var time = at ?? DateTimeOffset.Now;

            // One sample per minute is plenty for an hourly chart.
            if (_samples.Count > 0 && time - _samples[^1].Time < TimeSpan.FromMinutes(1))
            {
                return;
            }

            _samples.Add(new ServerSample { Time = time, Online = online });
            _samples.RemoveAll(s => time - s.Time > Retention);

            Save();
        }
    }

    /// <summary>Hourly averages and peaks for the requested window, oldest first.</summary>
    public IReadOnlyList<ServerHistoryBar> GetHourlyBars(TimeSpan window, DateTimeOffset? now = null)
    {
        var end = (now ?? DateTimeOffset.Now).ToLocalTime();
        var from = end - window;

        lock (_gate)
        {
            var relevant = _samples
                .Where(s => s.Time.ToLocalTime() >= from)
                .ToList();

            if (relevant.Count == 0)
            {
                return Array.Empty<ServerHistoryBar>();
            }

            var start = new DateTimeOffset(
                from.Year, from.Month, from.Day, from.Hour, 0, 0, from.Offset);

            var bars = new List<ServerHistoryBar>();
            var hours = (int)Math.Ceiling(window.TotalHours);

            for (var i = 0; i < hours; i++)
            {
                var bucketStart = start.AddHours(i);
                var bucketEnd = bucketStart.AddHours(1);

                var inBucket = relevant
                    .Where(s => s.Time.ToLocalTime() >= bucketStart && s.Time.ToLocalTime() < bucketEnd)
                    .ToList();

                if (inBucket.Count == 0)
                {
                    continue;
                }

                bars.Add(new ServerHistoryBar(
                    bucketStart,
                    inBucket.Average(s => s.Online),
                    inBucket.Max(s => s.Online)));
            }

            return bars;
        }
    }

    public (double Average, int Peak) GetSummary(TimeSpan window, DateTimeOffset? now = null)
    {
        var end = now ?? DateTimeOffset.Now;
        var from = end - window;

        lock (_gate)
        {
            var relevant = _samples.Where(s => s.Time >= from).ToList();

            return relevant.Count == 0
                ? (0, 0)
                : (relevant.Average(s => s.Online), relevant.Max(s => s.Online));
        }
    }

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

            if (loaded is not null)
            {
                _samples.AddRange(loaded);
            }
        }
        catch (Exception)
        {
            // A damaged history file must not stop the launcher.
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_samples, JsonOptions));
        }
        catch (Exception)
        {
        }
    }
}