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
using STlauncher.Core.Versions;

namespace STlauncher.Cli;

internal static class Program
{
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
        var java = new JavaManager(paths, downloader);
        var gameLauncher = new GameLauncher();
        var launch = new LaunchService(versions, assets, java, downloader, gameLauncher, paths);
        var loaders = new LoaderService(http, paths, downloader, java);
        var mods = new ModManager(downloader);
        var curseForge = new CurseForgeClient(http, Option(args, "--api-key"));
        var modpacks = new ModpackInstaller(downloader, curseForge);

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
            var java = new JavaManager(paths, downloader);
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
            ServerListName = Option(args, "--server-name") ?? "Server",
            ServerListAddress = server
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
                ? "No catalog configured. Pass --catalog <url>."
                : $"Failed to load the catalog: {result.Error}");
            return 1;
        }

        var catalog = result.Catalog;
        Console.WriteLine($"catalog : {catalog.Name} (schema {catalog.SchemaVersion}, {(result.FromRemote ? "remote" : "cache")})");
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

        var curseForge = new CurseForgeClient(http, Option(args, "--api-key"));
        var installer = new CatalogInstaller(downloader, new ModrinthClient(http), curseForge);

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

        Console.WriteLine($"Modpack   : {result.Plan.Name} {result.Plan.VersionId} [{result.Plan.Format}]");
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
              modpack <file.mrpack> [instanceId]   install a Modrinth/CurseForge modpack
              catalog [--catalog <url>]            show the content catalog
              catalog-install <itemId> [options]   install one catalog item

            options:
              --loader <fabric|quilt|forge|neoforge>
              --loader-version <version>
              --server <host[:port]>
              --memory <mb>
              --instance <id>                      game directory (default: "default")
              --api-key <curseforgeKey>            (or set CURSEFORGE_API_KEY)
            """);
    }
}
