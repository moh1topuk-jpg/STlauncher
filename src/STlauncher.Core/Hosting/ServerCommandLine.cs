using System;
using System.Collections.Generic;
using System.Globalization;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Hosting;

/// <summary>What to run to start a server: the Java executable, its arguments and the folder to run it in.</summary>
public sealed record ServerCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

public static class ServerCommandLine
{
    /// <summary>Below this the server does not get through loading a world.</summary>
    public const int MinimumMemoryMb = 512;

    public static ServerCommand Build(HostedServer server, string serverDirectory, string javaPath)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        if (string.IsNullOrWhiteSpace(server.LaunchJar))
        {
            throw new InvalidOperationException($"Server '{server.Id}' is not installed yet.");
        }

        if (string.IsNullOrWhiteSpace(javaPath))
        {
            throw new ArgumentException("A Java executable is required.", nameof(javaPath));
        }

        var memory = Math.Max(MinimumMemoryMb, server.MemoryMb);

        var arguments = new List<string>
        {
            "-Xmx" + memory.ToString(CultureInfo.InvariantCulture) + "M",

            // The console is read through a pipe, where Java before 18 falls back to the
            // system code page and turns Russian chat into question marks.
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",

            // Closes the Log4Shell hole in the 1.17-1.18 servers Mojang never re-released;
            // every other version ignores the flag.
            "-Dlog4j2.formatMsgNoLookups=true"
        };

        if (server.Loader == LoaderKind.Fabric)
        {
            // Fabric's launcher would otherwise download Mojang's server jar itself, to a
            // place of its own. The installer has already fetched and verified one.
            arguments.Add("-Dfabric.installer.server.gameJar=" + ServerInstaller.VanillaJarName);
        }

        arguments.Add("-jar");
        arguments.Add(server.LaunchJar!);

        // Without it the server opens its own Swing window next to the launcher.
        arguments.Add("nogui");

        return new ServerCommand(javaPath, arguments, serverDirectory);
    }
}
