using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Boost;

/// <summary>
/// What a performance preset wrote into one build, kept in the build's own launcher file
/// so that "put the settings back as they were before the preset" is possible at all.
/// The same shape as the switch's record of options: per key, what was there before and
/// what was written.
/// </summary>
public sealed class PresetRecord
{
    /// <summary>The preset applied last ("Low", "Balanced", "High"). For the log; nothing is decided by it.</summary>
    [JsonPropertyName("preset")]
    public string? Preset { get; set; }

    [JsonPropertyName("appliedAt")]
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>
    /// The options.txt keys the preset changed. "Previous" is the value before the first
    /// preset of a row: applying a second preset over the first does not move it.
    /// </summary>
    [JsonPropertyName("options")]
    public List<BoostOptionRecord> Options { get; set; } = new();

    /// <summary>The build's memory before the preset; null when the preset did not change it.</summary>
    [JsonPropertyName("memoryPrevious")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MemoryPrevious { get; set; }

    /// <summary>The memory the preset set. Put back only while the build still has exactly this.</summary>
    [JsonPropertyName("memoryWritten")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MemoryWritten { get; set; }

    /// <summary>True when there is nothing in it to take back.</summary>
    [JsonIgnore]
    public bool IsEmpty => Options.Count == 0 && MemoryWritten is null;
}
