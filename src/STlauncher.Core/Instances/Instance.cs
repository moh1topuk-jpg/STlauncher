using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Id of a ready-made profile under <c>versions/</c> that starts this build - one
    /// imported from another launcher. When set the build launches by it as it is, and
    /// <see cref="VersionId"/> only says which Minecraft version it is, for the mod catalog.
    /// </summary>
    [JsonPropertyName("profileVersionId")]
    public string? ProfileVersionId { get; set; }

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

    /// <summary>
    /// Id of the catalog build this instance was created from. The launcher keeps such an
    /// instance in step with the catalog on every start; a build without this link is the
    /// player's own and is never touched.
    /// </summary>
    [JsonPropertyName("catalogBuildId")]
    public string? CatalogBuildId { get; set; }

    /// <summary>
    /// Game folder outside the launcher's own directory - a build imported from another
    /// launcher and left where it is. Null means the folder under <c>instances/</c>.
    /// The definition always stays with the launcher either way, so removing the build
    /// never touches somebody else's files.
    /// </summary>
    [JsonPropertyName("externalGameDirectory")]
    public string? ExternalGameDirectory { get; set; }

    /// <summary>Server pinned by the catalog build. Null means the launcher default.</summary>
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

    /// <summary>Last time this build was launched, for sorting and display.</summary>
    [JsonPropertyName("lastPlayedAt")]
    public DateTimeOffset? LastPlayedAt { get; set; }

    /// <summary>Seconds played in this build, over all sessions ever, even those dropped from the list.</summary>
    [JsonPropertyName("playSeconds")]
    public long PlaySeconds { get; set; }

    /// <summary>The most recent sessions, newest last, for "this week".</summary>
    [JsonPropertyName("playSessions")]
    public List<PlaySession> PlaySessions { get; set; } = new();

    /// <summary>Catalog mods the build installs before launch. Not serialized.</summary>
    [JsonIgnore]
    public int ModCount => EnabledCatalogItems.Count > 0 ? EnabledCatalogItems.Count : DetectedModCount;

    /// <summary>
    /// Mod files found in the build's folder. Filled in by whoever can look at the disk;
    /// it is what an imported build has instead of a list from the catalog.
    /// </summary>
    [JsonIgnore]
    public int DetectedModCount { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}