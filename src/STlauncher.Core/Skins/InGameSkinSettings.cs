using System.Text.Json.Serialization;

namespace STlauncher.Core.Skins;

/// <summary>
/// What a build remembers about "show my skin in the game". Absent until the player
/// flips the switch for the first time.
/// </summary>
public sealed class InGameSkinSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// The jar the launcher itself put into <c>mods/</c>, by its enabled name. Null when
    /// the build already had CustomSkinLoader and the launcher only hands it the skin.
    /// Only this file is ever switched off by the launcher, and it is never fetched again
    /// on its own: a jar the player removed stays removed until the switch is flipped.
    /// </summary>
    [JsonPropertyName("jar")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Jar { get; set; }

    /// <summary>The mod version that jar is, for the line under the switch.</summary>
    [JsonPropertyName("modVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModVersion { get; set; }

    /// <summary>
    /// The skin copy the launcher last wrote into the game folder ("Nick.png"). It is the
    /// launcher's own copy, so it is the one file taken away again when the nickname
    /// changes or no skin is worn any more.
    /// </summary>
    [JsonPropertyName("skinFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkinFile { get; set; }
}
