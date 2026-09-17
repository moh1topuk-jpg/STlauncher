using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Content;

public enum CatalogItemType
{
    Mod,
    ResourcePack,
    ShaderPack,
    Modpack,
    Config,
    Other
}

public enum CatalogSourceKind
{
    /// <summary>Not specified or not recognised by this launcher version.</summary>
    Unknown,

    /// <summary>Resolved through the Modrinth API.</summary>
    Modrinth,

    /// <summary>Resolved through the CurseForge API (requires an API key).</summary>
    CurseForge,

    /// <summary>A direct download URL.</summary>
    Direct
}

public sealed class ContentCatalog
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("sections")]
    public List<CatalogSection> Sections { get; set; } = new();

    public int ItemCount
    {
        get
        {
            var total = 0;
            foreach (var section in Sections)
            {
                total += section.Items.Count;
            }

            return total;
        }
    }
}

public sealed class CatalogSection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("items")]
    public List<CatalogItem> Items { get; set; } = new();
}

public sealed class CatalogItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public CatalogItemType Type { get; set; } = CatalogItemType.Mod;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>client, server or both. Informational.</summary>
    [JsonPropertyName("side")]
    public string? Side { get; set; }

    /// <summary>
    /// Optional explicit destination relative to the instance directory. Overrides the
    /// folder mapping derived from <see cref="Type"/>, which keeps the catalog open for
    /// item kinds that do not exist yet.
    /// </summary>
    [JsonPropertyName("targetPath")]
    public string? TargetPath { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("source")]
    public CatalogSource Source { get; set; } = new();
}

public sealed class CatalogSource
{
    [JsonPropertyName("kind")]
    public CatalogSourceKind Kind { get; set; } = CatalogSourceKind.Unknown;

    /// <summary>Modrinth project slug/id, or a CurseForge project id.</summary>
    [JsonPropertyName("project")]
    public string? Project { get; set; }

    /// <summary>Modrinth version id or version number. Empty means "latest for the instance".</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>CurseForge file id.</summary>
    [JsonPropertyName("fileId")]
    public string? FileId { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("sha512")]
    public string? Sha512 { get; set; }
}