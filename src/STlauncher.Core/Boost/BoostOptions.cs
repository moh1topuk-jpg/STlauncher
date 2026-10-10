using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Boost;

/// <summary>One key the switch would write: what it says now (null for "not in the file") and what it would say.</summary>
public sealed record BoostOptionChange(string Key, string? Previous, string Value);

/// <summary>What switching off did to the recorded keys.</summary>
/// <param name="Restored">Keys put back to their previous value, or removed because they had not existed.</param>
/// <param name="LeftToPlayer">Keys that no longer say what the switch wrote: changed by hand since, and left so.</param>
public sealed record BoostOptionsRevert(IReadOnlyList<string> Restored, IReadOnlyList<string> LeftToPlayer);

/// <summary>
/// The game settings "Ускорение" lowers. Every rule only ever lowers: a player who
/// already runs at render distance 6 keeps 6. A key is touched when the file has it; a
/// key the file lacks is written only for a game version known to read it, so an old
/// game is never handed a line it does not know and a new one keeps its spelling.
/// </summary>
public static class BoostOptions
{
    public const int RenderDistance = 8;
    public const int SimulationDistance = 6;
    public const string EntityDistance = "0.75";
    public const int MipmapLevels = 2;
    public const int BiomeBlend = 1;

    /// <summary>The frame cap when the monitor is not known, or is an ordinary one.</summary>
    public const int DefaultFpsCap = 120;

    private static readonly Regex ReleaseVersion = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// The cap that goes with vsync off: enough for the monitor, never "unlimited" - an
    /// uncapped game on a laptop is a hot laptop and a flat battery for frames nobody
    /// sees. 120 for a monitor up to 120 Hz or an unknown one; a faster monitor gets its
    /// own rate, rounded up to the game's step of ten.
    /// </summary>
    public static int FpsCap(int? monitorHz)
    {
        if (monitorHz is not > DefaultFpsCap)
        {
            return DefaultFpsCap;
        }

        return Math.Min(250, (monitorHz.Value + 9) / 10 * 10);
    }

    /// <summary>What the switch would change in this file, in the order it is shown. Changes nothing.</summary>
    public static IReadOnlyList<BoostOptionChange> Plan(OptionsFile file, string? gameVersion, int? monitorHz = null)
    {
        var changes = new List<BoostOptionChange>();

        // A snapshot or an unnamed version: only what the file already has.
        var known = gameVersion is not null && ReleaseVersion.IsMatch(gameVersion);

        bool Since(string version) => known && VersionRange.CompareVersions(gameVersion!, version) >= 0;

        void Lower(string key, int target, bool writeWhenAbsent)
        {
            var current = file.Get(key);

            if (current is null)
            {
                if (writeWhenAbsent)
                {
                    changes.Add(new BoostOptionChange(key, null, target.ToString(CultureInfo.InvariantCulture)));
                }
            }
            else if (int.TryParse(current.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > target)
            {
                changes.Add(new BoostOptionChange(key, current, target.ToString(CultureInfo.InvariantCulture)));
            }
        }

        void Switch(string key, string from, string to, bool writeWhenAbsent)
        {
            var current = file.Get(key);

            if (current is null ? writeWhenAbsent : string.Equals(current.Trim(), from, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new BoostOptionChange(key, current, to));
            }
        }

        // Before 1.7 the file spelled these another way; such a game gets nothing new.
        var modern = Since("1.7");

        Lower("renderDistance", RenderDistance, modern);
        Lower("simulationDistance", SimulationDistance, Since("1.18"));

        if (file.Get("entityDistanceScaling") is { } scaling)
        {
            if (double.TryParse(scaling.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                value > double.Parse(EntityDistance, CultureInfo.InvariantCulture))
            {
                changes.Add(new BoostOptionChange("entityDistanceScaling", scaling, EntityDistance));
            }
        }
        else if (Since("1.16"))
        {
            changes.Add(new BoostOptionChange("entityDistanceScaling", null, EntityDistance));
        }

        // 0 is "all", 1 "decreased", 2 "minimal": one step down, and minimal stays minimal.
        Switch("particles", "0", "1", modern);

        // Three spellings over the years, and the oldest has no "fast" to step down to.
        // Fancy becomes fast in the quoting the file uses; a file without the key is left
        // alone, since which spelling this game reads is not something to guess.
        foreach (var key in new[] { "cloudStatus", "renderClouds" })
        {
            if (file.Get(key) is { } clouds)
            {
                var bare = clouds.Trim().Trim('"');

                if (bare is "fancy" or "true")
                {
                    changes.Add(new BoostOptionChange(key, clouds, clouds.Trim().StartsWith('"') ? "\"fast\"" : "fast"));
                }
            }
        }

        Switch("entityShadows", "true", "false", Since("1.9"));

        // Smooth lighting was a number once: maximum goes to minimum. Since 1.19 it is a
        // plain on or off, and off is flat blocks for a frame or two - not a trade the
        // switch makes for the player.
        Switch("ao", "2", "1", writeWhenAbsent: false);

        Lower("mipmapLevels", MipmapLevels, modern);
        Lower("biomeBlendRadius", BiomeBlend, Since("1.13"));

        Switch("enableVsync", "true", "false", modern);

        // The player's own cap stays when it is at or under what the monitor needs; a
        // missing key is the game's default of 120, which is already that.
        Lower("maxFps", FpsCap(monitorHz), writeWhenAbsent: false);

        return changes;
    }

    /// <summary>Writes the planned values into the file in memory and returns the record of them.</summary>
    public static List<BoostOptionRecord> Apply(OptionsFile file, IEnumerable<BoostOptionChange> changes)
    {
        var records = new List<BoostOptionRecord>();

        foreach (var change in changes)
        {
            file.Set(change.Key, change.Value);
            records.Add(new BoostOptionRecord { Key = change.Key, Previous = change.Previous, Written = change.Value });
        }

        return records;
    }

    /// <summary>
    /// Puts back what the record says, key by key: the previous value, or no line at all
    /// when there had been none. A key that no longer holds the written value was changed
    /// since - by the player in the game's menu - and is left as it is. That includes a
    /// key the game has dropped from the file.
    /// </summary>
    public static BoostOptionsRevert Revert(OptionsFile file, IEnumerable<BoostOptionRecord> records)
    {
        var restored = new List<string>();
        var left = new List<string>();

        foreach (var record in records)
        {
            var current = file.Get(record.Key);

            if (current is null || !string.Equals(current.Trim(), record.Written.Trim(), StringComparison.Ordinal))
            {
                left.Add(record.Key);
                continue;
            }

            if (record.Previous is null)
            {
                file.Remove(record.Key);
            }
            else
            {
                file.Set(record.Key, record.Previous);
            }

            restored.Add(record.Key);
        }

        return new BoostOptionsRevert(restored, left);
    }
}
