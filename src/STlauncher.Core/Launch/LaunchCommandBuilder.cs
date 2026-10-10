using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Metadata;

namespace STlauncher.Core.Launch;

public static class LaunchCommandBuilder
{
    private static readonly string[] DefaultJvmArguments =
    {
        "-Djava.library.path=${natives_directory}",
        "-Dminecraft.launcher.brand=${launcher_name}",
        "-Dminecraft.launcher.version=${launcher_version}",
        "-cp",
        "${classpath}"
    };

    public static LaunchCommand Build(LaunchOptions options, RuleContext context)
    {
        var version = options.Version;
        var values = BuildValueMap(options);
        var arguments = new List<string>();

        arguments.Add($"-Xmx{options.MaxMemoryMb}M");
        arguments.Add($"-Xms{options.MinMemoryMb}M");
        arguments.AddRange(options.ExtraJvmArgs);

        var jvmArguments = (version.JvmArguments.Count > 0
            ? Expand(version.JvmArguments, context, values)
            : ExpandDefaults(DefaultJvmArguments, values)).ToList();

        arguments.AddRange(OutputEncodingArguments(options.ExtraJvmArgs.Concat(jvmArguments)));

        // Log4Shell (CVE-2021-44228): a chat message could make an unpatched game run code.
        // This switch closes it for 1.17 to 1.18; the game ignores it elsewhere. It goes
        // after the player's own arguments so nothing typed there can turn it back on.
        arguments.Add(Log4ShellGuard);

        // Older versions carry a log4j that does not know the switch. For them Mojang
        // published a patched logging configuration; the version file names it.
        if (options.LoggingConfigPath is { Length: > 0 } loggingPath &&
            version.Logging?.Client?.Argument is { Length: > 0 } loggingArgument)
        {
            arguments.Add(loggingArgument.Replace("${path}", loggingPath, StringComparison.Ordinal));
        }

        arguments.AddRange(jvmArguments);

        // A profile whose arguments carry no classpath - a dialect the parser does not
        // know, or a hand-edited file - would start Java with nothing to load. The
        // classpath is the one argument the launcher can always supply itself.
        if (!jvmArguments.Any(a => a is "-cp" or "-classpath" or "--class-path"))
        {
            arguments.Add("-cp");
            arguments.Add(values["classpath"]);
        }

        arguments.Add(version.MainClass);

        if (version.GameArguments.Count > 0)
        {
            arguments.AddRange(Expand(version.GameArguments, context, values));
        }
        else if (!string.IsNullOrEmpty(version.MinecraftArguments))
        {
            foreach (var token in version.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                arguments.Add(Substitute(token, values));
            }
        }

        foreach (var extra in options.ExtraGameArgs)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                arguments.Add(Substitute(extra, values));
            }
        }

        return Shorten(new LaunchCommand(options.JavaPath, arguments));
    }

    public const string Log4ShellGuard = "-Dlog4j2.formatMsgNoLookups=true";

    /// <summary>
    /// The properties that decide what encoding the game prints in. file.encoding is the
    /// default for everything up to Java 17; sun.stdout.encoding and sun.stderr.encoding
    /// are what the console streams read up to Java 18, stdout.encoding and
    /// stderr.encoding from Java 19 on. A Java that does not know one of them keeps it as
    /// an ordinary system property and is none the worse, so all are passed whatever the
    /// Java: the launcher does not always know which one a player picked.
    /// </summary>
    private static readonly string[] EncodingProperties =
    {
        "file.encoding",
        "sun.stdout.encoding",
        "sun.stderr.encoding",
        "stdout.encoding",
        "stderr.encoding"
    };

    /// <summary>
    /// "Print in UTF-8", for every encoding property that the build's own arguments and
    /// the version's profile leave unset. Without it a Java older than 18 prints in the
    /// system code page, and a Cyrillic path in the log depends on the reader guessing right.
    /// </summary>
    public static IReadOnlyList<string> OutputEncodingArguments(IEnumerable<string> alreadyPassed)
    {
        var passed = alreadyPassed as IReadOnlyCollection<string> ?? alreadyPassed.ToList();

        return EncodingProperties
            .Where(property => !passed.Any(a => a.StartsWith("-D" + property + "=", StringComparison.Ordinal) || a == "-D" + property))
            .Select(property => "-D" + property + "=UTF-8")
            .ToList();
    }

    /// <summary>
    /// Windows refuses to start a process whose command line is longer than 32 767
    /// characters, and a big modded build with a long user folder gets there on the
    /// classpath alone. Past this length the classpath travels in the CLASSPATH variable
    /// instead: every Java reads it, and unlike an argument file it has no encoding of its
    /// own to get a Cyrillic user name wrong.
    /// </summary>
    public const int MaxCommandLineChars = 30_000;

    public static LaunchCommand Shorten(LaunchCommand command, int limit = MaxCommandLineChars)
    {
        // Quotes and the separating space, per argument.
        var length = command.FileName.Length + 2 + command.Arguments.Sum(a => a.Length + 3);

        if (length <= limit)
        {
            return command;
        }

        var arguments = new List<string>(command.Arguments);
        var at = arguments.FindIndex(a => a is "-cp" or "-classpath" or "--class-path");

        if (at < 0 || at + 1 >= arguments.Count)
        {
            return command;
        }

        var classpath = arguments[at + 1];
        arguments.RemoveRange(at, 2);

        var environment = command.Environment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(command.Environment, StringComparer.Ordinal);

        environment["CLASSPATH"] = classpath;

        return new LaunchCommand(command.FileName, arguments, environment);
    }

    private static Dictionary<string, string> BuildValueMap(LaunchOptions options)
    {
        var version = options.Version;
        var assetsIndex = version.Assets ?? version.AssetIndex?.Id ?? "legacy";

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = options.Account.Username,
            ["version_name"] = version.Id,
            ["game_directory"] = options.GameDirectory,
            ["assets_root"] = options.AssetsDirectory,
            ["assets_index_name"] = assetsIndex,
            ["auth_uuid"] = options.Account.UuidUndashed,
            ["auth_access_token"] = options.Account.AccessToken,
            ["auth_session"] = options.Account.AccessToken,
            ["clientid"] = "0",
            ["auth_xuid"] = "0",
            ["user_type"] = options.Account.UserType,
            ["version_type"] = version.Type ?? "release",
            ["user_properties"] = "{}",
            ["natives_directory"] = options.NativesDirectory,
            ["launcher_name"] = options.LauncherName,
            ["launcher_version"] = options.LauncherVersion,
            ["classpath"] = string.Join(Path.PathSeparator, options.Classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = options.LibrariesDirectory,
            ["game_assets"] = options.AssetsDirectory,
            ["resolution_width"] = (options.Width ?? 854).ToString(),
            ["resolution_height"] = (options.Height ?? 480).ToString(),
            ["quickPlayPath"] = string.Empty,
            ["quickPlaySingleplayer"] = string.Empty,
            ["quickPlayMultiplayer"] = options.ServerAddress ?? string.Empty,
            ["quickPlayRealms"] = string.Empty
        };

        return map;
    }

    private static IEnumerable<string> Expand(
        IReadOnlyList<GameArgument> arguments,
        RuleContext context,
        IReadOnlyDictionary<string, string> values)
    {
        foreach (var argument in arguments)
        {
            if (argument.IsConditional && !RuleEvaluator.IsAllowed(argument.Rules, context))
            {
                continue;
            }

            foreach (var value in argument.Values)
            {
                yield return Substitute(value, values);
            }
        }
    }

    private static IEnumerable<string> ExpandDefaults(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> values)
    {
        foreach (var argument in arguments)
        {
            yield return Substitute(argument, values);
        }
    }

    private static string Substitute(string value, IReadOnlyDictionary<string, string> values)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        foreach (var (key, replacement) in values)
        {
            value = value.Replace("${" + key + "}", replacement, StringComparison.Ordinal);
        }

        return value;
    }
}