using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using STlauncher.Core.Boost;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Launch;

/// <summary>What kind of setting a line of options.txt is, for the list the player reads.</summary>
public enum ControlsGroup
{
    Keys,
    ModKeys,
    Mouse,
    Sound,
    Chat,
    Accessibility,
    View
}

/// <summary>One key the transfer would write: what the target says now (null: no line) and what it would say.</summary>
/// <param name="SourceKey">The key as the source spells it, when the versions name it differently.</param>
public sealed record ControlsChange(string Key, string? Current, string Value, ControlsGroup Group, string? SourceKey = null);

/// <param name="Changes">Keys whose value differs and will be written.</param>
/// <param name="Unchanged">Keys of the safe list both builds already agree on.</param>
/// <param name="SkippedModKeys">Mod key bindings the target build does not have: its mods are not those.</param>
/// <param name="KeysSkippedForVersion">
/// The key bindings are written differently on the two sides of 1.13 (numbers before,
/// names since) and are not carried over at all.
/// </param>
public sealed record ControlsPlan(
    IReadOnlyList<ControlsChange> Changes,
    int Unchanged,
    IReadOnlyList<string> SkippedModKeys,
    bool KeysSkippedForVersion)
{
    public bool Any => Changes.Count > 0;
}

/// <summary>
/// The last controls transfer into a build: from where, and per key what was there before
/// and what was written, so that "put it back" is possible. Kept in the build's own file.
/// </summary>
public sealed class ControlsTransferRecord
{
    [JsonPropertyName("fromName")]
    public string? FromName { get; set; }

    [JsonPropertyName("fromId")]
    public string? FromId { get; set; }

    [JsonPropertyName("appliedAt")]
    public DateTimeOffset? AppliedAt { get; set; }

    [JsonPropertyName("options")]
    public List<BoostOptionRecord> Options { get; set; } = new();
}

/// <summary>
/// Carries controls from one build's options.txt into another's: key bindings, mouse,
/// sound volumes, chat and accessibility, field of view and interface scale. A fixed list,
/// on purpose: graphics, the resource pack order, the language and anything else that
/// depends on the build stay where they are.
/// </summary>
/// <remarks>
/// <para>
/// The rule that keeps an old game safe: a build that has been started has every option
/// its game knows written in its options.txt (the game saves the whole file, and stamps it
/// with a "version" line). A key such a file lacks is one its game does not know, and is
/// not added. A build never started gets the whole list: the game reads what it knows and
/// drops the rest on its first save.
/// </para>
/// <para>
/// Key bindings: before 1.13 they were key codes ("key_key.jump:57"), since then names
/// ("key_key.jump:key.keyboard.space"); across that line nothing is carried. Some actions
/// were renamed (swapHands became swapOffhand in 1.16) and follow the target's spelling.
/// A mod's key is carried only when the target has the same key, or was never started:
/// a mod key in a build without the mod is a line nobody reads.
/// </para>
/// <para>
/// Values: the field of view was a fraction (-1..1 around 70°) before 1.19 and degrees
/// since; text values gained quotes then too ("right" vs right). Both are converted to
/// what the target writes.
/// </para>
/// </remarks>
public static class ControlsTransfer
{
    /// <summary>The settings outside key bindings that travel, with their group.</summary>
    public static readonly IReadOnlyDictionary<string, ControlsGroup> SafeOptions = new Dictionary<string, ControlsGroup>(StringComparer.Ordinal)
    {
        ["mouseSensitivity"] = ControlsGroup.Mouse,
        ["invertYMouse"] = ControlsGroup.Mouse,
        ["mouseWheelSensitivity"] = ControlsGroup.Mouse,
        ["rawMouseInput"] = ControlsGroup.Mouse,
        ["discrete_mouse_scroll"] = ControlsGroup.Mouse,
        ["toggleCrouch"] = ControlsGroup.Mouse,
        ["toggleSprint"] = ControlsGroup.Mouse,
        ["autoJump"] = ControlsGroup.Mouse,
        ["attackIndicator"] = ControlsGroup.Mouse,
        ["mainHand"] = ControlsGroup.Mouse,

        ["showSubtitles"] = ControlsGroup.Sound,
        ["directionalAudio"] = ControlsGroup.Sound,

        ["chatVisibility"] = ControlsGroup.Chat,
        ["chatColors"] = ControlsGroup.Chat,
        ["chatLinks"] = ControlsGroup.Chat,
        ["chatLinksPrompt"] = ControlsGroup.Chat,
        ["chatOpacity"] = ControlsGroup.Chat,
        ["textBackgroundOpacity"] = ControlsGroup.Chat,
        ["backgroundForChatOnly"] = ControlsGroup.Chat,
        ["chatScale"] = ControlsGroup.Chat,
        ["chatWidth"] = ControlsGroup.Chat,
        ["chatHeightFocused"] = ControlsGroup.Chat,
        ["chatHeightUnfocused"] = ControlsGroup.Chat,
        ["chatDelay"] = ControlsGroup.Chat,
        ["chatLineSpacing"] = ControlsGroup.Chat,
        ["narrator"] = ControlsGroup.Chat,
        ["narratorHotkey"] = ControlsGroup.Chat,
        ["hideMatchedNames"] = ControlsGroup.Chat,
        ["onlyShowSecureChat"] = ControlsGroup.Chat,

        ["highContrast"] = ControlsGroup.Accessibility,
        ["darkMojangStudiosBackground"] = ControlsGroup.Accessibility,
        ["hideLightningFlashes"] = ControlsGroup.Accessibility,
        ["damageTiltStrength"] = ControlsGroup.Accessibility,
        ["fovEffectScale"] = ControlsGroup.Accessibility,
        ["darknessEffectScale"] = ControlsGroup.Accessibility,
        ["glintSpeed"] = ControlsGroup.Accessibility,
        ["glintStrength"] = ControlsGroup.Accessibility,
        ["notificationDisplayTime"] = ControlsGroup.Accessibility,
        ["panoramaScrollSpeed"] = ControlsGroup.Accessibility,
        ["hideSplashTexts"] = ControlsGroup.Accessibility,
        ["rotateWithMinecart"] = ControlsGroup.Accessibility,
        ["showAutosaveIndicator"] = ControlsGroup.Accessibility,

        ["fov"] = ControlsGroup.View,
        ["guiScale"] = ControlsGroup.View
    };

    /// <summary>Sound volumes: one line per category, the categories vary with the version.</summary>
    public const string SoundPrefix = "soundCategory_";

    public const string KeyPrefix = "key_";

    /// <summary>The game's own actions, every version since 1.0 included. Anything else after "key_" is a mod's.</summary>
    public static readonly IReadOnlySet<string> VanillaKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "key_key.attack", "key_key.use", "key_key.forward", "key_key.left", "key_key.back", "key_key.right",
        "key_key.jump", "key_key.sneak", "key_key.sprint", "key_key.drop", "key_key.inventory", "key_key.chat",
        "key_key.playerlist", "key_key.pickItem", "key_key.command", "key_key.socialInteractions",
        "key_key.screenshot", "key_key.togglePerspective", "key_key.smoothCamera", "key_key.fullscreen",
        "key_key.spectatorOutlines", "key_key.spectatorHotbar", "key_key.swapOffhand", "key_key.swapHands",
        "key_key.saveToolbarActivator", "key_key.loadToolbarActivator", "key_key.advancements",
        "key_key.quickActions", "key_key.toggleGui", "key_key.toggleSpectatorShaderEffects",
        "key_key.streamStartStop", "key_key.streamPauseUnpause", "key_key.streamCommercial", "key_key.streamToggleMic"
    };

    /// <summary>Actions the game renamed: the older spelling, the newer one, and the release that changed it.</summary>
    public static readonly IReadOnlyList<(string Old, string New, string Since)> RenamedKeys = new[]
    {
        ("key_key.swapHands", "key_key.swapOffhand", "1.16")
    };

    private static readonly Regex ReleaseVersion = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>True for a key the transfer may carry at all.</summary>
    public static bool IsSafeKey(string key)
        => key.StartsWith(KeyPrefix, StringComparison.Ordinal) ||
           key.StartsWith(SoundPrefix, StringComparison.Ordinal) ||
           SafeOptions.ContainsKey(key);

    public static bool IsVanillaKeyBinding(string key)
        => VanillaKeys.Contains(key) ||
           key.StartsWith("key_key.hotbar.", StringComparison.Ordinal) ||
           key.StartsWith("key_key.debug.", StringComparison.Ordinal);

    public static ControlsGroup GroupOf(string key)
    {
        if (key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return IsVanillaKeyBinding(key) ? ControlsGroup.Keys : ControlsGroup.ModKeys;
        }

        if (key.StartsWith(SoundPrefix, StringComparison.Ordinal))
        {
            return ControlsGroup.Sound;
        }

        return SafeOptions.TryGetValue(key, out var group) ? group : ControlsGroup.View;
    }

    /// <summary>
    /// What the transfer would write into <paramref name="target"/>. Changes nothing.
    /// </summary>
    /// <param name="sourceVersion">The source build's game version ("1.21.11"); null or a snapshot when not known.</param>
    /// <param name="targetVersion">The same for the target.</param>
    public static ControlsPlan Plan(OptionsFile source, string? sourceVersion, OptionsFile target, string? targetVersion)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        var neverStarted = target.Get("version") is null;
        var targetKeys = new HashSet<string>(target.Keys(), StringComparer.Ordinal);

        var sourceNamed = NamedKeys(source, sourceVersion);
        var targetNamed = targetKeys.Any(k => k.StartsWith(KeyPrefix, StringComparison.Ordinal))
            ? NamedKeys(target, targetVersion)
            : NamedKeysByVersion(targetVersion) ?? sourceNamed;
        var keysAcross = sourceNamed != targetNamed;

        var changes = new List<ControlsChange>();
        var skippedMods = new List<string>();
        var unchanged = 0;

        foreach (var sourceKey in source.Keys())
        {
            if (!IsSafeKey(sourceKey) || source.Get(sourceKey) is not { } raw)
            {
                continue;
            }

            var isBinding = sourceKey.StartsWith(KeyPrefix, StringComparison.Ordinal);

            if (isBinding && keysAcross)
            {
                continue;
            }

            var key = isBinding ? TargetSpelling(sourceKey, targetKeys, targetVersion, neverStarted) : sourceKey;
            var known = targetKeys.Contains(key);

            if (!known && !neverStarted)
            {
                // A started game wrote every option it knows: this one it does not.
                if (isBinding && !IsVanillaKeyBinding(sourceKey))
                {
                    skippedMods.Add(sourceKey);
                }

                continue;
            }

            var current = target.Get(key);
            var value = Convert(key, raw, source, sourceVersion, current, targetVersion);

            if (value is null)
            {
                continue;
            }

            if (current is not null && string.Equals(current.Trim(), value.Trim(), StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            changes.Add(new ControlsChange(key, current, value, GroupOf(key), key == sourceKey ? null : sourceKey));
        }

        return new ControlsPlan(changes, unchanged, skippedMods, keysAcross);
    }

    /// <summary>
    /// Writes the planned values into the file in memory and returns the record of them. A
    /// key that still holds what an earlier transfer wrote keeps that transfer's "previous":
    /// "put it back" leads to before the first transfer, not to the one in between.
    /// </summary>
    public static List<BoostOptionRecord> Apply(OptionsFile target, IEnumerable<ControlsChange> changes, IReadOnlyList<BoostOptionRecord>? earlier = null)
    {
        var records = new List<BoostOptionRecord>();
        var applied = new HashSet<string>(StringComparer.Ordinal);

        foreach (var change in changes)
        {
            var before = change.Current;

            if (before is not null &&
                earlier?.FirstOrDefault(r => r.Key == change.Key) is { } known &&
                string.Equals(before.Trim(), known.Written.Trim(), StringComparison.Ordinal))
            {
                before = known.Previous;
            }

            target.Set(change.Key, change.Value);
            applied.Add(change.Key);
            records.Add(new BoostOptionRecord { Key = change.Key, Previous = before, Written = change.Value });
        }

        // Keys of the earlier transfer this one did not touch keep their way back.
        if (earlier is not null)
        {
            records.AddRange(earlier.Where(r => !applied.Contains(r.Key)));
        }

        return records;
    }

    /// <summary>Puts back what a transfer wrote, key by key; a key changed since stays as the player set it.</summary>
    public static BoostOptionsRevert Revert(OptionsFile target, IEnumerable<BoostOptionRecord> records)
        => BoostOptions.Revert(target, records);

    /// <summary>The spelling of a key binding the target uses, where the game renamed the action.</summary>
    private static string TargetSpelling(string sourceKey, IReadOnlySet<string> targetKeys, string? targetVersion, bool neverStarted)
    {
        foreach (var (old, renamed, since) in RenamedKeys)
        {
            string? other = sourceKey == old ? renamed : sourceKey == renamed ? old : null;

            if (other is null)
            {
                continue;
            }

            if (targetKeys.Contains(sourceKey))
            {
                return sourceKey;
            }

            if (targetKeys.Contains(other))
            {
                return other;
            }

            if (neverStarted && IsAtLeast(targetVersion, since) is { } newer)
            {
                return newer ? renamed : old;
            }
        }

        return sourceKey;
    }

    /// <summary>The value as the target writes it; null when it cannot be carried.</summary>
    private static string? Convert(string key, string raw, OptionsFile source, string? sourceVersion, string? current, string? targetVersion)
    {
        var value = raw.Trim();

        if (key == "fov")
        {
            var sourceDegrees = FovIsDegrees(value, sourceVersion);
            var targetDegrees = current is not null ? FovIsDegrees(current.Trim(), targetVersion) : IsAtLeast(targetVersion, "1.19") ?? sourceDegrees;

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            if (sourceDegrees == targetDegrees)
            {
                return value;
            }

            return targetDegrees
                ? Math.Round(70 + number * 40).ToString(CultureInfo.InvariantCulture)
                : ((Math.Clamp(number, 30, 110) - 70) / 40).ToString("0.0###", CultureInfo.InvariantCulture);
        }

        // Text values: quoted since 1.19 ("right"), bare before. The target's own way wins.
        if (key == "mainHand")
        {
            var bare = value.Trim('"');
            var quoted = current is not null ? current.Trim().StartsWith('"') : IsAtLeast(targetVersion, "1.19") ?? value.StartsWith('"');
            return quoted ? "\"" + bare + "\"" : bare;
        }

        return value;
    }

    /// <summary>Degrees (1.19 and later) or the old fraction, by the value first and the version second.</summary>
    private static bool FovIsDegrees(string value, string? version)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (Math.Abs(number) > 1.0001)
            {
                return true;
            }

            if (value.Contains('.'))
            {
                return false;
            }
        }

        return IsAtLeast(version, "1.19") ?? true;
    }

    /// <summary>Key bindings by name (1.13 and later) or by code: by the values in the file, else by the version.</summary>
    private static bool NamedKeys(OptionsFile file, string? version)
    {
        foreach (var key in file.Keys())
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || file.Get(key) is not { } value)
            {
                continue;
            }

            value = value.Trim();

            if (value.StartsWith("key.", StringComparison.Ordinal))
            {
                return true;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return NamedKeysByVersion(version) ?? true;
    }

    private static bool? NamedKeysByVersion(string? version) => IsAtLeast(version, "1.13");

    /// <summary>Null when the version is not a release number that can be compared.</summary>
    private static bool? IsAtLeast(string? version, string release)
        => version is not null && ReleaseVersion.IsMatch(version)
            ? VersionRange.CompareVersions(version, release) >= 0
            : null;
}
