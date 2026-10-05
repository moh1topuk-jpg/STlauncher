using System;
using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Http;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Hosting;

public enum ServerInstallStatus
{
    /// <summary>Everything is known; the plan can be shown to the player and installed.</summary>
    Ready,

    /// <summary>Quilt servers are not in this version of the launcher.</summary>
    UnsupportedLoader,

    /// <summary>Mojang's list has no such game version.</summary>
    UnknownGameVersion,

    /// <summary>The version exists but Mojang publishes no server for it (very old ones).</summary>
    NoServerForVersion,

    /// <summary>The loader has no build for this game version.</summary>
    LoaderUnavailable,

    /// <summary>The lists could not be fetched; see <see cref="ServerInstallPlan.Failure"/>.</summary>
    NetworkError
}

public enum ServerDownloadKind
{
    /// <summary>Mojang's server jar.</summary>
    ServerJar,

    /// <summary>The small jar that starts a loader's server.</summary>
    LoaderLauncher,

    /// <summary>
    /// The loader's own libraries. Never fetched by the launcher: Fabric's starter gets
    /// them the first time the server starts, Forge's installer while it runs.
    /// </summary>
    LoaderLibraries,

    /// <summary>The installer jar of Forge or NeoForge, which is run once to put the server together.</summary>
    LoaderInstaller,

    /// <summary>A Java runtime, when the machine has none that fits the game version.</summary>
    JavaRuntime
}

/// <summary>
/// One thing that will be downloaded: what, from where and how big, so the screen can
/// say so before the player agrees.
/// </summary>
/// <param name="Host">The site it comes from, e.g. "piston-data.mojang.com".</param>
/// <param name="SizeIsExact">False when the source does not state a size and <paramref name="SizeBytes"/> is an estimate.</param>
/// <param name="AtFirstStart">
/// True for what is fetched not by the launcher during the install but by the server
/// itself when it is first started.
/// </param>
/// <param name="ByInstaller">
/// True for what the loader's installer downloads while it runs: the launcher does not
/// fetch it, but it does come down during the install and the player is told so.
/// </param>
public sealed record ServerDownload(
    ServerDownloadKind Kind,
    string Url,
    string Host,
    long SizeBytes,
    bool SizeIsExact,
    string? Sha1 = null,
    bool AtFirstStart = false,
    bool ByInstaller = false);

/// <summary>
/// What installing a server would take, worked out without downloading any of it.
/// </summary>
public sealed record ServerInstallPlan(
    ServerInstallStatus Status,
    string GameVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    string? InstallerVersion,
    int JavaMajor,
    IReadOnlyList<ServerDownload> Downloads,
    NetworkFailure? Failure = null)
{
    public bool CanInstall => Status == ServerInstallStatus.Ready;

    public long TotalBytes => Downloads.Sum(d => d.SizeBytes);

    /// <summary>False when any part is an estimate: the screen should say "about".</summary>
    public bool SizeIsExact => Downloads.All(d => d.SizeIsExact);

    /// <summary>
    /// True when installing means running the loader's own installer, which needs Java
    /// already at install time and not only when the server starts.
    /// </summary>
    public bool RunsInstaller => Downloads.Any(d => d.Kind == ServerDownloadKind.LoaderInstaller);

    /// <summary>What the loader's installer will download itself, in bytes; partly an estimate.</summary>
    public long InstallerFetchesBytes => Downloads.Where(d => d.ByInstaller).Sum(d => d.SizeBytes);

    internal static ServerInstallPlan Failed(
        ServerInstallStatus status,
        string gameVersion,
        LoaderKind loader,
        string? loaderVersion,
        NetworkFailure? failure = null)
        => new(status, gameVersion, loader, loaderVersion, null, 0, Array.Empty<ServerDownload>(), failure);
}

public enum ServerInstallOutcome
{
    Installed,

    /// <summary>The plan was not <see cref="ServerInstallStatus.Ready"/>, or was made for another version or loader.</summary>
    PlanNotReady,

    /// <summary>A file could not be fetched or did not match its checksum; see <see cref="ServerInstallResult.Failure"/>.</summary>
    DownloadFailed,

    /// <summary>The installer needs a Java this machine does not have, and it could not be downloaded.</summary>
    JavaUnavailable,

    /// <summary>
    /// The loader's installer ran and did not finish: it exited with an error, ran out
    /// of time, or left no server behind. <see cref="ServerInstallResult.Detail"/> has
    /// its last words. The server stays marked as not installed.
    /// </summary>
    InstallerFailed
}

/// <param name="Detail">For a failed installer or a missing Java: what went wrong, in the installer's or the system's words.</param>
/// <param name="InstallerLog">The end of the installer's output, when one was run.</param>
public sealed record ServerInstallResult(
    ServerInstallOutcome Outcome,
    NetworkFailure? Failure = null,
    string? Detail = null,
    IReadOnlyList<string>? InstallerLog = null)
{
    public bool Succeeded => Outcome == ServerInstallOutcome.Installed;
}

/// <summary>How far the install has got. Bytes are of the current file; totals are zero when its size is unknown.</summary>
/// <param name="InstallerRunning">True while the loader's installer is at work: there are no bytes to count, only its output.</param>
/// <param name="InstallerLine">One line the installer printed, for a console; null for an ordinary progress report.</param>
public sealed record ServerInstallProgress(
    ServerDownloadKind Kind,
    int FileIndex,
    int FileCount,
    long BytesDone,
    long BytesTotal,
    bool InstallerRunning = false,
    string? InstallerLine = null);
