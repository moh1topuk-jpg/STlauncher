using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace STlauncher.Core.Auth;

/// <summary>
/// Builds a readable default nickname. "Player" is technically valid and reads like a
/// placeholder the player forgot to change - which is exactly what it was, because a
/// shared default also means several people on the server share one skin and one
/// offline UUID.
/// </summary>
public static class NicknameGenerator
{
    /// <summary>The name older versions shipped with. Treated as "never chosen".</summary>
    public const string LegacyDefault = "Player";

    private static readonly string[] Adjectives =
    {
        "Brave", "Swift", "Silent", "Lucky", "Wild", "Frost", "Ember", "Storm",
        "Shadow", "Golden", "Iron", "Crimson", "Cosmic", "Rapid", "Noble", "Mystic",
        "Solar", "Lunar", "Nova", "Wander"
    };

    private static readonly string[] Nouns =
    {
        "Fox", "Wolf", "Raven", "Falcon", "Tiger", "Bear", "Otter", "Lynx",
        "Golem", "Phantom", "Miner", "Ranger", "Pilot", "Nomad", "Knight", "Drake",
        "Comet", "Panda", "Hawk", "Viper"
    };

    /// <summary>
    /// A name that always satisfies <see cref="OfflineAuth.IsValidUsername"/>: 3-16
    /// characters of A-Z, a-z, 0-9 and underscore.
    /// </summary>
    public static string Next()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate =
                Pick(Adjectives) + Pick(Nouns) + RandomNumberGenerator.GetInt32(10, 100).ToString();

            if (OfflineAuth.IsValidUsername(candidate))
            {
                return candidate;
            }
        }

        // Every pair above fits, so this is unreachable in practice - but a generator
        // that can return an invalid name is a generator that blocks the Play button.
        return "Player" + RandomNumberGenerator.GetInt32(1000, 10000);
    }

    /// <summary>
    /// A name the player has not chosen: empty, or the placeholder older builds saved
    /// into settings.json for everyone.
    /// </summary>
    public static bool IsPlaceholder(string? username)
        => string.IsNullOrWhiteSpace(username) ||
           string.Equals(username.Trim(), LegacyDefault, StringComparison.OrdinalIgnoreCase);

    /// <summary>Generates a name that is not already in <paramref name="taken"/>.</summary>
    public static string NextUnused(IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var candidate = Next();

            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return Next();
    }

    private static string Pick(string[] source) => source[RandomNumberGenerator.GetInt32(source.Length)];
}
