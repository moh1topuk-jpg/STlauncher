using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;

namespace STlauncher.Core.Import;

/// <summary>
/// Works out which Minecraft version a found build runs, from the most reliable evidence
/// down: what the profile is built on, then what the game itself wrote into its log, and
/// only then the name of the folder.
/// </summary>
/// <remarks>
/// The name comes last because it is free text. A pack called "NightfallCraft 2.2.9.7" or
/// "SkyBlock 1.4" carries its own version in the name, and reading that as the game's
/// gives the build a version it never ran - the mod catalog then offers mods for the
/// wrong game. Leaving the version unknown is the better mistake: the player picks it
/// once after importing.
/// </remarks>
public static class GameVersionDetector
{
    /// <summary>"1.21.1", "1.8", "26.2": a release of the game and nothing else.</summary>
    private static readonly Regex Release = new(
        @"^(1\.(\d|1\d|2[01])(\.\d{1,2})?|2[5-9]\.\d{1,2}(\.\d{1,2})?)$", RegexOptions.CultureInvariant);

    /// <summary>What a log or an id may call the game: a release, a pre-release, a snapshot.</summary>
    private static readonly Regex AnyVersion = new(@"^[0-9][0-9A-Za-z._-]{1,31}$", RegexOptions.CultureInvariant);

    /// <summary>Words a version folder is named with that say nothing but the loader or the flavour.</summary>
    private static readonly HashSet<string> NeutralWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt", "optifine", "optifabric", "liteloader", "forgeoptifine",
        "vanilla", "release", "minecraft", "mc", "loader", "hd", "u", "ultra", "client", "java", "edition"
    };

    /// <summary>A loader's own version ("0.16.0", "47.2.0", "14.23.5.2860") or an OptiFine build ("G8", "pre3").</summary>
    private static readonly Regex NeutralToken = new(
        @"^(\d+(\.\d+)*|[A-Za-z]\d{1,2}|pre\d*|rc\d*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly char[] NameSeparators = { ' ', '-', '_', '(', ')', '[', ']', ',', '+' };

    /// <summary>Ids Mojang itself gives: releases, "1.21-pre1", "26.3-snapshot-1", "24w14a", "b1.7.3".</summary>
    private static readonly Regex MojangId = new(
        @"^((1\.(\d|1\d|2[01])(\.\d{1,2})?|2[5-9]\.\d{1,2}(\.\d{1,2})?)(-(pre|rc|snapshot)[0-9A-Za-z.-]{0,12})?|\d{2}w\d{2}[a-z]|[ab]1\.\d{1,2}(\.\d{1,2})?(_\d{1,2})?)$",
        RegexOptions.CultureInvariant);

    public static bool IsRelease(string? value) => !string.IsNullOrEmpty(value) && Release.IsMatch(value);

    /// <summary>True for anything the game's own version list could call a version.</summary>
    public static bool IsGameId(string? value) => !string.IsNullOrEmpty(value) && MojangId.IsMatch(value);

    /// <summary>
    /// The version a profile is built on, or null when the profile does not say. The
    /// <paramref name="versionsDirectory"/> lets a profile that inherits from another
    /// custom profile be followed to the game it finally stands on.
    /// </summary>
    public static string? FromProfile(VersionJson json, string? versionsDirectory = null, int depth = 0)
    {
        if (json is null)
        {
            return null;
        }

        var parent = json.InheritsFrom;

        if (!string.IsNullOrWhiteSpace(parent))
        {
            // Almost always the game itself. When it is another profile ("Forge 1.20.1"
            // under "ForgeOptiFine 1.20.1"), that profile is asked instead of trusting its name.
            if (IsGameId(parent))
            {
                return parent;
            }

            if (versionsDirectory is not null && depth < 4 &&
                ReadProfile(versionsDirectory, parent!) is { } parentJson &&
                FromProfile(parentJson, versionsDirectory, depth + 1) is { } inherited)
            {
                return inherited;
            }

            // A parent that is not on disk: "1.20.1-forge-47.2.0" still names the game
            // among words that are all a loader's.
            if (FromName(parent) is { } named)
            {
                return named;
            }
        }

        if (IsRelease(json.Jar))
        {
            return json.Jar;
        }

        if (FromLibraries(json.Libraries.Select(l => l.Name ?? string.Empty)) is { } fromLibraries)
        {
            return fromLibraries;
        }

        return FromArguments(json);
    }

    /// <summary>The game version named by a library coordinate, when one names it.</summary>
    public static string? FromLibraries(IEnumerable<string> libraries)
    {
        foreach (var library in libraries)
        {
            var parts = library.Split(':');

            if (parts.Length < 3)
            {
                continue;
            }

            var (group, artifact, version) = (parts[0], parts[1], parts[2]);

            // Fabric and Quilt both map the game through intermediary, named by version.
            if (group == "net.fabricmc" && artifact == "intermediary" && AnyVersion.IsMatch(version))
            {
                return version;
            }

            // Forge, and NeoForge for 1.20.1: "1.20.1-47.2.0".
            if ((group == "net.minecraftforge" && artifact is "forge" or "fmlloader" or "fmlcore") ||
                (group == "net.neoforged" && artifact == "forge"))
            {
                var game = version.Split('-')[0];

                if (IsRelease(game))
                {
                    return game;
                }
            }

            // NeoForge numbers itself after the game without the leading "1.": 21.1.77 is for 1.21.1.
            if (group == "net.neoforged" && artifact == "neoforge")
            {
                var numbers = version.Split('.', '-');

                if (numbers.Length >= 2 && int.TryParse(numbers[0], out var major) && int.TryParse(numbers[1], out var minor) &&
                    major is >= 20 and <= 21)
                {
                    return minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";
                }
            }

            if ((group is "com.mojang" or "net.minecraft") && artifact is "minecraft" or "client" && IsRelease(version))
            {
                return version;
            }

            // "optifine:OptiFine:1.16.5_HD_U_G8"
            if (string.Equals(artifact, "OptiFine", StringComparison.OrdinalIgnoreCase) &&
                IsRelease(version.Split('_')[0]))
            {
                return version.Split('_')[0];
            }
        }

        return null;
    }

    /// <summary>Forge hands the game version to its own bootstrap as "--fml.mcVersion 1.20.1".</summary>
    private static string? FromArguments(VersionJson json)
    {
        var tokens = new List<string>();

        if (json.Arguments is not null)
        {
            tokens.AddRange(json.Arguments.Game.SelectMany(a => a.Values));
        }

        if (!string.IsNullOrWhiteSpace(json.MinecraftArguments))
        {
            tokens.AddRange(json.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        var index = tokens.IndexOf("--fml.mcVersion");

        return index >= 0 && index + 1 < tokens.Count && IsRelease(tokens[index + 1]) ? tokens[index + 1] : null;
    }

    /// <summary>
    /// The version in a folder or profile name, only when the name is nothing but that
    /// version and words everybody uses around it: "1.21.1", "Fabric 1.21.11",
    /// "Optifine 1.16.5 HD", "Forge-1.12". One word of its own - a pack's name - and the
    /// number next to it may just as well be the pack's version, so nothing is claimed.
    /// </summary>
    public static string? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string? found = null;

        foreach (var token in name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsRelease(token))
            {
                if (found is not null && found != token)
                {
                    return null;
                }

                found = token;
            }
            else if (!NeutralWords.Contains(token) && !NeutralToken.IsMatch(token))
            {
                return null;
            }
        }

        return found;
    }

    /// <summary>What the game said about itself the last time it ran in this folder.</summary>
    public sealed record LogVerdict(string GameVersion, LoaderKind Loader);

    private static readonly Regex FabricLine = new(
        @"Loading Minecraft (\S+) with (Fabric|Quilt) Loader", RegexOptions.CultureInvariant);

    private static readonly Regex ForgeArgumentsLine = new(
        @"--fml\.mcVersion,\s*([^,\]\s]+)", RegexOptions.CultureInvariant);

    private static readonly Regex OldForgeLine = new(
        @"Forge Mod Loader version \S+ for Minecraft (\S+) loading", RegexOptions.CultureInvariant);

    /// <summary>The log is megabytes on a long session; the version is said in the first lines.</summary>
    private const int MaxLogLines = 400;

    /// <summary>
    /// Reads logs/latest.log of a folder only one build plays in. In a shared .minecraft
    /// the log belongs to whichever version ran last, so it must not be asked there.
    /// </summary>
    public static LogVerdict? FromLog(string gameDirectory)
    {
        try
        {
            var logs = Path.Combine(gameDirectory, "logs");
            var path = Path.Combine(logs, "latest.log");

            if (!LinkGuard.IsRealDirectory(logs) || !LinkGuard.IsRealFile(path))
            {
                return null;
            }

            foreach (var line in File.ReadLines(path).Take(MaxLogLines))
            {
                if (line.Length > 8192)
                {
                    continue;
                }

                var match = FabricLine.Match(line);

                if (match.Success && AnyVersion.IsMatch(match.Groups[1].Value))
                {
                    return new LogVerdict(
                        match.Groups[1].Value,
                        match.Groups[2].Value == "Quilt" ? LoaderKind.Quilt : LoaderKind.Fabric);
                }

                match = ForgeArgumentsLine.Match(line);

                if (match.Success && IsRelease(match.Groups[1].Value))
                {
                    return new LogVerdict(
                        match.Groups[1].Value,
                        line.Contains("neoForgeVersion", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
                            ? LoaderKind.NeoForge
                            : LoaderKind.Forge);
                }

                match = OldForgeLine.Match(line);

                if (match.Success && IsRelease(match.Groups[1].Value))
                {
                    return new LogVerdict(match.Groups[1].Value, LoaderKind.Forge);
                }
            }
        }
        catch (Exception)
        {
            // A log held open by a running game, or one in an encoding nobody expected.
        }

        return null;
    }

    private static VersionJson? ReadProfile(string versionsDirectory, string id)
    {
        try
        {
            // An id is a folder name; one with a separator in it is somebody's path.
            if (id.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || id is "." or "..")
            {
                return null;
            }

            var directory = Path.Combine(versionsDirectory, id);
            var path = Path.Combine(directory, id + ".json");

            if (!LinkGuard.IsRealDirectory(directory) || !LinkGuard.IsRealFile(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(path), MetadataJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
