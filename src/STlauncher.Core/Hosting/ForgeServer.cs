using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Hosting;

/// <summary>
/// Where Forge and NeoForge keep things: the address of an installer, and what the
/// installer leaves in a server's folder. Two layouts exist. Since Minecraft 1.17 the
/// installer writes argument files under <c>libraries/</c> and the server is started as
/// <c>java @user_jvm_args.txt @libraries/.../win_args.txt nogui</c>; before that it
/// leaves a <c>forge-&lt;version&gt;.jar</c> that is started with <c>-jar</c>.
/// </summary>
public static class ForgeServer
{
    public const string UserJvmArgsFile = "user_jvm_args.txt";
    public const string WindowsArgsFile = "win_args.txt";
    public const string UnixArgsFile = "unix_args.txt";

    private const string ForgeMaven = "https://maven.minecraftforge.net/";
    private const string NeoForgeMaven = "https://maven.neoforged.net/releases/";

    /// <summary>True for the loaders whose server is put together by running their installer.</summary>
    public static bool IsForgeLike(LoaderKind loader) => loader is LoaderKind.Forge or LoaderKind.NeoForge;

    /// <summary>The maven repository the loader's installer comes from, and most of what it downloads.</summary>
    public static string MavenRoot(LoaderKind loader) => loader == LoaderKind.NeoForge ? NeoForgeMaven : ForgeMaven;

    /// <summary>
    /// The version as the maven spells it. Forge's always carries the game version in
    /// front ("1.21.1-52.1.16") while builds usually remember only the second half;
    /// NeoForge's stands on its own ("21.1.255").
    /// </summary>
    public static string MavenVersion(LoaderKind loader, string gameVersion, string loaderVersion)
    {
        var version = (loaderVersion ?? string.Empty).Trim();

        return loader == LoaderKind.Forge && !version.StartsWith(gameVersion + "-", StringComparison.Ordinal)
            ? gameVersion + "-" + version
            : version;
    }

    public static string InstallerUrl(LoaderKind loader, string mavenVersion)
    {
        var (group, artifact) = Coordinates(loader, mavenVersion);
        var version = Uri.EscapeDataString(mavenVersion);

        return $"{MavenRoot(loader)}{group}/{artifact}/{version}/{artifact}-{version}-installer.jar";
    }

    /// <summary>The folder, relative to the server's, where the installer puts the argument files.</summary>
    public static string ArgsDirectory(LoaderKind loader, string mavenVersion)
    {
        var (group, artifact) = Coordinates(loader, mavenVersion);
        return $"libraries/{group}/{artifact}/{mavenVersion}";
    }

    /// <summary>
    /// What starts the server the installer left in <paramref name="serverDirectory"/>,
    /// relative to it: the argument file for this system when there is one, otherwise
    /// the jar of the older layout. Null when the installer left neither - which is what
    /// a failed install looks like.
    /// </summary>
    public static string? FindLaunchTarget(string serverDirectory, LoaderKind loader, string mavenVersion, bool? windows = null)
    {
        var argsDirectory = ArgsDirectory(loader, mavenVersion);
        var args = argsDirectory + "/" + ((windows ?? OperatingSystem.IsWindows()) ? WindowsArgsFile : UnixArgsFile);

        if (File.Exists(Path.Combine(serverDirectory, args)))
        {
            return args;
        }

        // Through the years: "-universal" up to the middle of 1.12.2, the bare name up
        // to 1.16.5, and "-shim" on the builds that have one beside the argument files.
        var jars = new[]
        {
            $"forge-{mavenVersion}.jar",
            $"forge-{mavenVersion}-universal.jar",
            $"forge-{mavenVersion}-shim.jar"
        };

        return jars.FirstOrDefault(jar => File.Exists(Path.Combine(serverDirectory, jar)));
    }

    /// <summary>True when what starts the server is an argument file and not a jar.</summary>
    public static bool IsArgsFile(string? launchTarget)
        => launchTarget is not null &&
           (launchTarget.EndsWith("/" + WindowsArgsFile, StringComparison.OrdinalIgnoreCase) ||
            launchTarget.EndsWith("/" + UnixArgsFile, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The argument file for the system the server is being started on. The installer
    /// writes both, so a server's folder carried to another system still starts.
    /// </summary>
    public static string ArgsFileFor(string launchTarget, bool windows)
        => launchTarget[..(launchTarget.LastIndexOf('/') + 1)] + (windows ? WindowsArgsFile : UnixArgsFile);

    /// <summary>
    /// True when the player has set a heap limit in user_jvm_args.txt themselves. As the
    /// installer writes it the file holds only comments, "# -Xmx4G" among them.
    /// </summary>
    public static bool SetsHeapLimit(string userJvmArgsPath)
    {
        try
        {
            return File.Exists(userJvmArgsPath) &&
                   File.ReadLines(userJvmArgsPath)
                       .Select(line => line.Trim())
                       .Where(line => !line.StartsWith('#'))
                       .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                       .Any(token => token.StartsWith("-Xmx", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string Group, string Artifact) Coordinates(LoaderKind loader, string mavenVersion)
    {
        if (loader != LoaderKind.NeoForge)
        {
            return ("net/minecraftforge", "forge");
        }

        // NeoForge for 1.20.1 was still published as "forge", with Forge's numbering;
        // its own numbers ("20.2.x" onwards) never start with the game's "1.".
        return mavenVersion.StartsWith("1.", StringComparison.Ordinal) && mavenVersion.Contains('-')
            ? ("net/neoforged", "forge")
            : ("net/neoforged", "neoforge");
    }
}
