using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Boost;

/// <summary>
/// Everything "Ускорение" did to one build, kept in the build's own launcher file so
/// that switching it off puts back exactly this and nothing else. Without the record the
/// switch would be a preset: it could be applied, and never taken back.
/// </summary>
public sealed class BoostRecord
{
    /// <summary>True while the switch is on. An off record stays for <see cref="Parked"/>.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("appliedAt")]
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>The jars the switch brought into mods/, the mods they required included.</summary>
    [JsonPropertyName("jars")]
    public List<BoostJarRecord> Jars { get; set; } = new();

    /// <summary>The options.txt keys the switch wrote, with what each was before.</summary>
    [JsonPropertyName("options")]
    public List<BoostOptionRecord> Options { get; set; } = new();

    /// <summary>
    /// Jars the switch renamed to .disabled when it was turned off. Turned on again, it
    /// switches these back on instead of downloading them a second time.
    /// </summary>
    [JsonPropertyName("parked")]
    public List<BoostJarRecord> Parked { get; set; } = new();
}

/// <summary>One file in mods/ that came with the switch.</summary>
public sealed class BoostJarRecord
{
    /// <summary>The name it was installed under, without ".disabled".</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// SHA-1 of the file as it was installed. A file of this name with other contents is
    /// one the player put there since, and is the player's.
    /// </summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    /// <summary>True for a mod that came only because another one required it.</summary>
    [JsonPropertyName("dependency")]
    public bool Dependency { get; set; }
}

/// <summary>One key of options.txt the switch wrote.</summary>
public sealed class BoostOptionRecord
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The value before; null when the file had no such key.</summary>
    [JsonPropertyName("previous")]
    public string? Previous { get; set; }

    /// <summary>
    /// The value written. Switching off restores the key only while it still says this:
    /// anything else is the player's own change since.
    /// </summary>
    [JsonPropertyName("written")]
    public string Written { get; set; } = string.Empty;
}
