using System;
using System.Text.Json.Serialization;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Instances;

public sealed class Instance
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("versionId")]
    public string? VersionId { get; set; }

    [JsonPropertyName("loader")]
    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;

    [JsonPropertyName("loaderVersion")]
    public string? LoaderVersion { get; set; }

    [JsonPropertyName("maxMemoryMb")]
    public int MaxMemoryMb { get; set; } = 2048;

    [JsonPropertyName("minMemoryMb")]
    public int MinMemoryMb { get; set; } = 512;

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("javaPath")]
    public string? JavaPath { get; set; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; set; }

    [JsonPropertyName("serverAddress")]
    public string? ServerAddress { get; set; }

    /// <summary>Extra arguments appended after the standard Minecraft arguments.</summary>
    [JsonPropertyName("extraGameArgs")]
    public string? ExtraGameArgs { get; set; }

    /// <summary>Catalog item ids that this build installs before every launch.</summary>
    [JsonPropertyName("enabledCatalogItems")]
    public List<string> EnabledCatalogItems { get; set; } = new();

    /// <summary>
    /// What was installed into this build and where it came from. The file on disk stays
    /// the source of truth; this list only adds names, descriptions and provenance.
    /// </summary>
    [JsonPropertyName("installedMods")]
    public List<InstalledModRecord> InstalledMods { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}