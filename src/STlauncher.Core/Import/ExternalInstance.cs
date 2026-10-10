using System;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Import;

/// <summary>Where a found build came from. Shown to the player, so they recognise it.</summary>
public enum ExternalLauncherKind
{
    Unknown,

    /// <summary>A plain .minecraft folder: the official launcher, TLauncher, Legacy Launcher and friends.</summary>
    DotMinecraft,

    Prism,
    PolyMc,
    MultiMc,
    CurseForge,
    Modrinth,
    GdLauncher,
    AtLauncher,
    Ftb,
    Technic,
    Xmcl
}

/// <summary>Why a found folder cannot be imported as it stands.</summary>
public enum ExternalInstanceProblem
{
    None,

    /// <summary>The version folder has no profile JSON - nothing describes how to launch it.</summary>
    MissingVersionJson,

    /// <summary>The profile JSON is there but unreadable.</summary>
    BrokenVersionJson,

    /// <summary>The profile names no main class and inherits from nothing.</summary>
    IncompleteProfile,

    /// <summary>Already imported: an instance already points at this folder and version.</summary>
    AlreadyImported,

    /// <summary>
    /// The folder is a symbolic link or a junction. The import never walks through one
    /// inside a tree, and through a root only when it leads to an ordinary local folder
    /// (see <see cref="LinkedRootResolver"/>); otherwise the build is listed with where
    /// the link points and left alone.
    /// </summary>
    SourceIsLink
}

/// <summary>
/// A build found in another launcher's files.
/// </summary>
/// <param name="GameDirectory">
/// Where saves, mods and configs live. Several builds of a .minecraft-style launcher
/// share one, which is worth telling the player before they import all of them.
/// </param>
/// <param name="VersionId">
/// The id the build launches by. For a .minecraft profile that is the profile's id (a
/// free-form name); for every other launcher it is the Minecraft version itself. Empty
/// when nothing on disk says which version the build is - the player picks one after
/// importing.
/// </param>
/// <param name="VersionJsonPath">
/// The profile the game is launched by. Importing copies this (a few hundred kilobytes)
/// rather than the whole instance.
/// </param>
public sealed record ExternalInstance(
    string Name,
    string GameDirectory,
    string VersionId,
    LoaderKind Loader,
    ExternalLauncherKind Source,
    string? VersionJsonPath,
    int ModCount,
    ExternalInstanceProblem Problem = ExternalInstanceProblem.None)
{
    /// <summary>
    /// The Minecraft version the build runs, when it differs from <see cref="VersionId"/>:
    /// a .minecraft profile is named whatever its author liked ("fabric 1.21.11 shield"),
    /// and the mod catalog needs the real version to offer anything. Null when unknown.
    /// </summary>
    public string? GameVersion { get; init; }

    /// <summary>The loader build the launcher pins, when it says so.</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>
    /// The launcher's own name when it is not one the launcher kind already says: a fork
    /// of Prism found by its files is "PineconeMC" to its player, not "Prism Launcher".
    /// </summary>
    public string? LauncherName { get; init; }

    /// <summary>
    /// True when the version and loader were worked out from the mod files themselves,
    /// because the launcher kept no readable description. Right in practice, but worth
    /// a glance from the player.
    /// </summary>
    public bool VersionInferred { get; init; }

    /// <summary>
    /// True when this build's files live in a folder of its own even though it came from
    /// a .minecraft-style launcher: TLauncher's per-version folders, the official
    /// launcher's "game directory" profile setting.
    /// </summary>
    public bool HasOwnFolder { get; init; }

    /// <summary>
    /// What the build had set for itself in the old launcher - memory, window size, JVM
    /// arguments that passed the allowlist. Null when it overrode nothing.
    /// </summary>
    public CarriedSettings? Settings { get; init; }

    /// <summary>Where the link points, when the problem is <see cref="ExternalInstanceProblem.SourceIsLink"/>.</summary>
    public string? LinkTarget { get; init; }

    /// <summary>
    /// Why a root that is a link was not read through, when that is the reason it is
    /// listed as a problem: it leads nowhere, to another computer, into the system.
    /// <see cref="LinkedRootProblem.None"/> for a link inside a tree, which is never followed.
    /// </summary>
    public LinkedRootProblem LinkProblem { get; init; }

    /// <summary>
    /// The link the scan came in by, when the launcher's root folder was one: the path
    /// the other launcher knows, while <see cref="GameDirectory"/> is the real folder
    /// behind it. The import screen shows both, so what will be read is not a surprise.
    /// </summary>
    public string? ReachedThrough { get; init; }

    /// <summary>
    /// True when the build is started by its own profile JSON rather than assembled by
    /// the launcher from a version and a loader. Such a build must launch by that
    /// profile: re-installing a loader over it would throw away what makes it the build.
    /// </summary>
    public bool HasOwnProfile => VersionJsonPath is not null;

    /// <summary>False when the build needs attention before it can be imported.</summary>
    public bool IsUsable => Problem == ExternalInstanceProblem.None;

    /// <summary>False when the player will have to pick the version by hand after importing.</summary>
    public bool HasKnownVersion => !string.IsNullOrWhiteSpace(VersionId) || !string.IsNullOrWhiteSpace(GameVersion);

    /// <summary>
    /// True when other builds of the same launcher write into this very folder. Importing
    /// in place then means the two launchers share saves and mods.
    /// </summary>
    public bool SharesGameDirectory => Source is ExternalLauncherKind.DotMinecraft && !HasOwnFolder;
}
