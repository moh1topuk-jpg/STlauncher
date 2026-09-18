using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;

namespace STlauncher.Core.Import;

/// <summary>How the files of an imported build are treated.</summary>
public enum ImportMode
{
    /// <summary>
    /// Point at the folder where it already is. Nothing is copied and nothing is
    /// duplicated - but the other launcher keeps writing there too, so saves and mods
    /// are shared between the two.
    /// </summary>
    Link,

    /// <summary>
    /// Copy the build into the launcher's own folder. Independent from then on, at the
    /// cost of the disk space the worlds take.
    /// </summary>
    Copy
}

public sealed record ImportResult(Instance Instance, ImportMode Mode, long CopiedBytes, int CopiedFiles);

/// <summary>
/// Brings a build found by <see cref="ExternalInstanceScanner"/> into the launcher.
/// </summary>
public sealed class InstanceImporter
{
    /// <summary>
    /// Folders that are caches rather than content: they are large, shared, and the
    /// launcher rebuilds them from the network anyway. Copying them would turn a two
    /// hundred megabyte import into several gigabytes for no benefit.
    /// </summary>
    private static readonly string[] SkippedFolders =
    {
        "versions", "assets", "libraries", "runtime", "bin", "cache", "logs", "crash-reports", "natives"
    };

    private readonly LauncherPaths _paths;
    private readonly InstanceManager _instances;

    public InstanceImporter(LauncherPaths paths, InstanceManager instances)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
    }

    /// <summary>
    /// What copying this build would cost, so the choice between the two modes can be made
    /// with the number in front of the player rather than after the fact.
    /// </summary>
    public static long EstimateCopySize(ExternalInstance source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        return Directory.Exists(source.GameDirectory)
            ? MeasureDirectory(source.GameDirectory)
            : 0;
    }

    public async Task<ImportResult> ImportAsync(
        ExternalInstance source,
        ImportMode mode,
        string? name = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (!source.IsUsable)
        {
            throw new InvalidOperationException(
                $"'{source.Name}' cannot be imported: {source.Problem}.");
        }

        var instance = _instances.Create(string.IsNullOrWhiteSpace(name) ? source.Name : name!);

        instance.VersionId = string.IsNullOrWhiteSpace(source.VersionId) ? null : source.VersionId;
        instance.Loader = source.Loader;

        // The profile has to live where the launcher looks for versions, in both modes:
        // it is what describes how the game starts, and it is small.
        progress?.Report("profile");
        CopyVersionProfile(source);

        var copiedBytes = 0L;
        var copiedFiles = 0;

        if (mode == ImportMode.Link)
        {
            instance.ExternalGameDirectory = source.GameDirectory;
        }
        else
        {
            var destination = _paths.InstanceDirectory(instance.Id);

            (copiedBytes, copiedFiles) = await Task.Run(
                () => CopyGameFiles(source.GameDirectory, destination, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        _instances.Save(instance);

        return new ImportResult(instance, mode, copiedBytes, copiedFiles);
    }

    /// <summary>
    /// Copies the version profile - and its jar, when the profile carries no download of
    /// its own, which is the usual shape of a hand-made or launcher-generated build.
    /// </summary>
    private void CopyVersionProfile(ExternalInstance source)
    {
        if (string.IsNullOrWhiteSpace(source.VersionJsonPath) || !File.Exists(source.VersionJsonPath))
        {
            return;
        }

        var id = source.VersionId;
        var target = _paths.VersionDirectory(id);
        Directory.CreateDirectory(target);

        var targetJson = _paths.VersionJsonPath(id);

        if (!File.Exists(targetJson))
        {
            File.Copy(source.VersionJsonPath!, targetJson);
        }

        var sourceJar = Path.ChangeExtension(source.VersionJsonPath!, ".jar");
        var targetJar = _paths.VersionJarPath(id);

        if (File.Exists(sourceJar) && !File.Exists(targetJar))
        {
            File.Copy(sourceJar, targetJar);
        }
    }

    private static (long Bytes, int Files) CopyGameFiles(
        string sourceDirectory,
        string destinationDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return (0, 0);
        }

        Directory.CreateDirectory(destinationDirectory);

        var bytes = 0L;
        var files = 0;

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);

            if (Directory.Exists(entry))
            {
                if (SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                progress?.Report(name);
                var (subBytes, subFiles) = CopyDirectory(entry, Path.Combine(destinationDirectory, name), cancellationToken);
                bytes += subBytes;
                files += subFiles;
                continue;
            }

            // Loose files in the root: options.txt, servers.dat and the like. The
            // launcher's own definition must never be overwritten by one.
            if (string.Equals(name, InstanceManager.DefinitionFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(destinationDirectory, name);

            if (!File.Exists(target))
            {
                File.Copy(entry, target);
                bytes += new FileInfo(entry).Length;
                files++;
            }
        }

        return (bytes, files);
    }

    private static (long Bytes, int Files) CopyDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        var bytes = 0L;
        var files = 0;

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (subBytes, subFiles) = CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)),
                cancellationToken);

            bytes += subBytes;
            files += subFiles;
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = Path.Combine(destination, Path.GetFileName(file));

            if (File.Exists(target))
            {
                continue;
            }

            File.Copy(file, target);
            bytes += new FileInfo(file).Length;
            files++;
        }

        return (bytes, files);
    }

    private static long MeasureDirectory(string directory)
    {
        var total = 0L;

        foreach (var entry in SafeEntries(directory))
        {
            if (Directory.Exists(entry))
            {
                if (SkippedFolders.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                total += MeasureTree(entry);
            }
            else
            {
                total += SafeLength(entry);
            }
        }

        return total;
    }

    private static long MeasureTree(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(SafeLength);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static IEnumerable<string> SafeEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
