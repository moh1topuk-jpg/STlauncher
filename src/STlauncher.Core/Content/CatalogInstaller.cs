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

/// <param name="Downloaded">
/// False when the file was already on disk and passed its hash check. The launcher used
/// to report every verified file as downloaded, which made a no-op sync look like it had
/// just pulled the whole build again.
/// </param>
/// <param name="Version">What this file satisfies, so the next sync can compare.</param>
public sealed record CatalogInstallResult(
    bool Success,
    string? Path,
    string Message,
    bool Downloaded = false,
    string? Version = null)
{
    public static CatalogInstallResult Ok(string path, bool downloaded, string? version) =>
        new(true,
            path,
            downloaded
                ? $"Installed {System.IO.Path.GetFileName(path)}"
                : $"Already present: {System.IO.Path.GetFileName(path)}",
            downloaded,
            version);

    public static CatalogInstallResult Fail(string message) => new(false, null, message);
}

/// <summary>The file a catalog item stands for, or why there is none.</summary>
/// <param name="Version">What the file satisfies, recorded so the next sync can compare.</param>
public sealed record CatalogResolved(DownloadItem? Download, string? Version, string? Error)
{
    public static CatalogResolved Fail(string error) => new(null, null, error);
}

/// <summary>
/// Resolves a catalog item through Modrinth or a direct URL and places the file in the
/// selected instance. No API key is required for any installed content.
/// </summary>
public sealed class CatalogInstaller
{
    private readonly DownloadClient _downloader;
    private readonly ModrinthClient _modrinth;
    private readonly ILogger<CatalogInstaller>? _logger;

    public CatalogInstaller(
        DownloadClient downloader,
        ModrinthClient modrinth,
        ILogger<CatalogInstaller>? logger = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
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
            var resolved = await ResolveAsync(item, instanceDirectory, gameVersion, loader, cancellationToken).ConfigureAwait(false);

            if (resolved.Download is not { } download)
            {
                return CatalogInstallResult.Fail(resolved.Error ?? "?");
            }

            var downloaded = await _downloader.EnsureFileAsync(download, cancellationToken).ConfigureAwait(false);
            return CatalogInstallResult.Ok(download.DestinationPath, downloaded, resolved.Version);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to install catalog item {Id}.", item.Id);
            return CatalogInstallResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Which file an item stands for and where in the build it goes, without fetching it:
    /// what "prepare in advance" downloads ahead and the install later finds ready.
    /// </summary>
    public async Task<CatalogResolved> ResolveAsync(
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

        return item.Source.Kind switch
        {
            CatalogSourceKind.Modrinth => await ResolveModrinthAsync(item, instanceDirectory, gameVersion, loader, cancellationToken).ConfigureAwait(false),
            CatalogSourceKind.Direct => ResolveDirect(item, instanceDirectory),
            _ => CatalogResolved.Fail($"Unsupported source kind '{item.Source.Kind}'.")
        };
    }

    private async Task<CatalogResolved> ResolveModrinthAsync(
        CatalogItem item,
        string instanceDirectory,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken)
    {
        var project = item.Source.Project;
        if (string.IsNullOrWhiteSpace(project))
        {
            return CatalogResolved.Fail("Modrinth source has no project.");
        }

        IReadOnlyList<ModVersion> versions;

        if (!string.IsNullOrWhiteSpace(item.Source.Version))
        {
            // A pinned version is authoritative, but the same number can exist for several
            // Minecraft releases, so narrow it to the requested game version and loader.
            var all = await _modrinth.GetVersionsAsync(project!, null, LoaderKind.Vanilla, cancellationToken)
                .ConfigureAwait(false);

            var matches = all
                .Where(v => string.Equals(v.Id, item.Source.Version, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(v.VersionNumber, item.Source.Version, StringComparison.OrdinalIgnoreCase))
                .ToList();

            versions = ModrinthClient.NarrowTo(matches, gameVersion, loader);
        }
        else
        {
            var available = await _modrinth.GetVersionsAsync(project!, gameVersion, loader, cancellationToken)
                .ConfigureAwait(false);

            // Prefer a stable release even when a newer beta exists.
            var preferred = ModrinthClient.SelectPreferred(available);
            versions = preferred is null ? Array.Empty<ModVersion>() : new[] { preferred };
        }

        var version = versions.FirstOrDefault();
        var file = version is null ? null : ModrinthClient.SelectFile(version, gameVersion, loader);

        if (file is null || string.IsNullOrEmpty(file.Url))
        {
            return CatalogResolved.Fail(
                $"No Modrinth file found for '{project}'" +
                (string.IsNullOrWhiteSpace(item.Source.Version) ? $" ({gameVersion}, {loader})" : $" version {item.Source.Version}") +
                ".");
        }

        // The pin is stored verbatim rather than the resolved number: a pin may be a
        // version id, and the next sync compares against exactly this string.
        var satisfies = string.IsNullOrWhiteSpace(item.Source.Version)
            ? version?.VersionNumber
            : item.Source.Version;

        return Place(item, instanceDirectory, file.Url, file.FileName, file.Sha1, file.Sha512, file.Size, satisfies);
    }

    private static CatalogResolved ResolveDirect(CatalogItem item, string instanceDirectory)
    {
        var url = item.Source.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            return CatalogResolved.Fail("Direct source has no url.");
        }

        var fileName = item.Source.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = FileNameFromUrl(url!);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return CatalogResolved.Fail("Could not determine a file name; set 'fileName' in the catalog.");
        }

        return Place(item, instanceDirectory, url!, fileName!, item.Source.Sha1, item.Source.Sha512, 0, item.Source.Version);
    }

    private static CatalogResolved Place(
        CatalogItem item,
        string instanceDirectory,
        string url,
        string fileName,
        string? sha1,
        string? sha512,
        long size,
        string? satisfiesVersion)
    {
        var relative = CatalogPlacement.ResolveRelativePath(item, fileName);

        if (relative is null)
        {
            return CatalogResolved.Fail(
                $"Cannot place '{fileName}': set 'targetPath' in the catalog for item type '{item.Type}'.");
        }

        var destination = Path.Combine(instanceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

        return new CatalogResolved(new DownloadItem(url, destination, sha1, size, Sha512: sha512), satisfiesVersion, null);
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