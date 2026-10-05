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

    /// <summary>
    /// What a Forge or NeoForge installer downloads besides Mojang's server jar: the
    /// loader's libraries and Mojang's mappings, about ninety files. Measured on real
    /// installs for 1.21.1 - about 50 MB for Forge 52.1.16, 65 MB for NeoForge 21.1.255;
    /// the installer does not say in advance, and the launcher does not open it before
    /// the player has agreed. The folder ends up near three times that on disk: the
    /// installer also writes patched copies of the game.
    /// </summary>
    private const long ForgeLibrariesApproxBytes = 60L * 1024 * 1024;

    /// <summary>How much of the installer's output is kept to explain a failure.</summary>
    private const int InstallerLogLines = 40;

    private readonly HttpClient _http;
    private readonly MetadataClient _metadata;
    private readonly DownloadClient _downloader;
    private readonly LoaderService _loaders;
    private readonly HostedServerStore _store;
    private readonly ServerJava _java;
    private readonly ILogger<ServerInstaller>? _logger;

    public ServerInstaller(
        HttpClient http,
        MetadataClient metadata,
        DownloadClient downloader,
        LoaderService loaders,
        HostedServerStore store,
        ServerJava java,
        ILogger<ServerInstaller>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _logger = logger;

        ResolveJava = async (major, token) => _java.Find(major) ?? await _java.EnsureAsync(major, token).ConfigureAwait(false);
        RunInstaller = (command, onLine, token) => LoaderInstallerProcess.RunAsync(command, onLine, token);
    }

    /// <summary>
    /// Finds the Java a loader's installer is run with, downloading the launcher's own
    /// runtime when the machine has none. Replaceable so that tests need no Java.
    /// </summary>
    public Func<int, CancellationToken, Task<string>> ResolveJava { get; init; }

    /// <summary>Runs a loader's installer. Replaceable so that tests need neither Java nor Forge.</summary>
    public Func<LoaderInstallerCommand, Action<string>, CancellationToken, Task<LoaderInstallerExit>> RunInstaller { get; init; }

    /// <summary>
    /// Vanilla, Fabric, Forge and NeoForge. Quilt is not in this version: its server
    /// needs yet another installer of its own.
    /// </summary>
    public static bool IsSupported(LoaderKind loader)
        => loader is LoaderKind.Vanilla or LoaderKind.Fabric or LoaderKind.Forge or LoaderKind.NeoForge;

    /// <summary>
    /// True once what starts the server - a jar or, for Forge since 1.17, an argument
    /// file - is in its folder.
    /// </summary>
    public bool IsInstalled(HostedServer server)
        => !string.IsNullOrWhiteSpace(server.LaunchJar) &&
           File.Exists(Path.Combine(_store.ServerDirectory(server), server.LaunchJar!));

    public Task<ServerInstallPlan> PlanAsync(HostedServer server, CancellationToken cancellationToken = default)
        => PlanAsync(server.GameVersion, server.Loader, server.LoaderVersion, cancellationToken);

    /// <summary>
    /// Works out what the server consists of. Reads Mojang's and the loader's version
    /// lists and, for an installer, its size and checksum; no part of the server is
    /// downloaded.
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

            if (ForgeServer.IsForgeLike(loader))
            {
                return await PlanWithInstallerAsync(gameVersion, loader, loaderVersion, artifact, javaMajor, cancellationToken)
                    .ConfigureAwait(false);
            }

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

        if (plan.RunsInstaller)
        {
            return await InstallWithInstallerAsync(server, plan, directory, progress, cancellationToken).ConfigureAwait(false);
        }

        var now = plan.Downloads.Where(d => !d.AtFirstStart && !d.ByInstaller).ToList();
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
    /// The plan for Forge and NeoForge. The launcher fetches one file, the installer;
    /// everything else the installer downloads itself while it runs, and the plan says
    /// so line by line: Mojang's server jar with its exact size, and the loader's
    /// libraries with an estimate.
    /// </summary>
    private async Task<ServerInstallPlan> PlanWithInstallerAsync(
        string gameVersion,
        LoaderKind loader,
        string? loaderVersion,
        DownloadArtifact serverJar,
        int javaMajor,
        CancellationToken cancellationToken)
    {
        var resolved = string.IsNullOrWhiteSpace(loaderVersion)
            ? (await _loaders.GetLoaderVersionsAsync(loader, gameVersion, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault()?.Version
            : loaderVersion!.Trim();

        if (resolved is null)
        {
            // The list comes back empty both when the loader has nothing for this game
            // version and when its site cannot be reached. Asking the site once more
            // tells the two apart: this throws when there is no answer at all.
            using var probe = await _http.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, ForgeServer.MavenRoot(loader)), cancellationToken).ConfigureAwait(false);

            return ServerInstallPlan.Failed(ServerInstallStatus.LoaderUnavailable, gameVersion, loader, loaderVersion);
        }

        var mavenVersion = ForgeServer.MavenVersion(loader, gameVersion, resolved);
        var installerUrl = ForgeServer.InstallerUrl(loader, mavenVersion);
        long installerSize;

        // Headers only: the size for the list the player agrees to, and proof that this
        // build of the loader exists at all.
        using (var head = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, installerUrl), cancellationToken).ConfigureAwait(false))
        {
            if (head.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                return ServerInstallPlan.Failed(ServerInstallStatus.LoaderUnavailable, gameVersion, loader, loaderVersion);
            }

            head.EnsureSuccessStatusCode();
            installerSize = head.Content.Headers.ContentLength ?? 0;
        }

        var sha1 = await TryGetMavenSha1Async(installerUrl, cancellationToken).ConfigureAwait(false);
        var maven = ForgeServer.MavenRoot(loader);

        var downloads = new List<ServerDownload>
        {
            new(ServerDownloadKind.LoaderInstaller, installerUrl, HostOf(installerUrl), installerSize, installerSize > 0, sha1),
            new(ServerDownloadKind.ServerJar, serverJar.Url!, HostOf(serverJar.Url!), serverJar.Size, serverJar.Size > 0, serverJar.Sha1, ByInstaller: true),
            new(ServerDownloadKind.LoaderLibraries, maven, HostOf(maven), ForgeLibrariesApproxBytes, SizeIsExact: false, ByInstaller: true)
        };

        return new ServerInstallPlan(ServerInstallStatus.Ready, gameVersion, loader, resolved, null, javaMajor, downloads);
    }

    /// <summary>
    /// The checksum a maven serves beside every file, as "&lt;file&gt;.sha1". Null when
    /// there is none or it is not a SHA-1: the installer is then downloaded unverified,
    /// over HTTPS, as Forge's own site would hand it out.
    /// </summary>
    private async Task<string?> TryGetMavenSha1Async(string fileUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(fileUrl + ".sha1", cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        // Some mavens append the file name after the digest.
        var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        var digest = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        return digest.Length == 40 && digest.All(Uri.IsHexDigit) ? digest.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Forge and NeoForge: download the installer, run it in the server's folder with
    /// the Java the game version asks for, and record what it left to start the server
    /// with. Until the installer has finished cleanly the server stays marked as not
    /// installed, whatever is already lying in its folder.
    /// </summary>
    private async Task<ServerInstallResult> InstallWithInstallerAsync(
        HostedServer server,
        ServerInstallPlan plan,
        string directory,
        IProgress<ServerInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var download = plan.Downloads.First(d => d.Kind == ServerDownloadKind.LoaderInstaller);
        var mavenVersion = ForgeServer.MavenVersion(plan.Loader, plan.GameVersion, plan.LoaderVersion ?? string.Empty);
        var installerPath = Path.Combine(directory, $"{plan.Loader.ToString().ToLowerInvariant()}-{Safe(mavenVersion)}-installer.jar");

        try
        {
            var item = new DownloadItem(download.Url, installerPath, download.Sha1, download.SizeIsExact ? download.SizeBytes : 0);
            await DownloadWithProgressAsync(item, download, 0, 1, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger?.LogWarning(ex, "Installer download failed for {Id}.", server.Id);
            return new ServerInstallResult(ServerInstallOutcome.DownloadFailed, NetworkFailures.Classify(ex));
        }
        finally
        {
            _downloader.Cache.Save();
        }

        string javaPath;

        try
        {
            // Usually instant; a download of unknown length when the machine has no such Java.
            progress?.Report(new ServerInstallProgress(ServerDownloadKind.JavaRuntime, 0, 1, 0, 0));
            javaPath = await ResolveJava(plan.JavaMajor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "No Java {Major} to run the installer for {Id}.", plan.JavaMajor, server.Id);
            return new ServerInstallResult(ServerInstallOutcome.JavaUnavailable, Detail: ex.Message);
        }

        // Written down before the installer touches the folder: if the launcher is
        // closed or the machine goes down half-way, the server is found not installed
        // and the install is offered again, instead of a start on half a server.
        server.LaunchJar = null;
        _store.Save(server);

        var tail = new Queue<string>();
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        var skipped = 0;
        var reported = 0;

        void OnLine(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);

                if (tail.Count > InstallerLogLines)
                {
                    tail.Dequeue();
                }

                // A Forge install prints some thirty thousand lines in half a minute. Each
                // report is a hop to the UI thread, so past the first lines the console gets
                // one at most every quarter second with a count of what was left out; the
                // full tail is kept above for the error message.
                if (++reported > 100 && lastReport.ElapsedMilliseconds < 250)
                {
                    skipped++;
                    return;
                }

                if (skipped > 0)
                {
                    line = $"{line}   (+{skipped})";
                    skipped = 0;
                }

                lastReport.Restart();
            }

            progress?.Report(new ServerInstallProgress(
                ServerDownloadKind.LoaderInstaller, 0, 1, 0, 0, InstallerRunning: true, InstallerLine: line));
        }

        progress?.Report(new ServerInstallProgress(ServerDownloadKind.LoaderInstaller, 0, 1, 0, 0, InstallerRunning: true));

        // No folder after the flag: the installer installs into the one it is run in,
        // which every generation of it understands.
        var command = new LoaderInstallerCommand(javaPath, new[] { "-jar", installerPath, "--installServer" }, directory);
        LoaderInstallerExit exit;

        try
        {
            exit = await RunInstaller(command, OnLine, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RemoveLaunchFiles(directory, plan.Loader, mavenVersion);
            throw;
        }

        string[] log;

        lock (tail)
        {
            log = tail.ToArray();
        }

        var target = exit.Succeeded ? ForgeServer.FindLaunchTarget(directory, plan.Loader, mavenVersion) : null;

        if (target is null)
        {
            // The libraries stay: they are verified by the installer and the next
            // attempt does not fetch them again. What goes is whatever could start.
            RemoveLaunchFiles(directory, plan.Loader, mavenVersion);

            var detail = exit.End switch
            {
                LoaderInstallerEnd.FailedToStart => exit.Detail ?? "Java could not be started",
                LoaderInstallerEnd.TimedOut => "the installer did not finish in time and was stopped",
                LoaderInstallerEnd.WentSilent => "the installer stopped responding and was stopped",
                _ when exit.ExitCode != 0 => LastWords(log) ?? $"the installer exited with code {exit.ExitCode}",
                _ => "the installer finished but left no server to start"
            };

            _logger?.LogWarning(
                "The {Loader} installer failed for {Id}: {End}, code {Code}. {Detail}",
                plan.Loader, server.Id, exit.End, exit.ExitCode, detail);

            return new ServerInstallResult(ServerInstallOutcome.InstallerFailed, Detail: detail, InstallerLog: log);
        }

        // Nine megabytes that are never needed again, and a log that only repeats the console.
        TryDelete(installerPath);
        TryDelete(installerPath + ".log");

        server.LaunchJar = target;
        server.JavaMajor = plan.JavaMajor;
        server.LoaderVersion = plan.LoaderVersion;
        _store.Save(server);

        _logger?.LogInformation("Installed {Loader} server {Version} into {Directory}.", plan.Loader, mavenVersion, directory);
        return new ServerInstallResult(ServerInstallOutcome.Installed, InstallerLog: log);
    }

    /// <summary>
    /// After an install that did not finish: nothing that would start a server is left
    /// in the folder, so that neither the launcher nor a player looking into it takes
    /// half a server for a whole one.
    /// </summary>
    private static void RemoveLaunchFiles(string directory, LoaderKind loader, string mavenVersion)
    {
        var args = Path.Combine(directory, ForgeServer.ArgsDirectory(loader, mavenVersion));

        TryDelete(Path.Combine(args, ForgeServer.WindowsArgsFile));
        TryDelete(Path.Combine(args, ForgeServer.UnixArgsFile));
        TryDelete(Path.Combine(directory, "run.bat"));
        TryDelete(Path.Combine(directory, "run.sh"));

        foreach (var suffix in new[] { string.Empty, "-universal", "-shim" })
        {
            TryDelete(Path.Combine(directory, $"forge-{mavenVersion}{suffix}.jar"));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; it is not what decides whether the server counts as installed.
        }
    }

    /// <summary>The last thing the installer said that is not a stack frame: usually the reason.</summary>
    private static string? LastWords(IReadOnlyList<string> log)
    {
        var line = log
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0 && !l.StartsWith("at ", StringComparison.Ordinal) && !l.StartsWith("...", StringComparison.Ordinal));

        return line is null ? null : line.Length > 300 ? line[..300] : line;
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
