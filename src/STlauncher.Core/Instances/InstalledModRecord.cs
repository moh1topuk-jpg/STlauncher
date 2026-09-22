using System.Text.Json.Serialization;

namespace STlauncher.Core.Instances;

public enum ModSource
{
    /// <summary>Unknown origin: the file was placed into mods/ by hand.</summary>
    Manual,

    /// <summary>Comes from the server catalog and installs before every launch.</summary>
    Catalog,

    /// <summary>Installed from the Modrinth browser.</summary>
    Modrinth,

    /// <summary>Came from an imported modpack.</summary>
    Modpack
}

public sealed class InstalledModRecord
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public ModSource Source { get; set; } = ModSource.Manual;

    /// <summary>Catalog item id or Modrinth project slug. Empty for manual files.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Human readable name, when known.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>"mods", "resourcepacks" or "shaderpacks". Missing in old records means mods.</summary>
    [JsonPropertyName("folder")]
    public string? Folder { get; set; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>
    /// Version this file satisfies - the pin from the catalog, or the version resolved
    /// when it was installed. Without it the launcher cannot tell "already has the pinned
    /// build" from "has some build", and re-resolved every pinned mod on every start.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// The player switched this mod off. The catalog sync then leaves it alone: a build
    /// that reinstalls what you just disabled is a build you cannot configure.
    /// </summary>
    [JsonPropertyName("disabledByUser")]
    public bool DisabledByUser { get; set; }
}