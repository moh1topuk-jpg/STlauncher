using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;

namespace STlauncher.Core.Modpacks;

public sealed record ModpackInstallResult(
    ModpackPlan Plan,
    int InstalledFiles,
    int FailedFiles,
    int SkippedFiles);

public sealed class ModpackInstaller
{
    private readonly DownloadClient _downloader;

    public ModpackInstaller(DownloadClient downloader)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    }

    public async Task<ModpackInstallResult> InstallAsync(
        string modpackPath,
        string instanceDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modpackPath))
        {
            throw new FileNotFoundException("Modpack file was not found.", modpackPath);
        }

        var plan = ModpackReader.Read(modpackPath);
        Directory.CreateDirectory(instanceDirectory);

        var items = new List<DownloadItem>();
        var skipped = 0;

        foreach (var file in plan.Files)
        {
            if (string.IsNullOrEmpty(file.Url))
            {
                skipped++;
                continue;
            }

            items.Add(new DownloadItem(
                file.Url,
                SafeCombine(instanceDirectory, file.RelativePath),
                file.Sha1,
                file.Size,
                Sha512: file.Sha512));
        }

        var summary = await _downloader.DownloadAllAsync(items, progress, cancellationToken).ConfigureAwait(false);

        ExtractOverrides(modpackPath, instanceDirectory, ModpackReader.OverrideFolderPrefixes());

        return new ModpackInstallResult(plan, summary.Succeeded, summary.Failed, skipped);
    }

    public static void ExtractOverrides(
        string modpackPath,
        string instanceDirectory,
        IReadOnlyList<string> prefixes)
    {
        Directory.CreateDirectory(instanceDirectory);

        var root = Path.GetFullPath(instanceDirectory);

        // The separator matters: without it "...\instances\foo" also matches
        // "...\instances\foobar", so an entry could land in a sibling instance.
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(modpackPath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var prefix = prefixes.FirstOrDefault(p =>
                entry.FullName.StartsWith(p, StringComparison.Ordinal));

            if (prefix is null)
            {
                continue;
            }

            var relative = entry.FullName[prefix.Length..];
            if (!RelativePath.IsSafe(relative))
            {
                continue;
            }

            var target = Path.GetFullPath(SafeCombine(root, relative));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Blocked modpack entry outside of the instance: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string SafeCombine(string root, string relativePath)
        => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}