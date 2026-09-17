using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Http;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Content;

public sealed record CatalogInstallResult(bool Success, string? Path, string Message)
{
    public static CatalogInstallResult Ok(string path) => new(true, path, $"Installed {System.IO.Path.GetFileName(path)}");

    public static CatalogInstallResult Fail(string message) => new(false, null, message);
}

/// <summary>
/// Resolves a catalog item through its source (Modrinth, CurseForge or a direct URL)
/// and places the file in the selected instance.
/// </summary>
public sealed class CatalogInstaller
{
    private readonly DownloadClient _downloader;
    private readonly ModrinthClient _modrinth;
    private readonly CurseForgeClient _curseForge;
    private readonly ILogger<CatalogInstaller>? _logger;

    public CatalogInstaller(
        DownloadClient downloader,
        ModrinthClient modrinth,
        CurseForgeClient curseForge,
        ILogger<CatalogInstaller>? logger = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
        _curseForge = curseForge ?? throw new ArgumentNullException(nameof(curseForge));
        _logger = logger;
    }

    public async Task<CatalogInstallResult> InstallAsync(
        CatalogItem item,
        string instanceDirectory,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        try
        {
            return item.Source.Kind switch
            {
                CatalogSourceKind.Modrinth => await InstallFromModrinthAsync(item, instanceDirectory, gameVersion, loader, cancellationToken).ConfigureAwait(false),
                CatalogSourceKind.CurseForge => await InstallFromCurseForgeAsync(item, instanceDirectory, cancellationToken).ConfigureAwait(false),
                CatalogSourceKind.Direct => await InstallDirectAsync(item, instanceDirectory, cancellationToken).ConfigureAwait(false),
                _ => CatalogInstallResult.Fail($"Unsupported source kind '{item.Source.Kind}'.")
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to install catalog item {Id}.", item.Id);
            return CatalogInstallResult.Fail(ex.Message);
        }
    }

    private async Task<CatalogInstallResult> InstallFromModrinthAsync(
        CatalogItem item,
        string instanceDirectory,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken)
    {
        var project = item.Source.Project;
        if (string.IsNullOrWhiteSpace(project))
        {
            return CatalogInstallResult.Fail("Modrinth source has no project.");
        }

        IReadOnlyList<ModVersion> versions;

        if (!string.IsNullOrWhiteSpace(item.Source.Version))
        {
            // A pinned version is authoritative: do not filter it by the instance.
            var all = await _modrinth.GetVersionsAsync(project!, null, LoaderKind.Vanilla, cancellationToken)
                .ConfigureAwait(false);

            versions = all
                .Where(v => string.Equals(v.Id, item.Source.Version, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(v.VersionNumber, item.Source.Version, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            versions = await _modrinth.GetVersionsAsync(project!, gameVersion, loader, cancellationToken)
                .ConfigureAwait(false);
        }

        var file = versions.FirstOrDefault()?.PrimaryFile;

        if (file is null || string.IsNullOrEmpty(file.Url))
        {
            return CatalogInstallResult.Fail(
                $"No Modrinth file found for '{project}'" +
                (string.IsNullOrWhiteSpace(item.Source.Version) ? $" ({gameVersion}, {loader})" : $" version {item.Source.Version}") +
                ".");
        }

        return await DownloadAsync(item, instanceDirectory, file.Url, file.FileName, file.Sha1, file.Sha512, file.Size, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CatalogInstallResult> InstallFromCurseForgeAsync(
        CatalogItem item,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        if (!_curseForge.IsConfigured)
        {
            return CatalogInstallResult.Fail("A CurseForge API key is required for this item. Set it in Settings.");
        }

        if (!int.TryParse(item.Source.FileId, out var fileId) || fileId <= 0)
        {
            return CatalogInstallResult.Fail("CurseForge source has no valid fileId.");
        }

        var resolved = await _curseForge.GetFilesAsync(new[] { fileId }, cancellationToken).ConfigureAwait(false);

        if (!resolved.TryGetValue(fileId, out var info) || string.IsNullOrEmpty(info.DownloadUrl))
        {
            return CatalogInstallResult.Fail(
                $"CurseForge file {fileId} is unavailable (the author may have disabled third-party downloads).");
        }

        return await DownloadAsync(item, instanceDirectory, info.DownloadUrl!, info.FileName, info.Sha1, null, info.Length, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CatalogInstallResult> InstallDirectAsync(
        CatalogItem item,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        var url = item.Source.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            return CatalogInstallResult.Fail("Direct source has no url.");
        }

        var fileName = item.Source.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = FileNameFromUrl(url!);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return CatalogInstallResult.Fail("Could not determine a file name; set 'fileName' in the catalog.");
        }

        return await DownloadAsync(item, instanceDirectory, url!, fileName!, item.Source.Sha1, item.Source.Sha512, 0, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CatalogInstallResult> DownloadAsync(
        CatalogItem item,
        string instanceDirectory,
        string url,
        string fileName,
        string? sha1,
        string? sha512,
        long size,
        CancellationToken cancellationToken)
    {
        var relative = CatalogPlacement.ResolveRelativePath(item, fileName);

        if (relative is null)
        {
            return CatalogInstallResult.Fail(
                $"Cannot place '{fileName}': set 'targetPath' in the catalog for item type '{item.Type}'.");
        }

        var destination = Path.Combine(instanceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        await _downloader
            .EnsureFileAsync(new DownloadItem(url, destination, sha1, size, Sha512: sha512), cancellationToken)
            .ConfigureAwait(false);

        return CatalogInstallResult.Ok(destination);
    }

    public static string? FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? null : Uri.UnescapeDataString(name);
    }
}