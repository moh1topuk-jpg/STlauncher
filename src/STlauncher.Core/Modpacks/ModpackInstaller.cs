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
    int SkippedFiles)
{
    /// <summary>The files that did not come, each with its reason: (path in the build, why).</summary>
    public IReadOnlyList<(string Path, string Reason)> Failures { get; init; } = Array.Empty<(string, string)>();

    /// <summary>
    /// Files whose hash in the pack matched nothing and that were accepted by the hash
    /// Modrinth itself publishes for them.
    /// </summary>
    public IReadOnlyList<string> AcceptedByModrinthHash { get; init; } = Array.Empty<string>();
}

public sealed class ModpackInstaller
{
    private readonly DownloadClient _downloader;
    private readonly Mods.ModrinthClient? _modrinth;

    /// <param name="modrinth">Asked about a file only when the pack's own hash for it is wrong; without it such a file fails.</param>
    public ModpackInstaller(DownloadClient downloader, Mods.ModrinthClient? modrinth = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _modrinth = modrinth;
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

        // The whole list, before the first byte: a bad last entry must not be found out
        // after everything before it has been fetched.
        ModpackIndexCheck.ThrowIfBroken(plan);

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

        var failures = new List<(string Path, string Reason)>();
        var accepted = new List<string>();

        foreach (var (item, error) in summary.Errors)
        {
            var path = Path.GetRelativePath(instanceDirectory, item.DestinationPath).Replace('\\', '/');

            if (await TryAcceptByModrinthHashAsync(item, error, cancellationToken).ConfigureAwait(false))
            {
                accepted.Add(path);
            }
            else
            {
                failures.Add((path, error.Message));
            }
        }

        ExtractOverrides(modpackPath, instanceDirectory, ModpackReader.OverrideFolderPrefixes());

        return new ModpackInstallResult(plan, summary.Succeeded + accepted.Count, failures.Count, skipped)
        {
            Failures = failures,
            AcceptedByModrinthHash = accepted
        };
    }

    /// <summary>
    /// Some packs ship a hash that matches nothing - the author rebuilt the list by hand,
    /// or an exporter hashed the wrong file. The address still names the exact Modrinth
    /// version the file belongs to, and Modrinth publishes the hashes of its own files:
    /// a file whose bytes match those is the file the pack meant.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. Only a failure that is the digest and nothing else (the file
    /// came whole); only an address on Modrinth's CDN in its one known shape; only the
    /// hash of the file with that very address, taken from Modrinth's API and not from
    /// anything in the pack. Any doubt along the way and the file fails as it did before.
    /// </remarks>
    private async Task<bool> TryAcceptByModrinthHashAsync(DownloadItem item, Exception error, CancellationToken cancellationToken)
    {
        if (_modrinth is null ||
            error is not DownloadFailedException { Failure.Cause: NetworkFailureCause.HashMismatch } ||
            ModrinthCdnFile.TryParse(item.Url) is not { } address)
        {
            return false;
        }

        try
        {
            var version = await _modrinth.GetVersionAsync(address.VersionId, cancellationToken).ConfigureAwait(false);

            if (version is null ||
                (version.ProjectId is { Length: > 0 } project && !string.Equals(project, address.ProjectId, StringComparison.Ordinal)))
            {
                return false;
            }

            var file = version.Files.FirstOrDefault(f => SameAddress(f.Url, item.Url));

            if (file is null)
            {
                return false;
            }

            var sha512 = ModpackIndexCheck.IsHex(file.Sha512, 128) ? file.Sha512 : null;
            var sha1 = ModpackIndexCheck.IsHex(file.Sha1, 40) ? file.Sha1 : null;

            if (sha512 is null && sha1 is null)
            {
                return false;
            }

            // Fetched again and held to Modrinth's hash this time. A mismatch here means
            // the bytes really are not the file, and that is the failure to report.
            await _downloader
                .EnsureFileAsync(item with { Sha1 = sha1, Sha512 = sha512, Sha256 = null }, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool SameAddress(string? a, string? b)
        => Uri.TryCreate(a, UriKind.Absolute, out var left) &&
           Uri.TryCreate(b, UriKind.Absolute, out var right) &&
           Uri.Compare(left, right, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;

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

            // Not ExtractToFile(overwrite: true): an override may land on a mod the build
            // shares with others, and writing into it would change it for all of them.
            Storage.FileReplace.Extract(entry, target);
        }
    }

    private static string SafeCombine(string root, string relativePath)
        => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}