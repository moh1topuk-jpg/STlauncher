using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;
using STlauncher.Core.Loaders;

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

public sealed record ImportResult(Instance Instance, ImportMode Mode, long CopiedBytes, int CopiedFiles)
{
    /// <summary>
    /// Links found inside the build and left behind, as paths relative to its folder. A
    /// build whose saves folder is a link to another drive imports without its worlds,
    /// and the player has to hear that rather than find it out.
    /// </summary>
    public IReadOnlyList<string> SkippedLinks { get; init; } = Array.Empty<string>();

    /// <summary>The settings taken over from the old launcher, when there were any.</summary>
    public CarriedSettings? CarriedSettings { get; init; }
}

/// <summary>
/// The build's folder, or its profile, is a symbolic link or a junction. Nothing is
/// imported through one: what is behind it was never looked at.
/// </summary>
public sealed class LinkedSourceException : InvalidOperationException
{
    public LinkedSourceException(string path, string? target)
        : base(target is null
            ? $"'{path}' is a link to another place and is not imported through."
            : $"'{path}' is a link to '{target}' and is not imported through.")
    {
        LinkPath = path;
        Target = target;
    }

    public string LinkPath { get; }

    public string? Target { get; }
}

/// <summary>
/// Brings a build found by <see cref="ExternalInstanceScanner"/> into the launcher.
/// </summary>
public sealed class InstanceImporter
{
    private static readonly Regex LoaderVersionShape = new(@"^[0-9A-Za-z][0-9A-Za-z.+_-]{0,47}$", RegexOptions.CultureInvariant);

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

        return PlanFiles(source, skippedLinks: null, tolerant: true).Sum(f => SafeLength(f.File));
    }

    /// <inheritdoc cref="ImportCopyRules.IsLauncherFile"/>
    public static bool IsLauncherFile(string fileName) => ImportCopyRules.IsLauncherFile(fileName);

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

        if (source.Problem == ExternalInstanceProblem.SourceIsLink)
        {
            throw new LinkedSourceException(source.GameDirectory, source.LinkTarget);
        }

        if (!source.IsUsable)
        {
            throw new InvalidOperationException(
                $"'{source.Name}' cannot be imported: {source.Problem}.");
        }

        // Looked at again now, before anything is created: the scan may be minutes old,
        // and a folder swapped for a link in between must not be walked into.
        RefuseLink(source.GameDirectory);

        if (!string.IsNullOrWhiteSpace(source.VersionJsonPath))
        {
            RefuseLink(source.VersionJsonPath!);
            RefuseLink(Path.GetDirectoryName(source.VersionJsonPath!)!);
        }

        var instance = _instances.Create(string.IsNullOrWhiteSpace(name) ? source.Name : name!);

        instance.Loader = source.Loader;
        instance.DetectedModCount = source.ModCount;

        if (source.HasOwnProfile)
        {
            // Launched by its own profile; the version is only what the catalog filters by.
            instance.ProfileVersionId = source.VersionId;
            instance.VersionId = source.GameVersion;
            instance.LoaderVersion = source.LoaderVersion;
        }
        else
        {
            instance.VersionId = string.IsNullOrWhiteSpace(source.VersionId) ? null : source.VersionId;

            // The exact loader build the old launcher pinned, so the build starts on what
            // it was played with rather than on today's newest. A version the loader list
            // does not have is simply not selected, and the newest is taken as before.
            instance.LoaderVersion = PinnedLoaderVersion(source);
        }

        var carried = ApplyCarriedSettings(instance, source.Settings);

        // The profile has to live where the launcher looks for versions, in both modes:
        // it is what describes how the game starts, and it is small.
        progress?.Report("profile");
        CopyVersionProfile(source);

        var copiedBytes = 0L;
        var copiedFiles = 0;
        var skippedLinks = new List<string>();

        if (mode == ImportMode.Link)
        {
            instance.ExternalGameDirectory = source.GameDirectory;
        }
        else
        {
            var destination = _paths.InstanceDirectory(instance.Id);

            (copiedBytes, copiedFiles) = await Task.Run(
                () => CopyGameFiles(source, destination, skippedLinks, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        _instances.Save(instance);

        return new ImportResult(instance, mode, copiedBytes, copiedFiles)
        {
            SkippedLinks = skippedLinks,
            CarriedSettings = carried
        };
    }

    private static void RefuseLink(string path)
    {
        if (LinkGuard.IsLink(path))
        {
            throw new LinkedSourceException(path, LinkGuard.TargetOf(path));
        }
    }

    /// <summary>
    /// Writes the old launcher's per-build settings into the new build. Checked again
    /// here although the reader already did: the record may have been put together by
    /// anything, and this is the last point before the values reach a command line.
    /// </summary>
    public static CarriedSettings? ApplyCarriedSettings(Instance instance, CarriedSettings? settings)
    {
        if (settings is null)
        {
            return null;
        }

        int? maxMemory = null;
        int? minMemory = null;

        if (settings.MaxMemoryMb is { } max &&
            max is >= SourceInstanceSettings.MinAllowedMemoryMb and <= SourceInstanceSettings.MaxAllowedMemoryMb)
        {
            maxMemory = max;
            instance.MaxMemoryMb = max;

            if (settings.MinMemoryMb is { } min && min >= 128 && min <= max)
            {
                minMemory = min;
                instance.MinMemoryMb = min;
            }
            else if (instance.MinMemoryMb > max)
            {
                instance.MinMemoryMb = max;
            }
        }

        int? width = null;
        int? height = null;

        if (settings is { Width: >= 320 and <= 7680, Height: >= 240 and <= 4320 })
        {
            (width, height) = (settings.Width, settings.Height);
            (instance.Width, instance.Height) = (width, height);
        }

        var arguments = JvmArgumentAllowlist.Filter(settings.JvmArguments);

        if (arguments.Kept.Count > 0)
        {
            instance.ExtraJvmArgs = arguments.KeptText;
        }

        var applied = new CarriedSettings(
            maxMemory,
            minMemory,
            width,
            height,
            arguments.Kept,
            settings.DroppedJvmArguments.Concat(arguments.Dropped).ToList());

        return applied.HasAnything ? applied : null;
    }

    private static string? PinnedLoaderVersion(ExternalInstance source)
    {
        var version = source.LoaderVersion?.Trim();

        if (source.Loader == LoaderKind.Vanilla || string.IsNullOrEmpty(version) || !LoaderVersionShape.IsMatch(version))
        {
            return null;
        }

        // NeoForge for 1.20.1 is published as "1.20.1-47.1.3"; launchers record just "47.1.3".
        if (source.Loader == LoaderKind.NeoForge && source.VersionId == "1.20.1" &&
            !version.StartsWith("1.20.1-", StringComparison.Ordinal))
        {
            return "1.20.1-" + version;
        }

        return version;
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

        // A jar that is a link is left where it is: the launcher fetches the game itself.
        if (LinkGuard.IsRealFile(sourceJar) && !File.Exists(targetJar))
        {
            File.Copy(sourceJar, targetJar);
        }
    }

    private static (long Bytes, int Files) CopyGameFiles(
        ExternalInstance source,
        string destinationDirectory,
        List<string> skippedLinks,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source.GameDirectory))
        {
            return (0, 0);
        }

        Directory.CreateDirectory(destinationDirectory);

        var bytes = 0L;
        var files = 0;
        string? reported = null;

        foreach (var (file, relative) in PlanFiles(source, skippedLinks, tolerant: false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var top = relative.Split(Path.DirectorySeparatorChar, 2)[0];

            if (!string.Equals(top, reported, StringComparison.Ordinal) && relative.Length > top.Length)
            {
                reported = top;
                progress?.Report(top);
            }

            var target = Path.Combine(destinationDirectory, relative);

            if (File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            bytes += SafeLength(file);
            files++;
        }

        return (bytes, files);
    }

    /// <summary>
    /// Every file that copying this build would take, with its path relative to the
    /// build's folder. One list for both the estimate and the copy, so the number on the
    /// screen is the number of bytes that then move.
    /// </summary>
    private static IEnumerable<(string File, string Relative)> PlanFiles(
        ExternalInstance source,
        List<string>? skippedLinks,
        bool tolerant)
    {
        var root = source.GameDirectory;

        if (!Directory.Exists(root) || LinkGuard.IsLink(root))
        {
            yield break;
        }

        // When the build's folder is its version folder, the profile and the game jar
        // sit in it too. They are already copied to versions/, where they belong.
        var profileFiles = source.VersionJsonPath is null
            ? Array.Empty<string>()
            : new[]
            {
                Path.GetFileName(source.VersionJsonPath),
                Path.GetFileName(Path.ChangeExtension(source.VersionJsonPath, ".jar"))
            };

        foreach (var entry in Entries(root, tolerant))
        {
            var name = Path.GetFileName(entry);
            var isDirectory = Directory.Exists(entry);

            if (!ImportCopyRules.IsCopiedFromRoot(source, name, isDirectory))
            {
                continue;
            }

            if (LinkGuard.IsLink(entry))
            {
                skippedLinks?.Add(name);
                continue;
            }

            if (!isDirectory)
            {
                if (!profileFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    yield return (entry, name);
                }

                continue;
            }

            foreach (var file in Walk(entry, name, skippedLinks, tolerant))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<(string File, string Relative)> Walk(
        string directory,
        string relative,
        List<string>? skippedLinks,
        bool tolerant)
    {
        foreach (var entry in Entries(directory, tolerant))
        {
            var name = Path.GetFileName(entry);
            var entryRelative = Path.Combine(relative, name);

            if (LinkGuard.IsLink(entry))
            {
                skippedLinks?.Add(entryRelative);
                continue;
            }

            if (Directory.Exists(entry))
            {
                foreach (var file in Walk(entry, entryRelative, skippedLinks, tolerant))
                {
                    yield return file;
                }
            }
            else if (!ImportCopyRules.IsAccountFile(name))
            {
                yield return (entry, entryRelative);
            }
        }
    }

    /// <summary>
    /// The entries of one folder. The estimate shrugs at a folder it cannot read; the
    /// copy does not, because a build that silently lost a folder is worse than an error.
    /// </summary>
    private static IReadOnlyList<string> Entries(string directory, bool tolerant)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception) when (tolerant)
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
