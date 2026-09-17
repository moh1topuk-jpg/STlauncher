using System;
using System.Collections.Generic;
using System.IO;
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

        var jvmArguments = version.JvmArguments.Count > 0
            ? Expand(version.JvmArguments, context, values)
            : ExpandDefaults(DefaultJvmArguments, values);

        arguments.AddRange(jvmArguments);
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

        return new LaunchCommand(options.JavaPath, arguments);
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