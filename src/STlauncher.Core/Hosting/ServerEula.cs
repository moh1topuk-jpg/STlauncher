using System;
using System.Globalization;
using System.IO;

namespace STlauncher.Core.Hosting;

/// <summary>
/// <c>eula.txt</c>. The server refuses to start until the file says <c>eula=true</c>, and
/// that line is the player's own agreement with Mojang - not something a launcher may
/// write on their behalf. It is written only when the caller states that the player
/// agreed on the screen.
/// </summary>
public static class ServerEula
{
    public const string FileName = "eula.txt";

    /// <summary>The agreement itself, for the screen to link to.</summary>
    public const string Url = "https://aka.ms/MinecraftEULA";

    public static bool IsAccepted(string serverDirectory)
    {
        var path = Path.Combine(serverDirectory, FileName);

        return File.Exists(path) &&
               ServerProperties.Load(path).GetBool("eula", false);
    }

    /// <summary>
    /// Writes <c>eula=true</c>. Returns false and writes nothing unless
    /// <paramref name="playerAccepted"/> is true.
    /// </summary>
    public static bool Accept(string serverDirectory, bool playerAccepted, DateTimeOffset? when = null)
    {
        if (!playerAccepted)
        {
            return false;
        }

        var stamp = (when ?? DateTimeOffset.UtcNow).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        Directory.CreateDirectory(serverDirectory);

        AtomicFile.WriteAllLines(Path.Combine(serverDirectory, FileName), new[]
        {
            "#By changing the setting below to TRUE you are indicating your agreement to our EULA (" + Url + ").",
            "#Accepted by the player in STlauncher on " + stamp + " UTC",
            "eula=true"
        });

        return true;
    }
}
