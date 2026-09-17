using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Metadata;

public sealed class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions? Latest { get; set; }

    [JsonPropertyName("versions")]
    public List<VersionSummary> Versions { get; set; } = new();
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }
}

public sealed class VersionSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset ReleaseTime { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int ComplianceLevel { get; set; }
}