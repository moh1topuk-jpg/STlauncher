using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Assets;
using STlauncher.Core.Auth;
using STlauncher.Core.Http;
using STlauncher.Core.Java;
using STlauncher.Core.Metadata;
using STlauncher.Core.Versions;

namespace STlauncher.Core.Launch;

public sealed class LaunchService
{
    private readonly VersionService _versions;
    private readonly AssetService _assets;
    private readonly JavaManager _java;
    private readonly DownloadClient _downloader;
    private readonly GameLauncher _launcher;
    private readonly LauncherPaths _paths;
    private readonly ILogger<LaunchService>? _logger;

    public LaunchService(
        VersionService versions,
        AssetService assets,
        JavaManager java,
        DownloadClient downloader,
        GameLauncher launcher,
        LauncherPaths paths,
        ILogger<LaunchService>? logger = null)
    {
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
    }

    public GameLauncher Launcher => _launcher;

    public async Task<LaunchCommand> PrepareAsync(
        string versionId,
        OfflineAccount account,
        LaunchSettings settings,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();

        var context = RuleContext.Current(BuildFeatures(settings));

        if (settings.ForceUpdate)
        {
            // "Re-download the game files" means every file is hashed again, not only
            // the two that are deleted here.
            _downloader.Cache.Clear();
            ForceClean(versionId);
        }

        var resolved = await _versions.ResolveAsync(versionId, cancellationToken).ConfigureAwait(false);
        var libraries = LibraryResolver.Resolve(resolved, _paths, context);

        if (settings.ForceUpdate)
        {
            ForceCleanResolved(resolved);
        }

        var items = new List<DownloadItem>(libraries.Downloads);

        var clientJarPath = _paths.VersionJarPath(
            string.IsNullOrEmpty(resolved.ClientVersionId) ? versionId : resolved.ClientVersionId);

        if (resolved.Downloads?.Client is { Url: not null } client)
        {
            items.Add(new DownloadItem(client.Url, clientJarPath, client.Sha1, client.Size));
        }

        AssetIndex? assetIndex = null;
        if (resolved.AssetIndex is { Url.Length: > 0 } assetRef)
        {
            assetIndex = await _assets.GetIndexAsync(assetRef, cancellationToken).ConfigureAwait(false);
            items.AddRange(_assets.BuildDownloadPlan(assetIndex));
        }

        _logger?.LogInformation("Downloading {Count} files for {Version}.", items.Count, versionId);
        var summary = await _downloader.DownloadAllAsync(items, progress, cancellationToken).ConfigureAwait(false);

        if (summary.Failed > 0)
        {
            throw new IOException(
                $"Failed to download {summary.Failed} of {summary.Total} files for {versionId}.");
        }

        var nativesDirectory = Path.Combine(_paths.Versions, resolved.Id, "natives");
        foreach (var native in libraries.Natives)
        {
            NativesExtractor.Extract(native);
        }

        var assetsDirectory = _paths.Assets;
        if (assetIndex is not null && _assets.IsVirtual(resolved.Assets))
        {
            await _assets.BuildVirtualAssetsAsync(assetIndex, resolved.Assets!, cancellationToken).ConfigureAwait(false);
            assetsDirectory = _assets.VirtualDirectory(resolved.Assets!);
        }

        var javaPath = string.IsNullOrWhiteSpace(settings.JavaPath)
            ? await _java.EnsureJavaAsync(resolved.RequiredJavaMajor, cancellationToken).ConfigureAwait(false)
            : settings.JavaPath!;

        Directory.CreateDirectory(settings.GameDirectory);

        // Written before the first launch so the language picker and the accessibility
        // onboarding are skipped and server resource packs are accepted.
        GameOptions.EnsureDefaults(settings.GameDirectory, settings.LanguageCode);

        if (!string.IsNullOrWhiteSpace(settings.ServerListAddress))
        {
            var serversDat = Path.Combine(settings.GameDirectory, Instances.ServerList.FileName);
            Instances.ServerList.EnsureServer(
                serversDat,
                string.IsNullOrWhiteSpace(settings.ServerListName) ? "Server" : settings.ServerListName!,
                settings.ServerListAddress!);
        }

        var classpath = new List<string>(libraries.Classpath);
        if (File.Exists(clientJarPath))
        {
            classpath.Add(clientJarPath);
        }

        var options = new LaunchOptions
        {
            Version = resolved,
            Account = account,
            JavaPath = javaPath,
            GameDirectory = settings.GameDirectory,
            AssetsDirectory = assetsDirectory,
            NativesDirectory = nativesDirectory,
            LibrariesDirectory = _paths.Libraries,
            Classpath = classpath,
            MaxMemoryMb = settings.MaxMemoryMb,
            MinMemoryMb = settings.MinMemoryMb,
            Width = settings.Width,
            Height = settings.Height,
            ExtraJvmArgs = settings.ExtraJvmArgs,
            ExtraGameArgs = settings.ExtraGameArgs,
            Features = BuildFeatures(settings),
            ServerAddress = settings.ServerAddress
        };

        return LaunchCommandBuilder.Build(options, context);
    }

    /// <summary>
    /// Removes the vanilla version JSON and client jar so they are fetched again.
    /// Loader profiles are left alone: the launcher re-installs them on every launch.
    /// </summary>
    /// <remarks>
    /// Deliberately does not consult the manifest first. Looking the version up cost a
    /// full manifest download and changed nothing: the files are deleted by id either
    /// way, and <see cref="VersionService.EnsureVersionJsonAsync"/> reports a version
    /// that genuinely does not exist a moment later.
    /// </remarks>
    private void ForceClean(string versionId)
    {
        TryDeleteFile(_paths.VersionJsonPath(versionId));
        TryDeleteFile(_paths.VersionJarPath(versionId));
    }

    private void ForceCleanResolved(ResolvedVersion resolved)
    {
        var clientVersion = string.IsNullOrEmpty(resolved.ClientVersionId) ? resolved.Id : resolved.ClientVersionId;

        TryDeleteFile(_paths.VersionJarPath(clientVersion));
        TryDeleteDirectory(Path.Combine(_paths.Versions, resolved.Id, "natives"));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    public Task<int> LaunchAsync(
        LaunchCommand command,
        string workingDirectory,
        CancellationToken cancellationToken = default)
        => _launcher.RunAsync(command, workingDirectory, _paths.Logs, cancellationToken: cancellationToken);

    private static IReadOnlyDictionary<string, bool> BuildFeatures(LaunchSettings settings)
    {
        var features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = settings.Width.HasValue && settings.Height.HasValue,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = !string.IsNullOrEmpty(settings.ServerAddress),
            ["is_quick_play_realms"] = false
        };

        return features;
    }
}