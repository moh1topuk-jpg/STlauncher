using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Hosting;

/// <summary>What to run to start a server: the Java executable, its arguments and the folder to run it in.</summary>
public sealed record ServerCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

public static class ServerCommandLine
{
    /// <summary>Below this the server does not get through loading a world.</summary>
    public const int MinimumMemoryMb = 512;

    /// <param name="windows">
    /// Which system's argument file a Forge server is started with; null for the one
    /// the launcher runs on. Only tests say it out loud.
    /// </param>
    public static ServerCommand Build(HostedServer server, string serverDirectory, string javaPath, bool? windows = null)
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
        var arguments = new List<string>();

        // Forge since 1.17 has no jar to start: its installer leaves a file of arguments
        // for each system, and a second file, user_jvm_args.txt, for the owner's own.
        var argsFile = ForgeServer.IsForgeLike(server.Loader) && ForgeServer.IsArgsFile(server.LaunchJar)
            ? ForgeServer.ArgsFileFor(server.LaunchJar!, windows ?? OperatingSystem.IsWindows())
            : null;

        var ownHeapLimit = false;

        if (argsFile is not null && File.Exists(Path.Combine(serverDirectory, ForgeServer.UserJvmArgsFile)))
        {
            arguments.Add("@" + ForgeServer.UserJvmArgsFile);

            // A heap limit the owner wrote into that file is theirs to keep: a second
            // -Xmx after it would silently win over what they wrote.
            ownHeapLimit = ForgeServer.SetsHeapLimit(Path.Combine(serverDirectory, ForgeServer.UserJvmArgsFile));
        }

        if (!ownHeapLimit)
        {
            arguments.Add("-Xmx" + memory.ToString(CultureInfo.InvariantCulture) + "M");
        }

        arguments.AddRange(new[]
        {
            // The console is read through a pipe, where Java before 18 falls back to the
            // system code page and turns Russian chat into question marks.
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",

            // Closes the Log4Shell hole in the 1.17-1.18 servers Mojang never re-released;
            // every other version ignores the flag.
            "-Dlog4j2.formatMsgNoLookups=true"
        });

        if (server.Loader == LoaderKind.Fabric)
        {
            // Fabric's launcher would otherwise download Mojang's server jar itself, to a
            // place of its own. The installer has already fetched and verified one.
            arguments.Add("-Dfabric.installer.server.gameJar=" + ServerInstaller.VanillaJarName);
        }

        if (argsFile is not null)
        {
            // Relative to the server's folder and with forward slashes, as Forge's own
            // run.bat and run.sh write it; Java reads it the same on every system.
            arguments.Add("@" + argsFile);
        }
        else
        {
            arguments.Add("-jar");
            arguments.Add(server.LaunchJar!);
        }

        // Without it the server opens its own Swing window next to the launcher.
        arguments.Add("nogui");

        return new ServerCommand(javaPath, arguments, serverDirectory);
    }
}
