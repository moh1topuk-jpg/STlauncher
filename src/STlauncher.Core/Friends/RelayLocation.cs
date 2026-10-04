using System;
using STlauncher.Core.Content;

namespace STlauncher.Core.Friends;

/// <summary>
/// Where the relay lives. The catalog names it, so it can be moved without a release;
/// an environment variable overrides that for testing against a relay on the next desk.
/// With neither there is no relay, and the other ways of connecting still work.
/// </summary>
public static class RelayLocation
{
    public const string OverrideVariable = "STLAUNCHER_RELAY";

    /// <summary>The relay to use, or null when none is configured.</summary>
    public static RelayEndpoint? Resolve(ContentCatalog? catalog)
        => Resolve(Environment.GetEnvironmentVariable(OverrideVariable), catalog?.FriendsRelay);

    public static RelayEndpoint? Resolve(string? overrideValue, string? catalogValue)
    {
        // An override that does not parse means "no relay", not "the catalog's one":
        // somebody testing with a typo must not end up on the real relay unawares.
        var text = string.IsNullOrWhiteSpace(overrideValue) ? catalogValue : overrideValue;

        return RelayEndpoint.TryParse(text, out var endpoint) ? endpoint : null;
    }
}
