using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Metadata;

public sealed class VersionJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexRef? AssetIndex { get; set; }

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionRef? JavaVersion { get; set; }

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; set; } = new();

    [JsonPropertyName("arguments")]
    public Arguments? Arguments { get; set; }

    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    [JsonPropertyName("logging")]
    public LoggingConfig? Logging { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int ComplianceLevel { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset ReleaseTime { get; set; }

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }
}

public sealed class Arguments
{
    [JsonPropertyName("game")]
    public List<GameArgument> Game { get; set; } = new();

    [JsonPropertyName("jvm")]
    public List<GameArgument> Jvm { get; set; } = new();
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public DownloadArtifact? Client { get; set; }

    [JsonPropertyName("server")]
    public DownloadArtifact? Server { get; set; }

    [JsonPropertyName("client_mappings")]
    public DownloadArtifact? ClientMappings { get; set; }

    [JsonPropertyName("server_mappings")]
    public DownloadArtifact? ServerMappings { get; set; }
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

public sealed class LoggingConfig
{
    [JsonPropertyName("client")]
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")]
    public string? Argument { get; set; }

    [JsonPropertyName("file")]
    public DownloadArtifact? File { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public sealed class Library
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; set; }

    [JsonPropertyName("extract")]
    public ExtractRule? Extract { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadArtifact? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class DownloadArtifact
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class ExtractRule
{
    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; set; }
}