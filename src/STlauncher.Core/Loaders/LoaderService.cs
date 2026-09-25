using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Http;
using STlauncher.Core.Java;

namespace STlauncher.Core.Loaders;

public sealed partial class LoaderService
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3";
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged";

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly DownloadClient _downloader;
    private readonly JavaManager _java;
    private readonly ILogger<LoaderService>? _logger;

    public LoaderService(
        HttpClient http,
        LauncherPaths paths,
        DownloadClient downloader,
        JavaManager java,
        ILogger<LoaderService>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _logger = logger;
    }

    [GeneratedRegex("<version>([^<]+)</version>")]
    private static partial Regex MavenVersionRegex();

    /// <summary>A loader list younger than this is used without asking the network.</summary>
    private static readonly TimeSpan LoaderListFreshFor = TimeSpan.FromHours(12);

    /// <summary>
    /// The cached copy first: the list of loader builds changes a few times a month, and
    /// fetching it on every start cost one to two seconds of every launch. A failed fetch
    /// falls back to whatever copy is on disk, however old.
    /// </summary>
    public async Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
        LoaderKind kind,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (kind == LoaderKind.Vanilla || string.IsNullOrWhiteSpace(gameVersion))
        {
            return Array.Empty<LoaderVersion>();
        }

        var cachePath = LoaderListCachePath(kind, gameVersion);

        if (TryReadLoaderList(cachePath, LoaderListFreshFor) is { } fresh)
        {
            return fresh;
        }

        IReadOnlyList<LoaderVersion> fetched;

        try
        {
            fetched = kind switch
            {
                LoaderKind.Fabric => await GetFabricLikeAsync($"{FabricMeta}/versions/loader/{gameVersion}", cancellationToken)
                    .ConfigureAwait(false),
                LoaderKind.Quilt => await GetFabricLikeAsync($"{QuiltMeta}/versions/loader/{gameVersion}", cancellationToken)
                    .ConfigureAwait(false),
                LoaderKind.Forge => await GetForgeAsync(gameVersion, cancellationToken).ConfigureAwait(false),
                LoaderKind.NeoForge => await GetNeoForgeAsync(gameVersion, cancellationToken).ConfigureAwait(false),
                _ => Array.Empty<LoaderVersion>()
            };
        }
        catch (Exception) when (TryReadLoaderList(cachePath, TimeSpan.MaxValue) is { } stale)
        {
            _logger?.LogWarning("Loader list fetch failed for {Kind} {Version}; using the cached copy.", kind, gameVersion);
            return stale;
        }

        if (fetched.Count > 0)
        {
            WriteLoaderList(cachePath, fetched);
            return fetched;
        }

        // An empty answer is what a failed Fabric fetch looks like; the old copy beats nothing.
        return TryReadLoaderList(cachePath, TimeSpan.MaxValue) ?? fetched;
    }

    private string LoaderListCachePath(LoaderKind kind, string gameVersion)
    {
        var safeVersion = string.Concat(gameVersion.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_paths.Meta, "loaders", $"{kind.ToString().ToLowerInvariant()}-{safeVersion}.json");
    }

    private static IReadOnlyList<LoaderVersion>? TryReadLoaderList(string path, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > maxAge)
            {
                return null;
            }

            var list = JsonSerializer.Deserialize<List<LoaderVersion>>(File.ReadAllText(path));
            return list is { Count: > 0 } ? list : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void WriteLoaderList(string path, IReadOnlyList<LoaderVersion> versions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(versions));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not cache the loader list at {Path}.", path);
        }
    }

    public async Task<string> InstallAsync(
        LoaderKind kind,
        string gameVersion,
        string? loaderVersion,
        int javaMajorVersion = 8,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        switch (kind)
        {
            case LoaderKind.Fabric:
                return await InstallProfileAsync(
                        $"{FabricMeta}/versions/loader/{gameVersion}/{await ResolveLoaderVersionAsync(kind, gameVersion, loaderVersion, cancellationToken).ConfigureAwait(false)}/profile/json",
                        cancellationToken)
                    .ConfigureAwait(false);

            case LoaderKind.Quilt:
                return await InstallProfileAsync(
                        $"{QuiltMeta}/versions/loader/{gameVersion}/{await ResolveLoaderVersionAsync(kind, gameVersion, loaderVersion, cancellationToken).ConfigureAwait(false)}/profile/json",
                        cancellationToken)
                    .ConfigureAwait(false);

            case LoaderKind.Forge:
                return await InstallForgeLikeAsync(
                        gameVersion, loaderVersion, javaMajorVersion, log, cancellationToken)
                    .ConfigureAwait(false);

            case LoaderKind.NeoForge:
                return await InstallNeoForgeAsync(
                        gameVersion, loaderVersion, javaMajorVersion, log, cancellationToken)
                    .ConfigureAwait(false);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported loader kind.");
        }
    }

    private async Task<string> ResolveLoaderVersionAsync(
        LoaderKind kind,
        string gameVersion,
        string? loaderVersion,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(loaderVersion))
        {
            return loaderVersion!;
        }

        var versions = await GetLoaderVersionsAsync(kind, gameVersion, cancellationToken).ConfigureAwait(false);
        var preferred = versions.FirstOrDefault(v => v.Stable) ?? versions.FirstOrDefault();

        return preferred?.Version
               ?? throw new InvalidOperationException($"No {kind} builds found for {gameVersion}.");
    }

    private async Task<string> InstallProfileAsync(string profileUrl, CancellationToken cancellationToken)
    {
        var json = await _http.GetStringAsync(profileUrl, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("id").GetString()
                 ?? throw new InvalidDataException("Loader profile has no id.");

        var directory = _paths.VersionDirectory(id);
        Directory.CreateDirectory(directory);

        // A half-written profile is worse than no profile: the launcher would read it on
        // the next launch and fail to resolve the version instead of reinstalling it.
        AtomicFile.WriteAllText(_paths.VersionJsonPath(id), json);

        _logger?.LogInformation("Installed loader profile {Id}.", id);
        return id;
    }

    private async Task<IReadOnlyList<LoaderVersion>> GetFabricLikeAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var result = new List<LoaderVersion>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("loader", out var loader))
                {
                    continue;
                }

                var version = loader.GetProperty("version").GetString();
                if (version is null)
                {
                    continue;
                }

                var stable = loader.TryGetProperty("stable", out var s) && s.GetBoolean();
                result.Add(new LoaderVersion(version, stable));
            }

            return result;
        }
        catch (HttpRequestException)
        {
            return Array.Empty<LoaderVersion>();
        }
    }

    private async Task<IReadOnlyList<LoaderVersion>> GetForgeAsync(string gameVersion, CancellationToken cancellationToken)
    {
        var metadata = await GetMavenVersionsAsync($"{ForgeMaven}/maven-metadata.xml", cancellationToken)
            .ConfigureAwait(false);

        return metadata
            .Where(v => v.StartsWith(gameVersion + "-", StringComparison.Ordinal))
            .Select(v => new LoaderVersion(v[(gameVersion.Length + 1)..], true))
            .Reverse()
            .ToList();
    }

    private async Task<IReadOnlyList<LoaderVersion>> GetNeoForgeAsync(string gameVersion, CancellationToken cancellationToken)
    {
        var modernPrefix = NeoForgePrefix(gameVersion);

        if (modernPrefix is not null)
        {
            var metadata = await GetMavenVersionsAsync($"{NeoForgeMaven}/neoforge/maven-metadata.xml", cancellationToken)
                .ConfigureAwait(false);

            var matches = metadata
                .Where(v => v.StartsWith(modernPrefix + ".", StringComparison.Ordinal))
                .Reverse()
                .Select(v => new LoaderVersion(v, true))
                .ToList();

            if (matches.Count > 0)
            {
                return matches;
            }
        }

        var legacy = await GetMavenVersionsAsync($"{NeoForgeMaven}/forge/maven-metadata.xml", cancellationToken)
            .ConfigureAwait(false);

        return legacy
            .Where(v => v.StartsWith(gameVersion + "-", StringComparison.Ordinal))
            .Reverse()
            .Select(v => new LoaderVersion(v, true))
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetMavenVersionsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return MavenVersionRegex().Matches(xml).Select(m => m.Groups[1].Value).ToList();
        }
        catch (HttpRequestException)
        {
            return Array.Empty<string>();
        }
    }

    public static string? NeoForgePrefix(string gameVersion)
    {
        if (!gameVersion.StartsWith("1.", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = gameVersion.Split('.');

        if (parts.Length < 3)
        {
            return null;
        }

        // Real manifest ids include pre-releases such as "1.20.2-rc1", where the patch part
        // is not a number. int.Parse here threw a FormatException on those.
        if (!int.TryParse(parts[1], out var minor) || !int.TryParse(parts[2], out var patch))
        {
            return null;
        }

        if (minor == 20 && patch <= 1)
        {
            return null;
        }

        return $"{minor}.{patch}";
    }

    private async Task<string> InstallForgeLikeAsync(
        string gameVersion,
        string? loaderVersion,
        int javaMajorVersion,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var resolved = string.IsNullOrWhiteSpace(loaderVersion)
            ? (await GetForgeAsync(gameVersion, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Version
            : loaderVersion;

        if (resolved is null)
        {
            throw new InvalidOperationException($"No Forge builds found for {gameVersion}.");
        }

        var fullVersion = resolved.Contains('-') ? resolved : $"{gameVersion}-{resolved}";
        var installerUrl = $"{ForgeMaven}/{fullVersion}/forge-{fullVersion}-installer.jar";

        return await RunInstallerAsync(installerUrl, fullVersion, javaMajorVersion, log, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> InstallNeoForgeAsync(
        string gameVersion,
        string? loaderVersion,
        int javaMajorVersion,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var resolved = string.IsNullOrWhiteSpace(loaderVersion)
            ? (await GetLoaderVersionsAsync(LoaderKind.NeoForge, gameVersion, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault()?.Version
            : loaderVersion;

        if (resolved is null)
        {
            throw new InvalidOperationException($"No NeoForge builds found for {gameVersion}.");
        }

        var isLegacy = resolved.Contains('-');
        var url = isLegacy
            ? $"{NeoForgeMaven}/forge/{resolved}/forge-{resolved}-installer.jar"
            : $"{NeoForgeMaven}/neoforge/{resolved}/neoforge-{resolved}-installer.jar";

        return await RunInstallerAsync(url, resolved, javaMajorVersion, log, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> RunInstallerAsync(
        string installerUrl,
        string label,
        int javaMajorVersion,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.Meta);
        var installerPath = Path.Combine(_paths.Meta, $"installer-{label}.jar");

        log?.Report($"Downloading installer {label}...");
        await _downloader.EnsureFileAsync(new DownloadItem(installerUrl, installerPath), cancellationToken)
            .ConfigureAwait(false);

        var javaPath = await _java.EnsureJavaAsync(javaMajorVersion, cancellationToken).ConfigureAwait(false);
        var before = SnapshotVersions();

        log?.Report("Running installer...");
        await RunInstallerProcessAsync(javaPath, installerPath, _paths.Root, log, cancellationToken).ConfigureAwait(false);

        var created = SnapshotVersions().Except(before).ToList();

        var installed = created.FirstOrDefault(p =>
                            p.Contains("forge", StringComparison.OrdinalIgnoreCase))
                        ?? created.FirstOrDefault()
                        ?? throw new InvalidOperationException(
                            $"Installer {label} did not produce a version profile.");

        _logger?.LogInformation("Installed loader version {Id}.", installed);
        return installed;
    }

    private HashSet<string> SnapshotVersions()
    {
        if (!Directory.Exists(_paths.Versions))
        {
            return new HashSet<string>();
        }

        return Directory.EnumerateDirectories(_paths.Versions)
            .Where(d => File.Exists(Path.Combine(d, Path.GetFileName(d) + ".json")))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task RunInstallerProcessAsync(
        string javaPath,
        string installerPath,
        string targetDirectory,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = targetDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(targetDirectory);

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                log?.Report(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                log?.Report(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Installer exited with code {process.ExitCode}.");
        }
    }
}