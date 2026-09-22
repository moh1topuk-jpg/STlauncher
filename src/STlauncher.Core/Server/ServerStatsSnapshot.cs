using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Server;

/// <summary>
/// Statistics published by the monitoring collector (see docs/monitoring.md). The
/// launcher can only sample while it is open, so the history a player sees on a fresh
/// installation comes from here; the local samples are the fallback.
/// </summary>
public sealed class ServerStatsSnapshot
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = SupportedSchemaVersion;

    /// <summary>When the collector last managed to read the monitoring API.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; set; }

    [JsonPropertyName("server")]
    public ServerStatsServer? Server { get; set; }

    [JsonPropertyName("summary")]
    public ServerStatsSummary? Summary { get; set; }

    [JsonPropertyName("ranges")]
    public Dictionary<string, ServerStatsRange> Ranges { get; set; } = new();

    /// <summary>How many people use the launcher, when the collector counts the pings.</summary>
    [JsonPropertyName("launcher")]
    public ServerStatsLauncher? Launcher { get; set; }

    /// <summary>
    /// True when the payload is recent enough to show. A collector that stopped days ago
    /// would otherwise keep a frozen chart on screen forever.
    /// </summary>
    public bool IsFresh(TimeSpan maxAge, DateTimeOffset? now = null)
        => UpdatedAt is { } updated && (now ?? DateTimeOffset.Now) - updated <= maxAge;

    public ServerStatsRange? Range(string name)
        => Ranges.TryGetValue(name, out var range) ? range : null;

    public static ServerStatsSnapshot Parse(string json)
    {
        var snapshot = JsonSerializer.Deserialize<ServerStatsSnapshot>(json, JsonOptions)
                       ?? throw new InvalidDataException("Server stats payload is empty.");

        if (snapshot.SchemaVersion > SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Server stats schema version {snapshot.SchemaVersion} is newer than supported " +
                $"({SupportedSchemaVersion}). Update the launcher.");
        }

        return snapshot;
    }
}

/// <summary>Launcher usage the collector derives from anonymous pings. All optional.</summary>
public sealed class ServerStatsLauncher
{
    /// <summary>Distinct installations that started the launcher in the last 24 hours.</summary>
    [JsonPropertyName("usersToday")]
    public int? UsersToday { get; set; }

    [JsonPropertyName("usersWeek")]
    public int? UsersWeek { get; set; }

    [JsonPropertyName("launchesToday")]
    public int? LaunchesToday { get; set; }

    /// <summary>How update checks went over the week, most common first: "ok:mirror" 120, "fail:github=Blocked" 7.</summary>
    [JsonPropertyName("updates")]
    public List<ServerStatsOutcome> Updates { get; set; } = new();

    /// <summary>Which launcher versions players ran over the week.</summary>
    [JsonPropertyName("versions")]
    public List<ServerStatsOutcome> Versions { get; set; } = new();

    /// <summary>Game crashes by cause over the week.</summary>
    [JsonPropertyName("crashes")]
    public List<ServerStatsOutcome> Crashes { get; set; } = new();

    public bool HasAnything => UsersToday is not null || UsersWeek is not null;
}

public sealed class ServerStatsOutcome
{
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("n")]
    public int Count { get; set; }
}

public sealed class ServerStatsServer
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("online")]
    public int? Online { get; set; }

    [JsonPropertyName("max")]
    public int? Max { get; set; }

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }
}

/// <summary>
/// Aggregates the monitoring site computes itself. These are worth showing from the
/// first launch precisely because no amount of local sampling can produce them.
/// </summary>
public sealed class ServerStatsSummary
{
    /// <summary>Average online over the last 7 days, prime hours only.</summary>
    [JsonPropertyName("averageWeek")]
    public double? AverageWeek { get; set; }

    [JsonPropertyName("peak")]
    public int? Peak { get; set; }

    [JsonPropertyName("peakAt")]
    public DateTimeOffset? PeakAt { get; set; }

    /// <summary>Percentage, over the last few days.</summary>
    [JsonPropertyName("uptime")]
    public double? Uptime { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    public bool HasAnything => AverageWeek is not null || Peak is not null || Uptime is not null;
}

public sealed class ServerStatsRange
{
    [JsonPropertyName("from")]
    public DateTimeOffset From { get; set; }

    [JsonPropertyName("to")]
    public DateTimeOffset To { get; set; }

    [JsonPropertyName("buckets")]
    public List<ServerStatsBucket> Buckets { get; set; } = new();

    public bool HasData => Buckets.Any(b => b.Average is not null);

    /// <summary>
    /// Converts to the same shape the local history produces, so the chart does not care
    /// which source it is drawing. A bucket with no average is a gap: the collector was
    /// not running then, which is not the same as nobody being online.
    /// </summary>
    public IReadOnlyList<ServerHistoryBucket> ToBuckets()
    {
        var result = new List<ServerHistoryBucket>(Buckets.Count);

        for (var i = 0; i < Buckets.Count; i++)
        {
            var bucket = Buckets[i];
            var end = i + 1 < Buckets.Count ? Buckets[i + 1].Start : To;
            var hasData = bucket.Average is not null;

            result.Add(new ServerHistoryBucket(
                bucket.Start,
                end,
                hasData,
                bucket.Average ?? 0,
                bucket.Peak ?? 0,
                hasData ? 1 : 0));
        }

        return result;
    }
}

public sealed class ServerStatsBucket
{
    [JsonPropertyName("t")]
    public DateTimeOffset Start { get; set; }

    /// <summary>Null marks a slice the collector has no reading for.</summary>
    [JsonPropertyName("avg")]
    public double? Average { get; set; }

    [JsonPropertyName("peak")]
    public int? Peak { get; set; }
}
