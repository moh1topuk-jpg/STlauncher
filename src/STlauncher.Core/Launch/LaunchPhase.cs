namespace STlauncher.Core.Launch;

/// <summary>
/// Where the preparation of a launch is, coarse enough to be shown as a list of steps.
/// The file downloads report their own counts through <see cref="Http.DownloadProgress"/>.
/// </summary>
public enum LaunchPhase
{
    /// <summary>Reading the version's manifest and working out what it needs.</summary>
    Resolving,

    /// <summary>Libraries, the client jar and the assets are being downloaded.</summary>
    Downloading,

    /// <summary>Native libraries are being unpacked.</summary>
    Natives,

    /// <summary>A legacy asset layout is being laid out on disk.</summary>
    Assets,

    /// <summary>The Java runtime the version needs is being found or downloaded.</summary>
    Java,

    /// <summary>Everything is in place; the command line is being assembled.</summary>
    Ready
}
