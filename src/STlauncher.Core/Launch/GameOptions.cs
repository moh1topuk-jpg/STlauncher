using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Boost;

namespace STlauncher.Core.Launch;

/// <summary>Three settings profiles the launcher can write into options.txt.</summary>
public enum PerformancePreset
{
    /// <summary>A weak or old machine: short view distance, no clouds, minimal particles.</summary>
    Low,

    /// <summary>The middle: what the game's own defaults aim for, with a few savings.</summary>
    Balanced,

    /// <summary>A machine with headroom: long view distance, everything on.</summary>
    High
}

/// <summary>
/// Prepares options.txt before the first launch so a player skips the language picker
/// and the accessibility onboarding, and starts with the server resource pack accepted.
/// Existing values are never overwritten - only missing keys are added. The presets and
/// the copy between builds are the exception: they write on purpose, and a preset says
/// what it overwrote so that it can be taken back.
/// </summary>
public static class GameOptions
{
    public const string FileName = "options.txt";

    /// <summary>Keys that belong to one build's folder and must not travel with the settings.</summary>
    private static readonly HashSet<string> NotCopied = new(StringComparer.OrdinalIgnoreCase)
    {
        "resourcePacks", "incompatibleResourcePacks", "lastServer", "lang"
    };

    public static void EnsureDefaults(string gameDirectory, string languageCode = "ru_ru")
    {
        try
        {
            Directory.CreateDirectory(gameDirectory);

            var path = Path.Combine(gameDirectory, FileName);
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string>();

            var changed = false;

            changed |= Ensure(lines, "lang", languageCode);
            changed |= Ensure(lines, "narrator", "0");
            changed |= Ensure(lines, "onboardAccessibility", "false");
            changed |= Ensure(lines, "skipMultiplayerWarning", "true");

            if (changed)
            {
                AtomicFile.WriteAllLines(path, lines);
            }
        }
        catch (Exception)
        {
            // Game options are a convenience; a failure must not stop the launch.
        }
    }

    /// <summary>
    /// Writes the graphics settings of a preset. Only the keys that cost frames are
    /// touched: view distance, clouds, particles, shadows, smooth lighting, mipmaps.
    /// Controls, sound and everything else stay as the player left them.
    /// </summary>
    /// <remarks>
    /// The file is edited as bytes (<see cref="OptionsFile"/>): an old game's options.txt
    /// in the system code page keeps every byte the preset does not set. What each key
    /// said before is returned, so the caller can keep it and offer the way back.
    /// </remarks>
    /// <param name="earlier">
    /// The record of the preset applied before this one, if it was not taken back. A key
    /// that still holds what that preset wrote keeps its older "previous": the way back
    /// leads to before the first preset, not to the one in between.
    /// </param>
    /// <returns>
    /// One record per key that now differs from what it was before the presets. Null when
    /// options.txt cannot be read as text; such a file is left untouched.
    /// </returns>
    public static List<BoostOptionRecord>? ApplyPerformancePreset(
        string gameDirectory,
        PerformancePreset preset,
        IReadOnlyList<BoostOptionRecord>? earlier = null)
    {
        Directory.CreateDirectory(gameDirectory);

        var path = Path.Combine(gameDirectory, FileName);

        if (OptionsFile.TryLoad(path) is not { } file)
        {
            return null;
        }

        var records = new List<BoostOptionRecord>();

        foreach (var (key, value) in PresetValues(preset))
        {
            var current = file.Get(key);
            var before = current;

            if (current is not null &&
                earlier?.FirstOrDefault(r => r.Key == key) is { } known &&
                string.Equals(current.Trim(), known.Written.Trim(), StringComparison.Ordinal))
            {
                before = known.Previous;
            }

            if (current is null || !string.Equals(current.Trim(), value, StringComparison.Ordinal))
            {
                file.Set(key, value);
            }

            // A key that ends up where it started has nothing to be taken back.
            if (before is null || !string.Equals(before.Trim(), value, StringComparison.Ordinal))
            {
                records.Add(new BoostOptionRecord { Key = key, Previous = before, Written = value });
            }
        }

        if (file.Changed)
        {
            file.Save(path);
        }

        return records;
    }

    /// <summary>
    /// Takes a preset back, key by key: the value from before it, or no line where there
    /// had been none. A key that no longer says what the preset wrote was changed since,
    /// in the game's menu or by hand, and stays as it is. Null when options.txt cannot be
    /// read as text; nothing is written then.
    /// </summary>
    public static BoostOptionsRevert? RevertPerformancePreset(string gameDirectory, IEnumerable<BoostOptionRecord> records)
    {
        var path = Path.Combine(gameDirectory, FileName);

        if (OptionsFile.TryLoad(path) is not { } file)
        {
            return null;
        }

        var result = BoostOptions.Revert(file, records);

        if (file.Changed)
        {
            file.Save(path);
        }

        return result;
    }

    public static IReadOnlyList<(string Key, string Value)> PresetValues(PerformancePreset preset) => preset switch
    {
        PerformancePreset.Low => new[]
        {
            ("renderDistance", "6"), ("simulationDistance", "5"), ("graphicsMode", "0"), ("particles", "2"),
            ("entityShadows", "false"), ("entityDistanceScaling", "0.5"), ("cloudStatus", "\"off\""),
            ("biomeBlendRadius", "0"), ("mipmapLevels", "0"), ("ao", "false"), ("enableVsync", "false"),
            ("maxFps", "120"), ("screenEffectScale", "0.0")
        },
        PerformancePreset.High => new[]
        {
            ("renderDistance", "16"), ("simulationDistance", "12"), ("graphicsMode", "1"), ("particles", "0"),
            ("entityShadows", "true"), ("entityDistanceScaling", "1.0"), ("cloudStatus", "\"fancy\""),
            ("biomeBlendRadius", "4"), ("mipmapLevels", "4"), ("ao", "true"), ("enableVsync", "true"),
            ("maxFps", "260"), ("screenEffectScale", "1.0")
        },
        _ => new[]
        {
            ("renderDistance", "10"), ("simulationDistance", "8"), ("graphicsMode", "1"), ("particles", "1"),
            ("entityShadows", "true"), ("entityDistanceScaling", "1.0"), ("cloudStatus", "\"fast\""),
            ("biomeBlendRadius", "2"), ("mipmapLevels", "2"), ("ao", "true"), ("enableVsync", "true"),
            ("maxFps", "120"), ("screenEffectScale", "1.0")
        }
    };

    /// <summary>
    /// Carries controls, sound, graphics and interface settings from one build to another.
    /// The resource pack list, the last server and the language stay: they belong to the
    /// folder, not the player. Returns how many keys were written.
    /// </summary>
    public static int CopySettings(string sourceGameDirectory, string targetGameDirectory)
    {
        var source = Path.Combine(sourceGameDirectory, FileName);

        if (!File.Exists(source))
        {
            return 0;
        }

        Directory.CreateDirectory(targetGameDirectory);

        var target = Path.Combine(targetGameDirectory, FileName);
        var lines = File.Exists(target) ? File.ReadAllLines(target).ToList() : new List<string>();
        var copied = 0;

        foreach (var line in File.ReadAllLines(source))
        {
            var colon = line.IndexOf(':');

            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon];

            if (NotCopied.Contains(key))
            {
                continue;
            }

            Set(lines, key, line[(colon + 1)..]);
            copied++;
        }

        AtomicFile.WriteAllLines(target, lines);
        return copied;
    }

    private static bool Ensure(List<string> lines, string key, string value)
    {
        if (lines.Any(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        lines.Add($"{key}:{value}");
        return true;
    }

    private static void Set(List<string> lines, string key, string value)
    {
        var index = lines.FindIndex(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
        var line = $"{key}:{value}";

        if (index >= 0)
        {
            lines[index] = line;
        }
        else
        {
            lines.Add(line);
        }
    }
}
