using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Mods;

/// <summary>"Offer mod updates": how often the launcher brings up updates for the player's own mods.</summary>
public enum ModUpdateFrequency
{
    /// <summary>Whenever there are some (the check itself still runs at most once a day).</summary>
    Always,

    /// <summary>At most once a day per build. The default.</summary>
    Daily,

    /// <summary>At most once a week per build.</summary>
    Weekly,

    /// <summary>Never by itself; the "check for updates" button still works.</summary>
    Never
}

/// <summary>An update found for one of the player's mods, kept so a restart does not need the network.</summary>
public sealed class KnownModUpdate
{
    [JsonPropertyName("file")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string VersionNumber { get; set; } = string.Empty;

    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }
}

/// <summary>What a build remembers about mod updates: when it last looked, what it found, what the player skipped.</summary>
public sealed class ModUpdateState
{
    [JsonPropertyName("lastCheckedAt")]
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>When the home screen last said "there are updates" for this build.</summary>
    [JsonPropertyName("lastNoticeAt")]
    public DateTimeOffset? LastNoticeAt { get; set; }

    [JsonPropertyName("found")]
    public List<KnownModUpdate> Found { get; set; } = new();

    /// <summary>"Skip this version": the mod's project id (or file name) and the version id skipped.</summary>
    [JsonPropertyName("skipped")]
    public Dictionary<string, string> Skipped { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// When the launcher looks for mod updates and when it says so. The rules the players
/// asked for: the server's pinned mods are never offered (the catalog decides those);
/// no popup and no question at launch; a check at most once a day per build; a line on
/// the home screen at most as often as the setting says; a skipped version stays skipped
/// until a newer one appears.
/// </summary>
public static class ModUpdatePolicy
{
    public static readonly TimeSpan Day = TimeSpan.FromDays(1);
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    /// <summary>True when the build may be checked by itself now. A check asked for by hand is not asked here.</summary>
    public static bool ShouldCheck(ModUpdateFrequency frequency, DateTimeOffset? lastCheckedAt, DateTimeOffset now)
    {
        if (frequency == ModUpdateFrequency.Never)
        {
            return false;
        }

        var interval = frequency == ModUpdateFrequency.Weekly ? Week : Day;
        return lastCheckedAt is null || now - lastCheckedAt.Value >= interval || lastCheckedAt.Value > now;
    }

    /// <summary>True when the home screen may bring the updates up now.</summary>
    public static bool ShouldShowNotice(ModUpdateFrequency frequency, DateTimeOffset? lastNoticeAt, DateTimeOffset now)
    {
        switch (frequency)
        {
            case ModUpdateFrequency.Never:
                return false;
            case ModUpdateFrequency.Always:
                return true;
        }

        var interval = frequency == ModUpdateFrequency.Weekly ? Week : Day;
        return lastNoticeAt is null || now - lastNoticeAt.Value >= interval || lastNoticeAt.Value > now;
    }

    /// <summary>The key a skipped version is kept under: the Modrinth project when known, the file otherwise.</summary>
    public static string SkipKey(string? projectId, string fileName)
        => !string.IsNullOrWhiteSpace(projectId) ? projectId! : BuildWarningDismissals.Normalize(fileName);

    /// <summary>
    /// Whether a newer version is offered at all. A mod the server catalog pins is not:
    /// moving it off the server's version breaks the match with the server. A version the
    /// player skipped is not either - until a different one is the newest.
    /// </summary>
    public static bool IsOffered(bool pinnedByCatalog, string newestVersionId, string? skippedVersionId)
        => !pinnedByCatalog &&
           !string.IsNullOrWhiteSpace(newestVersionId) &&
           !string.Equals(newestVersionId, skippedVersionId, StringComparison.Ordinal);

    /// <summary>
    /// A fingerprint of what the catalog would change in a build, independent of the
    /// language the lines are written in. "Not now" is remembered against it: the same
    /// changes are not offered again, a new catalog revision is.
    /// </summary>
    public static string CatalogOfferKey(string? buildId, IEnumerable<string> parts)
    {
        var text = (buildId ?? string.Empty) + "\n" + string.Join("\n", parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>True when the offer should be put on screen: the player has not said "not now" to this one.</summary>
    public static bool ShouldOfferCatalog(string offerKey, string? declinedKey)
        => !string.Equals(offerKey, declinedKey, StringComparison.Ordinal);
}
