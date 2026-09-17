using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Modpacks;

public sealed record ModpackInstallResult(
    ModpackPlan Plan,
    int InstalledFiles,
    int FailedFiles,
    int SkippedFiles);

public sealed class ModpackInstaller
{
    private readonly DownloadClient _downloader;
    private readonly CurseForgeClient? _curseForge;

    public ModpackInstaller(DownloadClient downloader, CurseForgeClient? curseForge = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _curseForge = curseForge;
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

        var (items, skipped) = await BuildDownloadsAsync(plan, instanceDirectory, cancellationToken)
            .ConfigureAwait(false);

        var summary = await _downloader.DownloadAllAsync(items, progress, cancellationToken).ConfigureAwait(false);

        ExtractOverrides(modpackPath, instanceDirectory, ModpackReader.OverrideFolderPrefixes(plan));

        return new ModpackInstallResult(plan, summary.Succeeded, summary.Failed, skipped);
    }

    private async Task<(List<DownloadItem> Items, int Skipped)> BuildDownloadsAsync(
        ModpackPlan plan,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        var items = new List<DownloadItem>();
        var skipped = 0;

        if (plan.Format == ModpackFormat.Modrinth)
        {
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

            return (items, skipped);
        }

        if (_curseForge is null || !_curseForge.IsConfigured)
        {
            throw new InvalidOperationException(
                "A CurseForge API key is required to install CurseForge modpacks. " +
                "Set it in Settings or the CURSEFORGE_API_KEY environment variable.");
        }

        var fileIds = plan.Files
            .Where(f => f.FileId is > 0)
            .Select(f => f.FileId!.Value)
            .ToList();

        var resolved = await _curseForge.GetFilesAsync(fileIds, cancellationToken).ConfigureAwait(false);

        foreach (var file in plan.Files)
        {
            if (file.FileId is not > 0)
            {
                skipped++;
                continue;
            }

            if (!resolved.TryGetValue(file.FileId.Value, out var info) ||
                string.IsNullOrEmpty(info.DownloadUrl))
            {
                skipped++;
                continue;
            }

            var folder = CurseForgeClient.FolderForClassId(info.ClassId);
            if (string.IsNullOrEmpty(folder))
            {
                skipped++;
                continue;
            }

            var relative = $"{folder}/{info.FileName}";
            if (!ModpackReader.IsSafeRelativePath(relative))
            {
                skipped++;
                continue;
            }

            items.Add(new DownloadItem(info.DownloadUrl, SafeCombine(instanceDirectory, relative), info.Sha1, info.Length));
        }

        return (items, skipped);
    }

    public static void ExtractOverrides(string modpackPath, string instanceDirectory, IReadOnlyList<string> prefixes)
    {
        Directory.CreateDirectory(instanceDirectory);
        var root = Path.GetFullPath(instanceDirectory);

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
            if (!ModpackReader.IsSafeRelativePath(relative))
            {
                continue;
            }

            var target = Path.GetFullPath(SafeCombine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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