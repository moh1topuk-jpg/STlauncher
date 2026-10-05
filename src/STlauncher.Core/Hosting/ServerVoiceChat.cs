using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Hosting;

/// <summary>
/// Simple Voice Chat on a hosted server: whether the server has it, and the UDP port it
/// listens on. The mod talks to its clients over UDP on a port of its own, which neither
/// the relay's game tunnel nor a TCP port mapping carries, so the ways friends connect by
/// need to know about it.
/// </summary>
public static class ServerVoiceChat
{
    /// <summary>The mod's own default, used until it has written its config on the first start.</summary>
    public const int DefaultPort = 24454;

    /// <summary>The mod's id in fabric.mod.json and mods.toml.</summary>
    public const string ModId = "voicechat";

    private const string PortKey = "port";

    public static string ConfigPath(string serverDirectory)
        => Path.Combine(serverDirectory, ServerConfigFiles.ConfigFolderName, "voicechat", "voicechat-server.properties");

    /// <summary>
    /// The voice chat's UDP port, or null when the server has no voice chat. Never throws:
    /// a folder or file that cannot be read counts as what the mod would do by default.
    /// </summary>
    /// <param name="serverPort">The Minecraft server's port, which the mod's "-1" stands for.</param>
    public static int? FindPort(string serverDirectory, int serverPort)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory))
        {
            return null;
        }

        var config = ConfigPath(serverDirectory);
        bool hasConfig;

        try
        {
            hasConfig = File.Exists(config);
        }
        catch (Exception)
        {
            hasConfig = false;
        }

        // The jar decides, not the config: a config stays behind when the mod is removed.
        if (!HasMod(serverDirectory, lookInside: hasConfig))
        {
            return null;
        }

        if (!hasConfig)
        {
            return DefaultPort;
        }

        try
        {
            var port = ServerProperties.Load(config).GetInt(PortKey, DefaultPort);

            // The mod reads -1 as "the same number as the Minecraft server", on UDP.
            if (port == -1)
            {
                return serverPort is >= 1 and <= 65535 ? serverPort : DefaultPort;
            }

            return port is >= 1 and <= 65535 ? port : DefaultPort;
        }
        catch (Exception)
        {
            return DefaultPort;
        }
    }

    /// <summary>
    /// Whether an enabled Simple Voice Chat jar is in the server's mods folder. By file name
    /// first, which is cheap and how Modrinth and the mod's own releases name it; with
    /// <paramref name="lookInside"/>, renamed jars are also opened to read their mod id.
    /// </summary>
    public static bool HasMod(string serverDirectory, bool lookInside = false)
    {
        string[] jars;

        try
        {
            var mods = ModManager.ModsDirectory(serverDirectory);

            if (!Directory.Exists(mods))
            {
                return false;
            }

            jars = Directory.GetFiles(mods, "*.jar");
        }
        catch (Exception)
        {
            return false;
        }

        if (jars.Any(j => Path.GetFileName(j).StartsWith(ModId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return lookInside && jars.Any(j => ModMetadataReader.Read(j).Any(m => string.Equals(m.Id, ModId, StringComparison.OrdinalIgnoreCase)));
    }
}
