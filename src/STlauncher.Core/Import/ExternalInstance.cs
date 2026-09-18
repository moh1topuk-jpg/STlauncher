using System;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Import;

/// <summary>Where a found build came from. Shown to the player, so they recognise it.</summary>
public enum ExternalLauncherKind
{
    Unknown,

    /// <summary>A plain .minecraft folder: the official launcher, TLauncher and friends.</summary>
    DotMinecraft,

    Prism,
    MultiMc,
    CurseForge,
    Modrinth,
    GdLauncher,
    AtLauncher
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
    AlreadyImported
}

/// <summary>
/// A build found in another launcher's files.
/// </summary>
/// <param name="GameDirectory">
/// Where saves, mods and configs live. Several builds of a .minecraft-style launcher
/// share one, which is worth telling the player before they import all of them.
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
    /// <summary>False when the build needs attention before it can be imported.</summary>
    public bool IsUsable => Problem == ExternalInstanceProblem.None;

    /// <summary>
    /// True when other builds of the same launcher write into this very folder. Importing
    /// in place then means the two launchers share saves and mods.
    /// </summary>
    public bool SharesGameDirectory => Source is ExternalLauncherKind.DotMinecraft;
}
