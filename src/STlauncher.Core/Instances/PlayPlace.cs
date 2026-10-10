using System;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Instances;

/// <summary>
/// Time spent in one place in this build: on a server, or in single-player worlds. Kept
/// in the build's own file and nowhere else; an address here may be somebody's home.
/// </summary>
public sealed class PlayPlace
{
    /// <summary>Canonical server address; null stands for single-player.</summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("seconds")]
    public long Seconds { get; set; }

    [JsonPropertyName("lastPlayedAt")]
    public DateTimeOffset LastPlayedAt { get; set; }
}
