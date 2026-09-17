using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core;
using STlauncher.Core.Assets;
using STlauncher.Core.Auth;
using STlauncher.Core.Content;
using STlauncher.Core.Http;
using STlauncher.Core.Java;
using STlauncher.Core.Launch;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;
using STlauncher.Core.Mods;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Server;
using STlauncher.Core.Versions;

namespace STlauncher.Cli;

internal static class Program
{
    /// <summary>Same address the launcher injects into every instance; see ServerDefaults.</summary>
    private const string LauncherServerAddress = "mc.showtime.su";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var paths = LauncherPaths.Default();
        paths.EnsureCreated();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("STlauncher.Cli/0.1.0");

        var downloader = new DownloadClient(http);
        var metadata = new MetadataClient(http, paths);
        var versions = new VersionService(metadata, paths);
        var assets = new AssetService(metadata, paths, downloader);
        var java = new JavaManager(paths, downloader, http);
        var gameLauncher = new GameLauncher();
        var launch = new LaunchService(versions, assets, java, downloader, gameLauncher, paths);
        var loaders = new LoaderService(http, paths, downloader, java);
        var mods = new ModManager(downloader);
        var modpacks = new ModpackInstaller(downloader);

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "versions" => await ListVersionsAsync(versions, args),
                "java" => ListJava(java),
                "loaders" => await ListLoadersAsync(loaders, args),
                "install-loader" => await InstallLoaderAsync(loaders, versions, args),
                "prepare" => await PrepareAsync(launch, args, run: false),
                "run" => await PrepareAsync(launch, args, run: true),
                "plan" => await PlanAsync(versions, assets, paths, args),
                "modpack" => await InstallModpackAsync(modpacks, mods, args),
                "catalog" => await ShowCatalogAsync(http, paths, args),
                "catalog-install" => await InstallCatalogItemAsync(http, paths, downloader, args),
                "mods-search" => await SearchModsAsync(http, args),
                "build-plan" => await ShowBuildPlanAsync(http, args),
                "build-install" => await InstallBuildAsync(http, paths, downloader, args),
                "server-status" => await ShowServerStatusAsync(args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static async Task<int> ListVersionsAsync(VersionService versions, string[] args)
    {
        var includeSnapshots = args.Contains("--all");
        var manifest = await versions.GetManifestAsync();

        Console.WriteLine($"latest release : {manifest.Latest?.Release}");
        Console.WriteLine($"latest snapshot: {manifest.Latest?.Snapshot}");
        Console.WriteLine();

        foreach (var version in manifest.Versions)
        {
            if (!includeSnapshots && version.Type != "release")
            {
                continue;
            }

            Console.WriteLine($"{version.Id,-20} {version.Type,-12} {version.ReleaseTime:yyyy-MM-dd}");
        }

        return 0;
    }

    private static int ListJava(JavaManager java)
    {
        var found = java.DiscoverInstalled();

        if (found.Count == 0)
        {
            Console.WriteLine("No Java installations found.");
            return 0;
        }

        foreach (var installation in found)
        {
            Console.WriteLine($"java {installation.MajorVersion,-4} {installation.ExecutablePath}");
        }

        return 0;
    }

    private static async Task<int> ListLoadersAsync(LoaderService loaders, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: loaders <fabric|quilt|forge|neoforge> <gameVersion>");
            return 2;
        }

        var kind = ParseLoader(args[1]);
        var list = await loaders.GetLoaderVersionsAsync(kind, args[2]);
        Console.WriteLine($"{list.Count} {kind} builds for {args[2]}.");

        foreach (var version in list.Take(10))
        {
            Console.WriteLine($"  {version.Version}{(version.Stable ? string.Empty : " (beta)")}");
        }

        return 0;
    }

    private static async Task<int> PrepareAsync(LaunchService launch, string[] args, bool run)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: prepare|run <versionId> <username> [--loader kind] [--loader-version v] [--server host] [--memory mb]");
            return 2;
        }

        var versionId = args[1];
        var username = args[2];

        var loaderKind = LoaderKind.Vanilla;
        var loaderVersion = Option(args, "--loader-version");
        var server = Option(args, "--server");
        var memory = int.TryParse(Option(args, "--memory"), out var m) ? m : 2048;
        var loaderArg = Option(args, "--loader");

        if (loaderArg is not null)
        {
            loaderKind = ParseLoader(loaderArg);
        }

        if (!OfflineAuth.IsValidUsername(username))
        {
            Console.Error.WriteLine("error: username must be 3-16 chars: A-Z a-z 0-9 _");
            return 2;
        }

        var paths = LauncherPaths.Default();
        var gameDir = paths.InstanceDirectory(Option(args, "--instance") ?? "default");

        if (loaderKind != LoaderKind.Vanilla)
        {
            Console.WriteLine($"Installing {loaderKind}...");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("STlauncher.Cli/0.1.0");
            var downloader = new DownloadClient(http);
            var java = new JavaManager(paths, downloader, http);
            var metadata = new MetadataClient(http, paths);
            var versions = new VersionService(metadata, paths);
            var loaders = new LoaderService(http, paths, downloader, java);

            var javaMajor = (await versions.ResolveAsync(versionId)).JavaVersion?.MajorVersion ?? 8;
            var log = new Progress<string>(Console.WriteLine);

            versionId = await loaders.InstallAsync(loaderKind, versionId, loaderVersion, javaMajor, log);
            Console.WriteLine($"Loader version: {versionId}");
        }

        var account = OfflineAuth.Login(username);

        var settings = new LaunchSettings
        {
            GameDirectory = gameDir,
            MaxMemoryMb = memory,
            MinMemoryMb = 512,
            ServerAddress = server,

            // The launcher always adds its server to the in-game list, so the CLI does the
            // same unless explicitly told not to.
            ServerListName = Option(args, "--server-name") ?? "Showtime",
            ServerListAddress = args.Contains("--no-server-list") ? null : server ?? LauncherServerAddress
        };

        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Completed % 50 == 0 || p.Completed == p.Total)
            {
                Console.WriteLine($"  files {p.Completed}/{p.Total} (failed {p.Failed})");
            }
        });

        Console.WriteLine($"Preparing {versionId} as {username}...");
        var command = await launch.PrepareAsync(versionId, account, settings, progress);

        Console.WriteLine();
        Console.WriteLine("Command:");
        Console.WriteLine($"  {command.FileName}");
        foreach (var argument in command.Arguments)
        {
            Console.WriteLine($"    {argument}");
        }

        if (!run)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run completed. Add 'run' instead of 'prepare' to launch.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Launching...");
        launch.Launcher.OutputReceived += Console.WriteLine;
        launch.Launcher.ErrorReceived += line => Console.Error.WriteLine(line);

        var exitCode = await launch.LaunchAsync(command, gameDir);
        Console.WriteLine($"Minecraft exited with code {exitCode}.");
        return exitCode;
    }

    private static async Task<int> InstallLoaderAsync(
        LoaderService loaders,
        VersionService versions,
        string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: install-loader <kind> <gameVersion> [loaderVersion]");
            return 2;
        }

        var kind = ParseLoader(args[1]);
        var gameVersion = args[2];
        var loaderVersion = args.Length > 3 ? args[3] : null;

        var javaMajor = (await versions.ResolveAsync(gameVersion)).JavaVersion?.MajorVersion ?? 8;
        var log = new Progress<string>(Console.WriteLine);

        var id = await loaders.InstallAsync(kind, gameVersion, loaderVersion, javaMajor, log);
        Console.WriteLine($"installed version id: {id}");
        return 0;
    }

    private static async Task<int> PlanAsync(
        VersionService versions,
        AssetService assets,
        LauncherPaths paths,
        string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: plan <versionId>");
            return 2;
        }

        var resolved = await versions.ResolveAsync(args[1]);
        var context = RuleContext.Current();

        Console.WriteLine($"libraries in json : {resolved.Libraries.Count}");
        Console.WriteLine($"mainClass         : {resolved.MainClass}");

        var libraries = LibraryResolver.Resolve(resolved, paths, context);
        Console.WriteLine($"library downloads : {libraries.Downloads.Count}");
        Console.WriteLine($"natives           : {libraries.Natives.Count}");
        Console.WriteLine($"classpath entries : {libraries.Classpath.Count}");

        Console.WriteLine($"assetIndex ref    : {(resolved.AssetIndex is null ? "NULL" : resolved.AssetIndex.Id)}");
        Console.WriteLine($"assets field      : {resolved.Assets ?? "NULL"}");
        Console.WriteLine($"client download   : {(resolved.Downloads?.Client is null ? "NULL" : "yes")}");
        Console.WriteLine($"javaVersion       : {resolved.JavaVersion?.MajorVersion.ToString() ?? "NULL"}");
        Console.WriteLine($"game args         : {resolved.GameArguments.Count}");
        Console.WriteLine($"jvm args          : {resolved.JvmArguments.Count}");
        Console.WriteLine($"minecraftArguments: {(resolved.MinecraftArguments is null ? "NULL" : "present")}");

        if (resolved.AssetIndex is { Url.Length: > 0 } assetRef)
        {
            var index = await assets.GetIndexAsync(assetRef);
            Console.WriteLine($"asset objects     : {index.Objects.Count}");

            var plan = assets.BuildDownloadPlan(index);
            Console.WriteLine($"asset downloads   : {plan.Count}");

            foreach (var item in plan.Take(3))
            {
                Console.WriteLine($"  -> {item.DestinationPath}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Resolves every mod of a recommended build, without downloading anything. This is the
    /// check to run after the mod list changes.
    /// </summary>
    /// <summary>
    /// Installs a recommended build into an instance using the same CatalogInstaller the
    /// launcher uses, so this verifies the real code path rather than a reimplementation.
    /// </summary>
    private static async Task<int> InstallBuildAsync(
        HttpClient http,
        LauncherPaths paths,
        DownloadClient downloader,
        string[] args)
    {
        var service = new ContentCatalogService(http, paths) { CatalogUrl = Option(args, "--catalog") };
        var loaded = await service.LoadAsync();

        if (loaded.Catalog is null)
        {
            Console.Error.WriteLine(loaded.Error ?? "No catalog configured. Pass --catalog <url|path>.");
            return 1;
        }

        var catalog = loaded.Catalog;
        var buildId = Option(args, "--build");
        var build = buildId is null
            ? catalog.Builds.FirstOrDefault()
            : catalog.Builds.FirstOrDefault(b => string.Equals(b.Id, buildId, StringComparison.OrdinalIgnoreCase));

        if (build is null)
        {
            Console.Error.WriteLine("Build not found.");
            return 1;
        }

        var instanceId = Option(args, "--instance") ?? "build-test";
        var instanceDir = paths.InstanceDirectory(instanceId);
        Directory.CreateDirectory(instanceDir);

        Console.WriteLine($"build    : {build.Name}");
        Console.WriteLine($"instance : {instanceId}");
        Console.WriteLine($"mods     : {build.Items.Count}");
        Console.WriteLine();

        var installer = new CatalogInstaller(downloader, new ModrinthClient(http));
        var failed = 0;

        foreach (var id in build.Items)
        {
            var item = catalog.FindItem(id);

            if (item is null)
            {
                Console.WriteLine($"  {id,-26} MISSING in catalog");
                failed++;
                continue;
            }

            var result = await installer.InstallAsync(item, instanceDir, build.GameVersion, build.Loader);

            if (result.Success)
            {
                Console.WriteLine($"  {id,-26} {System.IO.Path.GetFileName(result.Path)}");
            }
            else
            {
                Console.WriteLine($"  {id,-26} FAILED: {result.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"Installed into {instanceDir}"
            : $"{failed} mod(s) failed.");

        return failed == 0 ? 0 : 1;
    }

    /// <summary>Pings a Minecraft server and prints what the launcher would show.</summary>
    private static async Task<int> ShowServerStatusAsync(string[] args)
    {
        var address = Option(args, "--server") ?? "mc.showtime.su";
        var host = address;
        var port = ServerPinger.DefaultPort;

        var separator = address.LastIndexOf(':');

        if (separator > 0 && int.TryParse(address[(separator + 1)..], out var parsedPort))
        {
            host = address[..separator];
            port = parsedPort;
        }

        Console.WriteLine($"pinging {host}:{port} ...");

        var status = await ServerPinger.PingAsync(host, port);

        if (status is null)
        {
            Console.WriteLine("no response");
            return 1;
        }

        Console.WriteLine($"motd    : {status.Motd}");
        Console.WriteLine($"players : {status.Online} / {status.Max}");
        Console.WriteLine($"version : {status.VersionName} (protocol {status.ProtocolVersion})");
        Console.WriteLine($"favicon : {(status.Favicon is { Length: > 0 } f ? $"{f.Length} bytes" : "none")}");

        return 0;
    }

    private static async Task<int> ShowBuildPlanAsync(HttpClient http, string[] args)
    {
        var service = new ContentCatalogService(http, LauncherPaths.Default()) { CatalogUrl = Option(args, "--catalog") };
        var loaded = await service.LoadAsync();

        if (loaded.Catalog is null)
        {
            Console.Error.WriteLine(loaded.Error ?? "No catalog configured. Pass --catalog <url|path>.");
            return 1;
        }

        var catalog = loaded.Catalog;
        var buildId = Option(args, "--build");
        var build = buildId is null
            ? catalog.Builds.FirstOrDefault()
            : catalog.Builds.FirstOrDefault(b => string.Equals(b.Id, buildId, StringComparison.OrdinalIgnoreCase));

        if (build is null)
        {
            Console.Error.WriteLine("Build not found.");
            return 1;
        }

        Console.WriteLine($"build    : {build.Name} ({build.Id})");
        Console.WriteLine($"minecraft: {build.GameVersion}   loader: {build.Loader} {build.LoaderVersion}");
        Console.WriteLine($"mods     : {build.Items.Count}");
        Console.WriteLine();

        var client = new ModrinthClient(http);
        var failed = 0;

        foreach (var id in build.Items)
        {
            var item = catalog.FindItem(id);

            if (item is null)
            {
                Console.WriteLine($"  {id,-26} MISSING in catalog sections");
                failed++;
                continue;
            }

            var project = item.Source.Project;

            if (string.IsNullOrWhiteSpace(project))
            {
                Console.WriteLine($"  {id,-26} no modrinth project");
                failed++;
                continue;
            }

            try
            {
                IReadOnlyList<ModVersion> versions;
                var pinned = item.Source.Version;

                if (!string.IsNullOrWhiteSpace(pinned))
                {
                    // A pinned version is authoritative and must not be filtered by
                    // version or loader, otherwise the pin silently does nothing.
                    var all = await client.GetVersionsAsync(project, null, LoaderKind.Vanilla);
                    var matches = all
                        .Where(v => string.Equals(v.Id, pinned, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(v.VersionNumber, pinned, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    versions = ModrinthClient.NarrowTo(matches, build.GameVersion, build.Loader);
                }
                else
                {
                    var available = await client.GetVersionsAsync(project, build.GameVersion, build.Loader);
                    var preferred = ModrinthClient.SelectPreferred(available);
                    versions = preferred is null ? Array.Empty<ModVersion>() : new[] { preferred };
                }

                var chosen = versions.FirstOrDefault();
                var file = chosen is null ? null : ModrinthClient.SelectFile(chosen, build.GameVersion, build.Loader);

                if (file is null)
                {
                    Console.WriteLine(
                        string.IsNullOrWhiteSpace(pinned)
                            ? $"  {id,-26} NOT FOUND for {build.GameVersion} + {build.Loader}"
                            : $"  {id,-26} PINNED VERSION NOT FOUND: {pinned}");
                    failed++;
                    continue;
                }

                var mark = string.IsNullOrWhiteSpace(pinned) ? "     " : "PIN  ";
                Console.WriteLine($"  {id,-26} {mark}[{chosen!.VersionType,-7}] {file.FileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {id,-26} ERROR {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "All mods resolve." : $"{failed} mod(s) failed.");

        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> SearchModsAsync(HttpClient http, string[] args)
    {
        var query = args.Length > 1 ? args[1] : string.Empty;
        var gameVersion = Option(args, "--game");
        var loader = ParseLoader(Option(args, "--loader") ?? "fabric");
        var category = Option(args, "--category");

        var client = new ModrinthClient(http);

        if (args.Contains("--categories"))
        {
            var categories = await client.GetCategoriesAsync();
            Console.WriteLine($"{categories.Count} categories:");
            Console.WriteLine("  " + string.Join(", ", categories.Select(c => c.Name)));
            return 0;
        }

        Console.WriteLine($"facets: {ModrinthClient.BuildFacets(gameVersion, loader, category)}");

        var page = await client.SearchAsync(query, gameVersion, loader, category, Option(args, "--sort") ?? "relevance");

        Console.WriteLine($"{page.Items.Count} of {page.TotalHits} result(s):");

        foreach (var result in page.Items)
        {
            Console.WriteLine($"  {result.Slug,-24} {result.Title}");
            Console.WriteLine($"      {result.Description}");
            Console.WriteLine($"      downloads={result.Downloads} icon={(string.IsNullOrEmpty(result.IconUrl) ? "-" : "yes")}");
        }

        return 0;
    }

    private static async Task<int> ShowCatalogAsync(
        HttpClient http,
        LauncherPaths paths,
        string[] args)
    {
        var service = new ContentCatalogService(http, paths) { CatalogUrl = Option(args, "--catalog") };
        var result = await service.LoadAsync();

        if (result.Catalog is null)
        {
            Console.Error.WriteLine(result.Error is null
                ? "No catalog found. Pass --catalog <url|path>, or drop catalog.json next to settings.json."
                : $"Failed to load the catalog: {result.Error}");
            return 1;
        }

        var catalog = result.Catalog;
        Console.WriteLine($"catalog : {catalog.Name} (schema {catalog.SchemaVersion}, {result.Origin})");
        Console.WriteLine($"items   : {catalog.ItemCount}");
        Console.WriteLine();

        foreach (var section in catalog.Sections)
        {
            Console.WriteLine($"[{section.Title}] {section.Description}");

            foreach (var item in section.Items)
            {
                var flag = item.Required ? "required" : "optional";
                Console.WriteLine($"  {item.Id,-28} {item.Type,-13} {flag,-8} {item.Name}");
                if (!string.IsNullOrWhiteSpace(item.Description))
                {
                    Console.WriteLine($"      {item.Description}");
                }
            }

            Console.WriteLine();
        }

        if (catalog.Builds.Count > 0)
        {
            Console.WriteLine("builds:");

            foreach (var build in catalog.Builds)
            {
                Console.WriteLine($"  {build.Id,-28} {build.GameVersion,-10} {build.Loader,-8} {build.Name}");
                Console.WriteLine($"      items: {string.Join(", ", build.Items)}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static async Task<int> InstallCatalogItemAsync(
        HttpClient http,
        LauncherPaths paths,
        DownloadClient downloader,
        string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: catalog-install <itemId> [--catalog <url>] [--instance id] [--game 1.20.1] [--loader fabric]");
            return 2;
        }

        var itemId = args[1];
        var instanceId = Option(args, "--instance") ?? "default";
        var gameVersion = Option(args, "--game");
        var loader = ParseLoader(Option(args, "--loader") ?? "vanilla");

        var service = new ContentCatalogService(http, paths) { CatalogUrl = Option(args, "--catalog") };
        var loaded = await service.LoadAsync();

        if (loaded.Catalog is null)
        {
            Console.Error.WriteLine(loaded.Error ?? "No catalog configured. Pass --catalog <url>.");
            return 1;
        }

        var item = loaded.Catalog.Sections
            .SelectMany(s => s.Items)
            .FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            Console.Error.WriteLine($"Item '{itemId}' was not found in the catalog.");
            return 1;
        }

        var installer = new CatalogInstaller(downloader, new ModrinthClient(http));

        var instanceDir = paths.InstanceDirectory(instanceId);
        Console.WriteLine($"Installing '{item.Name}' into '{instanceId}' (game {gameVersion ?? "?"}, {loader})...");

        var result = await installer.InstallAsync(item, instanceDir, gameVersion, loader);

        Console.WriteLine(result.Success ? $"OK   : {result.Path}" : $"FAIL : {result.Message}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> InstallModpackAsync(ModpackInstaller installer, ModManager mods, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: modpack <path.mrpack> [instanceId]");
            return 2;
        }

        var instanceId = args.Length > 2 ? args[2] : "default";
        var gameDir = LauncherPaths.Default().InstanceDirectory(instanceId);

        var progress = new Progress<DownloadProgress>(p => Console.WriteLine($"  files {p.Completed}/{p.Total}"));

        var result = await installer.InstallAsync(args[1], gameDir, progress);

        Console.WriteLine($"Modpack   : {result.Plan.Name} {result.Plan.VersionId}");
        Console.WriteLine($"Minecraft : {result.Plan.GameVersion}");
        Console.WriteLine($"Loader    : {result.Plan.Loader} {result.Plan.LoaderVersion}");
        Console.WriteLine($"Installed : {result.InstalledFiles} file(s), failed {result.FailedFiles}, skipped {result.SkippedFiles}");
        Console.WriteLine($"Mods      : {mods.ListMods(gameDir).Count}");

        return result.FailedFiles == 0 ? 0 : 1;
    }

    private static LoaderKind ParseLoader(string value) => value.ToLowerInvariant() switch
    {
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        "forge" => LoaderKind.Forge,
        "neoforge" => LoaderKind.NeoForge,
        "vanilla" => LoaderKind.Vanilla,
        _ => throw new ArgumentException($"Unknown loader '{value}'.")
    };

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            STlauncher CLI

            usage: stlauncher <command> [options]

            commands:
              versions [--all]                     list versions from the Mojang manifest
              java                                 list detected Java installations
              loaders <kind> <gameVersion>         list loader builds
              prepare <version> <nick> [options]    download and print the launch command
              run <version> <nick> [options]        download and launch the game
              modpack <file.mrpack> [instanceId]    install a Modrinth modpack
              catalog [--catalog <url|path>]       show the content catalog
              catalog-install <itemId> [options]   install one catalog item
              build-plan [--build id]              resolve every mod of a recommended build
              build-install [--build id] [--instance name]  install a build the way the launcher does
              server-status [--server host[:port]]  ping a Minecraft server

            options:
              --loader <fabric|quilt|forge|neoforge>
              --loader-version <version>
              --server <host[:port]>               quick-play: join this server on launch
              --no-server-list                     do not add the launcher server to the list
              --memory <mb>
              --instance <id>                      game directory (default: "default")
            """);
    }
}

