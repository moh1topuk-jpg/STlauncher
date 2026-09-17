using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace STlauncher.Core.Instances;

public sealed class InstanceManager
{
    public const string DefinitionFileName = "instance.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly LauncherPaths _paths;

    public InstanceManager(LauncherPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string GameDirectory(string instanceId) => _paths.InstanceDirectory(instanceId);

    public string DefinitionPath(string instanceId)
        => Path.Combine(_paths.InstanceDirectory(instanceId), DefinitionFileName);

    public bool Exists(string instanceId) => File.Exists(DefinitionPath(instanceId));

    /// <summary>
    /// True when an instance folder is present on disk, even if its definition could not be
    /// read. Used to avoid creating a brand-new profile over a build that is merely damaged.
    /// </summary>
    public bool HasAnyInstanceDirectory()
        => Directory.Exists(_paths.Instances) && Directory.EnumerateDirectories(_paths.Instances).Any();

    private readonly List<string> _unreadable = new();

    /// <summary>
    /// Definitions that failed to parse during the most recent <see cref="List"/> call.
    /// They are preserved on disk rather than dropped, so the caller can tell the user.
    /// </summary>
    public IReadOnlyList<string> UnreadableDefinitions => _unreadable;

    public IReadOnlyList<Instance> List()
    {
        _unreadable.Clear();

        if (!Directory.Exists(_paths.Instances))
        {
            return Array.Empty<Instance>();
        }

        var result = new List<Instance>();

        foreach (var directory in Directory.EnumerateDirectories(_paths.Instances))
        {
            var path = Path.Combine(directory, DefinitionFileName);

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var instance = JsonSerializer.Deserialize<Instance>(json, JsonOptions);

                if (instance is not null && !string.IsNullOrWhiteSpace(instance.Id))
                {
                    result.Add(instance);
                }
                else
                {
                    _unreadable.Add(directory);
                }
            }
            catch (Exception)
            {
                // A broken definition must not take down the whole list, but it must also
                // not vanish without a trace: keep it recoverable and report it.
                _unreadable.Add(directory);
            }
        }

        return result
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Instance? Get(string instanceId)
        => List().FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));

    public Instance Create(string name, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Instance name must not be empty.", nameof(name));
        }

        Directory.CreateDirectory(_paths.Instances);

        var instanceId = string.IsNullOrWhiteSpace(id) ? UniqueId(Slugify(name)) : id!;

        if (Exists(instanceId))
        {
            throw new InvalidOperationException($"Instance '{instanceId}' already exists.");
        }

        var instance = new Instance
        {
            Id = instanceId,
            Name = name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        Save(instance);
        return instance;
    }

    public void Save(Instance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Id))
        {
            throw new ArgumentException("Instance id must not be empty.", nameof(instance));
        }

        var directory = _paths.InstanceDirectory(instance.Id);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, DefinitionFileName);

        // Atomic: the definition is written on every settings change, so a torn write is
        // not a theoretical risk - it would show up as a build that disappeared.
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(instance, JsonOptions));
    }

    public void Delete(string instanceId)
    {
        // Validate before touching the filesystem: never delete anything that is not
        // a direct child of the instances folder, regardless of whether it exists.
        var instancesRoot = Path.GetFullPath(_paths.Instances).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(_paths.InstanceDirectory(instanceId));

        var parent = Path.GetDirectoryName(target)?.TrimEnd(Path.DirectorySeparatorChar);

        if (!string.Equals(parent, instancesRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete '{target}': it is outside the instances directory.");
        }

        if (!Directory.Exists(target))
        {
            return;
        }

        Directory.Delete(target, recursive: true);
    }

    /// <summary>
    /// Creates a new build from an existing one. Only the definition is copied — files,
    /// worlds and downloaded mods stay with the original, so builds do not silently
    /// duplicate gigabytes of data.
    /// </summary>
    public Instance Duplicate(string sourceId, string? newName = null)
    {
        var source = Get(sourceId)
                     ?? throw new InvalidOperationException($"Instance '{sourceId}' was not found.");

        var copy = Create(string.IsNullOrWhiteSpace(newName) ? $"{source.Name} copy" : newName!);

        copy.VersionId = source.VersionId;
        copy.Loader = source.Loader;
        copy.LoaderVersion = source.LoaderVersion;
        copy.MaxMemoryMb = source.MaxMemoryMb;
        copy.MinMemoryMb = source.MinMemoryMb;
        copy.Width = source.Width;
        copy.Height = source.Height;
        copy.JavaPath = source.JavaPath;
        copy.ServerName = source.ServerName;
        copy.ServerAddress = source.ServerAddress;
        copy.ExtraGameArgs = source.ExtraGameArgs;
        copy.EnabledCatalogItems = source.EnabledCatalogItems.ToList();

        Save(copy);
        return copy;
    }

    public static string Slugify(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastWasSeparator = false;

        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
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
        return string.IsNullOrEmpty(slug) ? "instance" : slug;
    }

    private string UniqueId(string baseId)
    {
        if (!Exists(baseId))
        {
            return baseId;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not allocate a unique instance id for '{baseId}'.");
    }
}