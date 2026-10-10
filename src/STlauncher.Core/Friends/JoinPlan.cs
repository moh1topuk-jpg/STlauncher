using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Friends;

/// <summary>One of the player's builds, as much of it as choosing a build for a server needs.</summary>
/// <param name="GameVersion">The Minecraft version, or null when the build does not say; such a build never fits.</param>
/// <param name="MadeForServer">The build the invite brought, or the one the server was made from.</param>
public sealed record JoinBuild(string Id, string? GameVersion, LoaderKind Loader, bool MadeForServer = false);

public enum JoinAction
{
    /// <summary>The build made for this server: one that is already here, or the one the invite brings.</summary>
    UseDedicated,

    /// <summary>A build the player already has and that fits the server.</summary>
    UseExisting,

    /// <summary>Nothing fits: a clean build of the server's version and loader, after the player agrees.</summary>
    CreateClean,

    /// <summary>The launcher cannot decide. Nothing starts unless the player says what to start.</summary>
    Ask
}

public enum JoinQuestion
{
    None,

    /// <summary>Nobody told the launcher which version the server runs.</summary>
    VersionUnknown,

    /// <summary>The version is known, no build fits it, and a clean one cannot be made here.</summary>
    NoBuildFits
}

/// <param name="BuildId">
/// The build to start. Null for <see cref="JoinAction.UseDedicated"/> means the build comes
/// with the invite and is not on this computer yet. For <see cref="JoinAction.Ask"/> it is
/// the build that may be offered to the player, if there is one.
/// </param>
/// <param name="GameVersion">For <see cref="JoinAction.CreateClean"/>: the version of the build to make.</param>
/// <param name="Loader">For <see cref="JoinAction.CreateClean"/>: its loader.</param>
public sealed record JoinDecision(
    JoinAction Action,
    string? BuildId = null,
    string? GameVersion = null,
    LoaderKind Loader = LoaderKind.Vanilla,
    JoinQuestion Question = JoinQuestion.None)
{
    /// <summary>The game may be started with <see cref="BuildId"/> without asking anything more.</summary>
    public bool Launches => Action is JoinAction.UseDedicated or JoinAction.UseExisting && BuildId is not null;

    /// <summary>The build is the one inside the invite; it has to be added first.</summary>
    public bool ComesWithInvite => Action == JoinAction.UseDedicated && BuildId is null;
}

/// <summary>
/// Which build to join a server with. One place for every way into a server - a friend's
/// invite, the player's own server - so that none of them sends a Forge modpack to a
/// Fabric server or starts a build of another version without saying so.
/// </summary>
public static class JoinPlan
{
    // "1.21.1", "1.21", and not the "1.21" of "1.21.x" or of a longer number.
    private static readonly Regex ReleaseVersion = new(@"(?<![\d.])\d+\.\d+(?:\.\d+)?(?!\d|\.[\dxX*])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SnapshotVersion = new(@"(?<![\dA-Za-z])\d{2}w\d{2}[a-z](?![\dA-Za-z])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <param name="serverVersion">The server's Minecraft version; null or empty when it is not known.</param>
    /// <param name="serverLoader">
    /// The server's loader; null when it is not known. An unknown loader is treated as a
    /// plain server: that is what an address without an invite almost always is, and a
    /// modded server turns a plain client away with a message of its own.
    /// </param>
    /// <param name="inviteBringsBuild">The invite carries the host's build.</param>
    /// <param name="builds">The player's builds, in the order they should be preferred.</param>
    /// <param name="selectedBuildId">The build selected in the launcher; it goes first among those that fit.</param>
    /// <param name="canCreate">A clean build of the server's version can be made here.</param>
    public static JoinDecision Decide(
        string? serverVersion,
        LoaderKind? serverLoader,
        bool inviteBringsBuild,
        IReadOnlyList<JoinBuild>? builds,
        string? selectedBuildId,
        bool canCreate = true)
    {
        builds ??= Array.Empty<JoinBuild>();

        // A build made for this server is the host's own choice of mods; nothing the
        // launcher could work out is better than that.
        if (builds.FirstOrDefault(b => b.MadeForServer) is { } dedicated)
        {
            return new JoinDecision(JoinAction.UseDedicated, dedicated.Id);
        }

        if (inviteBringsBuild)
        {
            return new JoinDecision(JoinAction.UseDedicated);
        }

        var selected = string.IsNullOrWhiteSpace(selectedBuildId)
            ? null
            : builds.FirstOrDefault(b => string.Equals(b.Id, selectedBuildId, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            // No guessing: the selected build is only what the question may be about.
            return new JoinDecision(JoinAction.Ask, selected?.Id, Question: JoinQuestion.VersionUnknown);
        }

        var version = serverVersion.Trim();
        var fitting = builds.Where(b => Fits(b, version, serverLoader)).ToList();

        if (fitting.Count > 0)
        {
            var exact = serverLoader ?? LoaderKind.Vanilla;

            // The player's own pick first, then a build of exactly the server's loader,
            // then a Fabric build for a plain server.
            var pick = fitting.FirstOrDefault(b => ReferenceEquals(b, selected))
                       ?? fitting.FirstOrDefault(b => b.Loader == exact)
                       ?? fitting[0];

            return new JoinDecision(JoinAction.UseExisting, pick.Id);
        }

        return canCreate
            ? new JoinDecision(JoinAction.CreateClean, GameVersion: version, Loader: serverLoader ?? LoaderKind.Vanilla)
            : new JoinDecision(JoinAction.Ask, Question: JoinQuestion.NoBuildFits);
    }

    /// <summary>
    /// A build fits when it is of the server's game version and of a loader the server
    /// takes: the same one, or, for a plain server, vanilla or Fabric - Fabric mods of the
    /// client side only are what players join plain servers with every day. A build of
    /// any other loader carries mods the server does not have.
    /// </summary>
    public static bool Fits(JoinBuild build, string serverVersion, LoaderKind? serverLoader)
    {
        if (build is null || string.IsNullOrWhiteSpace(build.GameVersion) || string.IsNullOrWhiteSpace(serverVersion))
        {
            return false;
        }

        if (!string.Equals(build.GameVersion.Trim(), serverVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return serverLoader is null or LoaderKind.Vanilla
            ? build.Loader is LoaderKind.Vanilla or LoaderKind.Fabric
            : build.Loader == serverLoader;
    }

    /// <summary>
    /// The game version out of what a server says about itself in its status: "1.21.1",
    /// "Paper 1.21.1". Null when it names no version or more than one ("1.8.x-1.21.x" is
    /// a proxy that takes many; which one to play it with is not for the launcher to guess).
    /// </summary>
    public static string? VersionFromStatus(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return null;
        }

        var found = ReleaseVersion.Matches(versionName)
            .Concat(SnapshotVersion.Matches(versionName))
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A range written with "x" leaves no whole version behind, or one end of it only.
        if (found.Count != 1 || versionName.Contains(".x", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return found[0];
    }
}
