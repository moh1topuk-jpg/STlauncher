using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Hosting;

public enum ServerLineKind
{
    /// <summary>An ordinary line of the console.</summary>
    Plain,

    /// <summary>The "Done" line: the world is loaded and players can join.</summary>
    Ready,

    PlayerJoined,

    PlayerLeft,

    /// <summary>The server announced that it is shutting down.</summary>
    Stopping,

    /// <summary>Something else already listens on the server's port.</summary>
    PortInUse,

    /// <summary>eula.txt does not say eula=true.</summary>
    EulaRequired,

    /// <summary>The server was built for a newer Java than the one it was started with.</summary>
    WrongJava,

    /// <summary>Java could not get the memory it was told to use.</summary>
    NotEnoughMemory,

    /// <summary>The world is locked by another running server or game.</summary>
    WorldInUse,

    /// <summary>The server reported a crash or an exception that ends it.</summary>
    Crash
}

/// <summary>One line of the server's console and what the launcher made of it.</summary>
/// <param name="Text">The line as the server printed it.</param>
/// <param name="Player">The nickname, for <see cref="ServerLineKind.PlayerJoined"/> and <see cref="ServerLineKind.PlayerLeft"/>.</param>
/// <param name="StartupSeconds">How long the start took, for <see cref="ServerLineKind.Ready"/>.</param>
/// <param name="IsError">True when the line came from the error stream.</param>
public sealed record ServerLine(
    string Text,
    ServerLineKind Kind = ServerLineKind.Plain,
    string? Player = null,
    double? StartupSeconds = null,
    bool IsError = false);

/// <summary>
/// Reads meaning out of the server's console. The server has no other way of saying it is
/// ready or who has joined, so these few lines - unchanged across a decade of versions -
/// are the protocol.
/// </summary>
public static partial class ServerOutput
{
    // "[12:34:56] [Server thread/INFO]: message" since 1.7, with a third bracket under
    // Forge and a single one under Paper; "2013-01-01 12:34:56 [INFO] message" before.
    [GeneratedRegex(@"^\[[^\]]*\](?: \[[^\]]*\]){0,2}: (?<msg>.*)$")]
    private static partial Regex ModernPrefixRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[\w+\] (?<msg>.*)$")]
    private static partial Regex LegacyPrefixRegex();

    [GeneratedRegex(@"^Done \((?<seconds>\d+(?:[.,]\d+)?)\s*s\)!")]
    private static partial Regex DoneRegex();

    // A name is one token with none of the characters chat wraps a speaker in, so a
    // player typing "joined the game" in chat ("<Steve> joined the game") is not a join.
    [GeneratedRegex(@"^(?<name>[^\s<>\[\]\*:]{1,32}) joined the game$")]
    private static partial Regex JoinedRegex();

    [GeneratedRegex(@"^(?<name>[^\s<>\[\]\*:]{1,32}) left the game$")]
    private static partial Regex LeftRegex();

    public static ServerLine Parse(string line, bool isError = false)
    {
        line ??= string.Empty;

        var prefixed = ModernPrefixRegex().Match(line);

        if (!prefixed.Success)
        {
            prefixed = LegacyPrefixRegex().Match(line);
        }

        // NeoForge's early lines end in a space, and a line from Windows may keep its \r.
        var message = (prefixed.Success ? prefixed.Groups["msg"].Value : line).TrimEnd();

        // Only a line the server itself logged can announce readiness or a player. A raw
        // line with no log prefix is Java or the loader talking, and is checked for
        // start-up failures only.
        if (prefixed.Success)
        {
            var done = DoneRegex().Match(message);

            if (done.Success)
            {
                var seconds = double.TryParse(
                    done.Groups["seconds"].Value.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : (double?)null;

                return new ServerLine(line, ServerLineKind.Ready, StartupSeconds: seconds, IsError: isError);
            }

            var joined = JoinedRegex().Match(message);

            if (joined.Success)
            {
                return new ServerLine(line, ServerLineKind.PlayerJoined, joined.Groups["name"].Value, IsError: isError);
            }

            var left = LeftRegex().Match(message);

            if (left.Success)
            {
                return new ServerLine(line, ServerLineKind.PlayerLeft, left.Groups["name"].Value, IsError: isError);
            }

            if (message is "Stopping server" or "Stopping the server")
            {
                return new ServerLine(line, ServerLineKind.Stopping, IsError: isError);
            }

            // Chat ("<Steve> ...", "[Not Secure] <Steve> ...") and command echoes
            // ("[Steve: ...]") are players' words: whatever they say is not a failure.
            if (message.StartsWith('<') || message.StartsWith('['))
            {
                return new ServerLine(line, IsError: isError);
            }
        }

        return new ServerLine(line, Problem(message), IsError: isError);
    }

    private static ServerLineKind Problem(string message)
    {
        if (Has(message, "FAILED TO BIND TO PORT") || Has(message, "Address already in use"))
        {
            return ServerLineKind.PortInUse;
        }

        if (Has(message, "You need to agree to the EULA"))
        {
            return ServerLineKind.EulaRequired;
        }

        if (Has(message, "UnsupportedClassVersionError") ||
            Has(message, "has been compiled by a more recent version of the Java Runtime") ||
            Has(message, "requires running the server with Java"))
        {
            return ServerLineKind.WrongJava;
        }

        if (Has(message, "Could not reserve enough space") ||
            Has(message, "Invalid maximum heap size") ||
            Has(message, "java.lang.OutOfMemoryError") ||
            Has(message, "There is insufficient memory for the Java Runtime"))
        {
            return ServerLineKind.NotEnoughMemory;
        }

        if (Has(message, "session.lock") && Has(message, "already locked"))
        {
            return ServerLineKind.WorldInUse;
        }

        if (Has(message, "This crash report has been saved to") ||
            Has(message, "Encountered an unexpected exception") ||
            Has(message, "Failed to start the minecraft server") ||
            Has(message, "Exception in thread \"main\"") ||
            Has(message, "Error: Unable to access jarfile") ||
            Has(message, "Error: Invalid or corrupt jarfile"))
        {
            return ServerLineKind.Crash;
        }

        return ServerLineKind.Plain;
    }

    private static bool Has(string text, string fragment)
        => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
