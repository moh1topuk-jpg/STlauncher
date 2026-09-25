using System;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Instances;

/// <summary>One run of the game: when it started and how long it lasted.</summary>
public sealed class PlaySession
{
    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("seconds")]
    public long Seconds { get; set; }
}
