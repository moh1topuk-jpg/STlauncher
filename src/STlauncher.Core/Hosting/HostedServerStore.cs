using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Hosting;

/// <summary>
/// The player's own servers: one folder each under <c>servers/</c> in the data root,
/// described by a <c>server.json</c> beside the game's files.
/// </summary>
public sealed class HostedServerStore
{
    public const string FolderName = "servers";
    public const string DefinitionFileName = "server.json";

    /// <summary>Where a removed server goes. A world is hours of somebody's life; it is never deleted here.</summary>
    public const string RemovedFolderName = ".removed";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly List<string> _unreadable = new();

    public HostedServerStore(LauncherPaths paths)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        Root = Path.Combine(paths.Root, FolderName);
    }

    /// <summary><c>&lt;data root&gt;/servers</c>.</summary>
    public string Root { get; }

    public string RemovedRoot => Path.Combine(Root, RemovedFolderName);

    public string ServerDirectory(string serverId) => Path.Combine(Root, serverId);

    public string ServerDirectory(HostedServer server) => ServerDirectory(server.Id);

    public string DefinitionPath(string serverId) => Path.Combine(ServerDirectory(serverId), DefinitionFileName);

    public bool Exists(string serverId) => File.Exists(DefinitionPath(serverId));

    /// <summary>Folders whose definition could not be read during the last <see cref="List"/>; kept on disk as they are.</summary>
    public IReadOnlyList<string> UnreadableDefinitions => _unreadable;

    public IReadOnlyList<HostedServer> List()
    {
        _unreadable.Clear();

        if (!Directory.Exists(Root))
        {
            return Array.Empty<HostedServer>();
        }

        var result = new List<HostedServer>();

        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            // ".removed" and anything else the launcher parks here is not a server.
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }

            var path = Path.Combine(directory, DefinitionFileName);

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var server = JsonSerializer.Deserialize<HostedServer>(File.ReadAllText(path), JsonOptions);

                if (server is not null && !string.IsNullOrWhiteSpace(server.Id))
                {
                    result.Add(server);
                }
                else
                {
                    _unreadable.Add(directory);
                }
            }
            catch (Exception)
            {
                // Same rule as for builds: a broken definition neither takes the list
                // down nor disappears without a trace.
                _unreadable.Add(directory);
            }
        }

        return result
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public HostedServer? Get(string serverId)
        => List().FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Registers a new server: its folder and <c>server.json</c>, nothing else. Nothing is
    /// downloaded and no game file is written until the installer is asked to.
    /// </summary>
    public HostedServer Create(
        string name,
        string gameVersion,
        LoaderKind loader = LoaderKind.Vanilla,
        string? loaderVersion = null,
        string? sourceInstanceId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Server name must not be empty.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new ArgumentException("Game version must not be empty.", nameof(gameVersion));
        }

        Directory.CreateDirectory(Root);

        var server = new HostedServer
        {
            Id = UniqueId(Slugify(name)),
            Name = name.Trim(),
            GameVersion = gameVersion.Trim(),
            Loader = loader,
            LoaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim(),
            SourceInstanceId = sourceInstanceId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Save(server);
        return server;
    }

    /// <summary>A server with the game version and loader of a build, linked back to it.</summary>
    public HostedServer CreateFrom(Instance instance, string? name = null)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        if (string.IsNullOrWhiteSpace(instance.VersionId))
        {
            throw new InvalidOperationException($"Build '{instance.Id}' has no game version to make a server for.");
        }

        var server = Create(
            string.IsNullOrWhiteSpace(name) ? instance.Name : name!,
            instance.VersionId!,
            instance.Loader,
            instance.LoaderVersion,
            instance.Id);

        return server;
    }

    public void Save(HostedServer server)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        if (string.IsNullOrWhiteSpace(server.Id))
        {
            throw new ArgumentException("Server id must not be empty.", nameof(server));
        }

        var directory = EnsureChildOfRoot(server.Id);
        Directory.CreateDirectory(directory);

        AtomicFile.WriteAllText(Path.Combine(directory, DefinitionFileName), JsonSerializer.Serialize(server, JsonOptions));
    }

    /// <summary>
    /// Changes the name the player sees. The id and the folder stay: a running server,
    /// its world and every path already shown to the player keep working. The message of
    /// the day follows the name only while it still is the old name - one the player
    /// wrote themselves is left alone.
    /// </summary>
    public HostedServer Rename(string serverId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Server name must not be empty.", nameof(newName));
        }

        var server = Get(serverId) ?? throw new InvalidOperationException($"Server '{serverId}' was not found.");
        var oldName = server.Name;

        server.Name = newName.Trim();
        Save(server);

        var propertiesPath = ServerProperties.PathIn(ServerDirectory(server));

        if (File.Exists(propertiesPath))
        {
            var properties = ServerProperties.Load(propertiesPath);

            if (string.Equals(properties.Get(ServerProperties.MotdKey), oldName, StringComparison.Ordinal))
            {
                properties.Set(ServerProperties.MotdKey, server.Name);
                properties.Save(propertiesPath);
            }
        }

        return server;
    }

    /// <summary>
    /// Records the player's agreement with Mojang's EULA and writes <c>eula.txt</c>.
    /// Does nothing and returns false unless <paramref name="playerAccepted"/> is true.
    /// </summary>
    public bool AcceptEula(HostedServer server, bool playerAccepted)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        var now = DateTimeOffset.UtcNow;

        if (!ServerEula.Accept(ServerDirectory(server), playerAccepted, now))
        {
            return false;
        }

        server.EulaAccepted = true;
        server.EulaAcceptedAt = now;
        Save(server);
        return true;
    }

    /// <summary>
    /// Writes the files a friends' server starts from - server.properties with the
    /// launcher's defaults, the port and the name - and puts the owner on the whitelist
    /// and among the operators. Existing files are edited, not replaced: keys the launcher
    /// does not manage and entries already in the lists stay as they are.
    /// </summary>
    /// <param name="ownerName">The player's own nickname; null leaves the lists alone.</param>
    public void WriteStartingFiles(HostedServer server, string? ownerName)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        var directory = ServerDirectory(server);
        Directory.CreateDirectory(directory);

        var path = ServerProperties.PathIn(directory);
        var properties = ServerProperties.Load(path);
        properties.ApplyFriendsDefaults(server.Name, server.Port);
        properties.Save(path);

        if (!string.IsNullOrWhiteSpace(ownerName))
        {
            ServerAccessLists.AddToWhitelist(directory, ownerName);
            ServerAccessLists.AddOp(directory, ownerName);
        }
    }

    /// <summary>Saves a new port to both <c>server.json</c> and server.properties. Takes effect at the next start.</summary>
    public void SetPort(HostedServer server, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "A port is a number from 1 to 65535.");
        }

        server.Port = port;
        Save(server);

        var path = ServerProperties.PathIn(ServerDirectory(server));

        if (File.Exists(path))
        {
            var properties = ServerProperties.Load(path);
            properties.Set(ServerProperties.PortKey, port);
            properties.Save(path);
        }
    }

    /// <summary>
    /// Takes a server off the list by moving its whole folder into <c>servers/.removed/</c>.
    /// Nothing is deleted; the returned path is where the world can still be found.
    /// Throws <see cref="IOException"/> while the server is running and holds its files.
    /// </summary>
    public string Remove(string serverId)
    {
        var source = EnsureChildOfRoot(serverId);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Server '{serverId}' was not found.");
        }

        Directory.CreateDirectory(RemovedRoot);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(RemovedRoot, $"{serverId}-{stamp}");

        for (var suffix = 2; Directory.Exists(target); suffix++)
        {
            target = Path.Combine(RemovedRoot, $"{serverId}-{stamp}-{suffix}");
        }

        Directory.Move(source, target);
        return target;
    }

    /// <summary>
    /// ASCII only, unlike a build's id: the folder becomes the working directory of a Java
    /// process, and old Java versions on Windows trip over paths outside the system code page.
    /// </summary>
    public static string Slugify(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastWasSeparator = false;

        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "server" : slug;
    }

    private string UniqueId(string baseId)
    {
        // A folder counts even without a definition: a half-made or damaged server must
        // not be silently taken over by a new one.
        if (!Directory.Exists(ServerDirectory(baseId)))
        {
            return baseId;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";

            if (!Directory.Exists(ServerDirectory(candidate)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not allocate a unique server id for '{baseId}'.");
    }

    /// <summary>An id is a single folder name directly under <c>servers/</c>, never a path out of it.</summary>
    private string EnsureChildOfRoot(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) || serverId.StartsWith('.'))
        {
            throw new InvalidOperationException($"'{serverId}' is not a server id.");
        }

        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(ServerDirectory(serverId));
        var parent = Path.GetDirectoryName(target)?.TrimEnd(Path.DirectorySeparatorChar);

        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to touch '{target}': it is outside the servers directory.");
        }

        return target;
    }
}
