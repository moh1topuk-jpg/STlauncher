using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Http;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;

namespace STlauncher.Core.Hosting;

/// <summary>
/// Finds and downloads the server for a game version and loader. Two steps on purpose:
/// <see cref="PlanAsync"/> only reads lists and says what would be fetched, from where
/// and how big; <see cref="InstallAsync"/> fetches it, and is called once the player
/// has seen that and agreed.
/// </summary>
public sealed class ServerInstaller
{
    public const string VanillaJarName = "server.jar";

    private const string FabricMeta = "https://meta.fabricmc.net/v2";
    private const string FabricMaven = "https://maven.fabricmc.net/";

    /// <summary>
    /// Fabric's meta server builds the launcher jar on request and does not state its
    /// size; it has been about this big for years.
    /// </summary>
    private const long FabricLauncherApproxBytes = 200 * 1024;

    /// <summary>The loader, the intermediary mappings, Mixin and ASM that Fabric's launcher fetches itself.</summary>
    private const long FabricLibrariesApproxBytes = 5 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly MetadataClient _metadata;
    private readonly DownloadClient _downloader;
    private readonly LoaderService _loaders;
    private readonly HostedServerStore _store;
    private readonly ILogger<ServerInstaller>? _logger;

    public ServerInstaller(
        HttpClient http,
        MetadataClient metadata,
        DownloadClient downloader,
        LoaderService loaders,
        HostedServerStore store,
        ILogger<ServerInstaller>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>Vanilla and Fabric. The other loaders need their own installers run and are not in this version.</summary>
    public static bool IsSupported(LoaderKind loader) => loader is LoaderKind.Vanilla or LoaderKind.Fabric;

    /// <summary>True once the jar that starts the server is in its folder.</summary>
    public bool IsInstalled(HostedServer server)
        => !string.IsNullOrWhiteSpace(server.LaunchJar) &&
           File.Exists(Path.Combine(_store.ServerDirectory(server), server.LaunchJar!));

    public Task<ServerInstallPlan> PlanAsync(HostedServer server, CancellationToken cancellationToken = default)
        => PlanAsync(server.GameVersion, server.Loader, server.LoaderVersion, cancellationToken);

    /// <summary>
    /// Works out what the server consists of. Reads Mojang's and Fabric's version lists
    /// and nothing more; no part of the server is downloaded.
    /// </summary>
    public async Task<ServerInstallPlan> PlanAsync(
        string gameVersion,
        LoaderKind loader,
        string? loaderVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported(loader))
        {
            return ServerInstallPlan.Failed(ServerInstallStatus.UnsupportedLoader, gameVersion, loader, loaderVersion);
        }

        try
        {
            var manifest = await _metadata.GetVersionManifestAsync(cancellationToken).ConfigureAwait(false);
            var summary = manifest.Versions.FirstOrDefault(v => v.Id == gameVersion);

            if (summary is null)
            {
                return ServerInstallPlan.Failed(ServerInstallStatus.UnknownGameVersion, gameVersion, loader, loaderVersion);
            }

            var version = await _metadata.GetVersionJsonAsync(summary.Url, cancellationToken).ConfigureAwait(false);
            var artifact = version.Downloads?.Server;

            if (string.IsNullOrEmpty(artifact?.Url))
            {
                return ServerInstallPlan.Failed(ServerInstallStatus.NoServerForVersion, gameVersion, loader, loaderVersion);
            }

            var javaMajor = version.JavaVersion is { MajorVersion: > 0 } java ? java.MajorVersion : 8;

            var downloads = new List<ServerDownload>
            {
                new(ServerDownloadKind.ServerJar, artifact.Url!, HostOf(artifact.Url!), artifact.Size, artifact.Size > 0, artifact.Sha1)
            };

            if (loader == LoaderKind.Vanilla)
            {
                return new ServerInstallPlan(ServerInstallStatus.Ready, gameVersion, loader, null, null, javaMajor, downloads);
            }

            // The installer list is asked first: it throws when Fabric cannot be reached,
            // whereas the loader list quietly comes back empty, which would read as
            // "Fabric has nothing for this version".
            var installer = await GetFabricInstallerVersionAsync(cancellationToken).ConfigureAwait(false);

            var resolvedLoader = string.IsNullOrWhiteSpace(loaderVersion)
                ? await PickFabricLoaderAsync(gameVersion, cancellationToken).ConfigureAwait(false)
                : loaderVersion!.Trim();

            if (installer is null || resolvedLoader is null)
            {
                return ServerInstallPlan.Failed(ServerInstallStatus.LoaderUnavailable, gameVersion, loader, loaderVersion);
            }

            var launcherUrl =
                $"{FabricMeta}/versions/loader/{Uri.EscapeDataString(gameVersion)}/" +
                $"{Uri.EscapeDataString(resolvedLoader)}/{Uri.EscapeDataString(installer)}/server/jar";

            downloads.Add(new ServerDownload(
                ServerDownloadKind.LoaderLauncher, launcherUrl, HostOf(launcherUrl), FabricLauncherApproxBytes, SizeIsExact: false));

            downloads.Add(new ServerDownload(
                ServerDownloadKind.LoaderLibraries, FabricMaven, HostOf(FabricMaven), FabricLibrariesApproxBytes,
                SizeIsExact: false, AtFirstStart: true));

            return new ServerInstallPlan(ServerInstallStatus.Ready, gameVersion, loader, resolvedLoader, installer, javaMajor, downloads);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _logger?.LogWarning(ex, "Could not plan a {Loader} server for {Version}.", loader, gameVersion);
            return ServerInstallPlan.Failed(
                ServerInstallStatus.NetworkError, gameVersion, loader, loaderVersion, NetworkFailures.Classify(ex));
        }
    }

    /// <summary>
    /// Downloads what the plan lists into the server's folder and records how to start
    /// it. Call only after the player has agreed to the plan. A file that is already
    /// there and matches its checksum is not fetched again.
    /// </summary>
    public async Task<ServerInstallResult> InstallAsync(
        HostedServer server,
        ServerInstallPlan plan,
        IProgress<ServerInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        // A plan shown to the player for one version must not install another.
        if (!plan.CanInstall || plan.GameVersion != server.GameVersion || plan.Loader != server.Loader)
        {
            return new ServerInstallResult(ServerInstallOutcome.PlanNotReady);
        }

        var directory = _store.ServerDirectory(server);
        Directory.CreateDirectory(directory);

        var now = plan.Downloads.Where(d => !d.AtFirstStart).ToList();
        string launchJar = VanillaJarName;

        try
        {
            for (var i = 0; i < now.Count; i++)
            {
                var download = now[i];
                var fileName = FileNameFor(download, plan);
                var item = new DownloadItem(
                    download.Url,
                    Path.Combine(directory, fileName),
                    download.Sha1,
                    download.SizeIsExact ? download.SizeBytes : 0);

                if (download.Kind == ServerDownloadKind.LoaderLauncher)
                {
                    launchJar = fileName;
                }

                await DownloadWithProgressAsync(item, download, i, now.Count, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger?.LogWarning(ex, "Server download failed for {Id}.", server.Id);
            return new ServerInstallResult(ServerInstallOutcome.DownloadFailed, NetworkFailures.Classify(ex));
        }
        finally
        {
            _downloader.Cache.Save();
        }

        server.LaunchJar = launchJar;
        server.JavaMajor = plan.JavaMajor;
        server.LoaderVersion = plan.LoaderVersion;
        _store.Save(server);

        _logger?.LogInformation("Installed {Loader} server {Version} into {Directory}.", plan.Loader, plan.GameVersion, directory);
        return new ServerInstallResult(ServerInstallOutcome.Installed);
    }

    /// <summary>
    /// Fabric's launcher carries the loader version inside, so each build gets its own
    /// file: changing the loader never overwrites the jar the server ran with before.
    /// </summary>
    private static string FileNameFor(ServerDownload download, ServerInstallPlan plan)
        => download.Kind == ServerDownloadKind.LoaderLauncher
            ? $"fabric-server-mc.{Safe(plan.GameVersion)}-loader.{Safe(plan.LoaderVersion)}-launcher.{Safe(plan.InstallerVersion)}.jar"
            : VanillaJarName;

    private static string Safe(string? part)
        => string.Concat((part ?? "unknown").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private async Task DownloadWithProgressAsync(
        DownloadItem item,
        ServerDownload download,
        int index,
        int count,
        IProgress<ServerInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = download.SizeIsExact ? download.SizeBytes : 0;
        progress?.Report(new ServerInstallProgress(download.Kind, index, count, 0, total));

        var task = _downloader.EnsureFileAsync(item, cancellationToken);

        if (progress is not null)
        {
            // The download client reports whole files only, and a server jar is fifty
            // megabytes of silence on a slow line. Its partial file is watched instead.
            var directory = Path.GetDirectoryName(item.DestinationPath)!;
            var pattern = Path.GetFileName(item.DestinationPath) + ".*.part";

            while (await Task.WhenAny(task, Task.Delay(400, CancellationToken.None)).ConfigureAwait(false) != task)
            {
                var done = PartialLength(directory, pattern);

                if (done > 0)
                {
                    progress.Report(new ServerInstallProgress(download.Kind, index, count, done, total));
                }
            }
        }

        await task.ConfigureAwait(false);

        var length = new FileInfo(item.DestinationPath).Length;
        progress?.Report(new ServerInstallProgress(download.Kind, index, count, length, length));
    }

    private static long PartialLength(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern).Select(p => new FileInfo(p).Length).DefaultIfEmpty(0).Max();
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private async Task<string?> GetFabricInstallerVersionAsync(CancellationToken cancellationToken)
    {
        var json = await _http.GetStringAsync($"{FabricMeta}/versions/installer", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? first = null;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var version = versionEl.GetString();
            first ??= version;

            if (element.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.True)
            {
                return version;
            }
        }

        return first;
    }

    private async Task<string?> PickFabricLoaderAsync(string gameVersion, CancellationToken cancellationToken)
    {
        var versions = await _loaders.GetLoaderVersionsAsync(LoaderKind.Fabric, gameVersion, cancellationToken).ConfigureAwait(false);
        return (versions.FirstOrDefault(v => v.Stable) ?? versions.FirstOrDefault())?.Version;
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
